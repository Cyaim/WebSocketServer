using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Cluster.Transports;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    /// <summary>
    /// Coverage-only tests for <see cref="ClusterRouter"/> that drive private helper methods
    /// directly via reflection: QueryConnectionAsync (storage/broadcast resolution branches),
    /// ForwardStreamToNodeAsync failure, HandleNodeDisconnectedAsync no-connections branch,
    /// BuildRedirectUrl (WebSocketClusterTransport + config + GlobalClusterCenter fallbacks),
    /// GetNodeInfoFromTransport, and CloseConnectionWithRedirectAsync URL-rewrite/truncate/catch.
    /// </summary>
    [Collection("ClusterStaticState")]
    public class ClusterRouterCov2Tests : StaticStateGuard
    {
        private const string NodeA = "nodeA";
        private const string NodeB = "nodeB";

        private readonly FakeClusterTransport _transport = new FakeClusterTransport();
        private readonly FakeConnectionProvider _provider = new FakeConnectionProvider();
        private readonly RaftNode _raft;
        private readonly ClusterRouter _router;
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        public ClusterRouterCov2Tests()
        {
            _raft = new RaftNode(NullLogger<RaftNode>.Instance, _transport, NodeA);
            _router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, _transport, _raft, NodeA);
            _router.SetConnectionProvider(_provider);
        }

        public override void Dispose()
        {
            foreach (var r in _routerDisposables) { try { r.Dispose(); } catch { } }
            foreach (var d in _disposables) { try { d.Dispose(); } catch { } }
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

        private static Task<T> InvokeAsync<T>(object target, string method, params object[] args)
            => (Task<T>)Invoke(target, method, args);

        private static Task InvokeAsync(object target, string method, params object[] args)
            => (Task)Invoke(target, method, args);

        private static void SetField(object target, string field, object value)
        {
            var fi = target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(fi);
            fi.SetValue(target, value);
        }

        private static ClusterMessage RegistrationMessage(string connectionId, string nodeId, string toNodeId = null)
            => new ClusterMessage
            {
                Type = ClusterMessageType.RegisterWebSocketConnection,
                FromNodeId = nodeId,
                ToNodeId = toNodeId,
                Payload = JsonSerializer.Serialize(new WebSocketConnectionRegistration
                {
                    ConnectionId = connectionId,
                    NodeId = nodeId,
                    RegisteredAt = DateTime.UtcNow
                })
            };

        // =====================================================================
        // QueryConnectionAsync branches (private)
        // =====================================================================

        [Fact]
        public async Task QueryConnection_StorageResolvesLocal_CachesRoute()
        {
            _transport.SupportsRouteStorage = true;
            _transport.StoredRoutes["x"] = NodeA; // local per storage

            var result = await InvokeAsync<string>(_router, "QueryConnectionAsync", "x");

            Assert.Equal(NodeA, result);
            Assert.Equal(NodeA, _router.ConnectionRoutes["x"]);
        }

        [Fact]
        public async Task QueryConnection_StorageResolvesRemote_NotCached()
        {
            _transport.SupportsRouteStorage = true;
            _transport.StoredRoutes["x"] = NodeB;

            var result = await InvokeAsync<string>(_router, "QueryConnectionAsync", "x");

            Assert.Equal(NodeB, result);
            Assert.False(_router.ConnectionRoutes.ContainsKey("x"));
        }

        [Fact]
        public async Task QueryConnection_BroadcastThrows_ReturnsNull()
        {
            _transport.OnBroadcastAsync = _ => throw new InvalidOperationException("broadcast down");

            var result = await InvokeAsync<string>(_router, "QueryConnectionAsync", "x");

            Assert.Null(result);
        }

        [Fact]
        public async Task QueryConnection_BroadcastAnsweredLocal_CachesRoute()
        {
            _transport.OnBroadcastAsync = message =>
            {
                if (message.Type != ClusterMessageType.QueryWebSocketConnection) return;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(30);
                    _transport.RaiseMessageReceived(RegistrationMessage("x", NodeA, toNodeId: NodeA), NodeA);
                });
            };

            var result = await InvokeAsync<string>(_router, "QueryConnectionAsync", "x");

            Assert.Equal(NodeA, result);
            Assert.Equal(NodeA, _router.ConnectionRoutes["x"]);
        }

        [Fact]
        public async Task QueryConnection_BroadcastAnsweredRemote_WithStorageFlag_NotCached()
        {
            // _transportSupportsRouteStorage == true takes the "supports storage, don't cache" branch.
            SetField(_router, "_transportSupportsRouteStorage", true);
            _transport.SupportsRouteStorage = true; // but no stored route for "x" -> proceeds to broadcast
            _transport.OnBroadcastAsync = message =>
            {
                if (message.Type != ClusterMessageType.QueryWebSocketConnection) return;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(30);
                    _transport.RaiseMessageReceived(RegistrationMessage("x", NodeB, toNodeId: NodeA), NodeB);
                });
            };

            var result = await InvokeAsync<string>(_router, "QueryConnectionAsync", "x");

            Assert.Equal(NodeB, result);
            Assert.False(_router.ConnectionRoutes.ContainsKey("x"));
        }

        // =====================================================================
        // ForwardStreamToNodeAsync failure (private)
        // =====================================================================

        [Fact]
        public async Task ForwardStreamToNode_SendThrows_ReturnsFalse()
        {
            _transport.OnSendAsync = (_, __) => throw new InvalidOperationException("send down");

            using var stream = new MemoryStream(new byte[200]);
            var result = await InvokeAsync<bool>(_router, "ForwardStreamToNodeAsync", NodeB, "c1", stream, (int)WebSocketMessageType.Binary, 64, CancellationToken.None);

            Assert.False(result);
        }

        // =====================================================================
        // HandleNodeDisconnectedAsync: no connections branch
        // =====================================================================

        [Fact]
        public async Task NodeDisconnected_NoConnectionsForNode_HitsNoConnectionsBranch()
        {
            // NodeB has no routes at all -> connectionsToTransfer empty -> "No connections found".
            await InvokeAsync(_router, "HandleNodeDisconnectedAsync", NodeB);
        }

        // =====================================================================
        // BuildRedirectUrl (private) - all fallback layers
        // =====================================================================

        private readonly List<ClusterRouter> _routerDisposables = new List<ClusterRouter>();

        private ClusterRouter CreateWsRouter(out WebSocketClusterTransport ws)
        {
            ws = new WebSocketClusterTransport(NullLogger<WebSocketClusterTransport>.Instance, NodeA);
            _disposables.Add(ws);
            var raft = new RaftNode(NullLogger<RaftNode>.Instance, ws, NodeA);
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, ws, raft, NodeA);
            _routerDisposables.Add(router);
            return router;
        }

        [Fact]
        public void BuildRedirectUrl_WebSocketTransport_UsesNodeInfo()
        {
            var router = CreateWsRouter(out var ws);
            ws.RegisterNode(new ClusterNode { NodeId = NodeB, Address = "10.0.0.9", Port = 6001, Protocol = "ws" });

            var url = (string)Invoke(router, "BuildRedirectUrl", NodeB, new ClusterOption());

            Assert.Equal("ws://10.0.0.9:6001/ws", url);
        }

        [Fact]
        public void BuildRedirectUrl_ConfigFallback_ParsesFromClusterOption()
        {
            var url = (string)Invoke(_router, "BuildRedirectUrl", NodeB, new ClusterOption { Nodes = new[] { "ws://127.0.0.1:7000/nodeB" } });

            Assert.Equal("ws://127.0.0.1:7000/ws", url);
        }

        [Fact]
        public void BuildRedirectUrl_GlobalCenterFallback_ParsesFromGlobalContext()
        {
            GlobalClusterCenter.ClusterContext = new ClusterOption { Nodes = new[] { "ws://127.0.0.1:7100/nodeB" } };

            // ClusterOption without matching node -> falls through to GlobalClusterCenter.
            var url = (string)Invoke(_router, "BuildRedirectUrl", NodeB, new ClusterOption { Nodes = new[] { "ws://127.0.0.1:1/other" } });

            Assert.Equal("ws://127.0.0.1:7100/ws", url);
        }

        [Fact]
        public void BuildRedirectUrl_GlobalCenterFallback_NonMatchingEntry_FallsThroughToNull()
        {
            // A GlobalClusterCenter node that parses fine but doesn't match the target exercises
            // the loop's normal (non-throwing, non-matching) fall-through completion, distinct
            // from the early-return-on-match path covered by the test above.
            GlobalClusterCenter.ClusterContext = new ClusterOption { Nodes = new[] { "ws://1.2.3.4:9999/other-node" } };

            var url = (string)Invoke(_router, "BuildRedirectUrl", NodeB, new ClusterOption { Nodes = new[] { "ws://127.0.0.1:1/also-other" } });

            Assert.Null(url);
        }

        [Fact]
        public void GetNodeInfoFromTransport_NullTransport_ReflectionThrows_IsCaught()
        {
            // Passing a null WebSocketClusterTransport makes the reflected _nodes field's
            // GetValue(transport) call throw TargetException ("Non-static field requires a
            // target"), exercising GetNodeInfoFromTransport's own catch block.
            var node = Invoke(_router, "GetNodeInfoFromTransport", null, "someNode");

            Assert.Null(node);
        }

        [Fact]
        public void BuildRedirectUrl_NoMatchAnywhere_ReturnsNull()
        {
            GlobalClusterCenter.ClusterContext = null;

            var url = (string)Invoke(_router, "BuildRedirectUrl", NodeB, new ClusterOption { Nodes = new[] { "ws://127.0.0.1:1/other" } });

            Assert.Null(url);
        }

        [Fact]
        public void GetNodeInfoFromTransport_UnknownNode_ReturnsNull()
        {
            var router = CreateWsRouter(out var ws);

            var node = Invoke(router, "GetNodeInfoFromTransport", ws, "ghost");

            Assert.Null(node);
        }

        // =====================================================================
        // CloseConnectionWithRedirectAsync (private)
        // =====================================================================

        [Fact]
        public async Task CloseConnectionWithRedirect_NoSocket_ReturnsImmediately()
        {
            await InvokeAsync(_router, "CloseConnectionWithRedirectAsync", "ghost", (string)null);
        }

        [Fact]
        public async Task CloseConnectionWithRedirect_RedirectUrlEmptyPath_RewritesWithEndpoint()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("c1", "/chat");

            // redirectUrl with no path -> rewritten to include the connection's endpoint.
            await InvokeAsync(_router, "CloseConnectionWithRedirectAsync", "c1", "ws://10.0.0.1:9000");

            Assert.Equal(WebSocketState.Closed, socket.State);
            Assert.Contains("/chat", socket.LastCloseDescription);
        }

        [Fact]
        public async Task CloseConnectionWithRedirect_RedirectUrlDifferentPath_RewritesWithEndpoint()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("c1", "/chat");

            // redirectUrl path ("/other") != endpoint ("/chat") -> rewritten to endpoint.
            await InvokeAsync(_router, "CloseConnectionWithRedirectAsync", "c1", "ws://10.0.0.1:9000/other");

            Assert.Equal(WebSocketState.Closed, socket.State);
            Assert.Contains("/chat", socket.LastCloseDescription);
        }

        [Fact]
        public async Task CloseConnectionWithRedirect_VeryLongRedirect_TruncatesCloseReason()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            _transport.SupportsRouteStorage = true;
            var longEndpoint = "/" + new string('p', 200);
            await _router.RegisterConnectionAsync("c1", longEndpoint);

            await InvokeAsync(_router, "CloseConnectionWithRedirectAsync", "c1", "ws://10.0.0.1:9000/other");

            Assert.Equal(WebSocketState.Closed, socket.State);
            // Close reason is truncated to <= 123 bytes.
            Assert.True(socket.LastCloseDescription.Length <= 123);
            Assert.EndsWith("...", socket.LastCloseDescription);
        }

        [Fact]
        public async Task CloseConnectionWithRedirect_CloseThrows_IsCaught()
        {
            var socket = new ThrowingCloseWebSocket();
            _provider.Connections["c1"] = socket;
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("c1", "/ws");

            // CloseAsync throws -> caught by CloseConnectionWithRedirectAsync's own try/catch.
            await InvokeAsync(_router, "CloseConnectionWithRedirectAsync", "c1", (string)null);
        }
    }
}
