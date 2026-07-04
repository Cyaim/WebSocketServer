using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Infrastructure.Injectors;
using Cyaim.WebSocketServer.Infrastructure.Metrics;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Final coverage sweep for the last reachable non-cluster lines:
    /// WebSocketManager stream-extension failure catches, the MvcForward transient-state
    /// (Connecting) wait branch, async-endpoint fault unwrapping, and the FormatException
    /// parameter-binding branch.
    /// </summary>
    [Collection("StaticState")]
    public class CoreFinishCovTests : IDisposable
    {
        private readonly IServiceProvider _previousServices;
        private readonly object _previousClusterManager;
        private readonly IWebSocketStatisticsRecorder _previousRecorder;

        public CoreFinishCovTests()
        {
            _previousServices = WebSocketRouteOption.ApplicationServices;
            _previousClusterManager = GlobalClusterCenter.ClusterManager;
            _previousRecorder = GlobalClusterCenter.StatisticsRecorder;
            GlobalClusterCenter.ClusterManager = null;
            MvcChannelHandler.Clients.Clear();
        }

        public void Dispose()
        {
            MvcChannelHandler.Clients.Clear();
            GlobalClusterCenter.ClusterManager = (ClusterManager)_previousClusterManager;
            GlobalClusterCenter.StatisticsRecorder = _previousRecorder;
            WebSocketRouteOption.ApplicationServices = _previousServices;
            MvcTestSupport.ResetCachedScopeFactory();
        }

        // ---------------------------------------------------------------
        // WebSocketManager stream-extension local failure catches
        // (Stream.SendAsync single + batch, send throws -> result false)
        // ---------------------------------------------------------------

        [Fact]
        public async Task StreamSendAsync_SingleConnection_SendThrows_ReturnsFalse()
        {
            MvcChannelHandler.Clients["c1"] = new ThrowingWebSocket();
            using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });

            bool ok = await ms.SendAsync("c1", WebSocketMessageType.Binary, chunkSize: 2);

            Assert.False(ok);
        }

        [Fact]
        public async Task StreamSendAsync_SingleInList_SendThrows_ReturnsFalseEntry()
        {
            MvcChannelHandler.Clients["c1"] = new ThrowingWebSocket();
            using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });

            Dictionary<string, bool> results = await ms.SendAsync(new[] { "c1" }, WebSocketMessageType.Binary, chunkSize: 2);

            Assert.False(results["c1"]);
        }

        [Fact]
        public async Task StreamSendAsync_MultipleConnections_SendThrows_AllFalse()
        {
            MvcChannelHandler.Clients["c1"] = new ThrowingWebSocket();
            MvcChannelHandler.Clients["c2"] = new ThrowingWebSocket();
            using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6 });

            Dictionary<string, bool> results = await ms.SendAsync(new[] { "c1", "c2" }, WebSocketMessageType.Binary, chunkSize: 2);

            Assert.False(results["c1"]);
            Assert.False(results["c2"]);
        }

        // ---------------------------------------------------------------
        // MvcForward: transient (Connecting) state -> Task.Delay + continue,
        // then Aborted -> break. Drives the private receive loop directly.
        // ---------------------------------------------------------------

        [Fact]
        public async Task MvcForward_TransientConnectingState_WaitsThenExitsOnAbort()
        {
            var handler = new MvcChannelHandler();
            var context = new DefaultHttpContext();
            context.Connection.Id = "conn-transient";
            var options = new WebSocketRouteOption
            {
                WatchAssemblyContext = MvcTestSupport.BuildContext(typeof(MvcTestSupport.WsTestController))
            };
            var socket = new TransientStateWebSocket();

            var mvcForward = typeof(MvcChannelHandler).GetMethod(
                "MvcForward", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(mvcForward);

            // Set the private logger/option fields the method reads.
            SetPrivate(handler, "logger", NullLogger<WebSocketRouteMiddleware>.Instance);
            SetPrivate(handler, "webSocketOption", options);

            var task = (Task)mvcForward.Invoke(handler, new object[]
            {
                context, socket, options, new MvcTestSupport.StubLifetime()
            });
            // Completes within a few hundred ms (one Task.Delay(300) cycle).
            await task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(socket.StateReads >= 6);
        }

        private static void SetPrivate(object target, string field, object value)
        {
            var f = target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(f);
            f.SetValue(target, value);
        }

        // ---------------------------------------------------------------
        // MvcForward: a statistics recorder that throws mid-receive still lets the message
        // complete via the EndOfMessage flag inside the receive-loop catch block.
        // ---------------------------------------------------------------

        [Fact]
        public async Task MvcForward_StatisticsRecorderThrows_MessageCompletesViaEndOfMessageCatch()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IHostApplicationLifetime, MvcTestSupport.StubLifetime>();
            var provider = services.BuildServiceProvider();
            WebSocketRouteOption.ApplicationServices = provider;
            MvcTestSupport.ResetCachedScopeFactory();

            var recorder = new ThrowingStatisticsRecorder();
            GlobalClusterCenter.StatisticsRecorder = recorder;

            var handler = new MvcChannelHandler();
            var context = new DefaultHttpContext();
            context.Connection.Id = "conn-stats";
            var options = new WebSocketRouteOption
            {
                WatchAssemblyContext = MvcTestSupport.BuildContext(typeof(MvcTestSupport.WsTestController))
            };
            SetPrivate(handler, "logger", NullLogger<WebSocketRouteMiddleware>.Instance);
            SetPrivate(handler, "webSocketOption", options);

            var json = Encoding.UTF8.GetBytes("{\"id\":\"1\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"x\"}}");
            var socket = new ScriptedFrameWebSocket(json);

            var mvcForward = typeof(MvcChannelHandler).GetMethod("MvcForward", BindingFlags.NonPublic | BindingFlags.Instance);
            var task = (Task)mvcForward.Invoke(handler, new object[] { context, socket, options, new MvcTestSupport.StubLifetime() });
            await task.WaitAsync(TimeSpan.FromSeconds(5));

            // RecordBytesReceived threw during the receive loop; the message still completed.
            Assert.True(recorder.ReceivedCalled);
        }

        private sealed class ThrowingStatisticsRecorder : IWebSocketStatisticsRecorder
        {
            public bool ReceivedCalled;
            public void RecordBytesSent(string connectionId, int bytes) { }
            public void RecordBytesReceived(string connectionId, int bytes)
            {
                ReceivedCalled = true;
                throw new InvalidOperationException("stats backend down");
            }
        }

        /// <summary>
        /// Returns one complete text frame (the supplied JSON) on the first ReceiveAsync, then a
        /// Close frame, so MvcForward processes exactly one message and exits.
        /// </summary>
        private sealed class ScriptedFrameWebSocket : WebSocket
        {
            private readonly byte[] _payload;
            private int _calls;
            private bool _closed;

            public ScriptedFrameWebSocket(byte[] payload) => _payload = payload;

            public override WebSocketState State => _closed ? WebSocketState.Closed : WebSocketState.Open;
            public override WebSocketCloseStatus? CloseStatus => _closed ? WebSocketCloseStatus.NormalClosure : (WebSocketCloseStatus?)null;
            public override string CloseStatusDescription => null;
            public override string SubProtocol => null;
            public override void Abort() => _closed = true;
            public override Task CloseAsync(WebSocketCloseStatus s, string d, CancellationToken c) { _closed = true; return Task.CompletedTask; }
            public override Task CloseOutputAsync(WebSocketCloseStatus s, string d, CancellationToken c) { _closed = true; return Task.CompletedTask; }
            public override void Dispose() { }
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            {
                if (_calls++ == 0)
                {
                    Array.Copy(_payload, 0, buffer.Array, buffer.Offset, _payload.Length);
                    return Task.FromResult(new WebSocketReceiveResult(_payload.Length, WebSocketMessageType.Text, endOfMessage: true));
                }
                _closed = true;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true,
                    WebSocketCloseStatus.NormalClosure, "bye"));
            }
            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }

        // ---------------------------------------------------------------
        // MvcDistributeAsync: async endpoint faults -> throw task.Exception
        // (AggregateException) -> caught -> unwrapped.
        // ---------------------------------------------------------------

        [Fact]
        public async Task MvcDistribute_AsyncEndpointThrows_ReturnsStatus1()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IHostApplicationLifetime, MvcTestSupport.StubLifetime>();
            var provider = services.BuildServiceProvider();
            WebSocketRouteOption.ApplicationServices = provider;
            MvcTestSupport.ResetCachedScopeFactory();

            var options = new WebSocketRouteOption
            {
                WatchAssemblyContext = MvcTestSupport.BuildContext(typeof(MvcTestSupport.WsTestController))
            };

            var response = await MvcChannelHandler.MvcDistributeAsync(
                options, new DefaultHttpContext(), new TestWebSocket(),
                new MvcRequestScheme { Id = "r", Target = "wstest.throwasync" },
                null, NullLogger<WebSocketRouteMiddleware>.Instance, new MvcTestSupport.StubLifetime());

            Assert.Equal(1, response.Status);
        }

        // ---------------------------------------------------------------
        // MvcDistributeAsync: FormatException during parameter binding
        // (bad DateTime string -> DataTypes.ConvertTo throws FormatException).
        // ---------------------------------------------------------------

        [Fact]
        public async Task MvcDistribute_BadDateTimeParam_ConvertFailurePropagatesToStatus1()
        {
            // DataTypes.ConvertTo(Type, JsonNode) uses JsonNode.GetValue<T>, which throws
            // InvalidOperationException (NOT FormatException) on a type mismatch. That exception
            // is not caught by the binding loop's `catch (FormatException)` (proven-dead defensive
            // arm) and propagates to the outer handler, producing Status 1.
            var services = new ServiceCollection();
            services.AddSingleton<IHostApplicationLifetime, MvcTestSupport.StubLifetime>();
            var provider = services.BuildServiceProvider();
            WebSocketRouteOption.ApplicationServices = provider;
            MvcTestSupport.ResetCachedScopeFactory();

            var options = new WebSocketRouteOption
            {
                WatchAssemblyContext = MvcTestSupport.BuildContext(typeof(FmtController))
            };

            var body = System.Text.Json.Nodes.JsonNode.Parse("{\"a\":\"not-a-number\",\"b\":2}").AsObject();
            var response = await MvcChannelHandler.MvcDistributeAsync(
                options, new DefaultHttpContext(), new TestWebSocket(),
                new MvcRequestScheme { Id = "r", Target = "fmt.addints" },
                body, NullLogger<WebSocketRouteMiddleware>.Instance, new MvcTestSupport.StubLifetime());

            Assert.Equal(1, response.Status);
        }

        [Fact]
        public async Task MvcDistribute_InvokerThrowsRawAggregateException_IsUnwrapped()
        {
            // Seed a custom IMethodInvoker that throws a raw AggregateException (not wrapped in
            // TargetInvocationException), so the outer handler's `ex is AggregateException` unwrap
            // branch executes and the inner exception surfaces.
            var services = new ServiceCollection();
            services.AddSingleton<IHostApplicationLifetime, MvcTestSupport.StubLifetime>();
            var provider = services.BuildServiceProvider();
            WebSocketRouteOption.ApplicationServices = provider;
            MvcTestSupport.ResetCachedScopeFactory();

            var ctx = MvcTestSupport.BuildContext(typeof(FmtController));
            var method = ctx.WatchMethods["fmt.noop"];

            var factory = new MethodInvokerFactory();
            var cacheField = typeof(MethodInvokerFactory).GetField("_invokerCache", BindingFlags.NonPublic | BindingFlags.Instance);
            var cache = (System.Collections.Concurrent.ConcurrentDictionary<MethodInfo, IMethodInvoker>)cacheField.GetValue(factory);
            cache[method] = new AggregateThrowingInvoker();

            var options = new WebSocketRouteOption
            {
                WatchAssemblyContext = ctx
            };
            // MethodInvokerFactory is internal; set it via reflection.
            typeof(WebSocketRouteOption).GetProperty("MethodInvokerFactory", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(options, factory);

            var response = await MvcChannelHandler.MvcDistributeAsync(
                options, new DefaultHttpContext(), new TestWebSocket(),
                new MvcRequestScheme { Id = "r", Target = "fmt.noop" },
                null, NullLogger<WebSocketRouteMiddleware>.Instance, new MvcTestSupport.StubLifetime());

            Assert.Equal(1, response.Status);
            Assert.Contains("inner-boom", response.Msg);
        }

        private sealed class AggregateThrowingInvoker : Cyaim.WebSocketServer.Infrastructure.Injectors.IMethodInvoker
        {
            public object Invoke(object instance, object[] args)
                => throw new AggregateException(new InvalidOperationException("inner-boom"));
        }

        public class FmtController
        {
            [WebSocket]
            public int AddInts(int a, int b) => a + b;

            [WebSocket]
            public string Noop() => "noop";
        }

        /// <summary>
        /// Reports Connecting for its first several State reads (forcing the transient-wait
        /// branch), then Aborted so the receive loop exits. Nothing else is invoked.
        /// </summary>
        private sealed class TransientStateWebSocket : WebSocket
        {
            public int StateReads;
            public override WebSocketState State => (++StateReads <= 5) ? WebSocketState.Connecting : WebSocketState.Aborted;
            public override WebSocketCloseStatus? CloseStatus => null;
            public override string CloseStatusDescription => null;
            public override string SubProtocol => null;
            public override void Abort() { }
            public override Task CloseAsync(WebSocketCloseStatus s, string d, CancellationToken c) => Task.CompletedTask;
            public override Task CloseOutputAsync(WebSocketCloseStatus s, string d, CancellationToken c) => Task.CompletedTask;
            public override void Dispose() { }
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
                => throw new InvalidOperationException("receive should not be called on the transient path");
            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }
    }
}
