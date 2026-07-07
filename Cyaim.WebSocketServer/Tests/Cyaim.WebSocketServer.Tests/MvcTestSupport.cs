using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Shared support types for tests that drive MvcChannelHandler.MvcDistributeAsync
    /// and the full middleware pipeline: a test endpoint controller, a manual
    /// WatchAssemblyContext builder and small static-state helpers.
    /// </summary>
    public static class MvcTestSupport
    {
        public interface IGreetService
        {
            string Greet(string name);
        }

        public sealed class GreetService : IGreetService
        {
            public string Greet(string name) => "hi " + name;
        }

        public class PocoInput
        {
            public string Text { get; set; }
            public int Number { get; set; }
        }

        /// <summary>
        /// Endpoint controller used by MvcDistributeAsync tests. Endpoints resolve as
        /// "wstest.&lt;methodname&gt;" (controller name minus "Controller", lowercase).
        /// </summary>
        public class WsTestController
        {
            private readonly IGreetService _greet;

            public WsTestController()
            {
            }

            public WsTestController(IGreetService greet)
            {
                _greet = greet;
            }

            public HttpContext WebSocketHttpContext { get; set; }
            public WebSocket WebSocketClient { get; set; }

            public string Echo(string text) => "echo:" + text;

            // Per-endpoint buffered cap override (8 MiB) — accepts larger messages than the global default.
            [Infrastructure.Attributes.WebSocket(MaxBytes = 8 * 1024 * 1024)]
            public string EchoBig(string text) => "echobig:" + (text?.Length.ToString() ?? "null");

            // Streaming endpoint — receives the payload as a Stream (fed frame-by-frame), never fully
            // buffered. Cap 1 MiB. Returns the total bytes read.
            [Infrastructure.Attributes.WebSocket(Stream = true, MaxBytes = 1024 * 1024)]
            public async Task<string> Upload(System.IO.Stream body, System.Threading.CancellationToken ct)
            {
                long total = 0;
                var buf = new byte[16 * 1024];
                int n;
                while ((n = await body.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                {
                    total += n;
                }
                return "upload:" + total;
            }

            // Streaming endpoint with no size cap — used by the throughput tests. Reads and discards.
            [Infrastructure.Attributes.WebSocket(Stream = true)]
            public async Task<string> UploadFast(System.IO.Stream body, System.Threading.CancellationToken ct)
            {
                long total = 0;
                var buf = new byte[64 * 1024];
                int n;
                while ((n = await body.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                {
                    total += n;
                }
                return "upload:" + total;
            }

            public int Add(int a, int b) => a + b;

            public string NoParams() => "noparams";

            public string WithDefaults(int x = 42, string s = "d") => x + ":" + (s ?? "null");

            public string TakeObject(PocoInput input) => input == null ? "null" : input.Text + "#" + input.Number;

            public async Task<string> EchoAsync(string text)
            {
                await Task.Yield();
                return "async:" + text;
            }

            public async Task PlainTaskAsync()
            {
                await Task.Yield();
            }

            public string Throw() => throw new InvalidOperationException("boom-endpoint");

            public async Task<string> ThrowAsync()
            {
                await Task.Yield();
                throw new InvalidOperationException("boom-async-endpoint");
            }

            public bool InjectionCheck() => WebSocketHttpContext != null && WebSocketClient != null;

            public string Greet(string name) => _greet == null ? "no-service" : _greet.Greet(name);
        }

        /// <summary>
        /// Builds a WatchAssemblyContext by hand for the given controller types, mirroring
        /// what WebSocketRouteServiceCollectionExtensions computes via assembly scanning.
        /// </summary>
        public static WatchAssemblyContext BuildContext(params Type[] controllers)
        {
            var endpoints = new List<WebSocketEndPoint>();
            var methods = new ConcurrentDictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
            var maxCtor = new Dictionary<Type, ConstructorParameter>();
            var methodParams = new Dictionary<MethodInfo, ParameterInfo[]>();
            var taskGetters = new Dictionary<MethodInfo, Func<Task, object>>();
            var endpointPolicies = new ConcurrentDictionary<string, Infrastructure.Configures.EndpointReceivePolicy>(StringComparer.OrdinalIgnoreCase);

            foreach (var type in controllers)
            {
                var ctor = type.GetConstructors()
                    .OrderByDescending(c => c.GetParameters().Length)
                    .First();
                maxCtor[type] = new ConstructorParameter
                {
                    ConstructorInfo = ctor,
                    ParameterInfos = ctor.GetParameters()
                };

                string prefix = type.Name.Replace("Controller", string.Empty).ToLowerInvariant();
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (method.IsSpecialName)
                    {
                        continue; // property accessors
                    }

                    string path = prefix + "." + method.Name.ToLowerInvariant();
                    methods[path] = method;
                    var wsAttr = method.GetCustomAttribute<Infrastructure.Attributes.WebSocketAttribute>();
                    endpoints.Add(new WebSocketEndPoint
                    {
                        MethodPath = path,
                        Class = type,
                        MethodInfo = method,
                        Controller = type.Name,
                        Action = method.Name,
                        Methods = new[] { method.Name },
                        IsStream = wsAttr?.Stream ?? false,
                        MaxBytes = wsAttr?.MaxBytes ?? 0
                    });
                    if (wsAttr != null && (wsAttr.Stream || wsAttr.MaxBytes > 0))
                    {
                        endpointPolicies[path] = new Infrastructure.Configures.EndpointReceivePolicy(wsAttr.Stream, wsAttr.MaxBytes);
                    }
                    methodParams[method] = method.GetParameters();

                    if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                    {
                        var resultProperty = method.ReturnType.GetProperty("Result");
                        taskGetters[method] = task => resultProperty.GetValue(task);
                    }
                }
            }

            return new WatchAssemblyContext
            {
                WatchEndPoint = endpoints.ToArray(),
                WatchMethods = methods,
                MaxConstructorParameters = maxCtor,
                MethodParameters = methodParams,
                MethodTaskResultGetters = taskGetters,
                EndpointPolicies = endpointPolicies
            };
        }

        /// <summary>
        /// MvcChannelHandler caches the first IServiceScopeFactory it sees in a private
        /// static field. Tests that swap WebSocketRouteOption.ApplicationServices must reset
        /// it, otherwise a later test can end up using a disposed provider.
        /// </summary>
        public static void ResetCachedScopeFactory()
        {
            var field = typeof(Infrastructure.Handlers.MvcHandler.MvcChannelHandler)
                .GetField("_cachedScopeFactory", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            field.SetValue(null, null);
        }

        public sealed class StubLifetime : IHostApplicationLifetime
        {
            public CancellationToken ApplicationStarted => CancellationToken.None;
            public CancellationToken ApplicationStopping => CancellationToken.None;
            public CancellationToken ApplicationStopped => CancellationToken.None;
            public void StopApplication()
            {
            }
        }
    }
}
