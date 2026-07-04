using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    [Collection("ClusterStaticState")]
    public class ClusterRouterTests : StaticStateGuard
    {
        private const string NodeA = "nodeA";
        private const string NodeB = "nodeB";
        private const string NodeC = "nodeC";

        private readonly FakeClusterTransport _transport = new FakeClusterTransport();
        private readonly FakeConnectionProvider _provider = new FakeConnectionProvider();
        private readonly RaftNode _raft;
        private readonly ClusterRouter _router;

        public ClusterRouterTests()
        {
            _raft = new RaftNode(NullLogger<RaftNode>.Instance, _transport, NodeA);
            _router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, _transport, _raft, NodeA);
            _router.SetConnectionProvider(_provider);
        }

        public override void Dispose()
        {
            _router.Dispose();
            base.Dispose();
        }

        private static ClusterMessage RegistrationMessage(string connectionId, string nodeId, string toNodeId = null, string endpoint = null)
        {
            return new ClusterMessage
            {
                Type = ClusterMessageType.RegisterWebSocketConnection,
                FromNodeId = nodeId,
                ToNodeId = toNodeId,
                Payload = JsonSerializer.Serialize(new WebSocketConnectionRegistration
                {
                    ConnectionId = connectionId,
                    NodeId = nodeId,
                    Endpoint = endpoint,
                    RegisteredAt = DateTime.UtcNow
                })
            };
        }

        // ---------------------------------------------------------------
        // Constructor
        // ---------------------------------------------------------------

        [Fact]
        public void Constructor_NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => new ClusterRouter(null, _transport, _raft, NodeA));
            Assert.Throws<ArgumentNullException>(() => new ClusterRouter(NullLogger<ClusterRouter>.Instance, null, _raft, NodeA));
            Assert.Throws<ArgumentNullException>(() => new ClusterRouter(NullLogger<ClusterRouter>.Instance, _transport, null, NodeA));
            Assert.Throws<ArgumentNullException>(() => new ClusterRouter(NullLogger<ClusterRouter>.Instance, _transport, _raft, null));
        }

        // ---------------------------------------------------------------
        // Register / Unregister
        // ---------------------------------------------------------------

        [Fact]
        public async Task RegisterConnection_WithRouteStorage_StoresRoute_AndDoesNotBroadcast()
        {
            _transport.SupportsRouteStorage = true;

            await _router.RegisterConnectionAsync("c1", "/ws", "10.0.0.9", 1234);

            Assert.Equal(NodeA, _router.ConnectionRoutes["c1"]);
            Assert.Equal(NodeA, _transport.StoredRoutes["c1"]);
            Assert.Equal("/ws", _transport.StoredMetadata["c1"]["Endpoint"]);
            Assert.Empty(_transport.Broadcasts);
            Assert.Equal(1, _router.GetLocalConnectionCount());

            var metadata = _router.GetConnectionMetadata("c1");
            Assert.Equal("10.0.0.9", metadata.RemoteIpAddress);
            Assert.Equal(1234, metadata.RemotePort);
            Assert.Equal("/ws", _router.GetConnectionEndpoint("c1"));
        }

        [Fact]
        public async Task RegisterConnection_WithoutRouteStorage_BroadcastsRegistration()
        {
            await _router.RegisterConnectionAsync("c1", "/chat");

            var broadcast = Assert.Single(_transport.Broadcasts);
            Assert.Equal(ClusterMessageType.RegisterWebSocketConnection, broadcast.Type);

            var registration = JsonSerializer.Deserialize<WebSocketConnectionRegistration>(broadcast.Payload);
            Assert.Equal("c1", registration.ConnectionId);
            Assert.Equal(NodeA, registration.NodeId);
            Assert.Equal("/chat", registration.Endpoint);
            Assert.Empty(_transport.StoredRoutes);
        }

        [Fact]
        public async Task RegisterConnection_BroadcastFailure_Rethrows()
        {
            _transport.OnBroadcastAsync = _ => throw new InvalidOperationException("boom");

            await Assert.ThrowsAsync<InvalidOperationException>(() => _router.RegisterConnectionAsync("c1"));
        }

        [Fact]
        public async Task UnregisterConnection_WithRouteStorage_RemovesStoredRoute_NoBroadcast()
        {
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("c1");

            await _router.UnregisterConnectionAsync("c1");

            Assert.False(_router.ConnectionRoutes.ContainsKey("c1"));
            Assert.Empty(_transport.StoredRoutes);
            Assert.Empty(_transport.Broadcasts);
            Assert.Equal(0, _router.GetLocalConnectionCount());
        }

        [Fact]
        public async Task UnregisterConnection_WithoutRouteStorage_BroadcastsUnregistration()
        {
            await _router.RegisterConnectionAsync("c1");

            await _router.UnregisterConnectionAsync("c1");

            Assert.Equal(2, _transport.Broadcasts.Count);
            var unregister = _transport.Broadcasts[1];
            Assert.Equal(ClusterMessageType.UnregisterWebSocketConnection, unregister.Type);
            var payload = JsonSerializer.Deserialize<JsonElement>(unregister.Payload);
            Assert.Equal("c1", payload.GetProperty("ConnectionId").GetString());
            Assert.Equal(NodeA, payload.GetProperty("NodeId").GetString());
            Assert.Equal(0, _router.GetLocalConnectionCount());
        }

        [Fact]
        public async Task UnregisterConnection_Unknown_DoesNotBroadcast()
        {
            await _router.UnregisterConnectionAsync("unknown");

            Assert.Empty(_transport.Broadcasts);
        }

        // ---------------------------------------------------------------
        // Remote registration caching (storage-less transports)
        // ---------------------------------------------------------------

        [Fact]
        public async Task RemoteRegistration_StorageLess_IsCachedInRoutingTable()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB, endpoint: "/chat"), NodeB);

            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.ContainsKey("r1")));
            Assert.Equal(NodeB, _router.ConnectionRoutes["r1"]);
            Assert.Equal(1, _router.GetTotalConnectionCount());
            Assert.Equal(0, _router.GetLocalConnectionCount());
            Assert.Equal("/chat", _router.GetConnectionEndpoint("r1"));
        }

        [Fact]
        public async Task RemoteRegistration_WhenTransportSupportsStorage_IsNotCached()
        {
            _transport.SupportsRouteStorage = true;
            // Establish the capability flag via a local registration first.
            await _router.RegisterConnectionAsync("local1");

            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB), NodeB);

            // Counts are still updated for the remote node, but the route is not cached.
            Assert.True(await Wait.UntilAsync(() => _router.GetTotalConnectionCount() == 2));
            Assert.False(_router.ConnectionRoutes.ContainsKey("r1"));
        }

        [Fact]
        public async Task RemoteRegistration_ForOwnNode_IsIgnored()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeA), NodeB);

            await Task.Delay(150);
            Assert.False(_router.ConnectionRoutes.ContainsKey("r1"));
            Assert.Equal(0, _router.GetTotalConnectionCount());
        }

        [Fact]
        public async Task CachedRemoteRoute_RouteMessage_ForwardsWithoutQuery()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB), NodeB);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.ContainsKey("r1")));

            var data = Encoding.UTF8.GetBytes("hello");
            var ok = await _router.RouteMessageAsync("r1", data, (int)WebSocketMessageType.Text, null);

            Assert.True(ok);
            // No broadcast query was needed / 无需广播查询
            Assert.DoesNotContain(_transport.Broadcasts, m => m.Type == ClusterMessageType.QueryWebSocketConnection);

            var (targetNode, message) = Assert.Single(_transport.Sent);
            Assert.Equal(NodeB, targetNode);
            Assert.Equal(ClusterMessageType.ForwardWebSocketMessage, message.Type);
            var forward = JsonSerializer.Deserialize<ForwardWebSocketMessage>(message.Payload);
            Assert.Equal("r1", forward.ConnectionId);
            Assert.Equal(NodeB, forward.TargetNodeId);
            Assert.Equal(data, forward.Data);
            Assert.Equal((int)WebSocketMessageType.Text, forward.MessageType);
        }

        [Fact]
        public async Task CachedRemoteRoute_TargetNodeNotConnected_ReturnsFalse()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB), NodeB);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.ContainsKey("r1")));
            _transport.NodeConnectedFunc = _ => false;

            var ok = await _router.RouteMessageAsync("r1", new byte[] { 1 }, (int)WebSocketMessageType.Binary, null);

            Assert.False(ok);
            Assert.Empty(_transport.Sent);
        }

        // ---------------------------------------------------------------
        // Local routing
        // ---------------------------------------------------------------

        [Fact]
        public async Task RouteMessage_LocalConnection_SendsThroughProvider()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("c1");

            var data = Encoding.UTF8.GetBytes("local");
            var ok = await _router.RouteMessageAsync("c1", data, (int)WebSocketMessageType.Text, null);

            Assert.True(ok);
            var frame = Assert.Single(socket.Frames);
            Assert.Equal(data, frame.Payload);
            Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
            Assert.Empty(_transport.Sent);
        }

        [Fact]
        public async Task RouteMessage_LocalConnection_InvokesLocalHandler()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("c1");

            string handledConnection = null;
            WebSocket handledSocket = null;
            var ok = await _router.RouteMessageAsync("c1", new byte[] { 1 }, (int)WebSocketMessageType.Binary, (id, ws) =>
            {
                handledConnection = id;
                handledSocket = ws;
                return Task.CompletedTask;
            });

            Assert.True(ok);
            Assert.Equal("c1", handledConnection);
            Assert.Same(socket, handledSocket);
            // Handler replaces the direct send / 处理程序取代直接发送
            Assert.Empty(socket.Frames);
        }

        [Fact]
        public async Task RouteMessage_LocalConnectionClosed_RemovesStaleRoute_ReturnsFalse()
        {
            var socket = new TestWebSocket(WebSocketState.Closed);
            _provider.Connections["c1"] = socket;
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("c1");

            var ok = await _router.RouteMessageAsync("c1", new byte[] { 1 }, (int)WebSocketMessageType.Text, null);

            Assert.False(ok);
            Assert.False(_router.ConnectionRoutes.ContainsKey("c1"));
        }

        [Fact]
        public async Task RouteMessage_NoConnectionProvider_ReturnsFalse()
        {
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, _transport, _raft, NodeA);
            try
            {
                _transport.SupportsRouteStorage = true;
                await router.RegisterConnectionAsync("c1");

                var ok = await router.RouteMessageAsync("c1", new byte[] { 1 }, (int)WebSocketMessageType.Text, null);

                Assert.False(ok);
            }
            finally
            {
                router.Dispose();
            }
        }

        // ---------------------------------------------------------------
        // Route lookup via route storage (Redis-like)
        // ---------------------------------------------------------------

        [Fact]
        public async Task RouteMessage_UnknownConnection_ResolvedFromStorage_RemoteForwardWithoutCaching()
        {
            _transport.SupportsRouteStorage = true;
            _transport.StoredRoutes["r1"] = NodeB;

            var ok = await _router.RouteMessageAsync("r1", new byte[] { 42 }, (int)WebSocketMessageType.Binary, null);

            Assert.True(ok);
            var (targetNode, message) = Assert.Single(_transport.Sent);
            Assert.Equal(NodeB, targetNode);
            Assert.Equal(ClusterMessageType.ForwardWebSocketMessage, message.Type);
            // Hybrid model: remote routes resolved from storage are not cached in memory
            Assert.False(_router.ConnectionRoutes.ContainsKey("r1"));
        }

        [Fact]
        public async Task RouteMessage_UnknownConnection_ResolvedFromStorage_LocalIsCachedAndDelivered()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            _transport.SupportsRouteStorage = true;
            _transport.StoredRoutes["c1"] = NodeA;

            var data = Encoding.UTF8.GetBytes("resolved");
            var ok = await _router.RouteMessageAsync("c1", data, (int)WebSocketMessageType.Text, null);

            Assert.True(ok);
            Assert.Equal(NodeA, _router.ConnectionRoutes["c1"]);
            Assert.Equal(data, Assert.Single(socket.Frames).Payload);
        }

        // ---------------------------------------------------------------
        // Broadcast query flow (storage-less)
        // ---------------------------------------------------------------

        [Fact]
        public async Task RouteMessage_UnknownConnection_BroadcastQueryAnswered_ForwardsAndCachesRoute()
        {
            _transport.OnBroadcastAsync = message =>
            {
                if (message.Type != ClusterMessageType.QueryWebSocketConnection)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    _transport.RaiseMessageReceived(RegistrationMessage("q1", NodeB, toNodeId: NodeA), NodeB);
                });
            };

            var ok = await _router.RouteMessageAsync("q1", new byte[] { 7 }, (int)WebSocketMessageType.Binary, null);

            Assert.True(ok);
            Assert.Contains(_transport.Broadcasts, m => m.Type == ClusterMessageType.QueryWebSocketConnection);
            Assert.Contains(_transport.Sent, s => s.NodeId == NodeB && s.Message.Type == ClusterMessageType.ForwardWebSocketMessage);
            // Storage-less transports cache the resolved remote route
            Assert.Equal(NodeB, _router.ConnectionRoutes["q1"]);
        }

        [Fact]
        public async Task RouteMessage_UnknownConnection_QueryTimesOut_ReturnsFalse()
        {
            var ok = await _router.RouteMessageAsync("missing", new byte[] { 1 }, (int)WebSocketMessageType.Text, null);

            Assert.False(ok);
            Assert.Contains(_transport.Broadcasts, m => m.Type == ClusterMessageType.QueryWebSocketConnection);
            Assert.Empty(_transport.Sent);
        }

        // ---------------------------------------------------------------
        // Inbound forward handling
        // ---------------------------------------------------------------

        [Fact]
        public async Task ForwardMessage_ForThisNode_DeliveredToLocalSocket()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;

            var data = Encoding.UTF8.GetBytes("forwarded");
            var message = new ClusterMessage
            {
                Type = ClusterMessageType.ForwardWebSocketMessage,
                FromNodeId = NodeB,
                Payload = JsonSerializer.Serialize(new ForwardWebSocketMessage
                {
                    ConnectionId = "c1",
                    TargetNodeId = NodeA,
                    Data = data,
                    MessageType = (int)WebSocketMessageType.Text
                })
            };

            _transport.RaiseMessageReceived(message, NodeB);

            Assert.True(await Wait.UntilAsync(() => socket.Frames.Count == 1));
            Assert.Equal(data, socket.Frames[0].Payload);
            Assert.Equal(WebSocketMessageType.Text, socket.Frames[0].MessageType);
        }

        [Fact]
        public async Task ForwardMessage_ForOtherNode_IsNotDelivered()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;

            var message = new ClusterMessage
            {
                Type = ClusterMessageType.ForwardWebSocketMessage,
                FromNodeId = NodeB,
                Payload = JsonSerializer.Serialize(new ForwardWebSocketMessage
                {
                    ConnectionId = "c1",
                    TargetNodeId = NodeC,
                    Data = new byte[] { 1 },
                    MessageType = (int)WebSocketMessageType.Text
                })
            };

            _transport.RaiseMessageReceived(message, NodeB);

            await Task.Delay(200);
            Assert.Empty(socket.Frames);
        }

        [Fact]
        public async Task ForwardMessage_LocalConnectionMissing_RemovesStaleRoute()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("c1", NodeB), NodeB);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.ContainsKey("c1")));

            var message = new ClusterMessage
            {
                Type = ClusterMessageType.ForwardWebSocketMessage,
                FromNodeId = NodeB,
                Payload = JsonSerializer.Serialize(new ForwardWebSocketMessage
                {
                    ConnectionId = "c1",
                    TargetNodeId = NodeA,
                    Data = new byte[] { 1 },
                    MessageType = (int)WebSocketMessageType.Text
                })
            };

            _transport.RaiseMessageReceived(message, NodeB);

            Assert.True(await Wait.UntilAsync(() => !_router.ConnectionRoutes.ContainsKey("c1")));
        }

        // ---------------------------------------------------------------
        // Inbound stream handling
        // ---------------------------------------------------------------

        [Fact]
        public async Task ForwardStream_Chunks_AreReassembledAndDelivered()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            var streamId = Guid.NewGuid().ToString("N");

            ClusterMessage Chunk(int index, bool last, byte[] data) => new ClusterMessage
            {
                Type = ClusterMessageType.ForwardWebSocketStream,
                FromNodeId = NodeB,
                Payload = JsonSerializer.Serialize(new ForwardWebSocketStream
                {
                    ConnectionId = "c1",
                    TargetNodeId = NodeA,
                    StreamId = streamId,
                    ChunkIndex = index,
                    IsLastChunk = last,
                    Data = data,
                    MessageType = (int)WebSocketMessageType.Binary
                })
            };

            _transport.RaiseMessageReceived(Chunk(0, false, Encoding.UTF8.GetBytes("Hello ")), NodeB);
            await Task.Delay(50);
            _transport.RaiseMessageReceived(Chunk(1, true, Encoding.UTF8.GetBytes("World")), NodeB);

            Assert.True(await Wait.UntilAsync(() => socket.Frames.Count == 1));
            Assert.Equal("Hello World", Encoding.UTF8.GetString(socket.Frames[0].Payload));
            Assert.Equal(WebSocketMessageType.Binary, socket.Frames[0].MessageType);
        }

        [Fact]
        public async Task RouteStream_RemoteConnection_SendsChunkedStreamMessages()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB), NodeB);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.ContainsKey("r1")));

            var source = new byte[150_000];
            new Random(42).NextBytes(source);
            using var stream = new MemoryStream(source);

            var ok = await _router.RouteStreamAsync("r1", stream, (int)WebSocketMessageType.Binary);

            Assert.True(ok);
            var chunks = _transport.Sent
                .Where(s => s.Message.Type == ClusterMessageType.ForwardWebSocketStream)
                .Select(s => JsonSerializer.Deserialize<ForwardWebSocketStream>(s.Message.Payload))
                .OrderBy(c => c.ChunkIndex)
                .ToList();

            Assert.Equal(3, chunks.Count);
            Assert.All(chunks, c => Assert.Equal("r1", c.ConnectionId));
            Assert.Single(chunks.Select(c => c.StreamId).Distinct());
            Assert.True(chunks[^1].IsLastChunk);
            Assert.False(chunks[0].IsLastChunk);
            Assert.Equal(source, chunks.SelectMany(c => c.Data).ToArray());
        }

        [Fact]
        public async Task RouteStream_InvalidArguments_ReturnFalse()
        {
            Assert.False(await _router.RouteStreamAsync(null, new MemoryStream(), (int)WebSocketMessageType.Binary));
            Assert.False(await _router.RouteStreamAsync("c1", null, (int)WebSocketMessageType.Binary));
        }

        // ---------------------------------------------------------------
        // Query connection request handling
        // ---------------------------------------------------------------

        [Fact]
        public async Task QueryConnection_LocalConnection_RespondsWithRegistration()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("c1");

            _transport.RaiseMessageReceived(new ClusterMessage
            {
                Type = ClusterMessageType.QueryWebSocketConnection,
                FromNodeId = NodeB,
                Payload = JsonSerializer.Serialize(new { ConnectionId = "c1" })
            }, NodeB);

            Assert.True(await Wait.UntilAsync(() =>
                _transport.Sent.Any(s => s.NodeId == NodeB && s.Message.Type == ClusterMessageType.RegisterWebSocketConnection)));

            var response = _transport.Sent.First(s => s.Message.Type == ClusterMessageType.RegisterWebSocketConnection).Message;
            var registration = JsonSerializer.Deserialize<WebSocketConnectionRegistration>(response.Payload);
            Assert.Equal("c1", registration.ConnectionId);
            Assert.Equal(NodeA, registration.NodeId);
            Assert.Equal(NodeB, response.ToNodeId);
        }

        [Fact]
        public async Task QueryConnection_KnownRemoteConnection_RespondsWithOwningNode()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeC), NodeC);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.ContainsKey("r1")));

            _transport.RaiseMessageReceived(new ClusterMessage
            {
                Type = ClusterMessageType.QueryWebSocketConnection,
                FromNodeId = NodeB,
                Payload = JsonSerializer.Serialize(new { ConnectionId = "r1" })
            }, NodeB);

            Assert.True(await Wait.UntilAsync(() =>
                _transport.Sent.Any(s => s.NodeId == NodeB && s.Message.Type == ClusterMessageType.RegisterWebSocketConnection)));

            var response = _transport.Sent.First(s => s.Message.Type == ClusterMessageType.RegisterWebSocketConnection).Message;
            var registration = JsonSerializer.Deserialize<WebSocketConnectionRegistration>(response.Payload);
            Assert.Equal(NodeC, registration.NodeId);
        }

        [Fact]
        public async Task QueryConnection_UnknownConnection_NoResponseSent()
        {
            _transport.RaiseMessageReceived(new ClusterMessage
            {
                Type = ClusterMessageType.QueryWebSocketConnection,
                FromNodeId = NodeB,
                Payload = JsonSerializer.Serialize(new { ConnectionId = "ghost" })
            }, NodeB);

            await Task.Delay(250);
            Assert.Empty(_transport.Sent);
        }

        [Fact]
        public async Task QueryConnection_RoutedButNotActuallyPresent_RemovesRoute()
        {
            // Route says local but the provider has no such socket / 路由表指向本地但连接实际不存在
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("phantom");

            _transport.RaiseMessageReceived(new ClusterMessage
            {
                Type = ClusterMessageType.QueryWebSocketConnection,
                FromNodeId = NodeB,
                Payload = JsonSerializer.Serialize(new { ConnectionId = "phantom" })
            }, NodeB);

            Assert.True(await Wait.UntilAsync(() => !_router.ConnectionRoutes.ContainsKey("phantom")));
            Assert.Empty(_transport.Sent);
        }

        // ---------------------------------------------------------------
        // Unregister broadcast handling + node disconnect cleanup
        // ---------------------------------------------------------------

        [Fact]
        public async Task UnregisterBroadcast_RemovesCachedRemoteRoute()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB), NodeB);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.ContainsKey("r1")));

            _transport.RaiseMessageReceived(new ClusterMessage
            {
                Type = ClusterMessageType.UnregisterWebSocketConnection,
                FromNodeId = NodeB,
                Payload = JsonSerializer.Serialize(new { ConnectionId = "r1", NodeId = NodeB })
            }, NodeB);

            Assert.True(await Wait.UntilAsync(() => !_router.ConnectionRoutes.ContainsKey("r1")));
        }

        [Fact]
        public async Task NodeDisconnected_CleansRoutes_AndBroadcastsUnregistrations()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB), NodeB);
            _transport.RaiseMessageReceived(RegistrationMessage("r2", NodeB), NodeB);
            Assert.True(await Wait.UntilAsync(() =>
                _router.ConnectionRoutes.ContainsKey("r1") && _router.ConnectionRoutes.ContainsKey("r2")));
            Assert.Equal(2, _router.GetTotalConnectionCount());

            _transport.RaiseNodeDisconnected(NodeB);

            Assert.True(await Wait.UntilAsync(() =>
                !_router.ConnectionRoutes.ContainsKey("r1") && !_router.ConnectionRoutes.ContainsKey("r2")));
            Assert.True(await Wait.UntilAsync(() =>
                _transport.Broadcasts.Count(m => m.Type == ClusterMessageType.UnregisterWebSocketConnection) == 2));
            Assert.Equal(0, _router.GetTotalConnectionCount());
        }

        [Fact]
        public async Task NodeDisconnected_SelfOrUnknown_IsIgnored()
        {
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("c1");

            _transport.RaiseNodeDisconnected(NodeA);
            _transport.RaiseNodeDisconnected(null);

            await Task.Delay(200);
            Assert.True(_router.ConnectionRoutes.ContainsKey("c1"));
            Assert.Equal(1, _router.GetLocalConnectionCount());
        }

        // ---------------------------------------------------------------
        // Connection counts and optimal node
        // ---------------------------------------------------------------

        [Fact]
        public async Task ConnectionCounts_TrackLocalAndRemoteConnections()
        {
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("c1");
            await _router.RegisterConnectionAsync("c2");

            Assert.Equal(2, _router.GetLocalConnectionCount());
            Assert.Equal(2, _router.GetTotalConnectionCount());

            await _router.UnregisterConnectionAsync("c1");
            Assert.Equal(1, _router.GetLocalConnectionCount());
            Assert.Equal(1, _router.GetTotalConnectionCount());
        }

        [Fact]
        public void GetOptimalNode_OnlySelf_ReturnsSelf()
        {
            Assert.Equal(NodeA, _router.GetOptimalNode());
        }

        [Fact]
        public async Task GetOptimalNode_PrefersLeastLoadedRemoteNode()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("b1", NodeB), NodeB);
            _transport.RaiseMessageReceived(RegistrationMessage("b2", NodeB), NodeB);
            _transport.RaiseMessageReceived(RegistrationMessage("c1", NodeC), NodeC);
            Assert.True(await Wait.UntilAsync(() => _router.GetTotalConnectionCount() == 3));

            Assert.Equal(NodeC, _router.GetOptimalNode());
        }

        [Fact]
        public void GetConnectionMetadata_Unknown_ReturnsNull()
        {
            Assert.Null(_router.GetConnectionMetadata("nope"));
            Assert.Null(_router.GetConnectionEndpoint("nope"));
        }

        // ---------------------------------------------------------------
        // Graceful shutdown
        // ---------------------------------------------------------------

        [Fact]
        public async Task GracefulShutdown_NoProvider_ReturnsWithoutClosingAnything()
        {
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, _transport, _raft, NodeA);
            try
            {
                var socket = new TestWebSocket();
                MvcChannelHandler.Clients["g1"] = socket;

                await router.GracefulShutdownAsync(new ClusterOption());

                Assert.Equal(WebSocketState.Open, socket.State);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Fact]
        public async Task GracefulShutdown_NoOtherNodes_ClosesLocalConnectionsWithoutRedirect()
        {
            var socket = new TestWebSocket();
            MvcChannelHandler.Clients["g1"] = socket;
            _provider.Connections["g1"] = socket;
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("g1", "/ws");

            await _router.GracefulShutdownAsync(new ClusterOption());

            Assert.Equal(WebSocketState.Closed, socket.State);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.LastCloseStatus);
            Assert.Equal("Node shutting down", socket.LastCloseDescription);
            Assert.False(_router.ConnectionRoutes.ContainsKey("g1"));
        }

        [Fact]
        public async Task GracefulShutdown_NoLocalConnections_CompletesQuickly()
        {
            await _router.GracefulShutdownAsync(new ClusterOption());
            Assert.Empty(_transport.Sent);
        }

        [Fact]
        public void Dispose_CanBeCalledSafely()
        {
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, _transport, _raft, NodeA);
            router.Dispose();
            router.Dispose();
        }
    }
}
