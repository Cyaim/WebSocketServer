using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Cluster.Transports;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    [Collection("ClusterStaticState")]
    public class RaftNodeMoreTests : StaticStateGuard
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

        private static void SetField(RaftNode node, string field, object value)
        {
            var fi = typeof(RaftNode).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(fi);
            fi.SetValue(node, value);
        }

        private static T GetField<T>(RaftNode node, string field)
        {
            var fi = typeof(RaftNode).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(fi);
            return (T)fi.GetValue(node);
        }

        private static void TriggerElection(RaftNode node) => Invoke(node, "BecomeCandidate");

        private static ClusterMessage VoteResponse(string fromNodeId, long term, bool granted, string payloadOverride = null)
        {
            return new ClusterMessage
            {
                Type = ClusterMessageType.RequestVoteResponse,
                FromNodeId = fromNodeId,
                Payload = payloadOverride ?? JsonSerializer.Serialize(new RequestVoteResponseMessage
                {
                    Term = term,
                    VoteGranted = granted
                })
            };
        }

        private static ClusterMessage AppendEntriesResponse(string fromNodeId, long term, bool success, long lastLogIndex, string payloadOverride = null)
        {
            return new ClusterMessage
            {
                Type = ClusterMessageType.AppendEntriesResponse,
                FromNodeId = fromNodeId,
                Payload = payloadOverride ?? JsonSerializer.Serialize(new AppendEntriesResponseMessage
                {
                    Term = term,
                    Success = success,
                    LastLogIndex = lastLogIndex
                })
            };
        }

        // ---------------------------------------------------------------
        // Election timer callback (fired directly via Timer.Change)
        // ---------------------------------------------------------------

        [Fact]
        public async Task ElectionTimer_Expired_BecomesCandidate_AndLeaderInSingleNodeCluster()
        {
            var node = CreateNode("nodeA");
            SetField(node, "_lastHeartbeatTime", DateTime.UtcNow.AddSeconds(-30));
            Invoke(node, "ResetElectionTimer");

            // Fire the timer callback immediately instead of waiting for the timeout
            // 立即触发定时器回调，而不是等待超时
            var timer = GetField<Timer>(node, "_electionTimer");
            timer.Change(0, Timeout.Infinite);

            Assert.True(await Wait.UntilAsync(() => node.IsLeader(), 3000));
            Assert.Equal(1, node.CurrentTerm);
        }

        [Fact]
        public async Task ElectionTimer_HeartbeatStillFresh_DoesNothing()
        {
            var node = CreateNode("nodeA");
            SetField(node, "_lastHeartbeatTime", DateTime.UtcNow);
            Invoke(node, "ResetElectionTimer");

            var timer = GetField<Timer>(node, "_electionTimer");
            timer.Change(0, Timeout.Infinite);

            await Task.Delay(150);
            Assert.Equal(RaftNodeState.Follower, node.State);
            Assert.Equal(0, node.CurrentTerm);
        }

        // ---------------------------------------------------------------
        // Heartbeats
        // ---------------------------------------------------------------

        [Fact]
        public void SendHeartbeat_WhenNotLeader_DisposesTimerAndSendsNothing()
        {
            var node = CreateNode("nodeA");

            Invoke(node, "SendHeartbeat", new object[] { null });

            Assert.Empty(_transport.Broadcasts);
        }

        [Fact]
        public async Task Heartbeat_BroadcastFailure_IsCaughtAndLeadershipKept()
        {
            var transport = new ConfigurableClusterTransport
            {
                BroadcastAsyncImpl = _ => throw new InvalidOperationException("broadcast down")
            };
            var node = CreateNode("nodeA", transport);

            TriggerElection(node);

            Assert.True(await Wait.UntilAsync(() => node.IsLeader(), 3000));
            Assert.True(await Wait.UntilAsync(() => transport.Broadcasts.Count >= 1, 3000));
            await Task.Delay(100);
            Assert.True(node.IsLeader());
        }

        // ---------------------------------------------------------------
        // Vote responses
        // ---------------------------------------------------------------

        [Fact]
        public async Task VoteResponse_WithHigherTerm_MatchedByNodePrefix_TriggersStepDown()
        {
            GlobalClusterCenter.ClusterContext = new ClusterOption { Nodes = new[] { "nodeB@127.0.0.1:1" } };
            var node = CreateNode("nodeA");

            _transport.OnSendAsync = (targetNodeId, message) =>
            {
                if (message.Type != ClusterMessageType.RequestVote)
                {
                    return;
                }
                // Denial carries the responder's higher term -> pending key falls back to prefix match
                // 拒票携带响应方更高的任期 -> 挂起键回退到前缀匹配
                _ = Task.Run(async () =>
                {
                    await Task.Delay(20);
                    _transport.RaiseMessageReceived(VoteResponse("nodeB", term: 99, granted: false), "nodeB");
                });
            };

            TriggerElection(node);

            Assert.True(await Wait.UntilAsync(() => node.CurrentTerm == 99 && node.State == RaftNodeState.Follower, 3000));
            Assert.False(node.IsLeader());
        }

        [Fact]
        public void VoteResponse_NoMatchingPendingRequest_IsIgnored()
        {
            var node = CreateNode("nodeA");
            var pending = GetField<ConcurrentDictionary<string, TaskCompletionSource<ClusterMessage>>>(node, "_pendingVoteRequests");
            var tcs = new TaskCompletionSource<ClusterMessage>();
            pending.TryAdd("nodeX:1", tcs);

            _transport.RaiseMessageReceived(VoteResponse("nodeC", term: 1, granted: true), "nodeC");

            // The unrelated pending request stays untouched / 无关的挂起请求保持不变
            Assert.True(pending.ContainsKey("nodeX:1"));
            Assert.False(tcs.Task.IsCompleted);
        }

        [Fact]
        public void VoteResponse_MalformedPayload_IsCaught()
        {
            var node = CreateNode("nodeA");

            _transport.RaiseMessageReceived(VoteResponse("nodeB", 0, false, payloadOverride: "{bad-json"), "nodeB");

            Assert.Equal(RaftNodeState.Follower, node.State);
        }

        [Fact]
        public async Task Election_GarbageVoteResponse_IsCaught_ThenTieBreakerElectsLeader()
        {
            GlobalClusterCenter.ClusterContext = new ClusterOption { Nodes = new[] { "nodeB@127.0.0.1:1" } };
            var node = CreateNode("aaa"); // sorts before "nodeB" -> wins the tie-break / 排序在 nodeB 之前 -> 赢得平局决胜

            _transport.OnSendAsync = (targetNodeId, message) =>
            {
                if (message.Type != ClusterMessageType.RequestVote)
                {
                    return;
                }
                // Complete the pending request with a garbage payload so response parsing throws
                // 用垃圾负载完成挂起请求，使响应解析抛出异常
                var pending = GetField<ConcurrentDictionary<string, TaskCompletionSource<ClusterMessage>>>(node, "_pendingVoteRequests");
                foreach (var kvp in pending)
                {
                    kvp.Value.TrySetResult(new ClusterMessage { Payload = "garbage-not-json" });
                }
            };

            TriggerElection(node);

            Assert.True(await Wait.UntilAsync(() => node.IsLeader(), 5000));
        }

        [Fact]
        public async Task TwoNodeElection_StateChangesDuringSelectionDelay_Aborts()
        {
            GlobalClusterCenter.ClusterContext = new ClusterOption { Nodes = new[] { "nodeB@127.0.0.1:1" } };
            var node = CreateNode("aaa");

            _transport.OnSendAsync = (targetNodeId, message) =>
            {
                if (message.Type != ClusterMessageType.RequestVote)
                {
                    return;
                }
                var request = JsonSerializer.Deserialize<RequestVoteMessage>(message.Payload);
                // Deny the vote immediately / 立即拒票
                _transport.RaiseMessageReceived(VoteResponse("nodeB", request.Term, granted: false), "nodeB");
                // Then deliver a heartbeat from the other node during the 100-300ms selection delay
                // 然后在 100-300ms 的选主延迟期间投递来自另一节点的心跳
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    _transport.RaiseMessageReceived(new ClusterMessage
                    {
                        Type = ClusterMessageType.AppendEntries,
                        FromNodeId = "nodeB",
                        Payload = JsonSerializer.Serialize(new AppendEntriesMessage
                        {
                            Term = request.Term,
                            LeaderId = "nodeB",
                            PrevLogIndex = 0,
                            PrevLogTerm = 0,
                            Entries = Array.Empty<RaftLogEntry>(),
                            LeaderCommit = 0
                        })
                    }, "nodeB");
                });
            };

            TriggerElection(node);

            Assert.True(await Wait.UntilAsync(() => node.State == RaftNodeState.Follower && node.LeaderId == "nodeB", 3000));
            await Task.Delay(400); // selection delay elapses and aborts / 选主延迟结束并中止
            Assert.False(node.IsLeader());
            Assert.Equal("nodeB", node.LeaderId);
        }

        [Fact]
        public async Task ThreeNodeElection_DeniedVotes_StaysCandidateForRetry()
        {
            GlobalClusterCenter.ClusterContext = new ClusterOption { Nodes = new[] { "nodeB@127.0.0.1:1", "nodeC@127.0.0.1:2" } };
            var node = CreateNode("aaa");

            _transport.OnSendAsync = (targetNodeId, message) =>
            {
                if (message.Type != ClusterMessageType.RequestVote)
                {
                    return;
                }
                var request = JsonSerializer.Deserialize<RequestVoteMessage>(message.Payload);
                _transport.RaiseMessageReceived(VoteResponse(targetNodeId, request.Term, granted: false), targetNodeId);
            };

            TriggerElection(node);

            Assert.True(await Wait.UntilAsync(() =>
                _transport.Sent.Count(s => s.Message.Type == ClusterMessageType.RequestVote) == 2, 3000));
            await Task.Delay(200);
            Assert.Equal(RaftNodeState.Candidate, node.State);
            Assert.False(node.IsLeader());
        }

        [Fact]
        public async Task WsTransport_Election_UnconnectedPeer_SendFails_TieBreakerElectsLeader()
        {
            var wsTransport = new WebSocketClusterTransport(NullLogger<WebSocketClusterTransport>.Instance, "aaa");
            _disposables.Add(wsTransport);
            GlobalClusterCenter.ClusterContext = new ClusterOption { Nodes = new[] { "ws://127.0.0.1:1/zzz" } };
            var node = CreateNode("aaa", wsTransport);

            TriggerElection(node);

            // Vote request send fails (node not registered), then the 2-node network-quality
            // tie-break (all qualities 0) elects the lexicographically smaller node id.
            // 投票请求发送失败（节点未注册），随后 2 节点网络质量平局决胜（质量均为 0）
            // 选出字典序较小的节点 ID 作为领导者。
            Assert.True(await Wait.UntilAsync(() => node.IsLeader(), 5000));
        }

        // ---------------------------------------------------------------
        // HandleRequestVote response-send failures
        // ---------------------------------------------------------------

        [Fact]
        public async Task HandleRequestVote_SendThrowsSynchronously_IsCaught()
        {
            var transport = new ConfigurableClusterTransport
            {
                SendAsyncImpl = (_, __) => throw new InvalidOperationException("send down")
            };
            var node = CreateNode("nodeA", transport);

            transport.RaiseMessageReceived(new ClusterMessage
            {
                Type = ClusterMessageType.RequestVote,
                FromNodeId = "nodeB",
                Payload = JsonSerializer.Serialize(new RequestVoteMessage { Term = 1, CandidateId = "nodeB" })
            }, "nodeB");

            await Task.Delay(50);
            Assert.Equal("nodeB", node.VotedFor); // vote recorded before send failed / 发送失败前投票已记录
            Assert.Equal(1, node.CurrentTerm);
        }

        [Fact]
        public async Task HandleRequestVote_SendReturnsFaultedTask_LogsFault()
        {
            var transport = new ConfigurableClusterTransport
            {
                SendAsyncImpl = (_, __) => Task.FromException(new InvalidOperationException("async send down"))
            };
            var node = CreateNode("nodeA", transport);

            transport.RaiseMessageReceived(new ClusterMessage
            {
                Type = ClusterMessageType.RequestVote,
                FromNodeId = "nodeB",
                Payload = JsonSerializer.Serialize(new RequestVoteMessage { Term = 1, CandidateId = "nodeB" })
            }, "nodeB");

            Assert.True(await Wait.UntilAsync(() => node.VotedFor == "nodeB", 2000));
            await Task.Delay(100); // let the ContinueWith fault handler run / 让 ContinueWith 故障处理器运行
        }

        // ---------------------------------------------------------------
        // AppendEntries edge cases
        // ---------------------------------------------------------------

        [Fact]
        public async Task AppendEntries_ConflictingEntries_AreReplaced()
        {
            var node = CreateNode("nodeA");

            ClusterMessage Entries(long commit, params RaftLogEntry[] entries) => new ClusterMessage
            {
                Type = ClusterMessageType.AppendEntries,
                FromNodeId = "nodeB",
                Payload = JsonSerializer.Serialize(new AppendEntriesMessage
                {
                    Term = 1,
                    LeaderId = "nodeB",
                    PrevLogIndex = 0,
                    PrevLogTerm = 0,
                    Entries = entries,
                    LeaderCommit = commit
                })
            };

            _transport.RaiseMessageReceived(Entries(0, new RaftLogEntry { Index = 1, Term = 1, Command = "old" }), "nodeB");
            Assert.True(await Wait.UntilAsync(() => node.Log.Count == 1, 2000));

            // PrevLogIndex(0) < Log.Count(1) -> conflicting suffix removed then replaced
            // PrevLogIndex(0) < Log.Count(1) -> 冲突的后缀被移除并替换
            _transport.RaiseMessageReceived(Entries(2,
                new RaftLogEntry { Index = 1, Term = 1, Command = "new1" },
                new RaftLogEntry { Index = 2, Term = 1, Command = "new2" }), "nodeB");

            Assert.True(await Wait.UntilAsync(() => node.Log.Count == 2 && node.CommitIndex == 2, 2000));
            Assert.Equal("new1", node.Log[0].Command);
        }

        [Fact]
        public async Task AppendEntries_MalformedPayload_IsCaught()
        {
            var node = CreateNode("nodeA");

            _transport.RaiseMessageReceived(new ClusterMessage
            {
                Type = ClusterMessageType.AppendEntries,
                FromNodeId = "nodeB",
                Payload = "{bad-json"
            }, "nodeB");

            await Task.Delay(50);
            Assert.Equal(0, node.CurrentTerm);
        }

        // ---------------------------------------------------------------
        // HandleAppendEntriesResponse (leader-side)
        // ---------------------------------------------------------------

        private async Task<RaftNode> CreateLeaderAsync()
        {
            var node = CreateNode("nodeA");
            TriggerElection(node);
            Assert.True(await Wait.UntilAsync(() => node.IsLeader(), 3000));
            return node;
        }

        [Fact]
        public async Task AppendEntriesResponse_WhenNotLeader_IsIgnored()
        {
            var node = CreateNode("nodeA");

            _transport.RaiseMessageReceived(AppendEntriesResponse("nodeB", 1, true, 5), "nodeB");

            await Task.Delay(50);
            Assert.Equal(RaftNodeState.Follower, node.State);
            Assert.Equal(0, node.CommitIndex);
        }

        [Fact]
        public async Task AppendEntriesResponse_MajorityReplication_AdvancesCommitIndex()
        {
            var node = await CreateLeaderAsync();
            node.Log.Add(new RaftLogEntry { Index = 1, Term = node.CurrentTerm, Command = "set" });

            _transport.RaiseMessageReceived(AppendEntriesResponse("nodeB", node.CurrentTerm, true, 1), "nodeB");
            _transport.RaiseMessageReceived(AppendEntriesResponse("nodeC", node.CurrentTerm, true, 1), "nodeC");

            Assert.True(await Wait.UntilAsync(() => node.CommitIndex == 1, 2000));
        }

        [Fact]
        public async Task AppendEntriesResponse_Failure_DecrementsNextIndex()
        {
            var node = await CreateLeaderAsync();

            // Success first establishes nextIndex for nodeB / 成功响应先建立 nodeB 的 nextIndex
            _transport.RaiseMessageReceived(AppendEntriesResponse("nodeB", node.CurrentTerm, true, 3), "nodeB");
            var nextIndex = GetField<ConcurrentDictionary<string, long>>(node, "_nextIndex");
            Assert.True(await Wait.UntilAsync(() => nextIndex.TryGetValue("nodeB", out var v) && v == 4, 2000));

            _transport.RaiseMessageReceived(AppendEntriesResponse("nodeB", node.CurrentTerm, false, 0), "nodeB");

            Assert.True(await Wait.UntilAsync(() => nextIndex.TryGetValue("nodeB", out var v) && v == 3, 2000));

            // Failure from an unknown node is a no-op / 未知节点的失败响应是空操作
            _transport.RaiseMessageReceived(AppendEntriesResponse("ghost", node.CurrentTerm, false, 0), "ghost");
            await Task.Delay(50);
            Assert.False(nextIndex.ContainsKey("ghost"));
        }

        [Fact]
        public async Task AppendEntriesResponse_HigherTerm_StepsDownToFollower()
        {
            var node = await CreateLeaderAsync();

            _transport.RaiseMessageReceived(AppendEntriesResponse("nodeB", node.CurrentTerm + 5, false, 0), "nodeB");

            Assert.True(await Wait.UntilAsync(() => node.State == RaftNodeState.Follower, 2000));
        }

        [Fact]
        public async Task AppendEntriesResponse_MalformedPayload_IsCaught()
        {
            var node = await CreateLeaderAsync();

            _transport.RaiseMessageReceived(AppendEntriesResponse("nodeB", 0, false, 0, payloadOverride: "{bad-json"), "nodeB");

            await Task.Delay(50);
            Assert.True(node.IsLeader());
        }

        // ---------------------------------------------------------------
        // GetKnownNodeIds
        // ---------------------------------------------------------------

        private static List<string> KnownNodeIds(RaftNode node)
            => (List<string>)Invoke(node, "GetKnownNodeIds");

        [Fact]
        public void GetKnownNodeIds_WsTransport_ParsesUrisFallbacksAndAlternativeFormats()
        {
            var wsTransport = new WebSocketClusterTransport(NullLogger<WebSocketClusterTransport>.Instance, "nodeA");
            _disposables.Add(wsTransport);
            var node = CreateNode("nodeA", wsTransport);

            GlobalClusterCenter.ClusterContext = new ClusterOption
            {
                Nodes = new[]
                {
                    "ws://127.0.0.1:5001/nodeB",  // path node id / 路径节点 ID
                    "ws://127.0.0.1:5002/",       // empty path -> host:port fallback / 空路径 -> host:port 回退
                    "bad@1.2.3.4:5",              // invalid URI -> alternative id@address format / 非法 URI -> id@address 备用格式
                    "ws://127.0.0.1:5003/nodeA",  // own id in path -> host:port fallback / 路径为自身 ID -> host:port 回退
                    "%%%"                          // invalid URI without '@' -> skipped / 无 '@' 的非法 URI -> 跳过
                }
            };

            var ids = KnownNodeIds(node);

            Assert.Contains("nodeB", ids);
            Assert.Contains("127.0.0.1:5002", ids);
            Assert.Contains("bad", ids);
            Assert.Contains("127.0.0.1:5003", ids);
            Assert.Equal(4, ids.Count);
        }

        [Fact]
        public void GetKnownNodeIds_WsTransport_NullContext_ReturnsEmpty()
        {
            var wsTransport = new WebSocketClusterTransport(NullLogger<WebSocketClusterTransport>.Instance, "nodeA");
            _disposables.Add(wsTransport);
            var node = CreateNode("nodeA", wsTransport);
            GlobalClusterCenter.ClusterContext = null;

            Assert.Empty(KnownNodeIds(node));
        }

        [Fact]
        public void GetKnownNodeIds_HybridTransport_UsesPublicMethod()
        {
            var transport = new HybridWithMethod.HybridClusterTransport
            {
                KnownNodes = new List<string> { "nodeB", "", "nodeA" }
            };
            var node = CreateNode("nodeA", transport);

            var ids = KnownNodeIds(node);

            Assert.Equal(new[] { "nodeB" }, ids);
        }

        [Fact]
        public void GetKnownNodeIds_HybridTransport_FallsBackToReflectionField()
        {
            var transport = new HybridWithField.HybridClusterTransport();
            transport.AddKnownNode("nodeB");
            transport.AddKnownNode("nodeA"); // self, filtered / 自身，被过滤
            var node = CreateNode("nodeA", transport);

            var ids = KnownNodeIds(node);

            Assert.Equal(new[] { "nodeB" }, ids);
        }

        [Fact]
        public void GetKnownNodeIds_HybridTransport_MethodThrows_FallsBackToConfiguration()
        {
            var transport = new HybridThrowing.HybridClusterTransport();
            var node = CreateNode("nodeA", transport);
            GlobalClusterCenter.ClusterContext = new ClusterOption { Nodes = new[] { "nodeB@127.0.0.1:1", "nodeA@127.0.0.1:2" } };

            var ids = KnownNodeIds(node);

            Assert.Equal(new[] { "nodeB" }, ids);
        }

        [Fact]
        public void GetKnownNodeIds_HybridTransport_NoDiscovery_FallsBackToConfiguration()
        {
            var transport = new HybridBare.HybridClusterTransport();
            var node = CreateNode("nodeA", transport);
            GlobalClusterCenter.ClusterContext = new ClusterOption { Nodes = new[] { "nodeC@127.0.0.1:1" } };

            var ids = KnownNodeIds(node);

            Assert.Equal(new[] { "nodeC" }, ids);
        }

        // ---------------------------------------------------------------
        // ShouldBecomeLeaderBasedOnNetworkQuality
        // ---------------------------------------------------------------

        private static Task<bool> ShouldLead(RaftNode node, List<string> knownNodes)
            => (Task<bool>)Invoke(node, "ShouldBecomeLeaderBasedOnNetworkQuality", knownNodes);

        [Fact]
        public async Task ShouldBecomeLeader_NoKnownNodes_ReturnsTrue()
        {
            var node = CreateNode("nodeA");
            Assert.True(await ShouldLead(node, new List<string>()));
        }

        [Fact]
        public async Task ShouldBecomeLeader_WsTransport_QualityMeasurementThrows_FallsBackToNodeIdComparison()
        {
            var wsTransport = new WebSocketClusterTransport(NullLogger<WebSocketClusterTransport>.Instance, "nodeA");
            _disposables.Add(wsTransport);
            var node = CreateNode("nodeA", wsTransport);

            // A null node id makes the connection lookup throw inside the quality loop
            // 空节点 ID 使质量测量循环中的连接查找抛出异常
            var result = await ShouldLead(node, new List<string> { null });

            // Fallback: string.Compare("nodeA", null) > 0 -> false / 回退比较 -> false
            Assert.False(result);
        }

        [Fact]
        public async Task ShouldBecomeLeader_WsTransport_MoreThanOneKnownNode_ReturnsFalse()
        {
            var wsTransport = new WebSocketClusterTransport(NullLogger<WebSocketClusterTransport>.Instance, "nodeA");
            _disposables.Add(wsTransport);
            var node = CreateNode("nodeA", wsTransport);

            Assert.False(await ShouldLead(node, new List<string> { "nodeB", "nodeC" }));
        }

        [Fact]
        public async Task ShouldBecomeLeader_NonWsTransport_UsesNodeIdComparison()
        {
            var node = CreateNode("aaa");
            Assert.True(await ShouldLead(node, new List<string> { "nodeB" }));

            var node2 = CreateNode("zzz");
            Assert.False(await ShouldLead(node2, new List<string> { "nodeB" }));
        }
    }
}
