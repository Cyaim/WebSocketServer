using System.Collections.Generic;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// WebSocket whose ReceiveAsync replays a scripted sequence of frames (or throws), so the
    /// private MvcForward receive loop can be driven through its error/edge branches deterministically.
    /// </summary>
    internal sealed class ScriptedWebSocket : WebSocket
    {
        private sealed class Recv
        {
            public byte[] Payload;
            public WebSocketMessageType Type;
            public bool Eom;
            public WebSocketCloseStatus? CloseStatus;
            public string CloseDesc;
            public Exception Throw;
        }

        private readonly Queue<Recv> _actions = new Queue<Recv>();
        public WebSocketState StateValue = WebSocketState.Open;
        public bool ThrowOnClose;
        public readonly List<byte[]> Sent = new List<byte[]>();

        public ScriptedWebSocket Text(string json, bool eom = true, string closeDesc = null)
        {
            _actions.Enqueue(new Recv { Payload = Encoding.UTF8.GetBytes(json), Type = WebSocketMessageType.Text, Eom = eom, CloseDesc = closeDesc });
            return this;
        }

        public ScriptedWebSocket ZeroFrame()
        {
            _actions.Enqueue(new Recv { Payload = Array.Empty<byte>(), Type = WebSocketMessageType.Text, Eom = true });
            return this;
        }

        public ScriptedWebSocket CloseFrame(string desc = "bye")
        {
            _actions.Enqueue(new Recv { Payload = Array.Empty<byte>(), Type = WebSocketMessageType.Close, Eom = true, CloseStatus = WebSocketCloseStatus.NormalClosure, CloseDesc = desc });
            return this;
        }

        public ScriptedWebSocket ThrowOnce()
        {
            _actions.Enqueue(new Recv { Throw = new System.IO.IOException("recv-fails") });
            return this;
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override WebSocketState State => StateValue;
        public override string SubProtocol => null;
        public override void Abort() => StateValue = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            if (ThrowOnClose) throw new System.IO.IOException("close-fails");
            StateValue = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override void Dispose() { }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_actions.Count == 0)
            {
                // Nothing left to replay: end the loop with a Close frame.
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "end"));
            }

            var a = _actions.Dequeue();
            if (a.Throw != null)
            {
                throw a.Throw;
            }
            Array.Copy(a.Payload, 0, buffer.Array, buffer.Offset, a.Payload.Length);
            return Task.FromResult(new WebSocketReceiveResult(a.Payload.Length, a.Type, a.Eom, a.CloseStatus, a.CloseDesc));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Sent.Add(buffer.ToArray());
            return Task.CompletedTask;
        }
    }

    /// <summary>Lifetime whose ApplicationStopping token is already cancelled.</summary>
    internal sealed class CancelledLifetime : IHostApplicationLifetime
    {
        private readonly CancellationToken _stopping;
        public CancelledLifetime()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            _stopping = cts.Token;
        }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    [Collection("StaticState")]
    public class MvcForwardScriptedCovTests : IDisposable
    {
        private readonly IServiceProvider _previousServices;
        private readonly ServiceProvider _provider;
        private readonly MvcTestSupport.StubLifetime _lifetime = new MvcTestSupport.StubLifetime();

        public MvcForwardScriptedCovTests()
        {
            _previousServices = WebSocketRouteOption.ApplicationServices;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHostApplicationLifetime>(_lifetime);
            services.AddSingleton<MvcTestSupport.IGreetService, MvcTestSupport.GreetService>();
            _provider = services.BuildServiceProvider();
            WebSocketRouteOption.ApplicationServices = _provider;
            MvcTestSupport.ResetCachedScopeFactory();
        }

        public void Dispose()
        {
            WebSocketRouteOption.ApplicationServices = _previousServices;
            MvcTestSupport.ResetCachedScopeFactory();
            _provider.Dispose();
        }

        private static WebSocketRouteOption Options(Action<WebSocketRouteOption> configure = null)
        {
            var o = new WebSocketRouteOption
            {
                WatchAssemblyContext = MvcTestSupport.BuildContext(typeof(MvcTestSupport.WsTestController))
            };
            configure?.Invoke(o);
            return o;
        }

        private MvcChannelHandler NewHandler(WebSocketRouteOption options, SemaphoreSlim parallelSlim = null)
        {
            var handler = new MvcChannelHandler();
            typeof(MvcChannelHandler).GetField("logger", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(handler, NullLogger<WebSocketRouteMiddleware>.Instance);
            typeof(MvcChannelHandler).GetField("webSocketOption", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(handler, options);
            if (parallelSlim != null)
            {
                handler.ParallelForwardLimitSlim = parallelSlim;
            }
            return handler;
        }

        private static readonly MethodInfo MvcForwardMethod =
            typeof(MvcChannelHandler).GetMethod("MvcForward", BindingFlags.NonPublic | BindingFlags.Instance);

        private Task RunForward(MvcChannelHandler handler, HttpContext ctx, ScriptedWebSocket ws, WebSocketRouteOption options, IHostApplicationLifetime lifetime)
            => (Task)MvcForwardMethod.Invoke(handler, new object[] { ctx, ws, options, lifetime });

        private static DefaultHttpContext Ctx()
        {
            var c = new DefaultHttpContext();
            c.Connection.Id = Guid.NewGuid().ToString("N");
            return c;
        }

        private const string Echo = "{\"id\":\"1\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"hi\"}}";

        [Fact]
        public async Task Loop_StateAlreadyClosed_BreaksImmediately()
        {
            var options = Options();
            var handler = NewHandler(options);
            var ws = new ScriptedWebSocket { StateValue = WebSocketState.Aborted };
            await RunForward(handler, Ctx(), ws, options, _lifetime);
        }

        [Fact]
        public async Task Loop_CloseFrame_CloseResponseThrows_IsCaught()
        {
            var options = Options();
            var handler = NewHandler(options);
            var ws = new ScriptedWebSocket { ThrowOnClose = true };
            ws.CloseFrame("desc");
            await RunForward(handler, Ctx(), ws, options, _lifetime);
        }

        [Fact]
        public async Task Loop_ZeroCountFrame_CompletesMessage_ThenEmptyParseCaught()
        {
            var options = Options();
            var handler = NewHandler(options);
            var ws = new ScriptedWebSocket();
            ws.ZeroFrame();
            await RunForward(handler, Ctx(), ws, options, _lifetime);
        }

        [Fact]
        public async Task Loop_ReceiveThrows_NullResult_ContinuesToNextIteration()
        {
            var options = Options();
            var handler = NewHandler(options);
            var ws = new ScriptedWebSocket();
            ws.ThrowOnce();
            await RunForward(handler, Ctx(), ws, options, _lifetime);
        }

        [Fact]
        public async Task Loop_EndpointParallelLimit_ExtractsEndpoint_AcquiresSlim()
        {
            var limits = new System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>();
            limits["wstest.echo"] = new SemaphoreSlim(1, 1);
            var options = Options(o => o.MaxEndPointParallelForwardLimit = limits);
            var handler = NewHandler(options);
            var ws = new ScriptedWebSocket();
            ws.Text(Echo);
            await RunForward(handler, Ctx(), ws, options, _lifetime);
            Assert.NotEmpty(ws.Sent);
        }

        [Fact]
        public async Task Loop_AsyncForward_TaskFaults_FaultedContinuationRuns()
        {
            var options = Options(o =>
            {
                o.EnableForwardTaskSyncProcessingMode = false;
                o.ExceptionEvent += (ex, req, resp, ctx, opt, channel, logger) => throw new InvalidOperationException("hook-throws");
            });
            var handler = NewHandler(options);
            var ws = new ScriptedWebSocket();
            ws.Text("{\"id\":\"1\",\"target\":\"wstest.throw\"}");
            await RunForward(handler, Ctx(), ws, options, _lifetime);
            // Give the OnlyOnFaulted continuation a chance to run.
            await Task.Delay(50);
        }

        [Fact]
        public async Task Loop_ResultCarriesCloseDescription_CapturedInFinally()
        {
            var options = Options();
            var handler = NewHandler(options);
            var ws = new ScriptedWebSocket();
            ws.Text(Echo, eom: true, closeDesc: "trailing-desc");
            await RunForward(handler, Ctx(), ws, options, _lifetime);
        }

        [Fact]
        public async Task Loop_AppStopping_PostLoopCloseRuns()
        {
            var options = Options();
            var handler = NewHandler(options);
            var ws = new ScriptedWebSocket();
            ws.Text(Echo);
            await RunForward(handler, Ctx(), ws, options, new CancelledLifetime());
        }

        [Fact]
        public async Task Loop_AppStopping_PostLoopCloseThrows_IsCaught()
        {
            var options = Options();
            var handler = NewHandler(options);
            var ws = new ScriptedWebSocket { ThrowOnClose = true };
            ws.Text(Echo);
            await RunForward(handler, Ctx(), ws, options, new CancelledLifetime());
        }

        [Fact]
        public async Task Loop_DisposedParallelSlim_ReleaseInFinallyThrows_OuterCatch()
        {
            var slim = new SemaphoreSlim(1, 1);
            slim.Dispose();
            var options = Options();
            var handler = NewHandler(options, slim);
            var ws = new ScriptedWebSocket();
            ws.Text(Echo);
            await RunForward(handler, Ctx(), ws, options, _lifetime);
        }
    }
}
