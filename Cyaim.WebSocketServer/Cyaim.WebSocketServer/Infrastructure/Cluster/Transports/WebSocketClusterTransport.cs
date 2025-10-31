using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
            // Connections will be established when SendAsync or BroadcastAsync is called
            // 连接将在调用 SendAsync 或 BroadcastAsync 时建立
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
                
                await client.ConnectAsync(uri, _cancellationTokenSource.Token);
                
                if (_connections.TryAdd(nodeId, client))
                {
                    _nodes.TryAdd(nodeId, node);
                    NodeConnected?.Invoke(this, new ClusterNodeEventArgs { NodeId = nodeId });
                    _logger.LogInformation($"Connected to node {nodeId} at {uri}");
                    
                    // Start receiving messages from this connection / 开始从此连接接收消息
                    _ = ReceiveMessagesAsync(nodeId, client);
                    
                    return client;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to connect to node {nodeId}");
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
                while (webSocket.State == WebSocketState.Open && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                    
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection closed", CancellationToken.None);
                        break;
                    }

                    if (result.EndOfMessage && result.Count > 0)
                    {
                        var messageJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        try
                        {
                            var message = JsonSerializer.Deserialize<ClusterMessage>(messageJson);
                            message.FromNodeId = nodeId;
                            
                            MessageReceived?.Invoke(this, new ClusterMessageEventArgs
                            {
                                FromNodeId = nodeId,
                                Message = message
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Failed to deserialize message from node {nodeId}");
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
            if (string.IsNullOrEmpty(nodeId))
                throw new ArgumentNullException(nameof(nodeId));

            if (!_nodes.TryGetValue(nodeId, out var node))
            {
                _logger.LogWarning($"Node {nodeId} not found, cannot send message");
                return;
            }

            var connection = await GetOrCreateConnection(nodeId, node);
            if (connection == null || connection.State != WebSocketState.Open)
            {
                _logger.LogWarning($"Cannot send message to node {nodeId}, connection not available");
                return;
            }

            message.FromNodeId = _nodeId;
            message.ToNodeId = nodeId;
            var messageJson = JsonSerializer.Serialize(message);
            var messageBytes = Encoding.UTF8.GetBytes(messageJson);

            try
            {
                await connection.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, _cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send message to node {nodeId}");
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
            var tasks = new List<Task>();

            foreach (var node in _nodes.Values)
            {
                tasks.Add(SendAsync(node.NodeId, message));
            }

            await Task.WhenAll(tasks);
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

