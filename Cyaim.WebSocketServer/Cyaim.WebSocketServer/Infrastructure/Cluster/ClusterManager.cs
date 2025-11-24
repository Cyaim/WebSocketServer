using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.Extensions.Logging;

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
        public async Task RegisterConnectionAsync(string connectionId, string endpoint = null)
        {
            await _router.RegisterConnectionAsync(connectionId, endpoint);
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

