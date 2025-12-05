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

namespace Cyaim.WebSocketServer.Infrastructure.Cluster.Transports
{
    /// <summary>
    /// WebSocket-based cluster transport
    /// 基于 WebSocket 的集群传输
    /// </summary>
    public class WebSocketClusterTransport : IClusterTransport
    {
        private readonly ILogger<WebSocketClusterTransport> _logger;
        private readonly string _nodeId;
        /// <summary>
        /// WebSocket connections to other nodes / 到其他节点的 WebSocket 连接
        /// </summary>
        private readonly ConcurrentDictionary<string, ClientWebSocket> _connections;
        /// <summary>
        /// Registered cluster nodes / 已注册的集群节点
        /// </summary>
        private readonly ConcurrentDictionary<string, ClusterNode> _nodes;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private bool _disposed = false;

        /// <summary>
        /// Event triggered when message received / 消息接收时触发的事件
        /// </summary>
        public event EventHandler<ClusterMessageEventArgs> MessageReceived;
        /// <summary>
        /// Event triggered when node connected / 节点连接时触发的事件
        /// </summary>
        public event EventHandler<ClusterNodeEventArgs> NodeConnected;
        /// <summary>
        /// Event triggered when node disconnected / 节点断开连接时触发的事件
        /// </summary>
        public event EventHandler<ClusterNodeEventArgs> NodeDisconnected;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        public WebSocketClusterTransport(ILogger<WebSocketClusterTransport> logger, string nodeId)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
            _connections = new ConcurrentDictionary<string, ClientWebSocket>();
            _nodes = new ConcurrentDictionary<string, ClusterNode>();
            _cancellationTokenSource = new CancellationTokenSource();
        }

        /// <summary>
        /// Start the transport service / 启动传输服务
        /// </summary>
        public async Task StartAsync()
        {
            _logger.LogInformation($"Starting WebSocket cluster transport for node {_nodeId}");
            _logger.LogInformation($"Registered nodes count: {_nodes.Count}");
            
            // Try to establish connections to all registered nodes / 尝试建立到所有已注册节点的连接
            if (_nodes.Count > 0)
            {
                _logger.LogInformation($"Attempting to connect to {_nodes.Count} cluster node(s): {string.Join(", ", _nodes.Values.Select(n => n.NodeId))}");
                var connectionTasks = new List<Task>();
                foreach (var node in _nodes.Values)
                {
                    if (node.NodeId != _nodeId)
                    {
                        var targetNodeId = node.NodeId; // Capture for closure
                        var targetNode = node; // Capture for closure
                        connectionTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                // Wait a bit before connecting to allow other nodes to start / 等待一下再连接，让其他节点有时间启动
                                // Use exponential backoff for retries / 使用指数退避进行重试
                                for (int attempt = 0; attempt < 3; attempt++)
                                {
                                    if (attempt > 0)
                                    {
                                        var delay = (int)(1000 * Math.Pow(2, attempt - 1)); // 1s, 2s, 4s
                                        _logger.LogInformation($"Retrying connection to node {targetNodeId} (attempt {attempt + 1}/3) after {delay}ms");
                                        await Task.Delay(delay, _cancellationTokenSource.Token);
                                    }
                                    else
                                    {
                                        _logger.LogInformation($"Waiting 2 seconds before initial connection attempt to node {targetNodeId}");
                                        await Task.Delay(2000, _cancellationTokenSource.Token); // Initial delay
                                    }
                                    
                                    try
                                    {
                                        _logger.LogInformation($"Connection attempt {attempt + 1} to node {targetNodeId} at ws://{targetNode.Address}:{targetNode.Port}{targetNode.TransportConfig ?? "/cluster"}");
                                        await GetOrCreateConnection(targetNodeId, targetNode);
                                        _logger.LogInformation($"✓ Successfully connected to node {targetNodeId}");
                                        return; // Success, exit retry loop
                                    }
                                    catch (Exception connectEx) when (attempt < 2) // Retry on failure except last attempt
                                    {
                                        _logger.LogWarning(connectEx, $"Connection attempt {attempt + 1} to node {targetNodeId} failed, will retry. Error: {connectEx.Message}");
                                        // Continue to next attempt
                                    }
                                }
                                _logger.LogError($"Failed to connect to node {targetNodeId} after 3 attempts");
                            }
                            catch (OperationCanceledException)
                            {
                                _logger.LogInformation($"Connection to node {targetNodeId} was cancelled");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"Failed to establish initial connection to node {targetNodeId} after retries, will retry on first message");
                            }
                        }));
                    }
                }
                
                // Wait a reasonable time for connections to establish / 等待合理时间让连接建立
                _logger.LogInformation("Waiting for cluster connections to establish...");
                try
                {
                    await Task.WhenAny(Task.WhenAll(connectionTasks), Task.Delay(10000)); // Wait up to 10 seconds
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error waiting for connections");
                }
                
                var connectedCount = _connections.Count;
                var expectedCount = _nodes.Count - 1; // Exclude self
                _logger.LogInformation($"Cluster transport initialization: {connectedCount}/{expectedCount} node(s) connected");
                
                if (connectedCount < expectedCount)
                {
                    _logger.LogWarning($"Only {connectedCount} out of {expectedCount} connections established. Some nodes may not be reachable.");
                }
            }
            else
            {
                _logger.LogInformation("No cluster nodes registered, running in standalone mode");
            }
            
            await Task.CompletedTask;
        }

        /// <summary>
        /// Stop the transport service / 停止传输服务
        /// </summary>
        public async Task StopAsync()
        {
            _logger.LogInformation($"Stopping WebSocket cluster transport for node {_nodeId}");
            _cancellationTokenSource.Cancel();

            foreach (var connection in _connections.Values)
            {
                try
                {
                    if (connection.State == WebSocketState.Open)
                    {
                        await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "Transport stopping", CancellationToken.None);
                    }
                    connection.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error closing WebSocket connection");
                }
            }

            _connections.Clear();
        }

        /// <summary>
        /// Get or create WebSocket connection to node / 获取或创建到节点的 WebSocket 连接
        /// </summary>
        /// <param name="nodeId">Target node ID / 目标节点 ID</param>
        /// <param name="node">Node information / 节点信息</param>
        /// <returns>WebSocket connection / WebSocket 连接</returns>
        private async Task<ClientWebSocket> GetOrCreateConnection(string nodeId, ClusterNode node)
        {
            if (_connections.TryGetValue(nodeId, out var existingConnection))
            {
                if (existingConnection.State == WebSocketState.Open)
                {
                    return existingConnection;
                }
                else
                {
                    existingConnection.Dispose();
                    _connections.TryRemove(nodeId, out _);
                }
            }

            try
            {
                var client = new ClientWebSocket();
                var uri = new Uri($"ws://{node.Address}:{node.Port}{node.TransportConfig ?? "/cluster"}");
                
                _logger.LogInformation($"Attempting to connect to node {nodeId} at {uri}");
                await client.ConnectAsync(uri, _cancellationTokenSource.Token);
                _logger.LogInformation($"WebSocket connection to {uri} established successfully");
                
                if (_connections.TryAdd(nodeId, client))
                {
                    _nodes.TryAdd(nodeId, node);
                    NodeConnected?.Invoke(this, new ClusterNodeEventArgs { NodeId = nodeId });
                    _logger.LogInformation($"Connected to node {nodeId} at {uri}");
                    
                    // Start receiving messages from this connection / 开始从此连接接收消息
                    _ = ReceiveMessagesAsync(nodeId, client);
                    
                    return client;
                }
                else
                {
                    // Connection was added by another thread / 连接已被另一个线程添加
                    client.Dispose();
                    if (_connections.TryGetValue(nodeId, out var existing))
                    {
                        return existing;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug($"Connection to node {nodeId} was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to connect to node {nodeId} at ws://{node.Address}:{node.Port}{node.TransportConfig ?? "/cluster"}. Error: {ex.GetType().Name}: {ex.Message}");
                // Don't throw, allow retry on next SendAsync call / 不抛出异常，允许在下次 SendAsync 调用时重试
                throw;
            }

            return null;
        }

        /// <summary>
        /// Receive messages from WebSocket connection / 从 WebSocket 连接接收消息
        /// </summary>
        /// <param name="nodeId">Source node ID / 源节点 ID</param>
        /// <param name="webSocket">WebSocket instance / WebSocket 实例</param>
        private async Task ReceiveMessagesAsync(string nodeId, WebSocket webSocket)
        {
            var buffer = new byte[4096];
            
            try
            {
                _logger.LogWarning($"[WebSocketClusterTransport] 开始接收消息 - NodeId: {nodeId}, CurrentNodeId: {_nodeId}");
                
                while (webSocket.State == WebSocketState.Open && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                    
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogWarning($"[WebSocketClusterTransport] 收到关闭消息 - NodeId: {nodeId}, CurrentNodeId: {_nodeId}");
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection closed", CancellationToken.None);
                        break;
                    }

                    if (result.EndOfMessage && result.Count > 0)
                    {
                        var messageJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        try
                        {
                            var message = JsonSerializer.Deserialize<ClusterMessage>(messageJson);
                            if (message == null)
                            {
                                _logger.LogError($"[WebSocketClusterTransport] 收到空或无效消息 - NodeId: {nodeId}, CurrentNodeId: {_nodeId}, MessageSize: {result.Count} bytes");
                                continue;
                            }
                            message.FromNodeId = nodeId;
                            
                            _logger.LogWarning($"[WebSocketClusterTransport] 收到集群消息 - NodeId: {nodeId}, MessageType: {message.Type}, MessageId: {message.MessageId}, CurrentNodeId: {_nodeId}, MessageSize: {result.Count} bytes");
                            
                            MessageReceived?.Invoke(this, new ClusterMessageEventArgs
                            {
                                FromNodeId = nodeId,
                                Message = message
                            });
                            
                            _logger.LogWarning($"[WebSocketClusterTransport] 集群消息已触发事件 - NodeId: {nodeId}, MessageType: {message.Type}, CurrentNodeId: {_nodeId}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"[WebSocketClusterTransport] 反序列化消息失败 - NodeId: {nodeId}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}, MessageJson: {messageJson.Substring(0, Math.Min(200, messageJson.Length))}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping / 停止时期望的异常
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error receiving messages from node {nodeId}");
            }
            finally
            {
                _connections.TryRemove(nodeId, out _);
                _nodes.TryRemove(nodeId, out _);
                NodeDisconnected?.Invoke(this, new ClusterNodeEventArgs { NodeId = nodeId });
                webSocket.Dispose();
            }
        }

        /// <summary>
        /// Send message to specific node / 向指定节点发送消息
        /// </summary>
        /// <param name="nodeId">Target node ID / 目标节点 ID</param>
        /// <param name="message">Message to send / 要发送的消息</param>
        public async Task SendAsync(string nodeId, ClusterMessage message)
        {
            _logger.LogWarning($"[WebSocketClusterTransport] 开始发送消息 - TargetNodeId: {nodeId}, CurrentNodeId: {_nodeId}, MessageType: {message.Type}, MessageId: {message.MessageId}");
            
            if (string.IsNullOrEmpty(nodeId))
            {
                _logger.LogError($"[WebSocketClusterTransport] 节点ID为空");
                throw new ArgumentNullException(nameof(nodeId));
            }

            if (!_nodes.TryGetValue(nodeId, out var node))
            {
                _logger.LogError($"[WebSocketClusterTransport] 节点未注册 - TargetNodeId: {nodeId}, 已注册节点: [{string.Join(", ", _nodes.Keys)}], 当前节点: {_nodeId}, 注册节点数: {_nodes.Count}");
                throw new InvalidOperationException($"Node {nodeId} not found in registered nodes. Available nodes: {string.Join(", ", _nodes.Keys)}");
            }

            _logger.LogWarning($"[WebSocketClusterTransport] 节点已注册 - TargetNodeId: {nodeId}, Address: {node.Address}, Port: {node.Port}, CurrentNodeId: {_nodeId}");

            ClientWebSocket connection = null;
            try
            {
                _logger.LogWarning($"[WebSocketClusterTransport] 获取或创建连接到节点 - TargetNodeId: {nodeId}, MessageType: {message.Type}, CurrentNodeId: {_nodeId}");
                connection = await GetOrCreateConnection(nodeId, node);
                _logger.LogWarning($"[WebSocketClusterTransport] 连接获取成功 - TargetNodeId: {nodeId}, ConnectionState: {connection?.State}, CurrentNodeId: {_nodeId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[WebSocketClusterTransport] 获取或创建连接失败 - TargetNodeId: {nodeId}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                throw new InvalidOperationException($"Failed to get or create connection to node {nodeId}", ex);
            }
            
            if (connection == null || connection.State != WebSocketState.Open)
            {
                var state = connection?.State.ToString() ?? "null";
                var activeConnections = _connections.Count;
                _logger.LogError($"[WebSocketClusterTransport] 连接不可用 - TargetNodeId: {nodeId}, ConnectionState: {state}, ActiveConnections: {activeConnections}, CurrentNodeId: {_nodeId}");
                throw new InvalidOperationException($"Cannot send message to node {nodeId}, connection not available (state: {state})");
            }
            
            _logger.LogWarning($"[WebSocketClusterTransport] 准备发送消息 - TargetNodeId: {nodeId}, MessageType: {message.Type}, MessageId: {message.MessageId}, CurrentNodeId: {_nodeId}");

            message.FromNodeId = _nodeId;
            message.ToNodeId = nodeId;
            var messageJson = JsonSerializer.Serialize(message);
            var messageBytes = Encoding.UTF8.GetBytes(messageJson);

            _logger.LogWarning($"[WebSocketClusterTransport] 消息序列化完成 - TargetNodeId: {nodeId}, MessageSize: {messageBytes.Length} bytes, CurrentNodeId: {_nodeId}");

            try
            {
                await connection.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, _cancellationTokenSource.Token);
                _logger.LogWarning($"[WebSocketClusterTransport] 消息发送成功 - TargetNodeId: {nodeId}, MessageId: {message.MessageId}, MessageSize: {messageBytes.Length} bytes, CurrentNodeId: {_nodeId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[WebSocketClusterTransport] 发送消息失败 - TargetNodeId: {nodeId}, MessageId: {message.MessageId}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// Broadcast message to all nodes / 向所有节点广播消息
        /// </summary>
        /// <param name="message">Message to broadcast / 要广播的消息</param>
        public async Task BroadcastAsync(ClusterMessage message)
        {
            message.FromNodeId = _nodeId;
            _logger.LogWarning($"[WebSocketClusterTransport] 开始广播消息 - MessageType: {message.Type}, MessageId: {message.MessageId}, CurrentNodeId: {_nodeId}, 目标节点数: {_nodes.Count}");
            
            var tasks = new List<Task>();
            var nodeIds = new List<string>();

            foreach (var node in _nodes.Values)
            {
                if (node.NodeId != _nodeId) // 不向自己发送
                {
                    nodeIds.Add(node.NodeId);
                    tasks.Add(SendAsync(node.NodeId, message));
                }
            }

            _logger.LogWarning($"[WebSocketClusterTransport] 广播消息到节点 - TargetNodeIds: [{string.Join(", ", nodeIds)}], CurrentNodeId: {_nodeId}, 任务数: {tasks.Count}");
            
            try
            {
                await Task.WhenAll(tasks);
                _logger.LogWarning($"[WebSocketClusterTransport] 广播消息完成 - MessageType: {message.Type}, CurrentNodeId: {_nodeId}, 成功节点数: {tasks.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[WebSocketClusterTransport] 广播消息时发生异常 - MessageType: {message.Type}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                // 不抛出异常，允许部分节点失败
            }
        }

        /// <summary>
        /// Check if node is connected / 检查节点是否已连接
        /// </summary>
        /// <param name="nodeId">Node ID to check / 要检查的节点 ID</param>
        /// <returns>True if connected, false otherwise / 已连接返回 true，否则返回 false</returns>
        public bool IsNodeConnected(string nodeId)
        {
            if (_connections.TryGetValue(nodeId, out var connection))
            {
                return connection.State == WebSocketState.Open;
            }
            return false;
        }

        /// <summary>
        /// Measure network latency to a node / 测量到节点的网络延迟
        /// </summary>
        /// <param name="nodeId">Target node ID / 目标节点 ID</param>
        /// <returns>Latency in milliseconds, or -1 if node is not connected / 延迟（毫秒），如果节点未连接则返回 -1</returns>
        public async Task<long> MeasureLatencyAsync(string nodeId)
        {
            if (!IsNodeConnected(nodeId))
            {
                return -1;
            }

            try
            {
                // Measure latency by sending a small message and measuring response time
                // 通过发送小消息并测量响应时间来测量延迟
                // For simplicity, we'll use connection state and a simple heuristic
                // 为简单起见，我们将使用连接状态和简单的启发式方法
                
                // Check connection state and estimate latency based on connection quality
                // 检查连接状态并根据连接质量估计延迟
                if (_connections.TryGetValue(nodeId, out var connection))
                {
                    if (connection.State == WebSocketState.Open)
                    {
                        // Estimate latency: if connection is stable, assume low latency
                        // 估计延迟：如果连接稳定，假设低延迟
                        // In a production system, you'd track actual RTT from message exchanges
                        // 在生产系统中，您会跟踪消息交换的实际RTT
                        
                        // For now, return a conservative estimate (50ms for local, 100ms for remote)
                        // 目前，返回保守估计（本地50ms，远程100ms）
                        var node = _nodes.TryGetValue(nodeId, out var n) ? n : null;
                        if (node != null)
                        {
                            // If address is localhost, assume low latency / 如果地址是localhost，假设低延迟
                            if (node.Address == "localhost" || node.Address == "127.0.0.1")
                            {
                                return 5; // Very low latency for local / 本地延迟非常低
                            }
                        }
                        return 50; // Default estimate for connected node / 已连接节点的默认估计
                    }
                }
                
                return -1;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to measure latency to node {nodeId}");
                return -1;
            }
        }

        /// <summary>
        /// Get network quality score for a node (0-100, higher is better) / 获取节点的网络质量分数（0-100，越高越好）
        /// </summary>
        /// <param name="nodeId">Target node ID / 目标节点 ID</param>
        /// <returns>Quality score (0-100) / 质量分数（0-100）</returns>
        public async Task<int> GetNetworkQualityAsync(string nodeId)
        {
            if (!IsNodeConnected(nodeId))
            {
                return 0;
            }

            try
            {
                var latency = await MeasureLatencyAsync(nodeId);
                if (latency < 0)
                {
                    return 0;
                }

                // Calculate quality score based on latency / 基于延迟计算质量分数
                // Lower latency = higher quality / 延迟越低 = 质量越高
                // 0ms = 100, 100ms = 50, 200ms+ = 0
                var quality = Math.Max(0, 100 - (int)(latency / 2));
                return quality;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to get network quality for node {nodeId}");
                return 0;
            }
        }

        /// <summary>
        /// Register a node to connect to / 注册要连接的节点
        /// </summary>
        /// <param name="node">Node information / 节点信息</param>
        public void RegisterNode(ClusterNode node)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));

            _nodes.AddOrUpdate(node.NodeId, node, (key, oldValue) => node);
        }

        /// <summary>
        /// Dispose resources / 释放资源
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                StopAsync().GetAwaiter().GetResult();
                _cancellationTokenSource.Dispose();
                _disposed = true;
            }
        }
    }
}

