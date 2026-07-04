using System.Reflection;
using System.Text.Json;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    [Collection("ClusterStaticState")]
    public class RaftNodeTests : StaticStateGuard
    {
        private readonly FakeClusterTransport _transport = new FakeClusterTransport();
        private readonly List<RaftNode> _nodes = new List<RaftNode>();

        private RaftNode CreateNode(string nodeId, FakeClusterTransport transport = null)
        {
            var node = new RaftNode(NullLogger<RaftNode>.Instance, transport ?? _transport, nodeId);
            _nodes.Add(node);
            return node;
        }

        public override void Dispose()
        {
            foreach (var node in _nodes)
            {
                try
                {
                    node.StopAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    // best effort cleanup
                }
            }
            base.Dispose();
        }

        private static void TriggerElection(RaftNode node)
        {
            var method = typeof(RaftNode).GetMethod("BecomeCandidate", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(node, null);
        }

        private static ClusterMessage VoteRequest(long term, string candidateId, long lastLogIndex = 0, long lastLogTerm = 0)
        {
            return new ClusterMessage
            {
                Type = ClusterMessageType.RequestVote,
                FromNodeId = candidateId,
                Payload = JsonSerializer.Serialize(new RequestVoteMessage
                {
                    Term = term,
                    CandidateId = candidateId,
                    LastLogIndex = lastLogIndex,
                    LastLogTerm = lastLogTerm
                })
            };
        }

        private static ClusterMessage AppendEntries(long term, string leaderId)
        {
            return new ClusterMessage
            {
                Type = ClusterMessageType.AppendEntries,
                FromNodeId = leaderId,
                Payload = JsonSerializer.Serialize(new AppendEntriesMessage
                {
                    Term = term,
                    LeaderId = leaderId,
                    PrevLogIndex = 0,
                    PrevLogTerm = 0,
                    Entries = Array.Empty<RaftLogEntry>(),
                    LeaderCommit = 0
                })
            };
        }

        private RequestVoteResponseMessage LastVoteResponseTo(string nodeId)
        {
            var sent = _transport.Sent.Last(s => s.NodeId == nodeId && s.Message.Type == ClusterMessageType.RequestVoteResponse);
            return JsonSerializer.Deserialize<RequestVoteResponseMessage>(sent.Message.Payload);
        }

        // ---------------------------------------------------------------
        // Construction / initial state
        // ---------------------------------------------------------------

        [Fact]
        public void Constructor_NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => new RaftNode(null, _transport, "n"));
            Assert.Throws<ArgumentNullException>(() => new RaftNode(NullLogger<RaftNode>.Instance, null, "n"));
            Assert.Throws<ArgumentNullException>(() => new RaftNode(NullLogger<RaftNode>.Instance, _transport, null));
        }

        [Fact]
        public void NewNode_StartsAsFollowerWithTermZero()
        {
            var node = CreateNode("nodeA");

            Assert.Equal(RaftNodeState.Follower, node.State);
            Assert.Equal(0, node.CurrentTerm);
            Assert.Null(node.VotedFor);
            Assert.Null(node.LeaderId);
            Assert.False(node.IsLeader());
            Assert.Empty(node.Log);
        }

        // ---------------------------------------------------------------
        // Single-node cluster: immediate leadership (recent fix)
        // ---------------------------------------------------------------

        [Fact]
        public async Task SingleNodeCluster_ElectionMakesNodeLeaderImmediately()
        {
            GlobalClusterCenter.ClusterContext = null; // no known peers
            var node = CreateNode("nodeA");

            TriggerElection(node);

            Assert.True(await Wait.UntilAsync(() => node.IsLeader(), 2000));
            Assert.Equal(RaftNodeState.Leader, node.State);
            Assert.Equal(1, node.CurrentTerm);
            Assert.Equal("nodeA", node.LeaderId);
            Assert.Equal("nodeA", node.VotedFor);
        }

        [Fact]
        public async Task Leader_SendsHeartbeatBroadcasts()
        {
            var node = CreateNode("nodeA");
            TriggerElection(node);
            Assert.True(await Wait.UntilAsync(() => node.IsLeader(), 2000));

            Assert.True(await Wait.UntilAsync(() =>
                _transport.Broadcasts.Any(m => m.Type == ClusterMessageType.AppendEntries), 2000));

            var heartbeat = _transport.Broadcasts.First(m => m.Type == ClusterMessageType.AppendEntries);
            Assert.Equal($"heartbeat:nodeA:{node.CurrentTerm}", heartbeat.MessageId);
            var payload = JsonSerializer.Deserialize<AppendEntriesMessage>(heartbeat.Payload);
            Assert.Equal("nodeA", payload.LeaderId);
            Assert.Equal(node.CurrentTerm, payload.Term);
        }

        [Fact]
        public void TriggeredElection_IncrementsTerm_EachTime()
        {
            GlobalClusterCenter.ClusterContext = new ClusterOption { Nodes = new[] { "nodeB@127.0.0.1:1" } };
            var node = CreateNode("nodeZ");

            TriggerElection(node);
            Assert.Equal(1, node.CurrentTerm);
            Assert.Equal("nodeZ", node.VotedFor);

            TriggerElection(node);
            Assert.Equal(2, node.CurrentTerm);
        }

        // ---------------------------------------------------------------
        // Vote request handling
        // ---------------------------------------------------------------

        [Fact]
        public async Task VoteRequest_HigherTerm_GrantsVote_AndBecomesFollowerOfThatTerm()
        {
            var node = CreateNode("nodeA");

            _transport.RaiseMessageReceived(VoteRequest(term: 1, candidateId: "nodeB"), "nodeB");

            Assert.True(await Wait.UntilAsync(() =>
                _transport.Sent.Any(s => s.NodeId == "nodeB" && s.Message.Type == ClusterMessageType.RequestVoteResponse)));

            var response = LastVoteResponseTo("nodeB");
            Assert.True(response.VoteGranted);
            Assert.Equal(1, response.Term);
            Assert.Equal(1, node.CurrentTerm);
            Assert.Equal("nodeB", node.VotedFor);
            Assert.Equal(RaftNodeState.Follower, node.State);
        }

        [Fact]
        public async Task VoteRequest_SameTermSecondCandidate_IsDenied()
        {
            var node = CreateNode("nodeA");
            _transport.RaiseMessageReceived(VoteRequest(1, "nodeB"), "nodeB");
            Assert.True(await Wait.UntilAsync(() => node.VotedFor == "nodeB"));

            _transport.RaiseMessageReceived(VoteRequest(1, "nodeC"), "nodeC");

            Assert.True(await Wait.UntilAsync(() =>
                _transport.Sent.Any(s => s.NodeId == "nodeC" && s.Message.Type == ClusterMessageType.RequestVoteResponse)));
            var response = LastVoteResponseTo("nodeC");
            Assert.False(response.VoteGranted);
            Assert.Equal("nodeB", node.VotedFor);
        }

        [Fact]
        public async Task VoteRequest_SameCandidateSameTerm_IsGrantedAgain()
        {
            var node = CreateNode("nodeA");
            _transport.RaiseMessageReceived(VoteRequest(1, "nodeB"), "nodeB");
            Assert.True(await Wait.UntilAsync(() => node.VotedFor == "nodeB"));

            _transport.RaiseMessageReceived(VoteRequest(1, "nodeB"), "nodeB");

            Assert.True(await Wait.UntilAsync(() =>
                _transport.Sent.Count(s => s.NodeId == "nodeB" && s.Message.Type == ClusterMessageType.RequestVoteResponse) == 2));
            var response = LastVoteResponseTo("nodeB");
            Assert.True(response.VoteGranted);
        }

        [Fact]
        public async Task VoteRequest_StaleTerm_IsDenied_AndReportsCurrentTerm()
        {
            var node = CreateNode("nodeA");
            _transport.RaiseMessageReceived(VoteRequest(5, "nodeB"), "nodeB");
            Assert.True(await Wait.UntilAsync(() => node.CurrentTerm == 5));

            _transport.RaiseMessageReceived(VoteRequest(3, "nodeD"), "nodeD");

            Assert.True(await Wait.UntilAsync(() =>
                _transport.Sent.Any(s => s.NodeId == "nodeD" && s.Message.Type == ClusterMessageType.RequestVoteResponse)));
            var response = LastVoteResponseTo("nodeD");
            Assert.False(response.VoteGranted);
            Assert.Equal(5, response.Term);
            Assert.Equal(5, node.CurrentTerm);
        }

        // ---------------------------------------------------------------
        // AppendEntries / heartbeat handling
        // ---------------------------------------------------------------

        [Fact]
        public async Task AppendEntries_FromLeader_BecomesFollowerAndAcknowledges()
        {
            var node = CreateNode("nodeA");

            _transport.RaiseMessageReceived(AppendEntries(2, "nodeB"), "nodeB");

            Assert.True(await Wait.UntilAsync(() =>
                _transport.Sent.Any(s => s.NodeId == "nodeB" && s.Message.Type == ClusterMessageType.AppendEntriesResponse)));

            Assert.Equal(RaftNodeState.Follower, node.State);
            Assert.Equal(2, node.CurrentTerm);
            Assert.Equal("nodeB", node.LeaderId);

            var sent = _transport.Sent.Last(s => s.Message.Type == ClusterMessageType.AppendEntriesResponse);
            var response = JsonSerializer.Deserialize<AppendEntriesResponseMessage>(sent.Message.Payload);
            Assert.True(response.Success);
            Assert.Equal(2, response.Term);
        }

        [Fact]
        public async Task AppendEntries_StaleTerm_IsRejected_WithCurrentTerm()
        {
            var node = CreateNode("nodeA");
            _transport.RaiseMessageReceived(AppendEntries(2, "nodeB"), "nodeB");
            Assert.True(await Wait.UntilAsync(() => node.CurrentTerm == 2));

            _transport.RaiseMessageReceived(AppendEntries(1, "nodeC"), "nodeC");

            Assert.True(await Wait.UntilAsync(() =>
                _transport.Sent.Any(s => s.NodeId == "nodeC" && s.Message.Type == ClusterMessageType.AppendEntriesResponse)));
            var sent = _transport.Sent.Last(s => s.NodeId == "nodeC");
            var response = JsonSerializer.Deserialize<AppendEntriesResponseMessage>(sent.Message.Payload);
            Assert.False(response.Success);
            Assert.Equal(2, response.Term);
            Assert.Equal(2, node.CurrentTerm);
            Assert.Equal("nodeB", node.LeaderId);
        }

        [Fact]
        public async Task AppendEntries_WithEntries_AppendsToLogAndUpdatesCommitIndex()
        {
            var node = CreateNode("nodeA");
            var message = new ClusterMessage
            {
                Type = ClusterMessageType.AppendEntries,
                FromNodeId = "nodeB",
                Payload = JsonSerializer.Serialize(new AppendEntriesMessage
                {
                    Term = 1,
                    LeaderId = "nodeB",
                    PrevLogIndex = 0,
                    PrevLogTerm = 0,
                    Entries = new[]
                    {
                        new RaftLogEntry { Index = 1, Term = 1, Command = "set", Data = "x" }
                    },
                    LeaderCommit = 1
                })
            };

            _transport.RaiseMessageReceived(message, "nodeB");

            Assert.True(await Wait.UntilAsync(() => node.Log.Count == 1));
            Assert.Equal(1, node.CommitIndex);
            Assert.Equal("set", node.Log[0].Command);
        }

        // ---------------------------------------------------------------
        // Two-node elections with scripted responses
        // ---------------------------------------------------------------

        [Fact]
        public async Task TwoNodeElection_VoteGranted_BecomesLeader()
        {
            GlobalClusterCenter.ClusterContext = new ClusterOption { Nodes = new[] { "nodeB@127.0.0.1:9001" } };
            var node = CreateNode("nodeA");

            _transport.OnSendAsync = (targetNodeId, message) =>
            {
                if (message.Type != ClusterMessageType.RequestVote || targetNodeId != "nodeB")
                {
                    return;
                }

                var request = JsonSerializer.Deserialize<RequestVoteMessage>(message.Payload);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(20);
                    _transport.RaiseMessageReceived(new ClusterMessage
                    {
                        Type = ClusterMessageType.RequestVoteResponse,
                        FromNodeId = "nodeB",
                        Payload = JsonSerializer.Serialize(new RequestVoteResponseMessage
                        {
                            Term = request.Term,
                            VoteGranted = true
                        })
                    }, "nodeB");
                });
            };

            TriggerElection(node);

            Assert.True(await Wait.UntilAsync(() => node.IsLeader(), 3000));
            Assert.Equal(1, node.CurrentTerm);
            Assert.Equal("nodeA", node.LeaderId);
            Assert.Contains(_transport.Sent, s => s.NodeId == "nodeB" && s.Message.Type == ClusterMessageType.RequestVote);
        }

        [Fact]
        public async Task TwoNodeElection_VoteDenied_HigherSortingNodeStaysCandidate()
        {
            // Node id "nodeZ" sorts after "nodeB": on a denied vote in a 2-node cluster the
            // tie-break (node id comparison) makes this node wait instead of becoming leader.
            GlobalClusterCenter.ClusterContext = new ClusterOption { Nodes = new[] { "nodeB@127.0.0.1:9001" } };
            var node = CreateNode("nodeZ");

            _transport.OnSendAsync = (targetNodeId, message) =>
            {
                if (message.Type != ClusterMessageType.RequestVote)
                {
                    return;
                }

                var request = JsonSerializer.Deserialize<RequestVoteMessage>(message.Payload);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(20);
                    _transport.RaiseMessageReceived(new ClusterMessage
                    {
                        Type = ClusterMessageType.RequestVoteResponse,
                        FromNodeId = "nodeB",
                        Payload = JsonSerializer.Serialize(new RequestVoteResponseMessage
                        {
                            Term = request.Term,
                            VoteGranted = false
                        })
                    }, "nodeB");
                });
            };

            TriggerElection(node);

            await Task.Delay(1500);
            Assert.False(node.IsLeader());
            Assert.Equal(RaftNodeState.Candidate, node.State);
            Assert.Equal(1, node.CurrentTerm);
            Assert.Equal("nodeZ", node.VotedFor);
        }

        // ---------------------------------------------------------------
        // Start / stop
        // ---------------------------------------------------------------

        [Fact]
        public async Task StartAsync_StartsTransport_StopAsync_StopsIt()
        {
            var node = CreateNode("nodeA");

            await node.StartAsync();
            Assert.Equal(1, _transport.StartCount);
            Assert.Equal(RaftNodeState.Follower, node.State);

            await node.StopAsync();
            Assert.Equal(1, _transport.StopCount);
        }
    }
}
