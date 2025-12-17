using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Infrastructure.Cluster
{
    /// <summary>
    /// Raft consensus algorithm implementation
    /// Raft 一致性算法实现
    /// </summary>
    public class RaftNode
    {
        private readonly ILogger<RaftNode> _logger;
        private readonly IClusterTransport _transport;
        private readonly string _nodeId;
        private readonly CancellationTokenSource _cancellationTokenSource;

        // Raft state / Raft 状态
        /// <summary>
        /// Current term / 当前任期
        /// </summary>
        public long CurrentTerm { get; private set; }
        /// <summary>
        /// Node ID that received vote in current term / 在当前任期收到投票的节点 ID
        /// </summary>
        public string VotedFor { get; private set; }
        /// <summary>
        /// Raft log entries / Raft 日志条目
        /// </summary>
        public List<RaftLogEntry> Log { get; private set; }
        /// <summary>
        /// Index of highest log entry known to be committed / 已知已提交的最高日志条目索引
        /// </summary>
        public long CommitIndex { get; private set; }
        /// <summary>
        /// Index of highest log entry applied to state machine / 已应用到状态机的最高日志条目索引
        /// </summary>
        public long LastApplied { get; private set; }

        // Leader state / 领导者状态
        /// <summary>
        /// For each node, index of next log entry to send / 每个节点的下一个要发送的日志条目索引
        /// </summary>
        private readonly ConcurrentDictionary<string, long> _nextIndex;
        /// <summary>
        /// For each node, index of highest log entry known to be replicated / 每个节点的已知已复制的最高日志条目索引
        /// </summary>
        private readonly ConcurrentDictionary<string, long> _matchIndex;

        // Node state / 节点状态
        /// <summary>
        /// Current node state (Follower, Candidate, Leader) / 当前节点状态（跟随者、候选者、领导者）
        /// </summary>
        public RaftNodeState State { get; private set; }
        /// <summary>
        /// Current leader node ID / 当前领导者节点 ID
        /// </summary>
        public string LeaderId { get; private set; }

        // Election timeout (randomized between min and max) / 选举超时（最小值和最大值之间的随机值）
        /// <summary>
        /// Minimum election timeout in milliseconds / 最小选举超时（毫秒）
        /// </summary>
        private readonly int _electionTimeoutMin = 150;
        /// <summary>
        /// Maximum election timeout in milliseconds / 最大选举超时（毫秒）
        /// </summary>
        private readonly int _electionTimeoutMax = 300;
        /// <summary>
        /// Heartbeat interval in milliseconds / 心跳间隔（毫秒）
        /// 默认 1000ms，避免过于频繁的心跳导致日志和 MQ 压力过大
        /// </summary>
        private readonly int _heartbeatInterval = 1000;

        private DateTime _lastHeartbeatTime;
        private Timer _electionTimer;
        private Timer _heartbeatTimer;
        private readonly object _stateLock = new object();

        // Request-response correlation for vote requests / 投票请求的请求-响应关联
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ClusterMessage>> _pendingVoteRequests = new ConcurrentDictionary<string, TaskCompletionSource<ClusterMessage>>();

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="transport">Cluster transport / 集群传输</param>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        public RaftNode(ILogger<RaftNode> logger, IClusterTransport transport, string nodeId)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
            _cancellationTokenSource = new CancellationTokenSource();

            CurrentTerm = 0;
            VotedFor = null;
            Log = new List<RaftLogEntry>();
            CommitIndex = 0;
            LastApplied = 0;
            State = RaftNodeState.Follower;
            LeaderId = null;

            _nextIndex = new ConcurrentDictionary<string, long>();
            _matchIndex = new ConcurrentDictionary<string, long>();

            _transport.MessageReceived += OnMessageReceived;
            _lastHeartbeatTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Start the Raft node / 启动 Raft 节点
        /// </summary>
        public async Task StartAsync()
        {
            _logger.LogInformation($"Starting Raft node {_nodeId}");
            await _transport.StartAsync();

            // Start as follower / 以跟随者身份启动
            BecomeFollower(CurrentTerm);

            // Start election timer / 启动选举定时器
            ResetElectionTimer();
        }

        /// <summary>
        /// Stop the Raft node / 停止 Raft 节点
        /// </summary>
        public async Task StopAsync()
        {
            _logger.LogInformation($"Stopping Raft node {_nodeId}");
            _cancellationTokenSource.Cancel();

            _electionTimer?.Dispose();
            _heartbeatTimer?.Dispose();

            await _transport.StopAsync();
        }

        /// <summary>
        /// Transition to Follower state / 转换为跟随者状态
        /// </summary>
        /// <param name="term">Term to set / 要设置的任期</param>
        private void BecomeFollower(long term)
        {
            lock (_stateLock)
            {
                var previousState = State;
                var previousTerm = CurrentTerm;

                if (term > CurrentTerm)
                {
                    CurrentTerm = term;
                    VotedFor = null;
                }

                State = RaftNodeState.Follower;
                LeaderId = null;

                // Only log if state actually changed / 只在状态真正改变时记录日志
                if (previousState != RaftNodeState.Follower || previousTerm != CurrentTerm)
                {
                    _logger.LogInformation($"Node {_nodeId} became Follower in term {CurrentTerm}");
                }
                ResetElectionTimer();
            }
        }

        /// <summary>
        /// Transition to Candidate state and start election / 转换为候选者状态并开始选举
        /// </summary>
        private void BecomeCandidate()
        {
            lock (_stateLock)
            {
                CurrentTerm++;
                State = RaftNodeState.Candidate;
                VotedFor = _nodeId;
                _logger.LogInformation($"Node {_nodeId} became Candidate in term {CurrentTerm}");

                // Start election / 开始选举
                _ = StartElectionAsync();
                ResetElectionTimer();
            }
        }

        /// <summary>
        /// Transition to Leader state / 转换为领导者状态
        /// </summary>
        private void BecomeLeader()
        {
            lock (_stateLock)
            {
                State = RaftNodeState.Leader;
                LeaderId = _nodeId;

                // Initialize leader state / 初始化领导者状态
                var lastLogIndex = Log.Count > 0 ? Log.Last().Index : 0;
                foreach (var nodeId in GetKnownNodeIds())
                {
                    _nextIndex[nodeId] = lastLogIndex + 1;
                    _matchIndex[nodeId] = 0;
                }

                _logger.LogInformation($"Node {_nodeId} became Leader in term {CurrentTerm}");
                ResetElectionTimer();

                // Start sending heartbeats / 开始发送心跳
                _heartbeatTimer?.Dispose();
                _heartbeatTimer = new Timer(SendHeartbeat, null, 0, _heartbeatInterval);
            }
        }

        /// <summary>
        /// Start election process / 开始选举过程
        /// </summary>
        private async Task StartElectionAsync()
        {
            var knownNodes = GetKnownNodeIds();
            _logger.LogInformation($"Node {_nodeId} starting election for term {CurrentTerm}. Known nodes: {string.Join(", ", knownNodes)}");

            if (knownNodes.Count == 0)
            {
                // 单节点场景是正常的，降低日志级别避免频繁警告
                // Single node scenario is normal, reduce log level to avoid frequent warnings
                _logger.LogDebug($"No known nodes found, cannot start election. Node will remain as candidate. This is normal in single-node scenarios or when other nodes haven't started yet.");
                return;
            }

            var requestVote = new RequestVoteMessage
            {
                Term = CurrentTerm,
                CandidateId = _nodeId,
                LastLogIndex = Log.Count > 0 ? Log.Last().Index : 0,
                LastLogTerm = Log.Count > 0 ? Log.Last().Term : 0
            };

            var message = new ClusterMessage
            {
                Type = ClusterMessageType.RequestVote,
                Payload = System.Text.Json.JsonSerializer.Serialize(requestVote)
            };

            var votesReceived = 1; // Vote for self / 为自己投票
            var votesNeeded = (knownNodes.Count + 1) / 2 + 1;
            _logger.LogInformation($"Election requires {votesNeeded} votes (including self). Total nodes: {knownNodes.Count + 1}");

            var voteTasks = knownNodes.Select(async nodeId =>
            {
                try
                {
                    // Check if connection is established / 检查连接是否已建立
                    if (_transport is Transports.WebSocketClusterTransport wsTransport)
                    {
                        var isConnected = wsTransport.IsNodeConnected(nodeId);
                        if (!isConnected)
                        {
                            _logger.LogWarning($"Node {nodeId} is not connected, attempting to establish connection before requesting vote");
                        }
                    }

                    _logger.LogDebug($"Requesting vote from node {nodeId}");
                    var responseMessage = await SendRequestVoteAsync(nodeId, message);
                    if (responseMessage != null)
                    {
                        var response = System.Text.Json.JsonSerializer.Deserialize<RequestVoteResponseMessage>(responseMessage.Payload);
                        if (response.VoteGranted && response.Term == CurrentTerm)
                        {
                            _logger.LogInformation($"✓ Received GRANTED vote from node {nodeId}");
                            return 1;
                        }
                        else if (response.Term > CurrentTerm)
                        {
                            _logger.LogInformation($"Node {nodeId} has higher term {response.Term}, becoming follower");
                            BecomeFollower(response.Term);
                        }
                        else
                        {
                            _logger.LogDebug($"Node {nodeId} did not grant vote (term: {response.Term}, granted: {response.VoteGranted})");
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"No response received from node {nodeId} (connection may not be established or request timed out)");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Error requesting vote from node {nodeId}: {ex.Message}");
                }
                return 0;
            });

            var results = await Task.WhenAll(voteTasks);
            votesReceived += results.Sum();

            _logger.LogInformation($"Election result: {votesReceived}/{votesNeeded} votes received");

            var totalNodes = knownNodes.Count + 1;
            var isTwoNodeCluster = totalNodes == 2;

            // Special handling for 2-node clusters or when majority cannot be achieved / 2节点集群或无法达到多数的特殊处理
            if (votesReceived < votesNeeded && State == RaftNodeState.Candidate)
            {
                if (isTwoNodeCluster)
                {
                    _logger.LogInformation($"Two-node cluster detected. Using network quality-based leader selection.");
                    
                    // Add a small random delay to avoid simultaneous elections / 添加小的随机延迟以避免同时选举
                    var randomDelay = new Random().Next(100, 300);
                    await Task.Delay(randomDelay);
                    
                    // Re-check state in case we received a heartbeat from the other node / 重新检查状态，以防我们从另一个节点收到心跳
                    if (State != RaftNodeState.Candidate)
                    {
                        _logger.LogInformation($"Node {_nodeId} state changed during delay, aborting leader selection");
                        return;
                    }
                    
                    // For 2-node clusters, select leader based on network quality / 对于2节点集群，基于网络质量选择Leader
                    var shouldBecomeLeader = await ShouldBecomeLeaderBasedOnNetworkQuality(knownNodes);
                    
                    if (shouldBecomeLeader)
                    {
                        _logger.LogInformation($"Node {_nodeId} selected as leader based on network quality in 2-node cluster");
                        BecomeLeader();
                        return;
                    }
                    else
                    {
                        _logger.LogInformation($"Node {_nodeId} will wait for other node to become leader based on network quality");
                        // Wait a bit longer before retrying / 在重试前等待更长时间
                        await Task.Delay(1000);
                    }
                }
                else
                {
                    _logger.LogInformation($"Node {_nodeId} did not receive enough votes ({votesReceived}/{votesNeeded}), will retry");
                }
            }
            else if (votesReceived >= votesNeeded && State == RaftNodeState.Candidate)
            {
                _logger.LogInformation($"Node {_nodeId} won election with {votesReceived} votes, becoming leader");
                BecomeLeader();
            }
        }

        /// <summary>
        /// Send vote request to node / 向节点发送投票请求
        /// </summary>
        /// <param name="nodeId">Target node ID / 目标节点 ID</param>
        /// <param name="message">Vote request message / 投票请求消息</param>
        /// <returns>Response message or null / 响应消息或 null</returns>
        private async Task<ClusterMessage> SendRequestVoteAsync(string nodeId, ClusterMessage message)
        {
            try
            {
                _logger.LogDebug($"Sending vote request to node {nodeId} for term {CurrentTerm}");

                // Create a task completion source to wait for response / 创建任务完成源以等待响应
                var tcs = new TaskCompletionSource<ClusterMessage>();
                var requestKey = $"{nodeId}:{CurrentTerm}";
                _pendingVoteRequests.TryAdd(requestKey, tcs);

                try
                {
                    await _transport.SendAsync(nodeId, message);

                    // Wait for response with timeout / 等待响应（带超时）
                    using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000)))
                    {
                        cts.Token.Register(() => tcs.TrySetCanceled());
                        var response = await tcs.Task;
                        _pendingVoteRequests.TryRemove(requestKey, out _);
                        _logger.LogDebug($"Received vote response from node {nodeId}");
                        return response;
                    }
                }
                catch (OperationCanceledException)
                {
                    _pendingVoteRequests.TryRemove(requestKey, out _);
                    _logger.LogWarning($"Vote request to node {nodeId} timed out");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to send vote request to {nodeId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Send heartbeat to all followers / 向所有跟随者发送心跳
        /// </summary>
        /// <param name="state">Timer state / 定时器状态</param>
        private void SendHeartbeat(object state)
        {
            if (State != RaftNodeState.Leader)
            {
                _heartbeatTimer?.Dispose();
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var appendEntries = new AppendEntriesMessage
                    {
                        Term = CurrentTerm,
                        LeaderId = _nodeId,
                        PrevLogIndex = 0,
                        PrevLogTerm = 0,
                        Entries = new RaftLogEntry[0],
                        LeaderCommit = CommitIndex
                    };

                    var message = new ClusterMessage
                    {
                        Type = ClusterMessageType.AppendEntries,
                        Payload = System.Text.Json.JsonSerializer.Serialize(appendEntries),
                        // Use a consistent MessageId for heartbeats to enable deduplication / 使用一致的消息ID以便去重
                        MessageId = $"heartbeat:{_nodeId}:{CurrentTerm}"
                    };

                    _logger.LogDebug($"[RaftNode] Sending heartbeat - NodeId: {_nodeId}, Term: {CurrentTerm}, MessageId: {message.MessageId}");
                    await _transport.BroadcastAsync(message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[RaftNode] Error sending heartbeat - NodeId: {_nodeId}");
                }
            });
        }

        /// <summary>
        /// Reset election timer / 重置选举定时器
        /// </summary>
        private void ResetElectionTimer()
        {
            _electionTimer?.Dispose();

            if (State == RaftNodeState.Leader)
            {
                return; // Leaders don't need election timer / 领导者不需要选举定时器
            }

            var timeout = new Random().Next(_electionTimeoutMin, _electionTimeoutMax);
            _electionTimer = new Timer(async _ =>
            {
                if (State != RaftNodeState.Leader &&
                    (DateTime.UtcNow - _lastHeartbeatTime).TotalMilliseconds > timeout)
                {
                    _logger.LogInformation($"Election timeout reached for node {_nodeId}");
                    BecomeCandidate();
                }
            }, null, timeout, Timeout.Infinite);
        }

        /// <summary>
        /// Handle received cluster messages / 处理接收到的集群消息
        /// </summary>
        /// <param name="sender">Event sender / 事件发送者</param>
        /// <param name="e">Message event arguments / 消息事件参数</param>
        private void OnMessageReceived(object sender, ClusterMessageEventArgs e)
        {
            _logger.LogWarning($"[RaftNode] OnMessageReceived 被调用 - NodeId: {_nodeId}, MessageType: {e.Message.Type}, FromNodeId: {e.Message.FromNodeId}, ToNodeId: {e.Message.ToNodeId}, MessageId: {e.Message.MessageId}");
            
            _lastHeartbeatTime = DateTime.UtcNow;

            switch (e.Message.Type)
            {
                case ClusterMessageType.RequestVote:
                    _logger.LogWarning($"[RaftNode] 处理 RequestVote 消息 - NodeId: {_nodeId}, FromNodeId: {e.Message.FromNodeId}");
                    HandleRequestVote(e.Message);
                    break;
                case ClusterMessageType.RequestVoteResponse:
                    _logger.LogWarning($"[RaftNode] 处理 RequestVoteResponse 消息 - NodeId: {_nodeId}, FromNodeId: {e.Message.FromNodeId}");
                    HandleRequestVoteResponse(e.Message);
                    break;
                case ClusterMessageType.AppendEntries:
                case ClusterMessageType.Heartbeat:
                    HandleAppendEntries(e.Message);
                    break;
                case ClusterMessageType.AppendEntriesResponse:
                    HandleAppendEntriesResponse(e.Message);
                    break;
            }
        }

        /// <summary>
        /// Handle request vote message / 处理请求投票消息
        /// </summary>
        /// <param name="message">Vote request message / 投票请求消息</param>
        private void HandleRequestVote(ClusterMessage message)
        {
            try
            {
                _logger.LogWarning($"[RaftNode] HandleRequestVote 开始处理 - NodeId: {_nodeId}, FromNodeId: {message.FromNodeId}, PayloadLength: {message.Payload?.Length ?? 0}");
                
                var request = System.Text.Json.JsonSerializer.Deserialize<RequestVoteMessage>(message.Payload);
                _logger.LogWarning($"[RaftNode] RequestVote 解析成功 - NodeId: {_nodeId}, RequestTerm: {request.Term}, CandidateId: {request.CandidateId}, CurrentTerm: {CurrentTerm}, VotedFor: {VotedFor}");

                bool voteGranted = false;

                if (request.Term > CurrentTerm)
                {
                    _logger.LogWarning($"[RaftNode] 请求的任期更高，成为跟随者 - NodeId: {_nodeId}, RequestTerm: {request.Term}, CurrentTerm: {CurrentTerm}");
                    BecomeFollower(request.Term);
                }

                if (request.Term == CurrentTerm &&
                    (VotedFor == null || VotedFor == request.CandidateId) &&
                    IsLogUpToDate(request.LastLogIndex, request.LastLogTerm))
                {
                    VotedFor = request.CandidateId;
                    voteGranted = true;
                    ResetElectionTimer();
                    _logger.LogWarning($"[RaftNode] 投票授予 - NodeId: {_nodeId}, CandidateId: {request.CandidateId}, Term: {CurrentTerm}");
                }
                else
                {
                    _logger.LogWarning($"[RaftNode] 投票拒绝 - NodeId: {_nodeId}, RequestTerm: {request.Term}, CurrentTerm: {CurrentTerm}, VotedFor: {VotedFor}, IsLogUpToDate: {IsLogUpToDate(request.LastLogIndex, request.LastLogTerm)}");
                }

                var response = new RequestVoteResponseMessage
                {
                    Term = CurrentTerm,
                    VoteGranted = voteGranted
                };

                var responseMessage = new ClusterMessage
                {
                    Type = ClusterMessageType.RequestVoteResponse,
                    Payload = System.Text.Json.JsonSerializer.Serialize(response)
                };

                _logger.LogWarning($"[RaftNode] 发送投票响应 - NodeId: {_nodeId}, ToNodeId: {message.FromNodeId}, VoteGranted: {voteGranted}, Term: {CurrentTerm}");
                _ = _transport.SendAsync(message.FromNodeId, responseMessage).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.LogError(t.Exception, $"[RaftNode] 发送投票响应失败 - NodeId: {_nodeId}, ToNodeId: {message.FromNodeId}");
                    }
                    else
                    {
                        _logger.LogWarning($"[RaftNode] 投票响应发送成功 - NodeId: {_nodeId}, ToNodeId: {message.FromNodeId}, VoteGranted: {voteGranted}");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[RaftNode] 处理请求投票时发生错误 - NodeId: {_nodeId}, FromNodeId: {message.FromNodeId}");
            }
        }

        /// <summary>
        /// Handle request vote response / 处理请求投票响应
        /// </summary>
        /// <param name="message">Vote response message / 投票响应消息</param>
        private void HandleRequestVoteResponse(ClusterMessage message)
        {
            try
            {
                var response = System.Text.Json.JsonSerializer.Deserialize<RequestVoteResponseMessage>(message.Payload);

                // Find and complete the pending request / 查找并完成待处理的请求
                var requestKey = $"{message.FromNodeId}:{response.Term}";
                if (_pendingVoteRequests.TryRemove(requestKey, out var tcs))
                {
                    tcs.TrySetResult(message);
                    _logger.LogDebug($"Completed vote request for node {message.FromNodeId}, term {response.Term}, granted: {response.VoteGranted}");
                }
                else
                {
                    _logger.LogDebug($"Received vote response from node {message.FromNodeId} but no pending request found (may have timed out)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling request vote response");
            }
        }

        /// <summary>
        /// Handle append entries message / 处理追加条目消息
        /// </summary>
        /// <param name="message">Append entries message / 追加条目消息</param>
        private void HandleAppendEntries(ClusterMessage message)
        {
            try
            {
                var request = System.Text.Json.JsonSerializer.Deserialize<AppendEntriesMessage>(message.Payload);

                if (request.Term >= CurrentTerm)
                {
                    BecomeFollower(request.Term);
                    LeaderId = request.LeaderId;
                    ResetElectionTimer();
                }

                bool success = false;

                if (request.Term == CurrentTerm)
                {
                    if (request.PrevLogIndex == 0 ||
                        (request.PrevLogIndex <= Log.Count &&
                         Log[(int)(request.PrevLogIndex - 1)].Term == request.PrevLogTerm))
                    {
                        success = true;

                        // Append new entries (simplified - should handle conflict resolution)
                        // 追加新条目（简化版本 - 应该处理冲突解决）
                        if (request.Entries != null && request.Entries.Length > 0)
                        {
                            // Remove conflicting entries / 删除冲突的条目
                            if (request.PrevLogIndex < Log.Count)
                            {
                                Log.RemoveRange((int)request.PrevLogIndex, Log.Count - (int)request.PrevLogIndex);
                            }

                            // Append new entries / 追加新条目
                            Log.AddRange(request.Entries);
                        }

                        // Update commit index / 更新提交索引
                        if (request.LeaderCommit > CommitIndex)
                        {
                            CommitIndex = Math.Min(request.LeaderCommit, Log.Count > 0 ? Log.Last().Index : 0);
                        }
                    }
                }

                var response = new AppendEntriesResponseMessage
                {
                    Term = CurrentTerm,
                    Success = success,
                    LastLogIndex = Log.Count > 0 ? Log.Last().Index : 0
                };

                var responseMessage = new ClusterMessage
                {
                    Type = ClusterMessageType.AppendEntriesResponse,
                    Payload = System.Text.Json.JsonSerializer.Serialize(response)
                };

                _transport.SendAsync(message.FromNodeId, responseMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling append entries");
            }
        }

        /// <summary>
        /// Handle append entries response / 处理追加条目响应
        /// </summary>
        /// <param name="message">Response message / 响应消息</param>
        private void HandleAppendEntriesResponse(ClusterMessage message)
        {
            if (State != RaftNodeState.Leader)
                return;

            try
            {
                var response = System.Text.Json.JsonSerializer.Deserialize<AppendEntriesResponseMessage>(message.Payload);

                if (response.Term > CurrentTerm)
                {
                    BecomeFollower(response.Term);
                    return;
                }

                if (response.Success)
                {
                    _matchIndex[message.FromNodeId] = response.LastLogIndex;
                    _nextIndex[message.FromNodeId] = response.LastLogIndex + 1;

                    // Update commit index if majority has replicated / 如果多数节点已复制，则更新提交索引
                    var majorityIndex = GetMajorityReplicatedIndex();
                    if (majorityIndex > CommitIndex &&
                        Log.Any() &&
                        Log.Last(e => e.Index == majorityIndex).Term == CurrentTerm)
                    {
                        CommitIndex = majorityIndex;
                    }
                }
                else
                {
                    // Decrement nextIndex and retry / 递减 nextIndex 并重试
                    if (_nextIndex.ContainsKey(message.FromNodeId))
                    {
                        _nextIndex[message.FromNodeId] = Math.Max(1, _nextIndex[message.FromNodeId] - 1);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling append entries response");
            }
        }

        /// <summary>
        /// Check if candidate's log is up to date / 检查候选者的日志是否是最新的
        /// </summary>
        /// <param name="lastLogIndex">Candidate's last log index / 候选者的最后日志索引</param>
        /// <param name="lastLogTerm">Candidate's last log term / 候选者的最后日志任期</param>
        /// <returns>True if up to date / 如果是最新的返回 true</returns>
        private bool IsLogUpToDate(long lastLogIndex, long lastLogTerm)
        {
            var ourLastTerm = Log.Count > 0 ? Log.Last().Term : 0;
            return lastLogTerm > ourLastTerm ||
                   (lastLogTerm == ourLastTerm && lastLogIndex >= (Log.Count > 0 ? Log.Last().Index : 0));
        }

        /// <summary>
        /// Get majority replicated log index / 获取多数节点已复制的日志索引
        /// </summary>
        /// <returns>Majority replicated index / 多数节点已复制的索引</returns>
        private long GetMajorityReplicatedIndex()
        {
            var indices = _matchIndex.Values.OrderByDescending(x => x).ToList();
            var majority = (indices.Count + 1) / 2;
            return majority < indices.Count ? indices[majority] : 0;
        }

        /// <summary>
        /// Get known node IDs from cluster configuration / 从集群配置获取已知节点 ID
        /// </summary>
        /// <returns>List of known node IDs / 已知节点 ID 列表</returns>
        private List<string> GetKnownNodeIds()
        {
            // Get known node IDs from transport / 从传输获取已知节点 ID
            // The transport should maintain a list of registered nodes / 传输应该维护已注册节点的列表
            var nodeIds = new List<string>();

            if (_transport is Transports.WebSocketClusterTransport wsTransport)
            {
                // For WebSocket transport, nodes are registered via RegisterNode
                // We need to access the internal node list
                // This is a simplified approach - in production, you might want to expose this via IClusterTransport
                var clusterOption = GlobalClusterCenter.ClusterContext;
                if (clusterOption?.Nodes != null)
                {
                    _logger.LogDebug($"Parsing {clusterOption.Nodes.Length} node(s) from configuration for node {_nodeId}");
                    foreach (var nodeUrl in clusterOption.Nodes)
                    {
                        // Parse node URL to extract node ID / 解析节点 URL 以提取节点 ID
                        // Format: ws://address:port/nodeId or nodeId@address:port / 格式：ws://address:port/nodeId 或 nodeId@address:port
                        try
                        {
                            var uri = new Uri(nodeUrl);
                            var nodeId = uri.PathAndQuery.TrimStart('/');
                            if (!string.IsNullOrEmpty(nodeId) && nodeId != _nodeId)
                            {
                                nodeIds.Add(nodeId);
                                _logger.LogDebug($"Parsed node ID '{nodeId}' from URL '{nodeUrl}'");
                            }
                            else
                            {
                                // Fallback: use address:port as node ID / 回退：使用 address:port 作为节点 ID
                                var fallbackId = $"{uri.Host}:{uri.Port}";
                                if (fallbackId != _nodeId)
                                {
                                    nodeIds.Add(fallbackId);
                                    _logger.LogDebug($"Using fallback node ID '{fallbackId}' from URL '{nodeUrl}'");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // If URL parsing fails, try to extract node ID from the string
                            // 如果 URL 解析失败，尝试从字符串中提取节点 ID
                            _logger.LogDebug(ex, $"Failed to parse URL '{nodeUrl}', trying alternative format");
                            var parts = nodeUrl.Split('@');
                            if (parts.Length > 1 && parts[0] != _nodeId)
                            {
                                nodeIds.Add(parts[0]);
                                _logger.LogDebug($"Parsed node ID '{parts[0]}' from alternative format '{nodeUrl}'");
                            }
                        }
                    }
                }
                else
                {
                    _logger.LogWarning($"Cluster context or nodes configuration is null for node {_nodeId}");
                }
            }
            else
            {
                // For HybridClusterTransport (Redis + RabbitMQ), nodes are discovered dynamically via Redis
                // 对于 HybridClusterTransport（Redis + RabbitMQ），节点通过 Redis 动态发现
                // Try to get known nodes from HybridClusterTransport via reflection or public method
                // 尝试通过反射或公共方法从 HybridClusterTransport 获取已知节点
                try
                {
                    var transportType = _transport.GetType();
                    if (transportType.Name == "HybridClusterTransport" || transportType.FullName?.Contains("HybridClusterTransport") == true)
                    {
                        _logger.LogWarning($"[RaftNode] Transport is HybridClusterTransport, attempting to get known nodes");
                        
                        // First try public method GetKnownNodeIds / 首先尝试公共方法 GetKnownNodeIds
                        var getKnownNodeIdsMethod = transportType.GetMethod("GetKnownNodeIds", 
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        
                        if (getKnownNodeIdsMethod != null)
                        {
                            var result = getKnownNodeIdsMethod.Invoke(_transport, null);
                            if (result is List<string> knownNodeIds)
                            {
                                foreach (var nodeId in knownNodeIds)
                                {
                                    if (!string.IsNullOrEmpty(nodeId) && nodeId != _nodeId)
                                    {
                                        nodeIds.Add(nodeId);
                                        _logger.LogWarning($"[RaftNode] Found node '{nodeId}' from HybridClusterTransport via GetKnownNodeIds()");
                                    }
                                }
                            }
                        }
                        
                        // Fallback to reflection if method not found / 如果方法未找到，回退到反射
                        if (nodeIds.Count == 0)
                        {
                            _logger.LogWarning($"[RaftNode] GetKnownNodeIds() returned no nodes, trying reflection");
                            var knownNodesField = transportType.GetField("_knownNodes", 
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            
                            if (knownNodesField != null)
                            {
                                var knownNodesDict = knownNodesField.GetValue(_transport);
                                if (knownNodesDict is System.Collections.IDictionary dict)
                                {
                                    foreach (System.Collections.DictionaryEntry entry in dict)
                                    {
                                        var nodeId = entry.Key?.ToString();
                                        if (!string.IsNullOrEmpty(nodeId) && nodeId != _nodeId)
                                        {
                                            nodeIds.Add(nodeId);
                                            _logger.LogWarning($"[RaftNode] Found node '{nodeId}' from HybridClusterTransport via reflection");
                                        }
                                    }
                                }
                            }
                        }
                        
                        // If no nodes found via reflection, fall back to cluster configuration
                        // 如果通过反射没有找到节点，回退到集群配置
                        if (nodeIds.Count == 0)
                        {
                            _logger.LogDebug($"No nodes found in HybridClusterTransport, falling back to cluster configuration");
                            var clusterOption = GlobalClusterCenter.ClusterContext;
                            if (clusterOption?.Nodes != null)
                            {
                                foreach (var nodeConfig in clusterOption.Nodes)
                                {
                                    var parts = nodeConfig.Split('@');
                                    var nodeId = parts.Length > 0 ? parts[0] : nodeConfig;
                                    if (!string.IsNullOrEmpty(nodeId) && nodeId != _nodeId)
                                    {
                                        nodeIds.Add(nodeId);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // For other transports (Redis/RabbitMQ), use cluster configuration
                        // 对于其他传输（Redis/RabbitMQ），使用集群配置
                        _logger.LogDebug($"Transport is not WebSocketClusterTransport or HybridClusterTransport, using cluster configuration");
                        var clusterOption = GlobalClusterCenter.ClusterContext;
                        if (clusterOption?.Nodes != null)
                        {
                            foreach (var nodeConfig in clusterOption.Nodes)
                            {
                                var parts = nodeConfig.Split('@');
                                var nodeId = parts.Length > 0 ? parts[0] : nodeConfig;
                                if (!string.IsNullOrEmpty(nodeId) && nodeId != _nodeId)
                                {
                                    nodeIds.Add(nodeId);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to get known nodes from transport, falling back to cluster configuration");
                    // Fall back to cluster configuration / 回退到集群配置
                    var clusterOption = GlobalClusterCenter.ClusterContext;
                    if (clusterOption?.Nodes != null)
                    {
                        foreach (var nodeConfig in clusterOption.Nodes)
                        {
                            var parts = nodeConfig.Split('@');
                            var nodeId = parts.Length > 0 ? parts[0] : nodeConfig;
                            if (!string.IsNullOrEmpty(nodeId) && nodeId != _nodeId)
                            {
                                nodeIds.Add(nodeId);
                            }
                        }
                    }
                }
            }

            _logger.LogTrace($"GetKnownNodeIds for node {_nodeId} returned {nodeIds.Count} node(s): {string.Join(", ", nodeIds)}");
            return nodeIds;
        }

        /// <summary>
        /// Determine if this node should become leader based on network quality (for 2-node clusters) / 基于网络质量确定此节点是否应成为Leader（用于2节点集群）
        /// </summary>
        /// <param name="knownNodes">List of known node IDs / 已知节点ID列表</param>
        /// <returns>True if this node should become leader / 如果此节点应成为Leader返回 true</returns>
        private async Task<bool> ShouldBecomeLeaderBasedOnNetworkQuality(List<string> knownNodes)
        {
            if (knownNodes.Count == 0)
            {
                return true; // Only node, become leader / 唯一节点，成为Leader
            }

            if (_transport is Transports.WebSocketClusterTransport wsTransport)
            {
                try
                {
                    // Measure network quality to all other nodes / 测量到所有其他节点的网络质量
                    var qualityScores = new Dictionary<string, int>();
                    var myTotalQuality = 0;

                    foreach (var nodeId in knownNodes)
                    {
                        var quality = await wsTransport.GetNetworkQualityAsync(nodeId);
                        qualityScores[nodeId] = quality;
                        myTotalQuality += quality;
                        _logger.LogDebug($"Network quality to node {nodeId}: {quality}/100");
                    }

                    // For 2-node cluster, compare our quality with the other node's quality
                    // 对于2节点集群，比较我们的质量与另一个节点的质量
                    if (knownNodes.Count == 1)
                    {
                        var otherNodeId = knownNodes[0];
                        var otherQuality = qualityScores[otherNodeId];

                        // If we have better or equal network quality, we should be leader
                        // 如果我们有更好或相等的网络质量，我们应该成为Leader
                        // Also consider: if we can't measure the other node's quality to us,
                        // we'll use a tie-breaker (node ID comparison)
                        // 同时考虑：如果我们无法测量另一个节点到我们的质量，我们将使用平局决胜（节点ID比较）
                        
                        if (myTotalQuality > otherQuality)
                        {
                            _logger.LogInformation($"Node {_nodeId} has better network quality ({myTotalQuality} vs {otherQuality}), becoming leader");
                            return true;
                        }
                        else if (myTotalQuality < otherQuality)
                        {
                            _logger.LogInformation($"Other node has better network quality ({otherQuality} vs {myTotalQuality}), waiting");
                            return false;
                        }
                        else
                        {
                            // Tie: use node ID as tie-breaker (lexicographically smaller becomes leader)
                            // 平局：使用节点ID作为平局决胜（字典序较小的成为Leader）
                            var shouldLead = string.Compare(_nodeId, otherNodeId, StringComparison.Ordinal) < 0;
                            _logger.LogInformation($"Network quality tie ({myTotalQuality} = {otherQuality}), using node ID comparison: {_nodeId} {(shouldLead ? "<" : ">")} {otherNodeId}");
                            return shouldLead;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error evaluating network quality, using node ID as tie-breaker");
                    // Fallback to node ID comparison / 回退到节点ID比较
                    if (knownNodes.Count == 1)
                    {
                        return string.Compare(_nodeId, knownNodes[0], StringComparison.Ordinal) < 0;
                    }
                }
            }

            // Fallback: use node ID comparison / 回退：使用节点ID比较
            if (knownNodes.Count == 1)
            {
                return string.Compare(_nodeId, knownNodes[0], StringComparison.Ordinal) < 0;
            }

            return false;
        }

        /// <summary>
        /// Check if this node is the leader / 检查此节点是否为领导者
        /// </summary>
        /// <returns>True if leader, false otherwise / 如果是领导者返回 true，否则返回 false</returns>
        public bool IsLeader()
        {
            return State == RaftNodeState.Leader && LeaderId == _nodeId;
        }
    }
}

