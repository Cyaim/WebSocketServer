using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    [Collection("ClusterStaticState")]
    public class ClusterManagerTests : StaticStateGuard
    {
        private const string NodeA = "nodeA";

        private readonly FakeClusterTransport _transport = new FakeClusterTransport();
        private readonly FakeConnectionProvider _provider = new FakeConnectionProvider();
        private readonly RaftNode _raft;
        private readonly ClusterRouter _router;

        public ClusterManagerTests()
        {
            _raft = new RaftNode(NullLogger<RaftNode>.Instance, _transport, NodeA);
            _router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, _transport, _raft, NodeA);
            _router.SetConnectionProvider(_provider);
        }

        public override void Dispose()
        {
            try
            {
                _raft.StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // best effort
            }
            _router.Dispose();
            base.Dispose();
        }

        private ClusterManager CreateManager(ClusterOption option = null)
        {
            return new ClusterManager(
                NullLogger<ClusterManager>.Instance,
                _transport,
                _raft,
                _router,
                NodeA,
                option ?? new ClusterOption());
        }

        // ---------------------------------------------------------------
        // Construction
        // ---------------------------------------------------------------

        [Fact]
        public void Constructor_NullArguments_Throw()
        {
            var option = new ClusterOption();
            Assert.Throws<ArgumentNullException>(() => new ClusterManager(null, _transport, _raft, _router, NodeA, option));
            Assert.Throws<ArgumentNullException>(() => new ClusterManager(NullLogger<ClusterManager>.Instance, null, _raft, _router, NodeA, option));
            Assert.Throws<ArgumentNullException>(() => new ClusterManager(NullLogger<ClusterManager>.Instance, _transport, null, _router, NodeA, option));
            Assert.Throws<ArgumentNullException>(() => new ClusterManager(NullLogger<ClusterManager>.Instance, _transport, _raft, null, NodeA, option));
            Assert.Throws<ArgumentNullException>(() => new ClusterManager(NullLogger<ClusterManager>.Instance, _transport, _raft, _router, null, option));
            Assert.Throws<ArgumentNullException>(() => new ClusterManager(NullLogger<ClusterManager>.Instance, _transport, _raft, _router, NodeA, null));
        }

        [Fact]
        public void TransportProperty_ExposesTransportInstance()
        {
            var manager = CreateManager();

            // Transport is internal — pin it via reflection / Transport 为 internal，通过反射验证
            var property = typeof(ClusterManager).GetProperty("Transport", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(property);
            Assert.Same(_transport, property.GetValue(manager));
        }

        // ---------------------------------------------------------------
        // StartAsync: node config parsing + registration
        // ---------------------------------------------------------------

        [Fact]
        public async Task StartAsync_ParsesNodeConfigs_AndRegistersThemInTransport()
        {
            var option = new ClusterOption
            {
                TransportType = "ws",
                ChannelName = "/cluster",
                Nodes = new[]
                {
                    "ws://127.0.0.1:5001/nodeB",   // URI with node id path
                    "nodeC@10.0.0.2:6000",         // nodeId@address:port
                    "nodeD",                       // bare node id
                    "ws://127.0.0.1:5002",         // URI without path -> host:port id
                    $"ws://127.0.0.1:5000/{NodeA}" // self, must be excluded
                }
            };
            var manager = CreateManager(option);

            await manager.StartAsync();
            try
            {
                var nodes = _transport.RegisteredNodes.ToDictionary(n => n.NodeId);

                Assert.Equal(4, nodes.Count);
                Assert.DoesNotContain(NodeA, nodes.Keys);

                var nodeB = nodes["nodeB"];
                Assert.Equal("127.0.0.1", nodeB.Address);
                Assert.Equal(5001, nodeB.Port);
                Assert.Equal("ws", nodeB.Protocol);
                Assert.Equal("/cluster", nodeB.TransportConfig);
                Assert.Equal("ws://127.0.0.1:5001", nodeB.FullAddress);

                var nodeC = nodes["nodeC"];
                Assert.Equal("10.0.0.2", nodeC.Address);
                Assert.Equal(6000, nodeC.Port);
                Assert.Equal("ws", nodeC.Protocol);

                var nodeD = nodes["nodeD"];
                Assert.Null(nodeD.Address);
                Assert.Equal(0, nodeD.Port);

                Assert.True(nodes.ContainsKey("127.0.0.1:5002"));

                // Transport is started both by the manager and the raft node (current behavior)
                Assert.Equal(2, _transport.StartCount);
            }
            finally
            {
                await manager.StopAsync();
            }
        }

        [Fact]
        public async Task StartAsync_RedisTransportType_CarriesRedisConnectionString()
        {
            var option = new ClusterOption
            {
                TransportType = "redis",
                RedisConnectionString = "localhost:6379",
                Nodes = new[] { "nodeR@1.2.3.4:1" }
            };
            var manager = CreateManager(option);

            await manager.StartAsync();
            try
            {
                var node = Assert.Single(_transport.RegisteredNodes);
                Assert.Equal("nodeR", node.NodeId);
                Assert.Equal("redis", node.Protocol);
                Assert.Equal("localhost:6379", node.TransportConfig);
            }
            finally
            {
                await manager.StopAsync();
            }
        }

        [Fact]
        public async Task StartAsync_NoNodesConfigured_StartsStandalone()
        {
            var manager = CreateManager(new ClusterOption { Nodes = null });

            await manager.StartAsync();
            try
            {
                Assert.Empty(_transport.RegisteredNodes);
                Assert.True(_transport.StartCount >= 1);
            }
            finally
            {
                await manager.StopAsync();
            }
        }

        [Fact]
        public async Task StopAsync_StopsRaftNodeAndTransport()
        {
            var manager = CreateManager();
            await manager.StartAsync();

            await manager.StopAsync();

            // Raft StopAsync stops the transport, manager stops it again (current behavior)
            Assert.Equal(2, _transport.StopCount);
        }

        [Fact]
        public async Task ShutdownAsync_Force_SkipsGracefulTransfer()
        {
            var socket = new TestWebSocket();
            Infrastructure.Handlers.MvcHandler.MvcChannelHandler.Clients["g1"] = socket;
            _provider.Connections["g1"] = socket;
            var manager = CreateManager();
            await manager.StartAsync();

            await manager.ShutdownAsync(force: true);

            // Forced shutdown must not close client connections / 强制关闭不会关闭客户端连接
            Assert.Equal(WebSocketState.Open, socket.State);
        }

        // ---------------------------------------------------------------
        // Register / unregister / routing delegation
        // ---------------------------------------------------------------

        [Fact]
        public async Task RegisterAndUnregisterConnection_DelegateToRouter()
        {
            _transport.SupportsRouteStorage = true;
            var manager = CreateManager();

            await manager.RegisterConnectionAsync("c1", "/ws", "1.2.3.4", 999);

            Assert.Equal(NodeA, manager.ConnectionRoutes["c1"]);
            Assert.Equal(1, manager.GetLocalConnectionCount());
            Assert.Equal(1, manager.GetTotalConnectionCount());
            Assert.Equal("/ws", manager.GetConnectionEndpoint("c1"));
            Assert.Equal("1.2.3.4", manager.GetConnectionMetadata("c1").RemoteIpAddress);

            await manager.UnregisterConnectionAsync("c1");

            Assert.False(manager.ConnectionRoutes.ContainsKey("c1"));
            Assert.Equal(0, manager.GetLocalConnectionCount());
        }

        [Fact]
        public async Task RouteMessageAsync_LocalPath_DeliversToSocket()
        {
            _transport.SupportsRouteStorage = true;
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            var manager = CreateManager();
            await manager.RegisterConnectionAsync("c1");

            var data = Encoding.UTF8.GetBytes("payload");
            var ok = await manager.RouteMessageAsync("c1", data, (int)WebSocketMessageType.Text);

            Assert.True(ok);
            Assert.Equal(data, Assert.Single(socket.Frames).Payload);
        }

        [Fact]
        public async Task RouteMessageAsync_WithLocalHandler_UsesHandler()
        {
            _transport.SupportsRouteStorage = true;
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            var manager = CreateManager();
            await manager.RegisterConnectionAsync("c1");

            var handled = false;
            var ok = await manager.RouteMessageAsync("c1", new byte[] { 1 }, (int)WebSocketMessageType.Binary, (_, __) =>
            {
                handled = true;
                return Task.CompletedTask;
            });

            Assert.True(ok);
            Assert.True(handled);
        }

        [Fact]
        public async Task SetConnectionProvider_OnManager_ForwardsToRouter()
        {
            // Fixed: ClusterManager.SetConnectionProvider now forwards the provider to
            // ClusterRouter (consistent with SetMetricsCollector), so local routing works
            // when the provider is set on the manager alone.
            var transport = new FakeClusterTransport { SupportsRouteStorage = true };
            var raft = new RaftNode(NullLogger<RaftNode>.Instance, transport, NodeA);
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, transport, raft, NodeA);
            try
            {
                var manager = new ClusterManager(NullLogger<ClusterManager>.Instance, transport, raft, router, NodeA, new ClusterOption());
                var provider = new FakeConnectionProvider();
                var socket = new TestWebSocket();
                provider.Connections["c1"] = socket;
                manager.SetConnectionProvider(provider);
                await manager.RegisterConnectionAsync("c1");

                var ok = await manager.RouteMessageAsync("c1", new byte[] { 1 }, (int)WebSocketMessageType.Text);

                Assert.True(ok);
                Assert.Single(socket.Frames);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Fact]
        public async Task RouteMessagesAsync_MultipleConnections_ReturnsPerConnectionResults()
        {
            _transport.SupportsRouteStorage = true;
            var socket1 = new TestWebSocket();
            var socket2 = new TestWebSocket();
            _provider.Connections["c1"] = socket1;
            _provider.Connections["c2"] = socket2;
            var manager = CreateManager();
            await manager.RegisterConnectionAsync("c1");
            await manager.RegisterConnectionAsync("c2");

            var results = await manager.RouteMessagesAsync(new[] { "c1", "c2", null, "" }, new byte[] { 9 }, (int)WebSocketMessageType.Binary);

            Assert.Equal(2, results.Count);
            Assert.True(results["c1"]);
            Assert.True(results["c2"]);
            Assert.Single(socket1.Frames);
            Assert.Single(socket2.Frames);
        }

        [Fact]
        public async Task RouteMessagesAsync_NullConnectionIds_Throws()
        {
            var manager = CreateManager();
            await Assert.ThrowsAsync<ArgumentNullException>(() => manager.RouteMessagesAsync(null, new byte[] { 1 }, 0));
        }

        [Fact]
        public async Task RouteTextAsync_EncodesUtf8Text()
        {
            _transport.SupportsRouteStorage = true;
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            var manager = CreateManager();
            await manager.RegisterConnectionAsync("c1");

            var ok = await manager.RouteTextAsync("c1", "你好 hello");

            Assert.True(ok);
            var frame = Assert.Single(socket.Frames);
            Assert.Equal("你好 hello", Encoding.UTF8.GetString(frame.Payload));
            Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
        }

        [Fact]
        public async Task RouteTextAsync_EmptyText_ReturnsFalse()
        {
            var manager = CreateManager();
            Assert.False(await manager.RouteTextAsync("c1", null));
            Assert.False(await manager.RouteTextAsync("c1", ""));
        }

        [Fact]
        public async Task RouteTextsAsync_EmptyText_ReturnsEmptyDictionary()
        {
            var manager = CreateManager();
            var results = await manager.RouteTextsAsync(new[] { "c1" }, "");
            Assert.Empty(results);
        }

        [Fact]
        public async Task RouteJsonAsync_SerializesObjectAsJsonText()
        {
            _transport.SupportsRouteStorage = true;
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            var manager = CreateManager();
            await manager.RegisterConnectionAsync("c1");

            var ok = await manager.RouteJsonAsync("c1", new { Name = "cyaim", Value = 3 });

            Assert.True(ok);
            var json = Encoding.UTF8.GetString(Assert.Single(socket.Frames).Payload);
            var element = JsonSerializer.Deserialize<JsonElement>(json);
            Assert.Equal("cyaim", element.GetProperty("Name").GetString());
            Assert.Equal(3, element.GetProperty("Value").GetInt32());
        }

        [Fact]
        public async Task RouteJsonAsync_NullData_ReturnsFalse()
        {
            var manager = CreateManager();
            Assert.False(await manager.RouteJsonAsync<object>("c1", null));
        }

        [Fact]
        public async Task RouteObjectAsync_UsesCustomSerializer()
        {
            _transport.SupportsRouteStorage = true;
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            var manager = CreateManager();
            await manager.RegisterConnectionAsync("c1");

            var ok = await manager.RouteObjectAsync("c1", "abc", s => Encoding.ASCII.GetBytes(s), WebSocketMessageType.Binary);

            Assert.True(ok);
            var frame = Assert.Single(socket.Frames);
            Assert.Equal(new byte[] { (byte)'a', (byte)'b', (byte)'c' }, frame.Payload);
            Assert.Equal(WebSocketMessageType.Binary, frame.MessageType);
        }

        [Fact]
        public async Task RouteObjectAsync_SerializerThrows_ReturnsFalse()
        {
            var manager = CreateManager();
            var ok = await manager.RouteObjectAsync<string>("c1", "abc", _ => throw new InvalidOperationException());
            Assert.False(ok);
        }

        [Fact]
        public async Task RouteObjectsAsync_SerializerThrows_AllConnectionsFail()
        {
            var manager = CreateManager();
            var results = await manager.RouteObjectsAsync<string>(new[] { "c1", "c2" }, "abc", _ => throw new InvalidOperationException());
            Assert.Equal(2, results.Count);
            Assert.All(results.Values, v => Assert.False(v));
        }

        [Fact]
        public async Task RouteStreamAsync_InvalidInput_ReturnsFalse()
        {
            var manager = CreateManager();
            Assert.False(await manager.RouteStreamAsync(null, new MemoryStream()));
            Assert.False(await manager.RouteStreamAsync("c1", null));
        }

        [Fact]
        public async Task RouteStreamsAsync_NullInputs_ReturnEmptyResults()
        {
            var manager = CreateManager();
            Assert.Empty(await manager.RouteStreamsAsync(null, new MemoryStream()));
            Assert.Empty(await manager.RouteStreamsAsync(new[] { "c1" }, null));
            Assert.Empty(await manager.RouteStreamsAsync(new string[] { null, "" }, new MemoryStream()));
        }

        // ---------------------------------------------------------------
        // Misc delegation
        // ---------------------------------------------------------------

        [Fact]
        public void IsLeader_FalseBeforeAnyElection()
        {
            var manager = CreateManager();
            Assert.False(manager.IsLeader());
        }

        [Fact]
        public void GetOptimalNode_DelegatesToRouter()
        {
            var manager = CreateManager();
            Assert.Equal(NodeA, manager.GetOptimalNode());
        }
    }
}
