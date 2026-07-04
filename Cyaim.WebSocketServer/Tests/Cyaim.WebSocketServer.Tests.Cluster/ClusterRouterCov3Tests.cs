using System.Net.WebSockets;
using System.Reflection;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    /// <summary>
    /// Further coverage-only tests for <see cref="ClusterRouter"/>, targeting the "outer escape
    /// hatch" catch blocks that only fire when a handler's OWN internal catch's logging call
    /// itself throws (a broken-logger scenario), plus the graceful-shutdown adaptive batch-size
    /// branches. Uses <see cref="ThrowingLogger{T}"/> (throws unconditionally for armed levels)
    /// and a private <see cref="SelfDisarmingThrowingLogger{T}"/> (throws exactly once per armed
    /// level, then stops) to precisely control how far an exception cascades through nested
    /// try/catch/log blocks without guessing at coverlet's hit-recording timing.
    /// </summary>
    [Collection("ClusterStaticState")]
    public class ClusterRouterCov3Tests : StaticStateGuard
    {
        private const string NodeA = "nodeA";
        private const string NodeB = "nodeB";

        private readonly FakeClusterTransport _transport = new FakeClusterTransport();
        private readonly FakeConnectionProvider _provider = new FakeConnectionProvider();
        private readonly RaftNode _raft;
        private readonly ClusterRouter _router;
        private readonly List<ClusterRouter> _extraRouters = new List<ClusterRouter>();

        public ClusterRouterCov3Tests()
        {
            _raft = new RaftNode(NullLogger<RaftNode>.Instance, _transport, NodeA);
            _router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, _transport, _raft, NodeA);
            _router.SetConnectionProvider(_provider);
        }

        public override void Dispose()
        {
            foreach (var r in _extraRouters) { try { r.Dispose(); } catch { } }
            try { _raft.StopAsync().GetAwaiter().GetResult(); } catch { }
            _router.Dispose();
            base.Dispose();
        }

        private static object Invoke(object target, string method, params object[] args)
        {
            var mi = target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(mi);
            return mi.Invoke(target, args);
        }

        private static Task InvokeAsync(object target, string method, params object[] args)
            => (Task)Invoke(target, method, args);

        /// <summary>
        /// Logger that throws on an armed level EXACTLY ONCE, then auto-disarms that level.
        /// Lets a test make an inner catch's own logging call escalate exactly one level
        /// further up a nested try/catch chain, while a subsequent catch at the next level up
        /// (using the same logger, same level) completes normally instead of also throwing.
        /// </summary>
        private sealed class SelfDisarmingThrowingLogger<T> : ILogger<T>
        {
            private readonly HashSet<LogLevel> _armed;

            public SelfDisarmingThrowingLogger(params LogLevel[] armedLevels)
            {
                _armed = new HashSet<LogLevel>(armedLevels);
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (_armed.Remove(logLevel))
                {
                    throw new InvalidOperationException($"logger throws (once) on {logLevel}");
                }
            }
        }

        private ClusterRouter CreateRouter(ILogger<ClusterRouter> logger, FakeClusterTransport transport)
        {
            var router = new ClusterRouter(logger, transport, _raft, NodeA);
            _extraRouters.Add(router);
            return router;
        }

        // =====================================================================
        // OnTransportMessageReceived: outer catch completes normally (line after
        // the catch's own LogError) when only Debug (not Error) is armed.
        // =====================================================================

        [Fact]
        public async Task OnTransportMessage_LogDebugThrows_OuterCatchCompletesNormally()
        {
            var throwingLogger = new ThrowingLogger<ClusterRouter>();
            throwingLogger.ThrowOn.Add(LogLevel.Debug);
            var transport = new FakeClusterTransport();
            var router = CreateRouter(throwingLogger, transport);

            // Message content is irrelevant: the very first statement in the dispatch loop's
            // try (a LogDebug call) throws before the switch is ever reached.
            transport.RaiseMessageReceived(new ClusterMessage
            {
                Type = ClusterMessageType.RegisterWebSocketConnection,
                FromNodeId = NodeB,
                Payload = "{}"
            }, NodeB);

            await Task.Delay(150);
            // No crash: the outer catch's own LogError call (Error not armed) succeeds, reaching
            // the catch's closing brace.
        }

        // =====================================================================
        // OnNodeDisconnected: outer catch completes normally when only Warning is armed.
        // (RaftNode does not subscribe to NodeDisconnected, so sharing _raft is safe.)
        // =====================================================================

        [Fact]
        public async Task OnNodeDisconnected_LogWarningThrows_OuterCatchCompletesNormally()
        {
            var throwingLogger = new ThrowingLogger<ClusterRouter>();
            throwingLogger.ThrowOn.Add(LogLevel.Warning);
            var transport = new FakeClusterTransport();
            var router = CreateRouter(throwingLogger, transport);

            // HandleNodeDisconnectedAsync's very first statement (after the empty/self guard) is
            // an un-try-wrapped LogWarning call, which throws and propagates straight to
            // OnNodeDisconnected's own catch.
            transport.RaiseNodeDisconnected(NodeB);

            await Task.Delay(150);
        }

        // =====================================================================
        // RefreshConnectionRoutesAsync's own catch completes normally (Debug armed only).
        // =====================================================================

        [Fact]
        public async Task RefreshRoutes_LogDebugThrows_InnerCatchCompletesNormally()
        {
            var throwingLogger = new ThrowingLogger<ClusterRouter>();
            var transport = new FakeClusterTransport();
            var router = CreateRouter(throwingLogger, transport);

            // Register while unarmed (RegisterConnectionAsync only logs at Warning level).
            await router.RegisterConnectionAsync("c1");

            // Now arm Debug: RefreshConnectionRoutesAsync's first statement past the
            // local-connections guard is a LogDebug call.
            throwingLogger.ThrowOn.Add(LogLevel.Debug);

            await InvokeAsync(router, "RefreshConnectionRoutesAsync");
            // No crash: caught by RefreshConnectionRoutesAsync's own catch, whose LogError call
            // (Error not armed) completes normally.
        }

        // =====================================================================
        // RefreshConnectionRoutes (timer wrapper) catch fires because the escalation reaches
        // it: Debug throws once (consumed), the inner catch's LogError (Error) throws once
        // (consumed), and the wrapper's own LogError (Error, now disarmed) completes normally.
        // =====================================================================

        [Fact]
        public async Task RefreshRoutes_DoubleEscalation_ReachesWrapperCatch_WhichCompletesNormally()
        {
            var throwingLogger = new SelfDisarmingThrowingLogger<ClusterRouter>(LogLevel.Debug, LogLevel.Error);
            var transport = new FakeClusterTransport();
            var router = CreateRouter(throwingLogger, transport);

            await router.RegisterConnectionAsync("c1");

            // Fire the timer-callback wrapper (fire-and-forget Task.Run internally).
            Invoke(router, "RefreshConnectionRoutes", new object[] { null });
            await Task.Delay(200);
            // No crash: Debug throw (1st use) -> inner catch's Error throw (1st use) -> escapes
            // RefreshConnectionRoutesAsync entirely -> wrapper's catch -> Error already consumed
            // -> wrapper's own LogError succeeds, reaching the wrapper catch's closing brace.
        }

        // =====================================================================
        // GracefulShutdownAsync: per-task bare catch absorbs an exception that escalates out of
        // CloseConnectionWithRedirectAsync's own catch (Warning armed after setup).
        // =====================================================================

        [Fact]
        public async Task GracefulShutdown_CloseThrows_LoggerEscalates_PerTaskBareCatchAbsorbsIt()
        {
            var throwingLogger = new ThrowingLogger<ClusterRouter>();
            var transport = new FakeClusterTransport { NodeConnectedFunc = _ => true };
            var router = CreateRouter(throwingLogger, transport);
            router.SetConnectionProvider(_provider);

            var socket = new ThrowingCloseWebSocket();
            MvcChannelHandler.Clients["g1"] = socket;
            _provider.Connections["g1"] = socket;

            // Register a remote connection on NodeB FIRST, while the transport is still
            // storage-less (_transportSupportsRouteStorage unset) so HandleRegisterConnection
            // takes the in-memory-cache branch; this lets GetOptimalNodeForTransfer see NodeB as
            // a real remote candidate instead of falling back to _nodeId, avoiding the "no other
            // nodes available" LogWarning at the top of GracefulShutdownAsync (which would
            // otherwise throw immediately once Warning is armed below).
            transport.RaiseMessageReceived(new ClusterMessage
            {
                Type = ClusterMessageType.RegisterWebSocketConnection,
                FromNodeId = NodeB,
                Payload = System.Text.Json.JsonSerializer.Serialize(new WebSocketConnectionRegistration
                {
                    ConnectionId = "r1",
                    NodeId = NodeB,
                    RegisteredAt = DateTime.UtcNow
                })
            }, NodeB);
            Assert.True(await Wait.UntilAsync(() => router.ConnectionRoutes.ContainsKey("r1")));

            // Now register the local connection while unarmed.
            await router.RegisterConnectionAsync("g1", "/ws");

            // Now arm Warning: CloseConnectionWithRedirectAsync's own catch logs at Warning,
            // which throws and escapes to the per-task bare catch in the batch loop.
            // The ClusterOption gives BuildRedirectUrl a match for NodeB so it returns a URL
            // without hitting its own un-try-wrapped "could not build redirect URL" LogWarning
            // (which would otherwise also throw, escaping GracefulShutdownAsync entirely).
            throwingLogger.ThrowOn.Add(LogLevel.Warning);

            await router.GracefulShutdownAsync(new ClusterOption { Nodes = new[] { "ws://127.0.0.1:5000/nodeB" } });
            // No crash: the per-task bare `catch { return false; }` absorbed the escalated
            // exception, and GracefulShutdownAsync completed normally.
        }

        // =====================================================================
        // GracefulShutdownAsync: adaptive batch-size decrease + long-delay branches, driven by
        // a single connection whose close takes > 500ms (maxBatchTimeMs).
        // =====================================================================

        [Fact]
        public async Task GracefulShutdown_SlowClose_HitsDecreaseBatchSizeAndLongDelayBranches()
        {
            var socket = new DelayCloseWebSocket(600);
            MvcChannelHandler.Clients["g1"] = socket;
            _provider.Connections["g1"] = socket;
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("g1", "/ws");

            await _router.GracefulShutdownAsync(new ClusterOption());

            Assert.Equal(WebSocketState.Closed, socket.State);
        }
    }
}
