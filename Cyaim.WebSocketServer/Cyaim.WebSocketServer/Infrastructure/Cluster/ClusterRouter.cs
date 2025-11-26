using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Cyaim.WebSocketServer.Infrastructure.Metrics;

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

        // Connection routing: connectionId -> nodeId / 连接路由：连接ID -> 节点ID
        /// <summary>
        /// Connection routing table / 连接路由表
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _connectionRoutes;

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

            _connectionRoutes = new ConcurrentDictionary<string, string>();
            _nodeConnectionCounts = new ConcurrentDictionary<string, int>();
            _pendingForwards = new ConcurrentDictionary<string, TaskCompletionSource<byte[]>>();

            _transport.MessageReceived += OnTransportMessageReceived;
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

            _nodeConnectionCounts.AddOrUpdate(_nodeId, 1, (key, value) => value + 1);

            // 更新集群连接数指标
            _metricsCollector?.UpdateClusterConnectionCount(1, _nodeId);

            // Broadcast connection registration to cluster
            if (_raftNode.IsLeader())
            {
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
            }

            _logger.LogDebug($"Registered connection {connectionId} to node {_nodeId}");
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
                _nodeConnectionCounts.AddOrUpdate(nodeId, 0, (key, value) => Math.Max(0, value - 1));

                // 更新集群连接数指标
                _metricsCollector?.UpdateClusterConnectionCount(-1, nodeId);

                if (_raftNode.IsLeader())
                {
                    var message = new ClusterMessage
                    {
                        Type = ClusterMessageType.UnregisterWebSocketConnection,
                        Payload = JsonSerializer.Serialize(new { ConnectionId = connectionId, NodeId = nodeId })
                    };

                    await _transport.BroadcastAsync(message);
                }

                _logger.LogDebug($"Unregistered connection {connectionId} from node {nodeId}");
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
            if (_nodeConnectionCounts.IsEmpty)
                return _nodeId;

            var optimalNode = _nodeConnectionCounts
                .OrderBy(kvp => kvp.Value)
                .FirstOrDefault();

            return optimalNode.Key ?? _nodeId;
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
                    _connectionRoutes.AddOrUpdate(registration.ConnectionId, registration.NodeId, (key, oldValue) => registration.NodeId);
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
    }
}

