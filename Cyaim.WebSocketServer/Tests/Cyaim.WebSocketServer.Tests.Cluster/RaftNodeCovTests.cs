using System.Reflection;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Cluster.Transports;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    /// <summary>
    /// Additional coverage-only tests for <see cref="RaftNode"/> targeting the last
    /// remaining uncovered lines after <c>RaftNodeTests</c>/<c>RaftNodeMoreTests</c>:
    /// - SendHeartbeat's not-leader early return with a *real* (non-null) heartbeat Timer
    ///   (already-leader node that stepped down, mirroring the production race where the
    ///   timer fires once more right after BecomeFollower before anyone disposes it),
    /// - ShouldBecomeLeaderBasedOnNetworkQuality's catch block when knownNodes.Count != 1
    ///   (falls through the inner `if (Count == 1)` without returning, reaching the
    ///   catch block's closing brace before the outer method-level fallback).
    /// </summary>
    [Collection("ClusterStaticState")]
    public class RaftNodeCovTests : StaticStateGuard
    {
        private readonly FakeClusterTransport _transport = new FakeClusterTransport();
        private readonly List<RaftNode> _nodes = new List<RaftNode>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        private RaftNode CreateNode(string nodeId, IClusterTransport transport = null)
        {
            var node = new RaftNode(NullLogger<RaftNode>.Instance, transport ?? _transport, nodeId);
            _nodes.Add(node);
            return node;
        }

        public override void Dispose()
        {
            foreach (var node in _nodes)
            {
                try { node.StopAsync().GetAwaiter().GetResult(); } catch { }
            }
            foreach (var d in _disposables)
            {
                try { d.Dispose(); } catch { }
            }
            base.Dispose();
        }

        private static object Invoke(RaftNode node, string method, params object[] args)
        {
            var mi = typeof(RaftNode).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(mi);
            return mi.Invoke(node, args);
        }

        private static T GetField<T>(RaftNode node, string field)
        {
            var fi = typeof(RaftNode).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(fi);
            return (T)fi.GetValue(node);
        }

        private static Task<bool> ShouldLead(RaftNode node, List<string> knownNodes)
            => (Task<bool>)Invoke(node, "ShouldBecomeLeaderBasedOnNetworkQuality", knownNodes);

        private static void TriggerElection(RaftNode node) => Invoke(node, "BecomeCandidate");

        [Fact]
        public async Task SendHeartbeat_AfterSteppingDownFromLeader_RealTimer_DisposesAndReturns()
        {
            var node = CreateNode("nodeA");
            TriggerElection(node);
            Assert.True(await Wait.UntilAsync(() => node.IsLeader(), 3000));

            // Real, non-null heartbeat Timer was created by BecomeLeader.
            var timerBefore = GetField<Timer>(node, "_heartbeatTimer");
            Assert.NotNull(timerBefore);

            // Step down without touching _heartbeatTimer (BecomeFollower does not dispose it),
            // mirroring the production race where the timer can fire once more right after
            // the state flips to Follower.
            Invoke(node, "BecomeFollower", (long)(node.CurrentTerm + 1));
            Assert.Equal(RaftNodeState.Follower, node.State);

            var broadcastsBefore = _transport.Broadcasts.Count;

            // Directly invoke the timer callback as the Timer itself would.
            Invoke(node, "SendHeartbeat", new object[] { null });

            // No *new* broadcast should have been sent for this manual invocation while a
            // Follower (the leader-election heartbeat that fired earlier, before step-down,
            // is unrelated and may already be present).
            Assert.Equal(broadcastsBefore, _transport.Broadcasts.Count);
        }

        [Fact]
        public async Task ShouldBecomeLeader_WsTransport_MultipleKnownNodes_QualityThrows_HitsCatchThenFallback()
        {
            var wsTransport = new WebSocketClusterTransport(NullLogger<WebSocketClusterTransport>.Instance, "nodeA");
            _disposables.Add(wsTransport);
            var node = CreateNode("nodeA", wsTransport);

            // knownNodes.Count == 2 (!= 1) so the inner "if (Count == 1)" block is skipped
            // entirely; the leading null makes the quality-measurement loop throw
            // (ConcurrentDictionary.TryGetValue(null,...) inside IsNodeConnected), which is
            // caught by the outer catch. Since Count != 1 the catch's own `if (Count==1)`
            // guard is false, so execution falls through its closing brace (line 1015) into
            // the method-level fallback below, ultimately returning false (Count != 1 there too).
            var result = await ShouldLead(node, new List<string> { null, "nodeC" });

            Assert.False(result);
        }
    }
}
