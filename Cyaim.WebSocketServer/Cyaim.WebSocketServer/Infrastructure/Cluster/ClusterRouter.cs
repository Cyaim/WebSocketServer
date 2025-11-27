using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Cyaim.WebSocketServer.Infrastructure.Metrics;
using Cyaim.WebSocketServer.Infrastructure.Configures;

namespace Cyaim.WebSocketServer.Infrastructure.Cluster
{
    /// <summary>
    /// Cluster router for managing WebSocket connections across nodes
    /// 用于跨节点管理 WebSocket 连接的集群路由器
    /// </summary>
    public class ClusterRouter
    {
        private readonly ILogger<ClusterRouter> _logger;
        private readonly IClusterTransport _transport;
        private readonly RaftNode _raftNode;
        private readonly string _nodeId;
        private IWebSocketConnectionProvider _connectionProvider;
        private WebSocketMetricsCollector _metricsCollector;
        private Timer _nodeHealthCheckTimer;
        private readonly CancellationTokenSource _cancellationTokenSource;

        // Connection routing: connectionId -> nodeId / 连接路由：连接ID -> 节点ID
        /// <summary>
        /// Connection routing table / 连接路由表
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _connectionRoutes;

        // Connection endpoints: connectionId -> endpoint / 连接端点：连接ID -> 端点
        /// <summary>
        /// Connection endpoints mapping / 连接端点映射
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _connectionEndpoints;

        /// <summary>
        /// Get connection routing table (read-only) / 获取连接路由表（只读）
        /// </summary>
        public IReadOnlyDictionary<string, string> ConnectionRoutes => _connectionRoutes;

        // Node connection counts: nodeId -> count / 节点连接数：节点ID -> 数量
        /// <summary>
        /// Node connection counts / 节点连接数统计
        /// </summary>
        private readonly ConcurrentDictionary<string, int> _nodeConnectionCounts;

        // Pending forward requests: messageId -> task completion source / 待转发的请求：消息ID -> 任务完成源
        /// <summary>
        /// Pending forward requests / 待转发的请求
        /// </summary>
        private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> _pendingForwards;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="transport">Cluster transport / 集群传输</param>
        /// <param name="raftNode">Raft node instance / Raft 节点实例</param>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        public ClusterRouter(ILogger<ClusterRouter> logger, IClusterTransport transport, RaftNode raftNode, string nodeId)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _raftNode = raftNode ?? throw new ArgumentNullException(nameof(raftNode));
            _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
            _cancellationTokenSource = new CancellationTokenSource();

            _connectionRoutes = new ConcurrentDictionary<string, string>();
            _connectionEndpoints = new ConcurrentDictionary<string, string>();
            _nodeConnectionCounts = new ConcurrentDictionary<string, int>();
            _pendingForwards = new ConcurrentDictionary<string, TaskCompletionSource<byte[]>>();

            _transport.MessageReceived += OnTransportMessageReceived;
            _transport.NodeDisconnected += OnNodeDisconnected;

            // Start periodic node health check / 启动定期节点健康检查
            _nodeHealthCheckTimer = new Timer(CheckNodeHealth, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Set WebSocket connection provider
        /// 设置 WebSocket 连接提供者
        /// </summary>
        /// <param name="provider">Connection provider / 连接提供者</param>
        public void SetConnectionProvider(IWebSocketConnectionProvider provider)
        {
            _connectionProvider = provider;
        }

        /// <summary>
        /// Set metrics collector
        /// 设置指标收集器
        /// </summary>
        /// <param name="metricsCollector">Metrics collector / 指标收集器</param>
        public void SetMetricsCollector(WebSocketMetricsCollector metricsCollector)
        {
            _metricsCollector = metricsCollector;
        }

        /// <summary>
        /// Register a WebSocket connection to this node
        /// 将 WebSocket 连接注册到此节点
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="endpoint">Endpoint path / 端点路径</param>
        public async Task RegisterConnectionAsync(string connectionId, string endpoint = null)
        {
            _connectionRoutes.AddOrUpdate(connectionId, _nodeId, (key, oldValue) => _nodeId);
            
            // Store endpoint information / 存储端点信息
            if (!string.IsNullOrEmpty(endpoint))
            {
                _connectionEndpoints.AddOrUpdate(connectionId, endpoint, (key, oldValue) => endpoint);
            }

            _nodeConnectionCounts.AddOrUpdate(_nodeId, 1, (key, value) => value + 1);

            // 更新集群连接数指标
            _metricsCollector?.UpdateClusterConnectionCount(1, _nodeId);

            // Always broadcast connection registration to cluster (not just when leader)
            // 始终向集群广播连接注册（不仅仅是领导者时）
            // This ensures all nodes know about all connections
            // 这确保所有节点都知道所有连接
                var registration = new WebSocketConnectionRegistration
                {
                    ConnectionId = connectionId,
                    NodeId = _nodeId,
                    Endpoint = endpoint,
                    RegisteredAt = DateTime.UtcNow
                };

                var message = new ClusterMessage
                {
                    Type = ClusterMessageType.RegisterWebSocketConnection,
                    Payload = JsonSerializer.Serialize(registration)
                };

                await _transport.BroadcastAsync(message);

            _logger.LogDebug($"Registered connection {connectionId} to node {_nodeId} and broadcast to cluster");
        }

        /// <summary>
        /// Unregister a WebSocket connection
        /// 注销 WebSocket 连接
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        public async Task UnregisterConnectionAsync(string connectionId)
        {
            if (_connectionRoutes.TryRemove(connectionId, out var nodeId))
            {
                // Remove endpoint information / 移除端点信息
                _connectionEndpoints.TryRemove(connectionId, out _);
                
                _nodeConnectionCounts.AddOrUpdate(nodeId, 0, (key, value) => Math.Max(0, value - 1));

                // 更新集群连接数指标
                _metricsCollector?.UpdateClusterConnectionCount(-1, nodeId);

                // Always broadcast connection unregistration to cluster (not just when leader)
                // 始终向集群广播连接注销（不仅仅是领导者时）
                    var message = new ClusterMessage
                    {
                        Type = ClusterMessageType.UnregisterWebSocketConnection,
                        Payload = JsonSerializer.Serialize(new { ConnectionId = connectionId, NodeId = nodeId })
                    };

                    await _transport.BroadcastAsync(message);

                _logger.LogDebug($"Unregistered connection {connectionId} from node {nodeId} and broadcast to cluster");
            }
        }

        /// <summary>
        /// Route WebSocket message to appropriate node
        /// 将 WebSocket 消息路由到适当的节点
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="data">Message data / 消息数据</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="localHandler">Handler for local connections / 本地连接的处理程序</param>
        /// <returns>True if routed successfully, false otherwise / 路由成功返回 true，否则返回 false</returns>
        public async Task<bool> RouteMessageAsync(string connectionId, byte[] data, int messageType, Func<string, WebSocket, Task> localHandler)
        {
            if (_connectionRoutes.TryGetValue(connectionId, out var targetNodeId))
            {
                if (targetNodeId == _nodeId)
                {
                    // Local connection - handle directly / 本地连接 - 直接处理
                    _logger.LogDebug($"Routing message to local connection {connectionId}");

                    if (_connectionProvider != null)
                    {
                        var webSocket = _connectionProvider.GetConnection(connectionId);
                        if (webSocket != null && webSocket.State == WebSocketState.Open)
                        {
                            if (localHandler != null)
                            {
                                await localHandler(connectionId, webSocket);
                            }
                            else
                            {
                                // Send directly if no handler provided / 如果没有提供处理程序，直接发送
                                var wsMessageType = (WebSocketMessageType)messageType;
                                await _connectionProvider.SendAsync(connectionId, data, wsMessageType);
                            }
                            return true;
                        }
                        else
                        {
                            _logger.LogWarning($"Local connection {connectionId} is not available or closed");
                            // Remove stale connection / 删除过时的连接
                            _connectionRoutes.TryRemove(connectionId, out _);
                            return false;
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"Connection provider not set, cannot route to local connection {connectionId}");
                        return false;
                    }
                }
                else
                {
                    // Remote connection - forward via transport / 远程连接 - 通过传输转发
                    return await ForwardToNodeAsync(targetNodeId, connectionId, data, messageType);
                }
            }
            else
            {
                // Connection not found - query cluster if leader / 未找到连接 - 如果是领导者则查询集群
                if (_raftNode.IsLeader())
                {
                    var found = await QueryConnectionAsync(connectionId);
                    if (found != null)
                    {
                        return await ForwardToNodeAsync(found, connectionId, data, messageType);
                    }
                }
                else
                {
                    // Query leader for connection location / 向领导者查询连接位置
                    // Simplified: would need to send query to leader / 简化版本：需要向领导者发送查询
                }

                _logger.LogWarning($"Connection {connectionId} not found in routing table");
                return false;
            }
        }

        /// <summary>
        /// Forward message to specific node / 将消息转发到指定节点
        /// </summary>
        /// <param name="targetNodeId">Target node ID / 目标节点 ID</param>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="data">Message data / 消息数据</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <returns>True if forwarded successfully / 转发成功返回 true</returns>
        private async Task<bool> ForwardToNodeAsync(string targetNodeId, string connectionId, byte[] data, int messageType)
        {
            try
            {
                var forwardMessage = new ForwardWebSocketMessage
                {
                    ConnectionId = connectionId,
                    TargetNodeId = targetNodeId,
                    Data = data,
                    MessageType = messageType
                };

                var message = new ClusterMessage
                {
                    Type = ClusterMessageType.ForwardWebSocketMessage,
                    Payload = JsonSerializer.Serialize(forwardMessage)
                };

                await _transport.SendAsync(targetNodeId, message);
                
                // 记录集群消息转发指标
                _metricsCollector?.RecordClusterMessageForwarded(_nodeId, targetNodeId);
                
                _logger.LogDebug($"Forwarded message for connection {connectionId} to node {targetNodeId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to forward message for connection {connectionId} to node {targetNodeId}");
                _metricsCollector?.RecordError("cluster_forward_failed", _nodeId);
                return false;
            }
        }

        /// <summary>
        /// Query connection location from cluster / 从集群查询连接位置
        /// </summary>
        /// <param name="connectionId">Connection ID to query / 要查询的连接 ID</param>
        /// <returns>Node ID where connection is located, or null if not found / 连接所在的节点 ID，如果未找到则返回 null</returns>
        private async Task<string> QueryConnectionAsync(string connectionId)
        {
            var message = new ClusterMessage
            {
                Type = ClusterMessageType.QueryWebSocketConnection,
                Payload = JsonSerializer.Serialize(new { ConnectionId = connectionId })
            };

            // Broadcast query / 广播查询
            await _transport.BroadcastAsync(message);

            // Wait for response (simplified - would need timeout and response handling)
            // 等待响应（简化版本 - 需要超时和响应处理）
            await Task.Delay(100);

            if (_connectionRoutes.TryGetValue(connectionId, out var nodeId))
            {
                return nodeId;
            }

            return null;
        }

        /// <summary>
        /// Get node with least connections for load balancing
        /// 获取连接数最少的节点（用于负载均衡）
        /// </summary>
        /// <returns>Optimal node ID / 最优节点 ID</returns>
        public string GetOptimalNode()
        {
            // Get all known nodes from routing table and cluster context / 从路由表和集群上下文获取所有已知节点
            var allKnownNodes = new HashSet<string> { _nodeId };
            
            // Add nodes from connection routes / 从连接路由添加节点
            foreach (var nodeId in _connectionRoutes.Values)
            {
                allKnownNodes.Add(nodeId);
            }
            
            // Add nodes from connection counts / 从连接计数添加节点
            foreach (var nodeId in _nodeConnectionCounts.Keys)
            {
                allKnownNodes.Add(nodeId);
            }

            if (allKnownNodes.Count == 1)
            {
                return _nodeId; // Only current node / 只有当前节点
            }

            // Find node with least connections / 查找连接数最少的节点
            var optimalNode = allKnownNodes
                .Where(n => n != _nodeId) // Exclude self / 排除自己
                .Select(n => new { NodeId = n, Count = _nodeConnectionCounts.GetValueOrDefault(n, 0) })
                .OrderBy(x => x.Count)
                .FirstOrDefault();

            return optimalNode?.NodeId ?? _nodeId;
        }

        /// <summary>
        /// Handle incoming transport messages / 处理传入的传输消息
        /// </summary>
        /// <param name="sender">Event sender / 事件发送者</param>
        /// <param name="e">Message event arguments / 消息事件参数</param>
        private void OnTransportMessageReceived(object sender, ClusterMessageEventArgs e)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    switch (e.Message.Type)
                    {
                        case ClusterMessageType.ForwardWebSocketMessage:
                            await HandleForwardMessage(e.Message);
                            break;

                        case ClusterMessageType.RegisterWebSocketConnection:
                            HandleRegisterConnection(e.Message);
                            break;

                        case ClusterMessageType.UnregisterWebSocketConnection:
                            HandleUnregisterConnection(e.Message);
                            break;

                        case ClusterMessageType.QueryWebSocketConnection:
                            await HandleQueryConnection(e.Message);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error handling cluster message {e.Message.Type}");
                }
            });
        }

        /// <summary>
        /// Handle forward WebSocket message / 处理转发 WebSocket 消息
        /// </summary>
        /// <param name="message">Forward message / 转发消息</param>
        private async Task HandleForwardMessage(ClusterMessage message)
        {
            try
            {
                var forward = JsonSerializer.Deserialize<ForwardWebSocketMessage>(message.Payload);

                // If this is the target node, find local WebSocket and send
                // 如果这是目标节点，查找本地 WebSocket 并发送
                if (forward.TargetNodeId == _nodeId)
                {
                    // 记录集群消息接收指标
                    _metricsCollector?.RecordClusterMessageReceived(forward.TargetNodeId);
                    
                    _logger.LogDebug($"Received forward message for local connection {forward.ConnectionId}");

                    if (_connectionProvider != null)
                    {
                        var webSocket = _connectionProvider.GetConnection(forward.ConnectionId);
                        if (webSocket != null && webSocket.State == WebSocketState.Open)
                        {
                            var wsMessageType = (WebSocketMessageType)forward.MessageType;
                            var success = await _connectionProvider.SendAsync(
                                forward.ConnectionId,
                                forward.Data,
                                wsMessageType);

                            if (success)
                            {
                                _logger.LogDebug($"Successfully forwarded message to local connection {forward.ConnectionId}");
                            }
                            else
                            {
                                _logger.LogWarning($"Failed to send message to local connection {forward.ConnectionId}");
                            }
                        }
                        else
                        {
                            _logger.LogWarning($"Local connection {forward.ConnectionId} not found or closed");
                            // Remove stale connection / 删除过时的连接
                            _connectionRoutes.TryRemove(forward.ConnectionId, out _);
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"Connection provider not set, cannot handle forward message for {forward.ConnectionId}");
                    }
                }
                else if (forward.TargetNodeId != _nodeId)
                {
                    // Not for us - forward again / 不是给我们的 - 再次转发
                    await ForwardToNodeAsync(forward.TargetNodeId, forward.ConnectionId, forward.Data, forward.MessageType);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling forward message");
            }
        }

        /// <summary>
        /// Handle connection registration message / 处理连接注册消息
        /// </summary>
        /// <param name="message">Registration message / 注册消息</param>
        private void HandleRegisterConnection(ClusterMessage message)
        {
            try
            {
                var registration = JsonSerializer.Deserialize<WebSocketConnectionRegistration>(message.Payload);

                if (registration.NodeId != _nodeId)
                {
                    // Check if connection was previously on a different node (transfer case) / 检查连接是否之前在另一个节点上（转移情况）
                    var previousNodeId = _connectionRoutes.GetValueOrDefault(registration.ConnectionId);
                    if (previousNodeId != null && previousNodeId != registration.NodeId)
                    {
                        // Connection transferred from another node / 连接从另一个节点转移
                        _logger.LogInformation($"Connection {registration.ConnectionId} transferred from node {previousNodeId} to node {registration.NodeId}");
                        
                        // Decrement count for previous node / 减少前一个节点的计数
                        _nodeConnectionCounts.AddOrUpdate(previousNodeId, 0, (key, value) => Math.Max(0, value - 1));
                    }

                    // Update routing table / 更新路由表
                    _connectionRoutes.AddOrUpdate(registration.ConnectionId, registration.NodeId, (key, oldValue) => registration.NodeId);
                    
                    // Store endpoint information if available / 如果可用，存储端点信息
                    if (!string.IsNullOrEmpty(registration.Endpoint))
                    {
                        _connectionEndpoints.AddOrUpdate(registration.ConnectionId, registration.Endpoint, (key, oldValue) => registration.Endpoint);
                    }
                    
                    // Increment count for new node / 增加新节点的计数
                    _nodeConnectionCounts.AddOrUpdate(registration.NodeId, 1, (key, value) => value + 1);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling register connection");
            }
        }

        /// <summary>
        /// Handle connection unregistration message / 处理连接注销消息
        /// </summary>
        /// <param name="message">Unregistration message / 注销消息</param>
        private void HandleUnregisterConnection(ClusterMessage message)
        {
            try
            {
                var unregister = JsonSerializer.Deserialize<JsonElement>(message.Payload);
                var connectionId = unregister.GetProperty("ConnectionId").GetString();
                var nodeId = unregister.GetProperty("NodeId").GetString();

                if (nodeId != _nodeId && _connectionRoutes.TryRemove(connectionId, out string _))
                {
                    _nodeConnectionCounts.AddOrUpdate(nodeId, 0, (key, value) => Math.Max(0, value - 1));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling unregister connection");
            }
        }

        /// <summary>
        /// Handle connection query message / 处理连接查询消息
        /// </summary>
        /// <param name="message">Query message / 查询消息</param>
        private async Task HandleQueryConnection(ClusterMessage message)
        {
            try
            {
                var query = JsonSerializer.Deserialize<JsonElement>(message.Payload);
                var connectionId = query.GetProperty("ConnectionId").GetString();

                if (_connectionRoutes.TryGetValue(connectionId, out string nodeId) && nodeId == _nodeId)
                {
                    // Respond to query / 响应查询
                    var registration = new WebSocketConnectionRegistration
                    {
                        ConnectionId = connectionId,
                        NodeId = _nodeId,
                        RegisteredAt = DateTime.UtcNow
                    };

                    var response = new ClusterMessage
                    {
                        Type = ClusterMessageType.RegisterWebSocketConnection,
                        Payload = JsonSerializer.Serialize(registration),
                        ToNodeId = message.FromNodeId
                    };

                    await _transport.SendAsync(message.FromNodeId, response);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling query connection");
            }
        }

        /// <summary>
        /// Get connection count for this node
        /// 获取此节点的连接数
        /// </summary>
        /// <returns>Connection count / 连接数</returns>
        public int GetLocalConnectionCount()
        {
            return _nodeConnectionCounts.GetValueOrDefault(_nodeId, 0);
        }

        /// <summary>
        /// Get total cluster connection count
        /// 获取集群总连接数
        /// </summary>
        /// <returns>Total connection count / 总连接数</returns>
        public int GetTotalConnectionCount()
        {
            return _nodeConnectionCounts.Values.Sum();
        }

        /// <summary>
        /// Handle node disconnected event / 处理节点断开连接事件
        /// </summary>
        /// <param name="sender">Event sender / 事件发送者</param>
        /// <param name="e">Node event arguments / 节点事件参数</param>
        private async void OnNodeDisconnected(object sender, ClusterNodeEventArgs e)
        {
            var disconnectedNodeId = e.NodeId;
            if (string.IsNullOrEmpty(disconnectedNodeId) || disconnectedNodeId == _nodeId)
            {
                return; // Ignore self or invalid node / 忽略自己或无效节点
            }

            _logger.LogWarning($"Node {disconnectedNodeId} disconnected. Cleaning up connections and attempting transfer.");

            // Find all connections on the disconnected node / 查找断开节点上的所有连接
            var connectionsToTransfer = _connectionRoutes
                .Where(kvp => kvp.Value == disconnectedNodeId)
                .Select(kvp => kvp.Key)
                .ToList();

            if (connectionsToTransfer.Count > 0)
            {
                _logger.LogInformation($"Found {connectionsToTransfer.Count} connection(s) on disconnected node {disconnectedNodeId}. Attempting to transfer.");

                // Remove connections from routing table / 从路由表中移除连接
                foreach (var connectionId in connectionsToTransfer)
                {
                    _connectionRoutes.TryRemove(connectionId, out _);
                }

                // Update connection counts / 更新连接数
                _nodeConnectionCounts.TryRemove(disconnectedNodeId, out _);
                var removedCount = connectionsToTransfer.Count;
                _metricsCollector?.UpdateClusterConnectionCount(-removedCount, disconnectedNodeId);

                // Attempt to transfer connections to optimal node / 尝试将连接转移到最优节点
                await TransferConnectionsAsync(connectionsToTransfer, disconnectedNodeId);
            }
            else
            {
                _logger.LogDebug($"No connections found on disconnected node {disconnectedNodeId}");
            }

            // Clean up node connection count / 清理节点连接数
            _nodeConnectionCounts.TryRemove(disconnectedNodeId, out _);
        }

        /// <summary>
        /// Transfer connections from disconnected node to optimal node / 将连接从断开节点转移到最优节点
        /// </summary>
        /// <param name="connectionIds">Connection IDs to transfer / 要转移的连接 ID</param>
        /// <param name="disconnectedNodeId">Disconnected node ID / 断开连接的节点 ID</param>
        private async Task TransferConnectionsAsync(List<string> connectionIds, string disconnectedNodeId)
        {
            if (connectionIds == null || connectionIds.Count == 0)
            {
                return;
            }

            try
            {
                // Get optimal node for load balancing / 获取用于负载均衡的最优节点
                // Exclude disconnected node from consideration / 从考虑中排除断开节点
                var availableNodes = _connectionRoutes.Values
                    .Distinct()
                    .Where(n => n != disconnectedNodeId && _transport.IsNodeConnected(n))
                    .ToList();
                
                // Also include current node if it's connected / 如果当前节点已连接，也包含它
                if (!availableNodes.Contains(_nodeId))
                {
                    availableNodes.Add(_nodeId);
                }

                string optimalNodeId;
                if (availableNodes.Count > 0)
                {
                    // Select node with least connections (load balancing) / 选择连接数最少的节点（负载均衡）
                    optimalNodeId = availableNodes
                        .Select(n => new { NodeId = n, Count = _nodeConnectionCounts.GetValueOrDefault(n, 0) })
                        .OrderBy(x => x.Count)
                        .First().NodeId;
                }
                else
                {
                    // No other nodes available, use current node / 没有其他可用节点，使用当前节点
                    optimalNodeId = _nodeId;
                }

                _logger.LogInformation($"Transferring {connectionIds.Count} connection(s) from node {disconnectedNodeId} to node {optimalNodeId}");

                // Note: WebSocket connections cannot be truly "transferred" as they are TCP connections
                // We can only notify clients to reconnect, or wait for them to reconnect naturally
                // 注意：WebSocket 连接无法真正"转移"，因为它们是 TCP 连接
                // 我们只能通知客户端重新连接，或等待它们自然重新连接

                // For now, we'll just update the routing table to point to the optimal node
                // This allows the system to handle reconnections gracefully
                // 目前，我们只是更新路由表指向最优节点
                // 这允许系统优雅地处理重新连接

                foreach (var connectionId in connectionIds)
                {
                    // Update routing table to point to optimal node / 更新路由表指向最优节点
                    _connectionRoutes.TryAdd(connectionId, optimalNodeId);
                    _nodeConnectionCounts.AddOrUpdate(optimalNodeId, 1, (key, value) => value + 1);
                }

                // Broadcast connection transfers to cluster / 向集群广播连接转移
                // Send individual registration messages for each connection / 为每个连接发送单独的注册消息
                // This ensures all nodes know about the transfer / 这确保所有节点都知道转移
                foreach (var connectionId in connectionIds)
                {
                    var registration = new WebSocketConnectionRegistration
                    {
                        ConnectionId = connectionId,
                        NodeId = optimalNodeId,
                        Endpoint = null, // Endpoint unknown after transfer / 转移后端点未知
                        RegisteredAt = DateTime.UtcNow
                    };

                    var message = new ClusterMessage
                    {
                        Type = ClusterMessageType.RegisterWebSocketConnection,
                        Payload = System.Text.Json.JsonSerializer.Serialize(registration)
                    };

                    await _transport.BroadcastAsync(message);
                }

                _logger.LogInformation($"Successfully transferred {connectionIds.Count} connection(s) from node {disconnectedNodeId} to node {optimalNodeId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error transferring connections from node {disconnectedNodeId}");
            }
        }

        /// <summary>
        /// Periodic health check for nodes / 定期节点健康检查
        /// </summary>
        /// <param name="state">Timer state / 定时器状态</param>
        private void CheckNodeHealth(object state)
        {
            try
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    return;
                }

                // Get all known nodes from routing table / 从路由表获取所有已知节点
                var knownNodeIds = _connectionRoutes.Values.Distinct().Where(n => n != _nodeId).ToList();
                
                if (knownNodeIds.Count == 0)
                {
                    return; // No other nodes to check / 没有其他节点需要检查
                }

                foreach (var nodeId in knownNodeIds)
                {
                    // Check if node is still connected / 检查节点是否仍连接
                    var isConnected = _transport.IsNodeConnected(nodeId);
                    
                    if (!isConnected)
                    {
                        // Check if we have connections for this node / 检查我们是否有此节点的连接
                        var hasConnections = _connectionRoutes.Any(kvp => kvp.Value == nodeId);
                        
                        if (hasConnections)
                        {
                            // Node appears disconnected and has connections, trigger cleanup / 节点似乎断开且有连接，触发清理
                            _logger.LogWarning($"Node {nodeId} appears disconnected during health check and has connections. Triggering cleanup.");
                            _ = Task.Run(() => OnNodeDisconnected(_transport, new ClusterNodeEventArgs { NodeId = nodeId }));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during node health check");
            }
        }

        /// <summary>
        /// Gracefully shutdown and transfer connections to optimal node / 优雅关闭并将连接转移到最优节点
        /// </summary>
        /// <param name="clusterOption">Cluster configuration / 集群配置</param>
        /// <returns>Task / 任务</returns>
        public async Task GracefulShutdownAsync(ClusterOption clusterOption)
        {
            if (_connectionProvider == null)
            {
                _logger.LogWarning("Connection provider not set, cannot perform graceful shutdown");
                return;
            }

            // Get all local connections / 获取所有本地连接
            var localConnections = GetLocalConnections();
            
            if (localConnections.Count == 0)
            {
                _logger.LogInformation("No local connections to transfer during shutdown");
                return;
            }

            _logger.LogInformation($"Starting graceful shutdown: transferring {localConnections.Count} connection(s)");

            // Get optimal node for load balancing / 获取用于负载均衡的最优节点
            var optimalNodeId = GetOptimalNodeForTransfer();
            
            if (optimalNodeId == _nodeId)
            {
                _logger.LogWarning("No other nodes available for transfer, connections will be closed without redirect");
                optimalNodeId = null;
            }

            // Build redirect URL if optimal node found / 如果找到最优节点，构建重定向URL
            string redirectUrl = null;
            if (optimalNodeId != null)
            {
                redirectUrl = BuildRedirectUrl(optimalNodeId, clusterOption);
                _logger.LogInformation($"Transferring connections to node {optimalNodeId} at {redirectUrl}");
            }

            // Transfer connections in batches with adaptive batch size / 使用自适应批次大小分批转移连接
            int currentBatchSize = 1000; // Start with 1000 connections per batch / 从每批1000个连接开始
            const int minBatchSize = 100; // Minimum batch size / 最小批次大小
            const int maxBatchSize = 10000; // Maximum batch size / 最大批次大小
            const int batchSizeIncrement = 500; // Increment step / 增量步长
            const double successThreshold = 0.95; // 95% success rate to increase batch size / 95%成功率才增加批次大小
            const int minBatchTimeMs = 50; // Minimum time per batch (ms) / 每批最小时间（毫秒）
            const int maxBatchTimeMs = 500; // Maximum time per batch (ms) / 每批最大时间（毫秒）

            var totalConnections = localConnections.Count;
            var processedCount = 0;
            var successCount = 0;
            var startTime = DateTime.UtcNow;

            _logger.LogInformation($"Starting adaptive batch processing: {totalConnections} connections, initial batch size: {currentBatchSize}");

            while (processedCount < totalConnections)
            {
                var batch = localConnections
                    .Skip(processedCount)
                    .Take(currentBatchSize)
                    .ToList();

                if (batch.Count == 0)
                {
                    break;
                }

                var batchStartTime = DateTime.UtcNow;
                var batchSuccessCount = 0;

                try
                {
                    // Process batch in parallel / 并行处理批次
                    var tasks = batch.Select(async connectionId =>
                    {
                        try
                        {
                            await CloseConnectionWithRedirectAsync(connectionId, redirectUrl);
                            Interlocked.Increment(ref batchSuccessCount);
                            return true;
                        }
                        catch
                        {
                            return false;
                        }
                    });

                    var results = await Task.WhenAll(tasks);
                    batchSuccessCount = results.Count(r => r);
                    successCount += batchSuccessCount;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Error processing batch of {batch.Count} connections");
                }

                processedCount += batch.Count;
                var batchElapsedMs = (DateTime.UtcNow - batchStartTime).TotalMilliseconds;
                var batchSuccessRate = batch.Count > 0 ? (double)batchSuccessCount / batch.Count : 0;

                _logger.LogInformation(
                    $"Batch processed: {batch.Count} connections, " +
                    $"success: {batchSuccessCount}/{batch.Count} ({batchSuccessRate:P1}), " +
                    $"time: {batchElapsedMs:F0}ms, " +
                    $"progress: {processedCount}/{totalConnections} ({100.0 * processedCount / totalConnections:F1}%)");

                // Adaptive batch size adjustment / 自适应批次大小调整
                if (batchSuccessRate >= successThreshold && batchElapsedMs < maxBatchTimeMs)
                {
                    // Performance is good, increase batch size / 性能良好，增加批次大小
                    var newBatchSize = Math.Min(currentBatchSize + batchSizeIncrement, maxBatchSize);
                    if (newBatchSize > currentBatchSize)
                    {
                        _logger.LogInformation($"Increasing batch size from {currentBatchSize} to {newBatchSize} (success rate: {batchSuccessRate:P1}, time: {batchElapsedMs:F0}ms)");
                        currentBatchSize = newBatchSize;
                    }
                }
                else if (batchSuccessRate < successThreshold || batchElapsedMs > maxBatchTimeMs)
                {
                    // Performance is poor, decrease batch size / 性能不佳，减少批次大小
                    var newBatchSize = Math.Max((int)(currentBatchSize * 0.7), minBatchSize);
                    if (newBatchSize < currentBatchSize)
                    {
                        _logger.LogInformation($"Decreasing batch size from {currentBatchSize} to {newBatchSize} (success rate: {batchSuccessRate:P1}, time: {batchElapsedMs:F0}ms)");
                        currentBatchSize = newBatchSize;
                    }
                }

                // Adaptive delay based on batch performance / 基于批次性能的自适应延迟
                if (batchElapsedMs < minBatchTimeMs)
                {
                    // Batch completed too quickly, add small delay to avoid overwhelming / 批次完成太快，添加小延迟避免过载
                    await Task.Delay(10);
                }
                else if (batchElapsedMs > maxBatchTimeMs)
                {
                    // Batch took too long, add longer delay / 批次耗时过长，添加较长延迟
                    await Task.Delay(100);
                }
                else
                {
                    // Normal delay / 正常延迟
                    await Task.Delay(50);
                }
            }

            var totalElapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var overallSuccessRate = totalConnections > 0 ? (double)successCount / totalConnections : 0;

            _logger.LogInformation(
                $"Adaptive batch processing completed: " +
                $"processed: {processedCount}, " +
                $"success: {successCount}/{totalConnections} ({overallSuccessRate:P1}), " +
                $"total time: {totalElapsedMs:F0}ms, " +
                $"average: {totalElapsedMs / Math.Max(processedCount, 1):F2}ms per connection");

            _logger.LogInformation($"Graceful shutdown completed: {localConnections.Count} connection(s) processed");
        }

        /// <summary>
        /// Get all local connections / 获取所有本地连接
        /// </summary>
        /// <returns>List of connection IDs / 连接ID列表</returns>
        private List<string> GetLocalConnections()
        {
            var connections = new List<string>();
            
            // Get connections from MvcChannelHandler / 从 MvcChannelHandler 获取连接
            if (Infrastructure.Handlers.MvcHandler.MvcChannelHandler.Clients != null)
            {
                foreach (var kvp in Infrastructure.Handlers.MvcHandler.MvcChannelHandler.Clients)
                {
                    if (kvp.Value.State == WebSocketState.Open)
                    {
                        connections.Add(kvp.Key);
                    }
                }
            }

            return connections;
        }

        /// <summary>
        /// Get optimal node for connection transfer / 获取用于连接转移的最优节点
        /// </summary>
        /// <returns>Optimal node ID / 最优节点ID</returns>
        private string GetOptimalNodeForTransfer()
        {
            // Get all available nodes (excluding current node) / 获取所有可用节点（排除当前节点）
            var availableNodes = _connectionRoutes.Values
                .Distinct()
                .Where(n => n != _nodeId && _transport.IsNodeConnected(n))
                .ToList();

            if (availableNodes.Count == 0)
            {
                return _nodeId; // No other nodes available / 没有其他可用节点
            }

            // Select node with least connections / 选择连接数最少的节点
            return availableNodes
                .Select(n => new { NodeId = n, Count = _nodeConnectionCounts.GetValueOrDefault(n, 0) })
                .OrderBy(x => x.Count)
                .First().NodeId;
        }

        /// <summary>
        /// Build redirect URL for target node / 为目标节点构建重定向URL
        /// </summary>
        /// <param name="targetNodeId">Target node ID / 目标节点ID</param>
        /// <param name="clusterOption">Cluster configuration / 集群配置</param>
        /// <returns>Redirect URL / 重定向URL</returns>
        private string BuildRedirectUrl(string targetNodeId, ClusterOption clusterOption)
        {
            // Try to get node info from transport layer / 尝试从传输层获取节点信息
            if (_transport is Transports.WebSocketClusterTransport wsTransport)
            {
                var nodeInfo = GetNodeInfoFromTransport(wsTransport, targetNodeId);
                if (nodeInfo != null)
                {
                    var endpoint = "/ws"; // Default WebSocket endpoint / 默认WebSocket端点
                    var protocol = nodeInfo.Protocol ?? "ws";
                    return $"{protocol}://{nodeInfo.Address}:{nodeInfo.Port}{endpoint}";
                }
            }

            // Fallback: parse from cluster configuration / 回退：从集群配置解析
            if (clusterOption?.Nodes != null)
            {
                foreach (var nodeConfig in clusterOption.Nodes)
                {
                    try
                    {
                        if (Uri.TryCreate(nodeConfig, UriKind.Absolute, out var uri))
                        {
                            var path = uri.PathAndQuery.TrimStart('/');
                            var nodeId = !string.IsNullOrEmpty(path) ? path : $"{uri.Host}:{uri.Port}";
                            
                            if (nodeId == targetNodeId)
                            {
                                var endpoint = "/ws"; // Default WebSocket endpoint / 默认WebSocket端点
                                return $"ws://{uri.Host}:{uri.Port}{endpoint}";
                            }
                        }
                    }
                    catch
                    {
                        // Ignore parsing errors / 忽略解析错误
                    }
                }
            }

            // Last fallback: try GlobalClusterCenter / 最后回退：尝试GlobalClusterCenter
            var globalContext = GlobalClusterCenter.ClusterContext;
            if (globalContext?.Nodes != null)
            {
                foreach (var nodeConfig in globalContext.Nodes)
                {
                    try
                    {
                        if (Uri.TryCreate(nodeConfig, UriKind.Absolute, out var uri))
                        {
                            var path = uri.PathAndQuery.TrimStart('/');
                            var nodeId = !string.IsNullOrEmpty(path) ? path : $"{uri.Host}:{uri.Port}";
                            
                            if (nodeId == targetNodeId)
                            {
                                var endpoint = "/ws";
                                return $"ws://{uri.Host}:{uri.Port}{endpoint}";
                            }
                        }
                    }
                    catch
                    {
                        // Ignore parsing errors / 忽略解析错误
                    }
                }
            }

            _logger.LogWarning($"Could not build redirect URL for node {targetNodeId}");
            return null;
        }

        /// <summary>
        /// Get node information from transport layer using reflection / 使用反射从传输层获取节点信息
        /// </summary>
        /// <param name="transport">WebSocket cluster transport / WebSocket集群传输</param>
        /// <param name="nodeId">Target node ID / 目标节点ID</param>
        /// <returns>Cluster node information or null / 集群节点信息或null</returns>
        private ClusterNode GetNodeInfoFromTransport(Transports.WebSocketClusterTransport transport, string nodeId)
        {
            try
            {
                // Use reflection to access private _nodes field / 使用反射访问私有_nodes字段
                var nodesField = typeof(Transports.WebSocketClusterTransport)
                    .GetField("_nodes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (nodesField?.GetValue(transport) is ConcurrentDictionary<string, ClusterNode> nodes)
                {
                    if (nodes.TryGetValue(nodeId, out var node))
                    {
                        return node;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"Failed to get node info from transport for node {nodeId}");
            }
            
            return null;
        }

        /// <summary>
        /// Close connection with redirect information / 使用重定向信息关闭连接
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接ID</param>
        /// <param name="redirectUrl">Redirect URL (null if no redirect) / 重定向URL（如果无重定向则为null）</param>
        /// <returns>Task / 任务</returns>
        private async Task CloseConnectionWithRedirectAsync(string connectionId, string redirectUrl)
        {
            try
            {
                var webSocket = _connectionProvider?.GetConnection(connectionId);
                if (webSocket == null || webSocket.State != WebSocketState.Open)
                {
                    return;
                }

                // Get endpoint for this connection if available / 如果可用，获取此连接的端点
                var endpoint = _connectionEndpoints.GetValueOrDefault(connectionId, "/ws");
                
                // If redirect URL is provided but doesn't include endpoint, append it / 如果提供了重定向URL但不包含端点，则追加它
                if (!string.IsNullOrEmpty(redirectUrl))
                {
                    try
                    {
                        if (Uri.TryCreate(redirectUrl, UriKind.Absolute, out var uri))
                        {
                            // Replace endpoint in URL if needed / 如果需要，替换URL中的端点
                            var path = uri.PathAndQuery;
                            if (string.IsNullOrEmpty(path) || path == "/")
                            {
                                redirectUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}{endpoint}";
                            }
                            else if (path != endpoint)
                            {
                                // Use the endpoint from connection / 使用连接中的端点
                                redirectUrl = $"{uri.Scheme}://{uri.Host}:{uri.Port}{endpoint}";
                            }
                        }
                    }
                    catch
                    {
                        // If parsing fails, use redirectUrl as is / 如果解析失败，按原样使用redirectUrl
                    }
                }

                // Build close reason with redirect information / 构建包含重定向信息的关闭原因
                // Format: JSON with redirect URL / 格式：包含重定向URL的JSON
                // WebSocket close reason is limited to 123 bytes / WebSocket关闭原因限制为123字节
                string closeReason = null;
                WebSocketCloseStatus closeStatus = WebSocketCloseStatus.EndpointUnavailable;

                if (!string.IsNullOrEmpty(redirectUrl))
                {
                    // Use JSON format for redirect: {"redirect":"ws://host:port/path"} / 使用JSON格式重定向
                    // Keep it short to fit in 123 bytes / 保持简短以适合123字节
                    var redirectInfo = new { redirect = redirectUrl };
                    closeReason = System.Text.Json.JsonSerializer.Serialize(redirectInfo);
                    
                    // Truncate if too long / 如果太长则截断
                    if (closeReason.Length > 123)
                    {
                        closeReason = closeReason.Substring(0, 120) + "...";
                    }
                    
                    closeStatus = WebSocketCloseStatus.EndpointUnavailable; // Indicates endpoint is being removed / 表示端点正在被移除
                    _logger.LogDebug($"Closing connection {connectionId} with redirect to {redirectUrl}");
                }
                else
                {
                    closeReason = "Node shutting down";
                    closeStatus = WebSocketCloseStatus.NormalClosure;
                    _logger.LogDebug($"Closing connection {connectionId} without redirect (no available nodes)");
                }

                // Close connection with redirect information / 使用重定向信息关闭连接
                await webSocket.CloseAsync(closeStatus, closeReason, CancellationToken.None);
                
                // Unregister connection / 注销连接
                await UnregisterConnectionAsync(connectionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error closing connection {connectionId} with redirect: {ex.Message}");
            }
        }

        /// <summary>
        /// Dispose resources / 释放资源
        /// </summary>
        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            _nodeHealthCheckTimer?.Dispose();
        }
    }
}

