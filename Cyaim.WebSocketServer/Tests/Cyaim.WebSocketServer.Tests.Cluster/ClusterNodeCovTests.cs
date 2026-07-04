using Cyaim.WebSocketServer.Infrastructure.Cluster;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    /// <summary>
    /// Coverage-only tests for the trivial auto-property getters/setters on
    /// <see cref="ClusterNode"/> that are otherwise never exercised (IsConnected,
    /// LastHeartbeat, State, CurrentTerm, LogLength).
    /// </summary>
    public class ClusterNodeCovTests
    {
        [Fact]
        public void AllProperties_CanBeSetAndRead()
        {
            var now = DateTime.UtcNow;
            var node = new ClusterNode
            {
                NodeId = "nodeX",
                Address = "10.0.0.1",
                Port = 1234,
                IsConnected = true,
                LastHeartbeat = now,
                State = RaftNodeState.Leader,
                CurrentTerm = 7,
                LogLength = 42,
                Protocol = "redis",
                TransportConfig = "cfg"
            };

            Assert.True(node.IsConnected);
            Assert.Equal(now, node.LastHeartbeat);
            Assert.Equal(RaftNodeState.Leader, node.State);
            Assert.Equal(7, node.CurrentTerm);
            Assert.Equal(42, node.LogLength);
            Assert.Equal("redis://10.0.0.1:1234", node.FullAddress);
        }

        [Fact]
        public void Defaults_AreFalseZeroFollowerWs()
        {
            var node = new ClusterNode { NodeId = "nodeY", Address = "127.0.0.1", Port = 1 };

            Assert.False(node.IsConnected);
            Assert.Equal(default(DateTime), node.LastHeartbeat);
            Assert.Equal(RaftNodeState.Follower, node.State);
            Assert.Equal(0, node.CurrentTerm);
            Assert.Equal(0, node.LogLength);
            Assert.Equal("ws", node.Protocol);
            Assert.Equal("ws://127.0.0.1:1", node.FullAddress);
        }
    }
}
