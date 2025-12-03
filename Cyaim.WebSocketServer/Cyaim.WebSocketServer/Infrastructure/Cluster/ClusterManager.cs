using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Metrics;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;

namespace Cyaim.WebSocketServer.Infrastructure.Cluster
{
    /// <summary>
    /// Cluster manager for coordinating Raft consensus and WebSocket routing
    /// 用于协调 Raft 共识和 WebSocket 路由的集群管理器
    /// </summary>
    public class ClusterManager
    {
        private readonly ILogger<ClusterManager> _logger;
        private readonly IClusterTransport _transport;
        private readonly RaftNode _raftNode;
        private readonly ClusterRouter _router;
        private readonly string _nodeId;
        private readonly ClusterOption _clusterOption;
        private IWebSocketConnectionProvider _connectionProvider;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="transport">Cluster transport / 集群传输</param>
        /// <param name="raftNode">Raft node instance / Raft 节点实例</param>
        /// <param name="router">Cluster router / 集群路由器</param>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        /// <param name="clusterOption">Cluster configuration / 集群配置</param>
        public ClusterManager(
            ILogger<ClusterManager> logger,
            IClusterTransport transport,
            RaftNode raftNode,
            ClusterRouter router,
            string nodeId,
            ClusterOption clusterOption)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _raftNode = raftNode ?? throw new ArgumentNullException(nameof(raftNode));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
            _clusterOption = clusterOption ?? throw new ArgumentNullException(nameof(clusterOption));
        }

        /// <summary>
        /// Get connection routing table (read-only) / 获取连接路由表（只读）
        /// </summary>
        public IReadOnlyDictionary<string, string> ConnectionRoutes => _router.ConnectionRoutes;

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
            _router.SetMetricsCollector(metricsCollector);
        }

        /// <summary>
        /// Initialize and start the cluster
        /// 初始化并启动集群
        /// </summary>
        public async Task StartAsync()
        {
            _logger.LogInformation($"Starting cluster manager for node {_nodeId}");

            try
            {
                // Parse and register cluster nodes / 解析并注册集群节点
                await RegisterClusterNodesAsync();

                // Start transport / 启动传输
                await _transport.StartAsync();

                // Start Raft node / 启动 Raft 节点
                await _raftNode.StartAsync();

                _logger.LogInformation($"Cluster manager started successfully for node {_nodeId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to start cluster manager for node {_nodeId}");
                throw;
            }
        }

        /// <summary>
        /// Gracefully shutdown the cluster with connection transfer / 优雅关闭集群并转移连接
        /// </summary>
        /// <param name="force">Force shutdown without transfer / 强制关闭不转移连接</param>
        /// <returns>Task / 任务</returns>
        public async Task ShutdownAsync(bool force = false)
        {
            _logger.LogInformation($"Shutting down cluster manager for node {_nodeId} (force: {force})");

            try
            {
                if (!force)
                {
                    // Graceful shutdown: transfer connections before stopping / 优雅关闭：在停止前转移连接
                    await _router.GracefulShutdownAsync(_clusterOption);
                }

                await StopAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error during cluster shutdown for node {_nodeId}");
                throw;
            }
        }

        /// <summary>
        /// Stop the cluster
        /// 停止集群
        /// </summary>
        public async Task StopAsync()
        {
            _logger.LogInformation($"Stopping cluster manager for node {_nodeId}");

            try
            {
                await _raftNode.StopAsync();
                await _transport.StopAsync();

                _logger.LogInformation($"Cluster manager stopped for node {_nodeId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error stopping cluster manager for node {_nodeId}");
                throw;
            }
        }

        /// <summary>
        /// Register a WebSocket connection to this node
        /// 将 WebSocket 连接注册到此节点
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="endpoint">Endpoint path / 端点路径</param>
        /// <param name="remoteIpAddress">Remote IP address / 远程 IP 地址</param>
        /// <param name="remotePort">Remote port / 远程端口</param>
        public async Task RegisterConnectionAsync(string connectionId, string endpoint = null, string remoteIpAddress = null, int remotePort = 0)
        {
            await _router.RegisterConnectionAsync(connectionId, endpoint, remoteIpAddress, remotePort);
        }

        /// <summary>
        /// Unregister a WebSocket connection
        /// 注销 WebSocket 连接
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        public async Task UnregisterConnectionAsync(string connectionId)
        {
            await _router.UnregisterConnectionAsync(connectionId);
        }

        /// <summary>
        /// Route WebSocket message (local or remote)
        /// 路由 WebSocket 消息（本地或远程）
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="data">Message data / 消息数据</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <returns>True if routed successfully / 路由成功返回 true</returns>
        public async Task<bool> RouteMessageAsync(string connectionId, byte[] data, int messageType)
        {
            return await _router.RouteMessageAsync(connectionId, data, messageType, null);
        }

        /// <summary>
        /// Route WebSocket message with local handler
        /// 使用本地处理程序路由 WebSocket 消息
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="data">Message data / 消息数据</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="localHandler">Handler for local connections / 本地连接的处理程序</param>
        /// <returns>True if routed successfully / 路由成功返回 true</returns>
        public async Task<bool> RouteMessageAsync(
            string connectionId, 
            byte[] data, 
            int messageType, 
            Func<string, System.Net.WebSockets.WebSocket, Task> localHandler)
        {
            return await _router.RouteMessageAsync(connectionId, data, messageType, localHandler);
        }

        /// <summary>
        /// Route WebSocket message to multiple connections (supports cross-node)
        /// 向多个连接路由 WebSocket 消息（支持跨节点）
        /// </summary>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表</param>
        /// <param name="data">Message data / 消息数据</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <returns>Dictionary of connection ID to routing result / 连接ID到路由结果的字典</returns>
        /// <remarks>
        /// This method can send messages to clients on different nodes.
        /// It automatically routes messages to the correct node for each connection.
        /// 此方法可以向不同节点上的客户端发送消息。
        /// 它会自动将消息路由到每个连接的正确节点。
        /// </remarks>
        public async Task<Dictionary<string, bool>> RouteMessagesAsync(
            IEnumerable<string> connectionIds, 
            byte[] data, 
            int messageType)
        {
            if (connectionIds == null)
            {
                throw new ArgumentNullException(nameof(connectionIds));
            }

            var results = new Dictionary<string, bool>();
            var tasks = new List<Task>();

            foreach (var connectionId in connectionIds)
            {
                if (string.IsNullOrEmpty(connectionId))
                {
                    continue;
                }

                var task = Task.Run(async () =>
                {
                    try
                    {
                        var success = await RouteMessageAsync(connectionId, data, messageType);
                        results[connectionId] = success;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to route message to connection {connectionId}");
                        results[connectionId] = false;
                    }
                });

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);
            return results;
        }

        #region Text Message Methods / 文本消息方法

        /// <summary>
        /// Route text message to a connection (supports cross-node)
        /// 向连接路由文本消息（支持跨节点）
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="text">Text message / 文本消息</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>True if routed successfully / 路由成功返回 true</returns>
        public async Task<bool> RouteTextAsync(string connectionId, string text, Encoding encoding = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            encoding ??= Encoding.UTF8;
            var data = encoding.GetBytes(text);
            return await RouteMessageAsync(connectionId, data, (int)WebSocketMessageType.Text);
        }

        /// <summary>
        /// Route text message to multiple connections (supports cross-node)
        /// 向多个连接路由文本消息（支持跨节点）
        /// </summary>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表</param>
        /// <param name="text">Text message / 文本消息</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>Dictionary of connection ID to routing result / 连接ID到路由结果的字典</returns>
        public async Task<Dictionary<string, bool>> RouteTextsAsync(
            IEnumerable<string> connectionIds, 
            string text, 
            Encoding encoding = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new Dictionary<string, bool>();
            }

            encoding ??= Encoding.UTF8;
            var data = encoding.GetBytes(text);
            return await RouteMessagesAsync(connectionIds, data, (int)WebSocketMessageType.Text);
        }

        #endregion

        #region Generic Serialization Methods / 泛型序列化方法

        /// <summary>
        /// Route object as JSON text message to a connection (supports cross-node)
        /// 将对象序列化为 JSON 文本消息路由到连接（支持跨节点）
        /// </summary>
        /// <typeparam name="T">Object type / 对象类型</typeparam>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="options">JSON serializer options / JSON 序列化选项</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>True if routed successfully / 路由成功返回 true</returns>
        public async Task<bool> RouteJsonAsync<T>(
            string connectionId, 
            T data, 
            JsonSerializerOptions options = null, 
            Encoding encoding = null)
        {
            if (data == null)
            {
                return false;
            }

            encoding ??= Encoding.UTF8;
            var json = JsonSerializer.Serialize(data, options);
            var bytes = encoding.GetBytes(json);
            return await RouteMessageAsync(connectionId, bytes, (int)WebSocketMessageType.Text);
        }

        /// <summary>
        /// Route object as JSON text message to multiple connections (supports cross-node)
        /// 将对象序列化为 JSON 文本消息路由到多个连接（支持跨节点）
        /// </summary>
        /// <typeparam name="T">Object type / 对象类型</typeparam>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表</param>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="options">JSON serializer options / JSON 序列化选项</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>Dictionary of connection ID to routing result / 连接ID到路由结果的字典</returns>
        public async Task<Dictionary<string, bool>> RouteJsonsAsync<T>(
            IEnumerable<string> connectionIds, 
            T data, 
            JsonSerializerOptions options = null, 
            Encoding encoding = null)
        {
            if (data == null)
            {
                return new Dictionary<string, bool>();
            }

            encoding ??= Encoding.UTF8;
            var json = JsonSerializer.Serialize(data, options);
            var bytes = encoding.GetBytes(json);
            return await RouteMessagesAsync(connectionIds, bytes, (int)WebSocketMessageType.Text);
        }

        /// <summary>
        /// Route object using custom serializer to a connection (supports cross-node)
        /// 使用自定义序列化器将对象路由到连接（支持跨节点）
        /// </summary>
        /// <typeparam name="T">Object type / 对象类型</typeparam>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="serializer">Custom serializer function / 自定义序列化函数</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <returns>True if routed successfully / 路由成功返回 true</returns>
        /// <remarks>
        /// The serializer function should convert the object to byte array.
        /// You can use JSON, MessagePack, Protobuf, or any other serialization format.
        /// 序列化器函数应将对象转换为字节数组。
        /// 您可以使用 JSON、MessagePack、Protobuf 或任何其他序列化格式。
        /// </remarks>
        public async Task<bool> RouteObjectAsync<T>(
            string connectionId, 
            T data, 
            Func<T, byte[]> serializer, 
            WebSocketMessageType messageType = WebSocketMessageType.Text)
        {
            if (data == null || serializer == null)
            {
                return false;
            }

            try
            {
                var bytes = serializer(data);
                return await RouteMessageAsync(connectionId, bytes, (int)messageType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to serialize and route object to connection {connectionId}");
                return false;
            }
        }

        /// <summary>
        /// Route object using custom serializer to multiple connections (supports cross-node)
        /// 使用自定义序列化器将对象路由到多个连接（支持跨节点）
        /// </summary>
        /// <typeparam name="T">Object type / 对象类型</typeparam>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表</param>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="serializer">Custom serializer function / 自定义序列化函数</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <returns>Dictionary of connection ID to routing result / 连接ID到路由结果的字典</returns>
        /// <remarks>
        /// The serializer function should convert the object to byte array.
        /// You can use JSON, MessagePack, Protobuf, or any other serialization format.
        /// 序列化器函数应将对象转换为字节数组。
        /// 您可以使用 JSON、MessagePack、Protobuf 或任何其他序列化格式。
        /// </remarks>
        public async Task<Dictionary<string, bool>> RouteObjectsAsync<T>(
            IEnumerable<string> connectionIds, 
            T data, 
            Func<T, byte[]> serializer, 
            WebSocketMessageType messageType = WebSocketMessageType.Text)
        {
            if (data == null || serializer == null)
            {
                return new Dictionary<string, bool>();
            }

            try
            {
                var bytes = serializer(data);
                return await RouteMessagesAsync(connectionIds, bytes, (int)messageType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to serialize object for routing");
                // Return failed results for all connections / 为所有连接返回失败结果
                var results = new Dictionary<string, bool>();
                foreach (var connectionId in connectionIds ?? Enumerable.Empty<string>())
                {
                    if (!string.IsNullOrEmpty(connectionId))
                    {
                        results[connectionId] = false;
                    }
                }
                return results;
            }
        }

        #endregion

        /// <summary>
        /// Get connection count for this node
        /// 获取此节点的连接数
        /// </summary>
        /// <returns>Connection count / 连接数</returns>
        public int GetLocalConnectionCount()
        {
            return _router.GetLocalConnectionCount();
        }

        /// <summary>
        /// Get total cluster connection count
        /// 获取集群总连接数
        /// </summary>
        /// <returns>Total connection count / 总连接数</returns>
        public int GetTotalConnectionCount()
        {
            return _router.GetTotalConnectionCount();
        }

        /// <summary>
        /// Check if this node is the leader
        /// 检查此节点是否为领导者
        /// </summary>
        /// <returns>True if leader / 如果是领导者返回 true</returns>
        public bool IsLeader()
        {
            return _raftNode.IsLeader();
        }

        /// <summary>
        /// Get optimal node for load balancing
        /// 获取用于负载均衡的最优节点
        /// </summary>
        /// <returns>Optimal node ID / 最优节点 ID</returns>
        public string GetOptimalNode()
        {
            return _router.GetOptimalNode();
        }

        /// <summary>
        /// Get connection metadata / 获取连接元数据
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接ID</param>
        /// <returns>Connection metadata or null / 连接元数据或null</returns>
        public ConnectionMetadata GetConnectionMetadata(string connectionId)
        {
            return _router.GetConnectionMetadata(connectionId);
        }

        /// <summary>
        /// Get connection endpoint / 获取连接端点
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接ID</param>
        /// <returns>Endpoint path or null / 端点路径或null</returns>
        public string GetConnectionEndpoint(string connectionId)
        {
            return _router.GetConnectionEndpoint(connectionId);
        }

        /// <summary>
        /// Register cluster nodes from configuration / 从配置注册集群节点
        /// </summary>
        private async Task RegisterClusterNodesAsync()
        {
            if (_clusterOption.Nodes == null || _clusterOption.Nodes.Length == 0)
            {
                _logger.LogWarning("No cluster nodes configured");
                return;
            }

            foreach (var nodeConfig in _clusterOption.Nodes)
            {
                try
                {
                    var node = ParseNodeConfig(nodeConfig);
                    if (node != null && node.NodeId != _nodeId)
                    {
                        RegisterNodeInTransport(node);
                        _logger.LogDebug($"Registered cluster node: {node.NodeId} at {node.FullAddress}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to register node from config: {nodeConfig}");
                }
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Parse node configuration string / 解析节点配置字符串
        /// </summary>
        /// <param name="nodeConfig">Node configuration string / 节点配置字符串</param>
        /// <returns>Parsed cluster node or null / 解析后的集群节点或 null</returns>
        private ClusterNode ParseNodeConfig(string nodeConfig)
        {
            if (string.IsNullOrEmpty(nodeConfig))
                return null;

            try
            {
                var node = new ClusterNode();

                // Try to parse as URI first / 首先尝试解析为 URI
                if (Uri.TryCreate(nodeConfig, UriKind.Absolute, out var uri))
                {
                    node.Protocol = uri.Scheme;
                    node.Address = uri.Host;
                    node.Port = uri.Port > 0 ? uri.Port : (uri.Scheme == "ws" || uri.Scheme == "wss" ? 80 : 0);
                    
                    // Extract node ID from path or use address:port / 从路径提取节点 ID 或使用 address:port
                    var path = uri.PathAndQuery.TrimStart('/');
                    node.NodeId = !string.IsNullOrEmpty(path) ? path : $"{uri.Host}:{uri.Port}";
                }
                else
                {
                    // Try format: nodeId@address:port or nodeId@address / 尝试格式：nodeId@address:port 或 nodeId@address
                    var parts = nodeConfig.Split('@');
                    if (parts.Length == 2)
                    {
                        node.NodeId = parts[0];
                        var addressParts = parts[1].Split(':');
                        node.Address = addressParts[0];
                        node.Port = addressParts.Length > 1 && int.TryParse(addressParts[1], out var port) ? port : 0;
                    }
                    else
                    {
                        // Assume it's just a node ID / 假设它只是一个节点 ID
                        node.NodeId = nodeConfig;
                    }
                }

                // Set protocol based on transport type / 根据传输类型设置协议
                node.Protocol = _clusterOption.TransportType?.ToLower() ?? "ws";

                // Set transport config if needed / 如果需要，设置传输配置
                if (node.Protocol == "redis" && !string.IsNullOrEmpty(_clusterOption.RedisConnectionString))
                {
                    node.TransportConfig = _clusterOption.RedisConnectionString;
                }
                else if (node.Protocol == "rabbitmq" && !string.IsNullOrEmpty(_clusterOption.RabbitMQConnectionString))
                {
                    node.TransportConfig = _clusterOption.RabbitMQConnectionString;
                }
                else if (node.Protocol == "ws" || node.Protocol == "wss")
                {
                    node.TransportConfig = _clusterOption.ChannelName;
                }

                return node;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to parse node config: {nodeConfig}");
                return null;
            }
        }

        /// <summary>
        /// Register node in transport / 在传输中注册节点
        /// </summary>
        /// <param name="node">Cluster node to register / 要注册的集群节点</param>
        private void RegisterNodeInTransport(ClusterNode node)
        {
            // Register node in transport based on type / 根据类型在传输中注册节点
            if (_transport is Transports.WebSocketClusterTransport wsTransport)
            {
                wsTransport.RegisterNode(node);
            }
            // Note: Redis and RabbitMQ transports are in separate packages
            // 注意：Redis 和 RabbitMQ 传输在单独的包中
            // They should implement a common interface or pattern for RegisterNode
            // 它们应该实现一个通用的接口或模式用于 RegisterNode
            // For now, we'll use reflection or check if the transport has RegisterNode method
            // 目前，我们将使用反射或检查传输是否有 RegisterNode 方法
            var registerMethod = _transport.GetType().GetMethod("RegisterNode");
            if (registerMethod != null)
            {
                registerMethod.Invoke(_transport, new object[] { node });
            }
        }
    }
}

