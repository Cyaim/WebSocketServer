using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    /// <summary>
    /// Covers the adaptive batch-size shrink and long-delay branches of
    /// GracefulShutdownAsync, which only trigger when a batch takes longer than the 500ms
    /// threshold. A single connection whose CloseAsync deliberately blocks ~650ms drives it.
    /// </summary>
    [Collection("ClusterStaticState")]
    public class ClusterRouterBatchTimingCovTests : StaticStateGuard
    {
        [Fact]
        public async Task GracefulShutdown_SlowBatch_ShrinksBatchSizeAndAddsLongDelay()
        {
            var transport = new FakeClusterTransport();
            var raft = new RaftNode(NullLogger<RaftNode>.Instance, transport, "nodeA");
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, transport, raft, "nodeA");
            try
            {
                var provider = new FakeConnectionProvider();
                var slow = new SlowCloseWebSocket(TimeSpan.FromMilliseconds(650));
                provider.Connections["c1"] = slow;
                router.SetConnectionProvider(provider);

                // GetLocalConnections() enumerates MvcChannelHandler.Clients for Open sockets.
                MvcChannelHandler.Clients["c1"] = slow;

                await router.GracefulShutdownAsync(new ClusterOption());

                // The slow close (>500ms) forces the adaptive loop through the shrink branch
                // (1000 -> 700) and the long-delay branch (Task.Delay(100)).
                Assert.True(slow.CloseCalled);
            }
            finally
            {
                try { await raft.StopAsync(); } catch { }
                router.Dispose();
            }
        }

        private sealed class SlowCloseWebSocket : WebSocket
        {
            private readonly TimeSpan _delay;
            private int _closed;
            public bool CloseCalled => _closed != 0;

            public SlowCloseWebSocket(TimeSpan delay) => _delay = delay;

            // Open until the (slow) close begins, so both GetLocalConnections and the
            // pre-close State guard see it as Open.
            public override WebSocketState State => _closed == 0 ? WebSocketState.Open : WebSocketState.Closed;
            public override WebSocketCloseStatus? CloseStatus => null;
            public override string CloseStatusDescription => null;
            public override string SubProtocol => null;
            public override void Abort() { }
            public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
            {
                Interlocked.Exchange(ref _closed, 1);
                await Task.Delay(_delay, cancellationToken);
            }
            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
                => Task.CompletedTask;
            public override void Dispose() { }
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
                => throw new InvalidOperationException("receive not used");
            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
                => Task.CompletedTask;
        }
    }
}
