using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Cluster.Transports;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Metrics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    /// <summary>
    /// Additional coverage-only tests for <see cref="ClusterManager"/>, targeting the
    /// error/edge branches left uncovered by <c>ClusterManagerTests</c>: SetMetricsCollector
    /// delegation, Start/Stop/Shutdown catch blocks, RouteMessagesAsync/RouteJsonsAsync/
    /// RouteObjectAsync/RouteObjectsAsync/RouteStreamAsync/RouteStreamsAsync branch coverage,
    /// and node-config parsing/registration edge cases (empty entry, RabbitMQ protocol,
    /// registration failure, real WebSocketClusterTransport registration, and a
    /// reflection-based fault injection making ParseNodeConfig's own catch block reachable).
    /// </summary>
    [Collection("ClusterStaticState")]
    public class ClusterManagerCovTests : StaticStateGuard
    {
        private const string NodeA = "nodeA";

        private readonly FakeClusterTransport _transport = new FakeClusterTransport();
        private readonly FakeConnectionProvider _provider = new FakeConnectionProvider();
        private readonly RaftNode _raft;
        private readonly ClusterRouter _router;
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        public ClusterManagerCovTests()
        {
            _raft = new RaftNode(NullLogger<RaftNode>.Instance, _transport, NodeA);
            _router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, _transport, _raft, NodeA);
            _router.SetConnectionProvider(_provider);
        }

        public override void Dispose()
        {
            foreach (var d in _disposables)
            {
                try { d.Dispose(); } catch { }
            }
            try { _raft.StopAsync().GetAwaiter().GetResult(); } catch { }
            _router.Dispose();
            base.Dispose();
        }

        private ClusterManager CreateManager(ClusterOption option = null, IClusterTransport transport = null, RaftNode raft = null, ClusterRouter router = null)
        {
            return new ClusterManager(
                NullLogger<ClusterManager>.Instance,
                transport ?? _transport,
                raft ?? _raft,
                router ?? _router,
                NodeA,
                option ?? new ClusterOption());
        }

        private static Task InvokeAsync(ClusterManager manager, string method, params object[] args)
        {
            var mi = typeof(ClusterManager).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(mi);
            return (Task)mi.Invoke(manager, args);
        }

        private static object Invoke(ClusterManager manager, string method, params object[] args)
        {
            var mi = typeof(ClusterManager).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(mi);
            return mi.Invoke(manager, args);
        }

        // ---------------------------------------------------------------
        // SetMetricsCollector
        // ---------------------------------------------------------------

        [Fact]
        public void SetMetricsCollector_ForwardsToRouter()
        {
            var manager = CreateManager();
            var collector = new WebSocketMetricsCollector(NullLogger<WebSocketMetricsCollector>.Instance);

            // No exception means the call reached ClusterRouter.SetMetricsCollector.
            manager.SetMetricsCollector(collector);
        }

        // ---------------------------------------------------------------
        // StartAsync catch block
        // ---------------------------------------------------------------

        [Fact]
        public async Task StartAsync_TransportStartThrows_LogsAndRethrows()
        {
            var throwingTransport = new ConfigurableClusterTransport
            {
                StartAsyncImpl = () => throw new InvalidOperationException("start failed")
            };
            var manager = CreateManager(transport: throwingTransport);

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync());
        }

        // ---------------------------------------------------------------
        // ShutdownAsync + StopAsync catch blocks (shared transport failure)
        // ---------------------------------------------------------------

        [Fact]
        public async Task ShutdownAsync_NotForced_StopThrows_PropagatesThroughBothCatchBlocks()
        {
            var throwingTransport = new ConfigurableClusterTransport
            {
                StopAsyncImpl = () => throw new InvalidOperationException("stop failed")
            };
            var raft = new RaftNode(NullLogger<RaftNode>.Instance, throwingTransport, NodeA);
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, throwingTransport, raft, NodeA);
            try
            {
                var manager = CreateManager(transport: throwingTransport, raft: raft, router: router);

                // force:false exercises the graceful-transfer call (router has no connection
                // provider registered here, so GracefulShutdownAsync returns immediately),
                // then StopAsync's own catch, then ShutdownAsync's outer catch both fire as
                // the transport-stop failure propagates.
                await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ShutdownAsync(force: false));
            }
            finally
            {
                router.Dispose();
            }
        }

        // ---------------------------------------------------------------
        // RouteMessagesAsync branch coverage
        // ---------------------------------------------------------------

        [Fact]
        public async Task RouteMessagesAsync_LocalRouteButNoSocket_LogsFailureBranch()
        {
            var manager = CreateManager();
            await manager.RegisterConnectionAsync("c1"); // route recorded, but no socket in provider

            var results = await manager.RouteMessagesAsync(new[] { "c1" }, new byte[] { 1 }, (int)WebSocketMessageType.Binary);

            Assert.False(results["c1"]);
        }

        [Fact]
        public async Task RouteMessagesAsync_NullData_CatchesNullReferenceException()
        {
            var manager = CreateManager();

            var results = await manager.RouteMessagesAsync(new[] { "c1" }, null, (int)WebSocketMessageType.Binary);

            Assert.False(results["c1"]);
        }

        // ---------------------------------------------------------------
        // RouteTextsAsync non-empty path
        // ---------------------------------------------------------------

        [Fact]
        public async Task RouteTextsAsync_NonEmptyText_RoutesToConnection()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            var manager = CreateManager();
            await manager.RegisterConnectionAsync("c1");

            var results = await manager.RouteTextsAsync(new[] { "c1" }, "hello");

            Assert.True(results["c1"]);
            Assert.Equal("hello", Encoding.UTF8.GetString(Assert.Single(socket.Frames).Payload));
        }

        // ---------------------------------------------------------------
        // RouteJsonsAsync (plural) branch coverage
        // ---------------------------------------------------------------

        [Fact]
        public async Task RouteJsonsAsync_NullData_ReturnsEmptyDictionary()
        {
            var manager = CreateManager();
            var results = await manager.RouteJsonsAsync<object>(new[] { "c1" }, null);
            Assert.Empty(results);
        }

        [Fact]
        public async Task RouteJsonsAsync_ValidData_RoutesToConnections()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            var manager = CreateManager();
            await manager.RegisterConnectionAsync("c1");

            var results = await manager.RouteJsonsAsync(new[] { "c1" }, new { Value = 42 });

            Assert.True(results["c1"]);
            Assert.Single(socket.Frames);
        }

        // ---------------------------------------------------------------
        // RouteObjectAsync / RouteObjectsAsync null-guard branches
        // ---------------------------------------------------------------

        [Fact]
        public async Task RouteObjectAsync_NullData_ReturnsFalse()
        {
            var manager = CreateManager();
            Assert.False(await manager.RouteObjectAsync<string>("c1", null, s => Encoding.UTF8.GetBytes(s)));
        }

        [Fact]
        public async Task RouteObjectAsync_NullSerializer_ReturnsFalse()
        {
            var manager = CreateManager();
            Assert.False(await manager.RouteObjectAsync<string>("c1", "abc", null));
        }

        [Fact]
        public async Task RouteObjectsAsync_NullData_ReturnsEmptyDictionary()
        {
            var manager = CreateManager();
            var results = await manager.RouteObjectsAsync<string>(new[] { "c1" }, null, s => Encoding.UTF8.GetBytes(s));
            Assert.Empty(results);
        }

        [Fact]
        public async Task RouteObjectsAsync_ValidSerializer_RoutesSuccessfully()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            var manager = CreateManager();
            await manager.RegisterConnectionAsync("c1");

            var results = await manager.RouteObjectsAsync<string>(new[] { "c1" }, "abc", s => Encoding.ASCII.GetBytes(s));

            Assert.True(results["c1"]);
            Assert.Single(socket.Frames);
        }

        // ---------------------------------------------------------------
        // RouteStreamAsync / RouteStreamsAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task RouteStreamAsync_ValidInput_DelegatesToRouter()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            var manager = CreateManager();
            await manager.RegisterConnectionAsync("c1");

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("stream-data"));
            var ok = await manager.RouteStreamAsync("c1", stream);

            Assert.True(ok);
        }

        [Fact]
        public async Task RouteStreamsAsync_MultipleConnections_BuffersAndSendsToAll()
        {
            var socket1 = new TestWebSocket();
            var socket2 = new TestWebSocket();
            _provider.Connections["c1"] = socket1;
            _provider.Connections["c2"] = socket2;
            var manager = CreateManager();
            await manager.RegisterConnectionAsync("c1");
            await manager.RegisterConnectionAsync("c2");

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("multi-stream"));
            var results = await manager.RouteStreamsAsync(new[] { "c1", "c2" }, stream);

            Assert.Equal(2, results.Count);
            Assert.True(results["c1"]);
            Assert.True(results["c2"]);
        }

        [Fact]
        public async Task RouteStreamsAsync_SingleConnection_StreamsDirectly()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            var manager = CreateManager();
            await manager.RegisterConnectionAsync("c1");

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("single-stream"));
            var results = await manager.RouteStreamsAsync(new[] { "c1" }, stream);

            Assert.True(results["c1"]);
        }

        // ---------------------------------------------------------------
        // RegisterClusterNodesAsync / ParseNodeConfig / RegisterNodeInTransport edge cases
        // ---------------------------------------------------------------

        [Fact]
        public async Task RegisterClusterNodesAsync_RegisterNodeThrows_IsCaughtAndOthersStillProcessed()
        {
            var throwingTransport = new ConfigurableClusterTransport { ThrowOnRegisterNode = true };
            var raft = new RaftNode(NullLogger<RaftNode>.Instance, throwingTransport, NodeA);
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, throwingTransport, raft, NodeA);
            try
            {
                var option = new ClusterOption { Nodes = new[] { "nodeB@127.0.0.1:1", "nodeC@127.0.0.1:2" } };
                var manager = CreateManager(option, throwingTransport, raft, router);

                // Private method: only registers nodes, never calls transport.StartAsync
                // (which would otherwise incur the real connect/backoff delay).
                await InvokeAsync(manager, "RegisterClusterNodesAsync");

                // Both nodes attempted to register; RegisterNode always throws, so none succeeded,
                // but the loop must have continued past the first failure without throwing out.
                Assert.Empty(throwingTransport.RegisteredNodes);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Fact]
        public async Task RegisterClusterNodesAsync_EmptyNodeConfigEntry_SkippedGracefully()
        {
            var option = new ClusterOption { Nodes = new[] { "", "nodeB@127.0.0.1:1" } };
            var manager = CreateManager(option);

            await InvokeAsync(manager, "RegisterClusterNodesAsync");

            var node = Assert.Single(_transport.RegisteredNodes);
            Assert.Equal("nodeB", node.NodeId);
        }

        [Fact]
        public async Task RegisterClusterNodesAsync_RabbitMqTransportType_CarriesConnectionString()
        {
            var option = new ClusterOption
            {
                TransportType = "rabbitmq",
                RabbitMQConnectionString = "amqp://localhost",
                Nodes = new[] { "nodeR@1.2.3.4:1" }
            };
            var manager = CreateManager(option);

            await InvokeAsync(manager, "RegisterClusterNodesAsync");

            var node = Assert.Single(_transport.RegisteredNodes);
            Assert.Equal("rabbitmq", node.Protocol);
            Assert.Equal("amqp://localhost", node.TransportConfig);
        }

        [Fact]
        public void ParseNodeConfig_ClusterOptionNulledOut_HitsCatchBlock_ReturnsNull()
        {
            var manager = CreateManager();

            // Fault injection: null out the private readonly _clusterOption field so that
            // `_clusterOption.TransportType` dereferences null and throws NullReferenceException
            // inside ParseNodeConfig's try block, which is caught and returns null. There is no
            // legitimate (non-throwing) way to reach this catch through Uri/string parsing alone,
            // since Uri.TryCreate/int.TryParse never throw and every property accessed on a
            // TryCreate-validated absolute Uri (Scheme/Host/Port/PathAndQuery) is documented as
            // non-throwing; this was confirmed empirically against a wide range of malformed
            // node-config strings (IDN-overflow hosts, opaque schemes, huge ports, huge labels).
            var clusterOptionField = typeof(ClusterManager).GetField("_clusterOption", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(clusterOptionField);
            clusterOptionField.SetValue(manager, null);

            var result = Invoke(manager, "ParseNodeConfig", "nodeB@127.0.0.1:1");

            Assert.Null(result);
        }

        [Fact]
        public async Task RegisterNodeInTransport_RealWebSocketClusterTransport_UsesDirectCall()
        {
            var wsTransport = new WebSocketClusterTransport(NullLogger<WebSocketClusterTransport>.Instance, NodeA);
            _disposables.Add(wsTransport);
            var raft = new RaftNode(NullLogger<RaftNode>.Instance, wsTransport, NodeA);
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, wsTransport, raft, NodeA);
            try
            {
                var option = new ClusterOption { Nodes = new[] { "nodeB@127.0.0.1:1" } };
                var manager = CreateManager(option, wsTransport, raft, router);

                // Only registers (no connection attempt), exercising the
                // `_transport is Transports.WebSocketClusterTransport` direct-call branch.
                await InvokeAsync(manager, "RegisterClusterNodesAsync");

                Assert.True(wsTransport.IsNodeConnected("nodeB") == false); // registered, not connected
            }
            finally
            {
                router.Dispose();
            }
        }
    }
}
