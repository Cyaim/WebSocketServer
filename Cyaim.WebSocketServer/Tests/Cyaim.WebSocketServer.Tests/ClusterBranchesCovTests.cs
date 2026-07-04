using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Minimal in-memory IClusterTransport. Route storage can be made to throw so the
    /// cluster register/unregister catch blocks in MvcChannelHandler get exercised.
    /// </summary>
    internal sealed class StubClusterTransport : IClusterTransport
    {
        public bool ThrowOnRouteStorage { get; set; }

        public Task StartAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task SendAsync(string nodeId, ClusterMessage message) => Task.CompletedTask;
        public Task BroadcastAsync(ClusterMessage message) => Task.CompletedTask;
        public event EventHandler<ClusterMessageEventArgs> MessageReceived { add { } remove { } }
        public event EventHandler<ClusterNodeEventArgs> NodeConnected { add { } remove { } }
        public event EventHandler<ClusterNodeEventArgs> NodeDisconnected { add { } remove { } }
        public bool IsNodeConnected(string nodeId) => false;

        public Task<bool> StoreConnectionRouteAsync(string connectionId, string nodeId, Dictionary<string, string> metadata = null)
        {
            if (ThrowOnRouteStorage) throw new InvalidOperationException("store-fails");
            return Task.FromResult(true);
        }

        public Task<string> GetConnectionRouteAsync(string connectionId) => Task.FromResult<string>(null);

        public Task<bool> RemoveConnectionRouteAsync(string connectionId)
        {
            if (ThrowOnRouteStorage) throw new InvalidOperationException("remove-fails");
            return Task.FromResult(true);
        }

        public Task<bool> RefreshConnectionRouteAsync(string connectionId, string nodeId) => Task.FromResult(true);
        public void Dispose() { }
    }

    /// <summary>Connection provider that always resolves the same open socket.</summary>
    internal sealed class StubConnectionProvider : IWebSocketConnectionProvider
    {
        private readonly WebSocket _socket;
        public StubConnectionProvider(WebSocket socket) { _socket = socket; }
        public WebSocket GetConnection(string connectionId) => _socket;
        public Task<bool> SendAsync(string connectionId, byte[] data, WebSocketMessageType messageType, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    internal static class ClusterTestFactory
    {
        public static ClusterManager Create(StubClusterTransport transport, string nodeId, out ClusterRouter router, out RaftNode raftNode)
        {
            raftNode = new RaftNode(NullLogger<RaftNode>.Instance, transport, nodeId);
            router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, transport, raftNode, nodeId);
            var option = new ClusterOption { ChannelName = "/cluster" };
            return new ClusterManager(NullLogger<ClusterManager>.Instance, transport, raftNode, router, nodeId, option);
        }
    }

    [Collection("StaticState")]
    public class ClusterBranchesCovTests : IDisposable
    {
        private readonly ClusterManager _prevManager;
        private readonly IWebSocketConnectionProvider _prevProvider;
        private ClusterRouter _router;

        public ClusterBranchesCovTests()
        {
            _prevManager = GlobalClusterCenter.ClusterManager;
            _prevProvider = GlobalClusterCenter.ConnectionProvider;
        }

        public void Dispose()
        {
            GlobalClusterCenter.ClusterManager = _prevManager;
            GlobalClusterCenter.ConnectionProvider = _prevProvider;
            try { _router?.Dispose(); } catch { }
        }

        private ClusterManager SetupManager()
        {
            var transport = new StubClusterTransport();
            var mgr = ClusterTestFactory.Create(transport, "node-1", out _router, out _);
            var providerSocket = new TestWebSocket();
            var provider = new StubConnectionProvider(providerSocket);
            mgr.SetConnectionProvider(provider);
            GlobalClusterCenter.ClusterManager = mgr;
            return mgr;
        }

        [Fact]
        public async Task SendAsync_Batch_ClusterEnabled_RoutesViaClusterManager()
        {
            var mgr = SetupManager();
            await mgr.RegisterConnectionAsync("c1", "/ws", "1.2.3.4", 5);

            var results = await WebSocketManager.SendAsync(new[] { "c1" }, new byte[] { 1, 2, 3 }, WebSocketMessageType.Text);

            Assert.True(results.ContainsKey("c1"));
        }

        [Fact]
        public async Task StreamSendAsync_Single_ClusterEnabled_RoutesViaClusterManager()
        {
            var mgr = SetupManager();
            await mgr.RegisterConnectionAsync("c1", "/ws", "1.2.3.4", 5);

            using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4 });
            // Exercises the cluster branch of Stream.SendAsync(connectionId).
            await ms.SendAsync("c1");
        }

        [Fact]
        public async Task StreamSendAsync_Many_ClusterEnabled_RoutesViaClusterManager()
        {
            var mgr = SetupManager();
            await mgr.RegisterConnectionAsync("c1", "/ws", "1.2.3.4", 5);

            using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4 });
            var results = await ms.SendAsync(new[] { "c1" });
            Assert.NotNull(results);
        }
    }
}
