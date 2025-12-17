using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
        private Timer _connectionRouteRefreshTimer; // 定时器，用于刷新连接路由过期时间
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

        // Connection metadata: connectionId -> connection info / 连接元数据：连接ID -> 连接信息
        /// <summary>
        /// Connection metadata mapping / 连接元数据映射
        /// </summary>
        private readonly ConcurrentDictionary<string, ConnectionMetadata> _connectionMetadata;

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

        // Stream chunks: streamId -> list of chunks / 流块：流ID -> 块列表
        /// <summary>
        /// Stream chunks for reassembly / 用于重组的流块
        /// </summary>
        private readonly ConcurrentDictionary<string, StreamChunkBuffer> _streamChunks;

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
            _connectionMetadata = new ConcurrentDictionary<string, ConnectionMetadata>();
            _nodeConnectionCounts = new ConcurrentDictionary<string, int>();
            _pendingForwards = new ConcurrentDictionary<string, TaskCompletionSource<byte[]>>();
            _streamChunks = new ConcurrentDictionary<string, StreamChunkBuffer>();

            _transport.MessageReceived += OnTransportMessageReceived;
            _transport.NodeDisconnected += OnNodeDisconnected;

            // Start periodic node health check / 启动定期节点健康检查
            _nodeHealthCheckTimer = new Timer(CheckNodeHealth, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            // Start periodic connection route refresh / 启动定期连接路由刷新
            // 每 12 小时刷新一次，确保在 24 小时过期前刷新
            // Refresh every 12 hours to ensure refresh before 24-hour expiration
            _connectionRouteRefreshTimer = new Timer(RefreshConnectionRoutes, null, TimeSpan.FromHours(12), TimeSpan.FromHours(12));
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
        /// <param name="remoteIpAddress">Remote IP address / 远程 IP 地址</param>
        /// <param name="remotePort">Remote port / 远程端口</param>
        public async Task RegisterConnectionAsync(string connectionId, string endpoint = null, string remoteIpAddress = null, int remotePort = 0)
        {
            _logger.LogWarning($"[ClusterRouter] 注册连接 - ConnectionId: {connectionId}, NodeId: {_nodeId}, Endpoint: {endpoint}, RemoteIp: {remoteIpAddress}, RemotePort: {remotePort}");

            _connectionRoutes.AddOrUpdate(connectionId, _nodeId, (key, oldValue) =>
            {
                if (oldValue != _nodeId)
                {
                    _logger.LogWarning($"[ClusterRouter] 连接从其他节点转移 - ConnectionId: {connectionId}, OldNodeId: {oldValue}, NewNodeId: {_nodeId}");
                }
                return _nodeId;
            });

            // Store endpoint information / 存储端点信息
            if (!string.IsNullOrEmpty(endpoint))
            {
                _connectionEndpoints.AddOrUpdate(connectionId, endpoint, (key, oldValue) => endpoint);
            }

            // Store connection metadata / 存储连接元数据
            var metadata = new ConnectionMetadata
            {
                RemoteIpAddress = remoteIpAddress,
                RemotePort = remotePort,
                ConnectedAt = DateTime.UtcNow
            };
            _connectionMetadata.AddOrUpdate(connectionId, metadata, (key, oldValue) => metadata);

            _nodeConnectionCounts.AddOrUpdate(_nodeId, 1, (key, value) => value + 1);

            // 更新集群连接数指标
            _metricsCollector?.UpdateClusterConnectionCount(1, _nodeId);

            // 尝试通过传输层存储连接路由信息（如果支持）
            // Try to store connection route info through transport layer (if supported)
            var routeMetadata = new Dictionary<string, string>
            {
                ["Endpoint"] = endpoint ?? string.Empty,
                ["RemoteIpAddress"] = remoteIpAddress ?? string.Empty,
                ["RemotePort"] = remotePort.ToString(),
                ["RegisteredAt"] = DateTime.UtcNow.ToString("O")
            };

            var stored = await _transport.StoreConnectionRouteAsync(connectionId, _nodeId, routeMetadata);

            if (stored)
            {
                _logger.LogWarning($"[ClusterRouter] 连接路由已通过传输层存储 - ConnectionId: {connectionId}, NodeId: {_nodeId}");
            }
            else
            {
                // 如果传输层不支持存储，使用广播方式（向后兼容）
                // If transport doesn't support storage, use broadcast (backward compatibility)
                // Always broadcast connection registration to cluster (not just when leader)
                // 始终向集群广播连接注册（不仅仅是领导者时）
                // This ensures all nodes know about all connections
                // 这确保所有节点都知道所有连接
                var registration = new WebSocketConnectionRegistration
                {
                    ConnectionId = connectionId,
                    NodeId = _nodeId,
                    Endpoint = endpoint,
                    RemoteIpAddress = remoteIpAddress,
                    RemotePort = remotePort,
                    RegisteredAt = DateTime.UtcNow
                };

                var message = new ClusterMessage
                {
                    Type = ClusterMessageType.RegisterWebSocketConnection,
                    Payload = JsonSerializer.Serialize(registration)
                };

                try
                {
                    _logger.LogWarning($"[ClusterRouter] 广播连接注册消息 - ConnectionId: {connectionId}, NodeId: {_nodeId}");
                    await _transport.BroadcastAsync(message);
                    _logger.LogWarning($"[ClusterRouter] 连接注册广播成功 - ConnectionId: {connectionId}, NodeId: {_nodeId}, 当前路由表大小: {_connectionRoutes.Count}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[ClusterRouter] 连接注册广播失败 - ConnectionId: {connectionId}, NodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                    throw;
                }
            }
        }

        /// <summary>
        /// Unregister a WebSocket connection
        /// 注销 WebSocket 连接
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        public async Task UnregisterConnectionAsync(string connectionId)
        {
            // 尝试从路由表中获取节点ID（可能不在路由表中，但仍然需要删除 Redis 中的记录）
            // Try to get node ID from routing table (may not be in routing table, but still need to remove from Redis)
            var nodeId = _connectionRoutes.GetValueOrDefault(connectionId);
            var wasInRoutingTable = _connectionRoutes.TryRemove(connectionId, out _);

            if (wasInRoutingTable)
            {
                // Remove endpoint information / 移除端点信息
                _connectionEndpoints.TryRemove(connectionId, out _);

                // Remove connection metadata / 移除连接元数据
                _connectionMetadata.TryRemove(connectionId, out _);

                if (!string.IsNullOrEmpty(nodeId))
                {
                    _nodeConnectionCounts.AddOrUpdate(nodeId, 0, (key, value) => Math.Max(0, value - 1));

                    // 更新集群连接数指标
                    _metricsCollector?.UpdateClusterConnectionCount(-1, nodeId);
                }
            }

            // 无论是否在路由表中，都尝试从 Redis 删除连接路由信息
            // 这确保即使路由表不一致，Redis 中的记录也能被正确删除
            // Whether in routing table or not, always try to remove connection route info from Redis
            // This ensures Redis records are properly deleted even if routing table is inconsistent
            var removed = await _transport.RemoveConnectionRouteAsync(connectionId);

            if (removed)
            {
                _logger.LogWarning($"[ClusterRouter] 连接路由已通过传输层删除 - ConnectionId: {connectionId}, NodeId: {nodeId ?? "unknown"}, WasInRoutingTable: {wasInRoutingTable}");
            }
            else if (wasInRoutingTable && !string.IsNullOrEmpty(nodeId))
            {
                // 如果传输层不支持删除，使用广播方式（向后兼容）
                // If transport doesn't support removal, use broadcast (backward compatibility)
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
            else
            {
                _logger.LogWarning($"[ClusterRouter] 连接不在路由表中，仅从 Redis 删除 - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}");
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
            _logger.LogWarning($"[ClusterRouter] 开始路由消息 - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}, MessageSize: {data.Length} bytes, MessageType: {messageType}, RoutingTableSize: {_connectionRoutes.Count}");

            if (_connectionRoutes.TryGetValue(connectionId, out var targetNodeId))
            {
                _logger.LogWarning($"[ClusterRouter] 连接在路由表中找到 - ConnectionId: {connectionId}, TargetNodeId: {targetNodeId}, CurrentNodeId: {_nodeId}");

                if (targetNodeId == _nodeId)
                {
                    // Local connection - handle directly / 本地连接 - 直接处理
                    _logger.LogInformation($"[ClusterRouter] 本地连接处理 - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}");

                    if (_connectionProvider != null)
                    {
                        var webSocket = _connectionProvider.GetConnection(connectionId);
                        if (webSocket != null && webSocket.State == WebSocketState.Open)
                        {
                            _logger.LogDebug($"[ClusterRouter] 本地连接可用 - ConnectionId: {connectionId}, WebSocketState: {webSocket.State}");
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
                            _logger.LogInformation($"[ClusterRouter] 本地连接消息发送成功 - ConnectionId: {connectionId}");
                            return true;
                        }
                        else
                        {
                            var state = webSocket?.State.ToString() ?? "null";
                            _logger.LogWarning($"[ClusterRouter] 本地连接不可用 - ConnectionId: {connectionId}, WebSocketState: {state}");
                            // Remove stale connection / 删除过时的连接
                            _connectionRoutes.TryRemove(connectionId, out _);
                            return false;
                        }
                    }
                    else
                    {
                        _logger.LogError($"[ClusterRouter] 连接提供者未设置 - ConnectionId: {connectionId}");
                        return false;
                    }
                }
                else
                {
                    // Remote connection - forward via transport / 远程连接 - 通过传输转发
                    _logger.LogWarning($"[ClusterRouter] 远程连接转发 - ConnectionId: {connectionId}, TargetNodeId: {targetNodeId}, CurrentNodeId: {_nodeId}, MessageSize: {data.Length} bytes, 路由表大小: {_connectionRoutes.Count}");

                    // 验证目标节点是否在已知节点列表中（使用接口方法，不依赖具体实现）
                    // Verify target node is in known nodes list (use interface method, don't depend on concrete implementation)
                    try
                    {
                        var isConnected = _transport.IsNodeConnected(targetNodeId);
                        _logger.LogWarning($"[ClusterRouter] 目标节点连接状态 - TargetNodeId: {targetNodeId}, IsConnected: {isConnected}, ConnectionId: {connectionId}, TransportType: {_transport.GetType().Name}");
                        if (!isConnected)
                        {
                            _logger.LogError($"[ClusterRouter] 目标节点未连接，无法转发 - ConnectionId: {connectionId}, TargetNodeId: {targetNodeId}, CurrentNodeId: {_nodeId}");
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        // 如果传输层不支持节点连接检查，记录警告但继续尝试发送
                        // If transport doesn't support node connection check, log warning but continue trying to send
                        _logger.LogWarning(ex, $"[ClusterRouter] 目标节点连接状态检查失败，继续尝试发送 - TargetNodeId: {targetNodeId}, ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}, TransportType: {_transport.GetType().Name}");
                    }

                    var result = await ForwardToNodeAsync(targetNodeId, connectionId, data, messageType);
                    if (!result)
                    {
                        _logger.LogError($"[ClusterRouter] 远程连接转发失败 - ConnectionId: {connectionId}, TargetNodeId: {targetNodeId}, CurrentNodeId: {_nodeId}, 路由表大小: {_connectionRoutes.Count}");
                    }
                    return result;
                }
            }
            else
            {
                // Connection not found - query cluster to find the node / 未找到连接 - 查询集群以找到节点
                // 无论是否为 leader，都尝试查询连接位置（因为连接可能在其他节点）
                // Whether leader or not, try to query connection location (connection might be on another node)
                _logger.LogError($"[ClusterRouter] 连接不在本地路由表中 - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}, RoutingTableSize: {_connectionRoutes.Count}, 开始查询集群...");

                // 先检查路由表中是否有类似的前缀匹配（用于调试）
                var similarConnections = _connectionRoutes.Keys.Where(k => k.StartsWith(connectionId.Substring(0, Math.Min(10, connectionId.Length)))).Take(5).ToArray();
                if (similarConnections.Length > 0)
                {
                    _logger.LogWarning($"[ClusterRouter] 发现类似连接ID（用于调试）- ConnectionId: {connectionId}, 类似连接: {string.Join(", ", similarConnections)}");
                }

                var found = await QueryConnectionAsync(connectionId);
                if (found != null)
                {
                    _logger.LogWarning($"[ClusterRouter] 查询成功找到连接 - ConnectionId: {connectionId}, FoundNodeId: {found}, CurrentNodeId: {_nodeId}, 开始转发消息");
                    var result = await ForwardToNodeAsync(found, connectionId, data, messageType);
                    if (!result)
                    {
                        _logger.LogError($"[ClusterRouter] 查询后转发失败 - ConnectionId: {connectionId}, FoundNodeId: {found}, CurrentNodeId: {_nodeId}");
                    }
                    return result;
                }

                _logger.LogError($"[ClusterRouter] 连接未找到且查询无结果 - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}, RoutingTableSize: {_connectionRoutes.Count}. 可能原因：1) 连接未在集群中注册 2) 连接注册广播失败 3) 其他节点未收到注册消息 4) 连接已断开但路由表未更新");
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
                _logger.LogWarning($"[ClusterRouter] 开始转发消息 - ConnectionId: {connectionId}, TargetNodeId: {targetNodeId}, CurrentNodeId: {_nodeId}, MessageSize: {data.Length} bytes");

                // 检查目标节点是否可用（如果传输层支持）
                // Check if target node is available (if transport supports it)
                // 使用接口方法，不依赖具体实现类型
                // Use interface method, don't depend on concrete implementation type
                try
                {
                    var isConnected = _transport.IsNodeConnected(targetNodeId);
                    _logger.LogWarning($"[ClusterRouter] 节点连接状态检查 - TargetNodeId: {targetNodeId}, IsConnected: {isConnected}, ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}, TransportType: {_transport.GetType().Name}");

                    if (!isConnected)
                    {
                        _logger.LogError($"[ClusterRouter] 无法转发消息 - TargetNodeId: {targetNodeId}, ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}, 原因: 节点未连接。请检查节点之间的连接是否已建立。");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    // 如果传输层不支持节点连接检查，记录警告但继续尝试发送
                    // If transport doesn't support node connection check, log warning but continue trying to send
                    _logger.LogWarning(ex, $"[ClusterRouter] 节点连接状态检查失败，继续尝试发送 - TargetNodeId: {targetNodeId}, ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}, TransportType: {_transport.GetType().Name}");
                }

                var forwardMessage = new ForwardWebSocketMessage
                {
                    ConnectionId = connectionId,
                    TargetNodeId = targetNodeId,
                    Data = data,
                    MessageType = messageType
                };

                // 为每个连接生成唯一的 MessageId，确保即使消息内容相同，也不会被去重
                // 格式：{nodeId}:{targetNodeId}:{connectionId}:{timestamp}:{guid}
                // 这样可以确保每个连接、每个时间点的消息都有唯一的 MessageId
                var uniqueMessageId = $"{_nodeId}:{targetNodeId}:{connectionId}:{DateTime.UtcNow.Ticks}:{Guid.NewGuid():N}";

                var message = new ClusterMessage
                {
                    Type = ClusterMessageType.ForwardWebSocketMessage,
                    MessageId = uniqueMessageId, // 设置唯一的 MessageId，避免基于消息内容去重
                    Payload = JsonSerializer.Serialize(forwardMessage)
                };

                _logger.LogWarning($"[ClusterRouter] 准备通过传输层发送消息 - ConnectionId: {connectionId}, TargetNodeId: {targetNodeId}, MessageId: {uniqueMessageId}, CurrentNodeId: {_nodeId}, MessageType: {message.Type}");

                await _transport.SendAsync(targetNodeId, message);

                // 记录集群消息转发指标
                _metricsCollector?.RecordClusterMessageForwarded(_nodeId, targetNodeId);

                _logger.LogWarning($"[ClusterRouter] 成功转发消息 - ConnectionId: {connectionId}, TargetNodeId: {targetNodeId}, CurrentNodeId: {_nodeId}");
                return true;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, $"[ClusterRouter] 转发消息失败（操作无效）- ConnectionId: {connectionId}, TargetNodeId: {targetNodeId}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                _metricsCollector?.RecordError("cluster_forward_failed", _nodeId);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[ClusterRouter] 转发消息失败（异常）- ConnectionId: {connectionId}, TargetNodeId: {targetNodeId}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                _metricsCollector?.RecordError("cluster_forward_failed", _nodeId);
                return false;
            }
        }

        /// <summary>
        /// Route stream to connection (local or remote) - supports chunked transmission
        /// 将流转发到连接（本地或远程）- 支持分块传输
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="stream">Stream to send / 要发送的流</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="chunkSize">Chunk size in bytes / 块大小（字节）</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <returns>True if routed successfully / 路由成功返回 true</returns>
        public async Task<bool> RouteStreamAsync(
            string connectionId,
            Stream stream,
            int messageType,
            int chunkSize = 64 * 1024,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(connectionId) || stream == null || !stream.CanRead)
            {
                return false;
            }

            // Check if connection is local or remote / 检查连接是本地还是远程
            if (_connectionRoutes.TryGetValue(connectionId, out var targetNodeId))
            {
                if (targetNodeId == _nodeId)
                {
                    // Local connection - send stream directly / 本地连接 - 直接发送流
                    _logger.LogDebug($"Routing stream to local connection {connectionId}");

                    if (_connectionProvider != null)
                    {
                        var webSocket = _connectionProvider.GetConnection(connectionId);
                        if (webSocket != null && webSocket.State == WebSocketState.Open)
                        {
                            try
                            {
                                // Use WebSocketManager to send stream / 使用 WebSocketManager 发送流
                                await Infrastructure.WebSocketManager.SendLocalAsync(
                                    stream,
                                    (WebSocketMessageType)messageType,
                                    cancellationToken,
                                    timeout: null,
                                    sendAtOnce: false,
                                    sendBufferSize: (uint)chunkSize,
                                    webSocket);
                                return true;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"Failed to send stream to local connection {connectionId}");
                                return false;
                            }
                        }
                        else
                        {
                            _logger.LogWarning($"Local connection {connectionId} is not available or closed");
                            _connectionRoutes.TryRemove(connectionId, out _);
                            return false;
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"Connection provider not set, cannot route stream to local connection {connectionId}");
                        return false;
                    }
                }
                else
                {
                    // Remote connection - forward stream chunks via transport / 远程连接 - 通过传输转发流块
                    return await ForwardStreamToNodeAsync(targetNodeId, connectionId, stream, messageType, chunkSize, cancellationToken);
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
                        return await ForwardStreamToNodeAsync(found, connectionId, stream, messageType, chunkSize, cancellationToken);
                    }
                }

                _logger.LogWarning($"Connection {connectionId} not found in routing table for stream routing");
                return false;
            }
        }

        /// <summary>
        /// Forward stream to specific node in chunks / 将流分块转发到指定节点
        /// </summary>
        /// <param name="targetNodeId">Target node ID / 目标节点 ID</param>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="stream">Stream to send / 要发送的流</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="chunkSize">Chunk size in bytes / 块大小（字节）</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <returns>True if forwarded successfully / 转发成功返回 true</returns>
        private async Task<bool> ForwardStreamToNodeAsync(
            string targetNodeId,
            string connectionId,
            Stream stream,
            int messageType,
            int chunkSize,
            CancellationToken cancellationToken)
        {
            try
            {
                var streamId = Guid.NewGuid().ToString("N");
                var buffer = new byte[chunkSize];
                int chunkIndex = 0;
                long totalSize = stream.CanSeek ? stream.Length : 0;
                bool isLastChunk = false;

                _logger.LogDebug($"Starting stream forwarding for connection {connectionId} to node {targetNodeId}, streamId: {streamId}");

                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, chunkSize, cancellationToken);

                    if (bytesRead == 0)
                    {
                        // End of stream / 流结束
                        break;
                    }

                    // Check if this is the last chunk / 检查是否是最后一块
                    isLastChunk = bytesRead < chunkSize;

                    // Create chunk data / 创建块数据
                    var chunkData = new byte[bytesRead];
                    Array.Copy(buffer, chunkData, bytesRead);

                    var forwardStream = new ForwardWebSocketStream
                    {
                        ConnectionId = connectionId,
                        TargetNodeId = targetNodeId,
                        StreamId = streamId,
                        ChunkIndex = chunkIndex,
                        IsLastChunk = isLastChunk,
                        Data = chunkData,
                        MessageType = messageType,
                        TotalSize = totalSize > 0 ? totalSize : (long?)null
                    };

                    var message = new ClusterMessage
                    {
                        Type = ClusterMessageType.ForwardWebSocketStream,
                        Payload = JsonSerializer.Serialize(forwardStream)
                    };

                    await _transport.SendAsync(targetNodeId, message);

                    // 记录集群消息转发指标
                    _metricsCollector?.RecordClusterMessageForwarded(_nodeId, targetNodeId);

                    chunkIndex++;

                    if (isLastChunk)
                    {
                        break;
                    }
                }

                _logger.LogDebug($"Completed stream forwarding for connection {connectionId} to node {targetNodeId}, streamId: {streamId}, chunks: {chunkIndex + 1}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to forward stream for connection {connectionId} to node {targetNodeId}");
                _metricsCollector?.RecordError("cluster_stream_forward_failed", _nodeId);
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
            _logger.LogWarning($"[ClusterRouter] 查询连接位置 - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}, 当前路由表连接数: {_connectionRoutes.Count}");

            // 首先尝试通过传输层查询连接路由（如果支持）
            // First try to query connection route through transport layer (if supported)
            var nodeId = await _transport.GetConnectionRouteAsync(connectionId);

            if (!string.IsNullOrEmpty(nodeId))
            {
                // 更新本地路由表缓存
                // Update local routing table cache
                _connectionRoutes.AddOrUpdate(connectionId, nodeId, (key, oldValue) => nodeId);
                _logger.LogWarning($"[ClusterRouter] 通过传输层查询成功 - ConnectionId: {connectionId}, NodeId: {nodeId}, CurrentNodeId: {_nodeId}");
                return nodeId;
            }

            // 如果传输层不支持查询或查询失败，使用广播方式（向后兼容）
            // If transport doesn't support query or query failed, use broadcast (backward compatibility)
            // 记录当前路由表中的一些连接ID（用于调试）
            var sampleConnections = _connectionRoutes.Keys.Take(10).ToArray();
            _logger.LogWarning($"[ClusterRouter] 路由表示例连接（前10个）: {string.Join(", ", sampleConnections)}");

            // 为查询消息生成唯一的 MessageId，避免被去重逻辑过滤
            // Generate unique MessageId for query message to avoid being filtered by deduplication logic
            var message = new ClusterMessage
            {
                Type = ClusterMessageType.QueryWebSocketConnection,
                Payload = JsonSerializer.Serialize(new { ConnectionId = connectionId }),
                MessageId = $"{_nodeId}:query:{connectionId}:{DateTime.UtcNow.Ticks}:{Guid.NewGuid():N}", // 生成唯一的 MessageId
                FromNodeId = _nodeId
            };

            // Broadcast query / 广播查询
            _logger.LogWarning($"[ClusterRouter] 广播查询消息 - ConnectionId: {connectionId}, MessageId: {message.MessageId}, CurrentNodeId: {_nodeId}");
            try
            {
                await _transport.BroadcastAsync(message);
                _logger.LogWarning($"[ClusterRouter] 查询消息广播成功 - ConnectionId: {connectionId}, MessageId: {message.MessageId}, CurrentNodeId: {_nodeId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[ClusterRouter] 查询消息广播失败 - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                return null;
            }

            // Wait for response with retries / 等待响应，带重试
            // 增加等待时间，因为网络延迟可能导致响应较慢
            // Increase wait time as network latency may cause slower responses
            for (int i = 0; i < 10; i++) // 增加到10次重试，总共最多500ms
            {
                await Task.Delay(50); // 每次等待50ms

                if (_connectionRoutes.TryGetValue(connectionId, out var foundNodeId))
                {
                    _logger.LogWarning($"[ClusterRouter] 查询成功 - ConnectionId: {connectionId}, NodeId: {foundNodeId}, 查询次数: {i + 1}, CurrentNodeId: {_nodeId}, 路由表大小: {_connectionRoutes.Count}");
                    return foundNodeId;
                }
            }

            _logger.LogError($"[ClusterRouter] 查询失败 - ConnectionId: {connectionId} 在集群中未找到，当前路由表连接数: {_connectionRoutes.Count}, CurrentNodeId: {_nodeId}。可能原因：1) 连接未注册 2) 注册广播未到达其他节点 3) 其他节点未处理注册消息");
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
            // #region agent log
            try
            {
                var logData = new
                {
                    location = "ClusterRouter.cs:690",
                    message = "OnTransportMessageReceived entry",
                    data = new
                    {
                        messageType = e.Message.Type.ToString(),
                        fromNodeId = e.FromNodeId,
                        toNodeId = e.Message.ToNodeId ?? "null",
                        messageId = e.Message.MessageId,
                        currentNodeId = _nodeId
                    },
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    sessionId = "debug-session",
                    runId = "run1",
                    hypothesisId = "E"
                };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData);
                System.IO.File.AppendAllText(@"e:\OneDrive\Work\WorkSpaces\.cursor\debug.log", logJson + Environment.NewLine);
            }
            catch { }
            // #endregion

            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogWarning($"[ClusterRouter] 收到集群消息 - MessageType: {e.Message.Type}, FromNodeId: {e.FromNodeId}, ToNodeId: {e.Message.ToNodeId}, MessageId: {e.Message.MessageId}, CurrentNodeId: {_nodeId}");

                    // #region agent log
                    try
                    {
                        var logData = new
                        {
                            location = "ClusterRouter.cs:710",
                            message = "Before switch statement",
                            data = new
                            {
                                messageType = e.Message.Type.ToString(),
                                fromNodeId = e.FromNodeId,
                                toNodeId = e.Message.ToNodeId ?? "null",
                                messageId = e.Message.MessageId,
                                currentNodeId = _nodeId
                            },
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            sessionId = "debug-session",
                            runId = "run1",
                            hypothesisId = "E"
                        };
                        var logJson = System.Text.Json.JsonSerializer.Serialize(logData);
                        System.IO.File.AppendAllText(@"e:\OneDrive\Work\WorkSpaces\.cursor\debug.log", logJson + Environment.NewLine);
                    }
                    catch { }
                    // #endregion

                    switch (e.Message.Type)
                    {
                        case ClusterMessageType.ForwardWebSocketMessage:
                            _logger.LogWarning($"[ClusterRouter] 处理转发WebSocket消息 - FromNodeId: {e.FromNodeId}, CurrentNodeId: {_nodeId}");
                            // #region agent log
                            try
                            {
                                var logData = new
                                {
                                    location = "ClusterRouter.cs:702",
                                    message = "Before HandleForwardMessage",
                                    data = new
                                    {
                                        messageType = e.Message.Type.ToString(),
                                        fromNodeId = e.FromNodeId,
                                        messageId = e.Message.MessageId,
                                        currentNodeId = _nodeId
                                    },
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                    sessionId = "debug-session",
                                    runId = "run1",
                                    hypothesisId = "E"
                                };
                                var logJson = System.Text.Json.JsonSerializer.Serialize(logData);
                                System.IO.File.AppendAllText(@"e:\OneDrive\Work\WorkSpaces\.cursor\debug.log", logJson + Environment.NewLine);
                            }
                            catch { }
                            // #endregion
                            await HandleForwardMessage(e.Message);
                            // #region agent log
                            try
                            {
                                var logData = new
                                {
                                    location = "ClusterRouter.cs:725",
                                    message = "After HandleForwardMessage",
                                    data = new
                                    {
                                        messageType = e.Message.Type.ToString(),
                                        fromNodeId = e.FromNodeId,
                                        messageId = e.Message.MessageId,
                                        currentNodeId = _nodeId
                                    },
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                    sessionId = "debug-session",
                                    runId = "run1",
                                    hypothesisId = "E"
                                };
                                var logJson = System.Text.Json.JsonSerializer.Serialize(logData);
                                System.IO.File.AppendAllText(@"e:\OneDrive\Work\WorkSpaces\.cursor\debug.log", logJson + Environment.NewLine);
                            }
                            catch { }
                            // #endregion
                            break;

                        case ClusterMessageType.ForwardWebSocketStream:
                            _logger.LogWarning($"[ClusterRouter] 处理转发WebSocket流 - FromNodeId: {e.FromNodeId}, CurrentNodeId: {_nodeId}");
                            await HandleForwardStream(e.Message);
                            break;

                        case ClusterMessageType.RegisterWebSocketConnection:
                            _logger.LogWarning($"[ClusterRouter] 处理连接注册消息 - FromNodeId: {e.FromNodeId}, CurrentNodeId: {_nodeId}");
                            HandleRegisterConnection(e.Message);
                            break;

                        case ClusterMessageType.UnregisterWebSocketConnection:
                            _logger.LogWarning($"[ClusterRouter] 处理连接注销消息 - FromNodeId: {e.FromNodeId}, CurrentNodeId: {_nodeId}");
                            HandleUnregisterConnection(e.Message);
                            break;

                        case ClusterMessageType.QueryWebSocketConnection:
                            _logger.LogWarning($"[ClusterRouter] 处理连接查询消息 - FromNodeId: {e.FromNodeId}, CurrentNodeId: {_nodeId}");
                            await HandleQueryConnection(e.Message);
                            break;

                        default:
                            _logger.LogWarning($"[ClusterRouter] 未知消息类型 - MessageType: {e.Message.Type}, FromNodeId: {e.FromNodeId}, CurrentNodeId: {_nodeId}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[ClusterRouter] 处理集群消息时发生异常 - MessageType: {e.Message.Type}, FromNodeId: {e.FromNodeId}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
                }
            });
        }

        /// <summary>
        /// Handle forward WebSocket stream / 处理转发 WebSocket 流
        /// </summary>
        /// <param name="message">Forward stream message / 转发流消息</param>
        private async Task HandleForwardStream(ClusterMessage message)
        {
            try
            {
                var forwardStream = JsonSerializer.Deserialize<ForwardWebSocketStream>(message.Payload);

                // If this is the target node, find local WebSocket and send
                // 如果这是目标节点，查找本地 WebSocket 并发送
                if (forwardStream.TargetNodeId == _nodeId)
                {
                    // 记录集群消息接收指标
                    _metricsCollector?.RecordClusterMessageReceived(forwardStream.TargetNodeId);

                    _logger.LogDebug($"Received stream chunk {forwardStream.ChunkIndex} for local connection {forwardStream.ConnectionId}, streamId: {forwardStream.StreamId}");

                    if (_connectionProvider != null)
                    {
                        var webSocket = _connectionProvider.GetConnection(forwardStream.ConnectionId);
                        if (webSocket != null && webSocket.State == WebSocketState.Open)
                        {
                            // Get or create stream buffer / 获取或创建流缓冲区
                            var buffer = _streamChunks.GetOrAdd(forwardStream.StreamId, _ => new StreamChunkBuffer
                            {
                                ConnectionId = forwardStream.ConnectionId,
                                MessageType = forwardStream.MessageType
                            });

                            // Add chunk to buffer / 将块添加到缓冲区
                            lock (buffer)
                            {
                                buffer.Chunks[forwardStream.ChunkIndex] = forwardStream.Data;
                                buffer.ReceivedChunks++;

                                // If this is the last chunk, mark as complete / 如果是最后一块，标记为完成
                                if (forwardStream.IsLastChunk)
                                {
                                    buffer.IsComplete = true;
                                }
                            }

                            // If stream is complete, send to WebSocket / 如果流完成，发送到 WebSocket
                            if (forwardStream.IsLastChunk)
                            {
                                // Wait a bit to ensure all chunks are received (in case of out-of-order delivery)
                                // 等待一下以确保收到所有块（以防乱序传递）
                                await Task.Delay(50);

                                lock (buffer)
                                {
                                    if (buffer.IsComplete)
                                    {
                                        // Reassemble stream and send / 重组流并发送
                                        var totalSize = buffer.Chunks.Values.Sum(chunk => chunk.Length);
                                        var streamData = new byte[totalSize];
                                        int offset = 0;

                                        // Sort chunks by index and copy to streamData / 按索引排序块并复制到 streamData
                                        foreach (var kvp in buffer.Chunks.OrderBy(c => c.Key))
                                        {
                                            Array.Copy(kvp.Value, 0, streamData, offset, kvp.Value.Length);
                                            offset += kvp.Value.Length;
                                        }

                                        // Send to WebSocket / 发送到 WebSocket
                                        _ = Task.Run(async () =>
                                        {
                                            try
                                            {
                                                var wsMessageType = (WebSocketMessageType)buffer.MessageType;
                                                await webSocket.SendAsync(
                                                    new ArraySegment<byte>(streamData),
                                                    wsMessageType,
                                                    true,
                                                    CancellationToken.None);

                                                _logger.LogDebug($"Successfully forwarded stream to local connection {buffer.ConnectionId}, streamId: {forwardStream.StreamId}");
                                            }
                                            catch (Exception ex)
                                            {
                                                _logger.LogError(ex, $"Failed to send stream to local connection {buffer.ConnectionId}");
                                            }
                                            finally
                                            {
                                                // Clean up buffer / 清理缓冲区
                                                _streamChunks.TryRemove(forwardStream.StreamId, out _);
                                            }
                                        });
                                    }
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning($"Local connection {forwardStream.ConnectionId} not found or closed");
                            _connectionRoutes.TryRemove(forwardStream.ConnectionId, out _);
                            // Clean up buffer / 清理缓冲区
                            _streamChunks.TryRemove(forwardStream.StreamId, out _);
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"Connection provider not set, cannot handle forward stream for {forwardStream.ConnectionId}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling forward stream message");
            }
        }

        /// <summary>
        /// Stream chunk buffer for reassembly / 用于重组的流块缓冲区
        /// </summary>
        private class StreamChunkBuffer
        {
            public string ConnectionId { get; set; }
            public int MessageType { get; set; }
            public Dictionary<int, byte[]> Chunks { get; } = new Dictionary<int, byte[]>();
            public int ReceivedChunks { get; set; }
            public bool IsComplete { get; set; }
        }

        /// <summary>
        /// Handle forward WebSocket message / 处理转发 WebSocket 消息
        /// </summary>
        /// <param name="message">Forward message / 转发消息</param>
        private async Task HandleForwardMessage(ClusterMessage message)
        {
            try
            {
                _logger.LogWarning($"[ClusterRouter] 收到转发消息 - MessageId: {message.MessageId}, FromNodeId: {message.FromNodeId}, ToNodeId: {message.ToNodeId}, CurrentNodeId: {_nodeId}");

                var forward = JsonSerializer.Deserialize<ForwardWebSocketMessage>(message.Payload);
                _logger.LogWarning($"[ClusterRouter] 解析转发消息 - ConnectionId: {forward.ConnectionId}, TargetNodeId: {forward.TargetNodeId}, DataSize: {forward.Data?.Length ?? 0} bytes, MessageType: {forward.MessageType}, CurrentNodeId: {_nodeId}");

                // If this is the target node, find local WebSocket and send
                // 如果这是目标节点，查找本地 WebSocket 并发送
                if (forward.TargetNodeId == _nodeId)
                {
                    _logger.LogWarning($"[ClusterRouter] 这是目标节点，处理本地连接 - ConnectionId: {forward.ConnectionId}, TargetNodeId: {forward.TargetNodeId}, CurrentNodeId: {_nodeId}");

                    // 记录集群消息接收指标
                    _metricsCollector?.RecordClusterMessageReceived(forward.TargetNodeId);

                    if (_connectionProvider != null)
                    {
                        var webSocket = _connectionProvider.GetConnection(forward.ConnectionId);
                        if (webSocket != null && webSocket.State == WebSocketState.Open)
                        {
                            _logger.LogWarning($"[ClusterRouter] 本地连接可用，发送消息 - ConnectionId: {forward.ConnectionId}, WebSocketState: {webSocket.State}, CurrentNodeId: {_nodeId}");
                            var wsMessageType = (WebSocketMessageType)forward.MessageType;
                            var success = await _connectionProvider.SendAsync(
                                forward.ConnectionId,
                                forward.Data,
                                wsMessageType);

                            if (success)
                            {
                                _logger.LogWarning($"[ClusterRouter] 成功转发消息到本地连接 - ConnectionId: {forward.ConnectionId}, CurrentNodeId: {_nodeId}");
                            }
                            else
                            {
                                _logger.LogError($"[ClusterRouter] 发送消息到本地连接失败 - ConnectionId: {forward.ConnectionId}, CurrentNodeId: {_nodeId}");
                            }
                        }
                        else
                        {
                            var state = webSocket?.State.ToString() ?? "null";
                            _logger.LogError($"[ClusterRouter] 本地连接不可用 - ConnectionId: {forward.ConnectionId}, WebSocketState: {state}, CurrentNodeId: {_nodeId}");
                            // Remove stale connection / 删除过时的连接
                            _connectionRoutes.TryRemove(forward.ConnectionId, out _);
                        }
                    }
                    else
                    {
                        _logger.LogError($"[ClusterRouter] 连接提供者未设置 - ConnectionId: {forward.ConnectionId}, CurrentNodeId: {_nodeId}");
                    }
                }
                else
                {
                    // Not for us - this should not happen if routing is correct
                    // 不是给我们的 - 如果路由正确，这不应该发生
                    // Log error but don't forward again to avoid infinite loop
                    // 记录错误但不再转发，避免无限循环
                    _logger.LogError($"[ClusterRouter] 收到转发消息但目标节点不匹配 - ConnectionId: {forward.ConnectionId}, TargetNodeId: {forward.TargetNodeId}, CurrentNodeId: {_nodeId}, FromNodeId: {message.FromNodeId}. 这可能表示路由表不一致或消息被错误路由。");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[ClusterRouter] 处理转发消息时发生异常 - MessageId: {message.MessageId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
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
                _logger.LogWarning($"[ClusterRouter] 收到连接注册消息 - FromNodeId: {message.FromNodeId}, CurrentNodeId: {_nodeId}");

                var registration = JsonSerializer.Deserialize<WebSocketConnectionRegistration>(message.Payload);
                _logger.LogWarning($"[ClusterRouter] 解析连接注册 - ConnectionId: {registration.ConnectionId}, NodeId: {registration.NodeId}, CurrentNodeId: {_nodeId}");

                // 如果使用 Redis，连接路由信息应该已经在 Redis 中，这里只需要更新本地缓存
                // If using Redis, connection route info should already be in Redis, just update local cache here
                // 如果未使用 Redis，则从广播消息中更新路由表（向后兼容）
                // If not using Redis, update routing table from broadcast message (backward compatibility)
                if (registration.NodeId != _nodeId)
                {
                    // Check if connection was previously on a different node (transfer case) / 检查连接是否之前在另一个节点上（转移情况）
                    var previousNodeId = _connectionRoutes.GetValueOrDefault(registration.ConnectionId);
                    if (previousNodeId != null && previousNodeId != registration.NodeId)
                    {
                        // Connection transferred from another node / 连接从另一个节点转移
                        _logger.LogWarning($"[ClusterRouter] 连接从其他节点转移 - ConnectionId: {registration.ConnectionId}, PreviousNodeId: {previousNodeId}, NewNodeId: {registration.NodeId}, CurrentNodeId: {_nodeId}");

                        // Decrement count for previous node / 减少前一个节点的计数
                        _nodeConnectionCounts.AddOrUpdate(previousNodeId, 0, (key, value) => Math.Max(0, value - 1));
                    }

                    // Update routing table / 更新路由表
                    _connectionRoutes.AddOrUpdate(registration.ConnectionId, registration.NodeId, (key, oldValue) =>
                    {
                        if (oldValue != registration.NodeId)
                        {
                            _logger.LogWarning($"[ClusterRouter] 更新路由表 - ConnectionId: {registration.ConnectionId}, OldNodeId: {oldValue}, NewNodeId: {registration.NodeId}, CurrentNodeId: {_nodeId}");
                        }
                        return registration.NodeId;
                    });

                    _logger.LogWarning($"[ClusterRouter] 连接注册处理完成 - ConnectionId: {registration.ConnectionId}, NodeId: {registration.NodeId}, CurrentNodeId: {_nodeId}, 路由表大小: {_connectionRoutes.Count}");

                    // Store endpoint information if available / 如果可用，存储端点信息
                    if (!string.IsNullOrEmpty(registration.Endpoint))
                    {
                        _connectionEndpoints.AddOrUpdate(registration.ConnectionId, registration.Endpoint, (key, oldValue) => registration.Endpoint);
                    }
                    else
                    {
                        // If endpoint is not provided but we have it stored, keep the existing one / 如果未提供端点但我们已存储，保留现有的
                        // This ensures endpoint is preserved during transfers / 这确保在转移期间保留端点
                    }

                    // Store connection metadata / 存储连接元数据
                    var metadata = new ConnectionMetadata
                    {
                        RemoteIpAddress = registration.RemoteIpAddress,
                        RemotePort = registration.RemotePort,
                        ConnectedAt = registration.RegisteredAt
                    };
                    _connectionMetadata.AddOrUpdate(registration.ConnectionId, metadata, (key, oldValue) =>
                    {
                        // Preserve existing metadata if new one is missing fields / 如果新元数据缺少字段，保留现有元数据
                        if (string.IsNullOrEmpty(metadata.RemoteIpAddress) && !string.IsNullOrEmpty(oldValue.RemoteIpAddress))
                        {
                            metadata.RemoteIpAddress = oldValue.RemoteIpAddress;
                        }
                        if (metadata.RemotePort == 0 && oldValue.RemotePort != 0)
                        {
                            metadata.RemotePort = oldValue.RemotePort;
                        }
                        if (metadata.ConnectedAt == default && oldValue.ConnectedAt != default)
                        {
                            metadata.ConnectedAt = oldValue.ConnectedAt;
                        }
                        return metadata;
                    });

                    // Increment count for new node / 增加新节点的计数
                    _nodeConnectionCounts.AddOrUpdate(registration.NodeId, 1, (key, value) => value + 1);

                    _logger.LogWarning($"[ClusterRouter] 连接注册处理完成 - ConnectionId: {registration.ConnectionId}, NodeId: {registration.NodeId}, CurrentNodeId: {_nodeId}, 路由表大小: {_connectionRoutes.Count}, 节点连接数: {_nodeConnectionCounts.GetValueOrDefault(registration.NodeId, 0)}");
                }
                else
                {
                    _logger.LogWarning($"[ClusterRouter] 收到自己节点的连接注册消息，忽略 - ConnectionId: {registration.ConnectionId}, NodeId: {registration.NodeId}, CurrentNodeId: {_nodeId}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[ClusterRouter] 处理连接注册消息时发生异常 - FromNodeId: {message.FromNodeId}, CurrentNodeId: {_nodeId}, Payload: {message.Payload}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
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
                _logger.LogWarning($"[ClusterRouter] 处理连接查询 - FromNodeId: {message.FromNodeId}, CurrentNodeId: {_nodeId}");

                var query = JsonSerializer.Deserialize<JsonElement>(message.Payload);
                var connectionId = query.GetProperty("ConnectionId").GetString();

                _logger.LogWarning($"[ClusterRouter] 查询连接ID - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}, 路由表大小: {_connectionRoutes.Count}");

                string nodeId = null;
                bool foundInRoutingTable = _connectionRoutes.TryGetValue(connectionId, out nodeId);

                // 如果不在路由表中，先尝试从 Redis 查询（如果传输层支持）
                // If not in routing table, try querying from Redis first (if transport supports it)
                if (!foundInRoutingTable)
                {
                    _logger.LogWarning($"[ClusterRouter] 连接不在路由表中，尝试从 Redis 查询 - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}");
                    var redisNodeId = await _transport.GetConnectionRouteAsync(connectionId);
                    if (!string.IsNullOrEmpty(redisNodeId))
                    {
                        nodeId = redisNodeId;
                        // 更新本地路由表缓存
                        _connectionRoutes.AddOrUpdate(connectionId, nodeId, (key, oldValue) => nodeId);
                        _logger.LogWarning($"[ClusterRouter] 从 Redis 查询成功，更新路由表 - ConnectionId: {connectionId}, NodeId: {nodeId}, CurrentNodeId: {_nodeId}");
                        foundInRoutingTable = true;
                    }
                }

                if (foundInRoutingTable && !string.IsNullOrEmpty(nodeId))
                {
                    _logger.LogWarning($"[ClusterRouter] 连接已找到 - ConnectionId: {connectionId}, NodeId: {nodeId}, CurrentNodeId: {_nodeId}, IsLocal: {nodeId == _nodeId}");

                    // Always respond to query if connection is found, regardless of whether it's local or remote
                    // 如果找到连接，始终响应查询，无论它是本地还是远程连接
                    // This helps other nodes discover connections even if registration broadcast failed
                    // 这有助于其他节点发现连接，即使注册广播失败
                    if (nodeId == _nodeId)
                    {
                        // Connection is local - verify it exists and respond / 连接是本地的 - 验证它存在并响应
                        _logger.LogWarning($"[ClusterRouter] 连接在本节点，验证并响应查询 - ConnectionId: {connectionId}, FromNodeId: {message.FromNodeId}, CurrentNodeId: {_nodeId}");

                        // Verify connection actually exists locally / 验证连接实际存在于本地
                        var connectionExists = _connectionProvider != null &&
                                             _connectionProvider.GetConnection(connectionId) != null &&
                                             _connectionProvider.GetConnection(connectionId).State == System.Net.WebSockets.WebSocketState.Open;

                        if (connectionExists)
                        {
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
                                ToNodeId = message.FromNodeId,
                                MessageId = Guid.NewGuid().ToString("N"), // 生成唯一的 MessageId，避免被去重逻辑过滤
                                FromNodeId = _nodeId
                            };

                            await _transport.SendAsync(message.FromNodeId, response);
                            _logger.LogWarning($"[ClusterRouter] 查询响应已发送（本地连接）- ConnectionId: {connectionId}, ToNodeId: {message.FromNodeId}, MessageId: {response.MessageId}, CurrentNodeId: {_nodeId}");
                        }
                        else
                        {
                            _logger.LogWarning($"[ClusterRouter] 连接在本节点路由表中但实际不存在，移除路由表项和 Redis 记录 - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}");
                            _connectionRoutes.TryRemove(connectionId, out _);
                            // 同时从 Redis 删除
                            await _transport.RemoveConnectionRouteAsync(connectionId);
                        }
                    }
                    else
                    {
                        // Connection is on another node - respond with the node ID so querying node can update its routing table
                        // 连接在其他节点上 - 响应节点ID，以便查询节点可以更新其路由表
                        _logger.LogWarning($"[ClusterRouter] 连接在其他节点，响应查询（告知节点ID）- ConnectionId: {connectionId}, NodeId: {nodeId}, FromNodeId: {message.FromNodeId}, CurrentNodeId: {_nodeId}");

                        var registration = new WebSocketConnectionRegistration
                        {
                            ConnectionId = connectionId,
                            NodeId = nodeId, // 告知查询者连接在哪个节点
                            RegisteredAt = DateTime.UtcNow
                        };

                        var response = new ClusterMessage
                        {
                            Type = ClusterMessageType.RegisterWebSocketConnection,
                            Payload = JsonSerializer.Serialize(registration),
                            ToNodeId = message.FromNodeId,
                            MessageId = Guid.NewGuid().ToString("N"), // 生成唯一的 MessageId，避免被去重逻辑过滤
                            FromNodeId = _nodeId
                        };

                        await _transport.SendAsync(message.FromNodeId, response);
                        _logger.LogWarning($"[ClusterRouter] 查询响应已发送（远程连接）- ConnectionId: {connectionId}, NodeId: {nodeId}, ToNodeId: {message.FromNodeId}, MessageId: {response.MessageId}, CurrentNodeId: {_nodeId}");
                    }
                }
                else
                {
                    _logger.LogWarning($"[ClusterRouter] 连接未找到，无法响应查询 - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}, 路由表大小: {_connectionRoutes.Count}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[ClusterRouter] 处理连接查询时发生异常 - FromNodeId: {message.FromNodeId}, CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
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
        /// Handle connections from disconnected node / 处理断开节点上的连接
        /// Note: WebSocket connections cannot be truly "transferred" as they are TCP connections.
        /// We only remove them from routing table and wait for clients to reconnect.
        /// 注意：WebSocket 连接无法真正"转移"，因为它们是 TCP 连接。
        /// 我们只从路由表中移除它们，等待客户端重新连接。
        /// </summary>
        /// <param name="connectionIds">Connection IDs from disconnected node / 断开节点上的连接 ID</param>
        /// <param name="disconnectedNodeId">Disconnected node ID / 断开连接的节点 ID</param>
        private async Task TransferConnectionsAsync(List<string> connectionIds, string disconnectedNodeId)
        {
            if (connectionIds == null || connectionIds.Count == 0)
            {
                return;
            }

            try
            {
                _logger.LogInformation($"Node {disconnectedNodeId} disconnected. Removed {connectionIds.Count} connection(s) from routing table. Clients will need to reconnect.");

                // Note: WebSocket connections cannot be truly "transferred" as they are TCP connections
                // We can only remove them from the routing table and wait for clients to reconnect
                // When clients reconnect, they will be registered to an available node via RegisterConnectionAsync
                // 注意：WebSocket 连接无法真正"转移"，因为它们是 TCP 连接
                // 我们只能从路由表中移除它们，等待客户端重新连接
                // 当客户端重新连接时，它们将通过 RegisterConnectionAsync 注册到可用节点

                // Remove endpoint information for disconnected connections / 移除断开连接的端点信息
                foreach (var connectionId in connectionIds)
                {
                    _connectionEndpoints.TryRemove(connectionId, out _);
                }

                // Broadcast connection unregistration to cluster / 向集群广播连接注销
                // This ensures all nodes know these connections are no longer valid
                // 这确保所有节点都知道这些连接不再有效
                foreach (var connectionId in connectionIds)
                {
                    var message = new ClusterMessage
                    {
                        Type = ClusterMessageType.UnregisterWebSocketConnection,
                        Payload = System.Text.Json.JsonSerializer.Serialize(new { ConnectionId = connectionId, NodeId = disconnectedNodeId })
                    };

                    await _transport.BroadcastAsync(message);
                }

                _logger.LogInformation($"Successfully removed {connectionIds.Count} connection(s) from node {disconnectedNodeId}. Waiting for clients to reconnect.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling disconnected connections from node {disconnectedNodeId}");
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
        /// Refresh connection routes for all local active connections / 刷新所有本地活跃连接的路由信息
        /// </summary>
        /// <param name="state">Timer state / 定时器状态</param>
        private async void RefreshConnectionRoutes(object state)
        {
            try
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    return;
                }

                // Get all local connections (connections on this node) / 获取所有本地连接（此节点上的连接）
                var localConnections = _connectionRoutes
                    .Where(kvp => kvp.Value == _nodeId)
                    .Select(kvp => kvp.Key)
                    .ToList();

                if (localConnections.Count == 0)
                {
                    return; // No local connections to refresh / 没有本地连接需要刷新
                }

                _logger.LogWarning($"[ClusterRouter] 开始刷新连接路由 - 本地连接数: {localConnections.Count}, CurrentNodeId: {_nodeId}");

                int refreshedCount = 0;
                int failedCount = 0;

                foreach (var connectionId in localConnections)
                {
                    try
                    {
                        // Verify connection is still active by checking if it exists in local routing table
                        // 通过检查连接是否仍在本地路由表中来验证连接是否仍然活跃
                        if (_connectionRoutes.TryGetValue(connectionId, out var nodeId) && nodeId == _nodeId)
                        {
                            // Refresh connection route expiration / 刷新连接路由过期时间
                            var refreshed = await _transport.RefreshConnectionRouteAsync(connectionId, _nodeId);
                            if (refreshed)
                            {
                                refreshedCount++;
                            }
                            else
                            {
                                // If refresh is not supported, try to re-store the route / 如果不支持刷新，尝试重新存储路由
                                if (_connectionMetadata.TryGetValue(connectionId, out var metadata))
                                {
                                    var routeMetadata = new Dictionary<string, string>
                                    {
                                        ["RemoteIpAddress"] = metadata.RemoteIpAddress ?? string.Empty,
                                        ["RemotePort"] = metadata.RemotePort.ToString(),
                                        ["ConnectedAt"] = metadata.ConnectedAt.ToString("O")
                                    };
                                    await _transport.StoreConnectionRouteAsync(connectionId, _nodeId, routeMetadata);
                                    refreshedCount++;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        _logger.LogError(ex, $"[ClusterRouter] 刷新连接路由失败 - ConnectionId: {connectionId}, CurrentNodeId: {_nodeId}, Error: {ex.Message}");
                    }
                }

                _logger.LogWarning($"[ClusterRouter] 连接路由刷新完成 - 成功: {refreshedCount}, 失败: {failedCount}, 总计: {localConnections.Count}, CurrentNodeId: {_nodeId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[ClusterRouter] 刷新连接路由时发生异常 - CurrentNodeId: {_nodeId}, Error: {ex.Message}, StackTrace: {ex.StackTrace}");
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
        /// Get connection metadata / 获取连接元数据
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接ID</param>
        /// <returns>Connection metadata or null / 连接元数据或null</returns>
        public ConnectionMetadata GetConnectionMetadata(string connectionId)
        {
            return _connectionMetadata.TryGetValue(connectionId, out var metadata) ? metadata : null;
        }

        /// <summary>
        /// Get connection endpoint / 获取连接端点
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接ID</param>
        /// <returns>Endpoint path or null / 端点路径或null</returns>
        public string GetConnectionEndpoint(string connectionId)
        {
            return _connectionEndpoints.TryGetValue(connectionId, out var endpoint) ? endpoint : null;
        }

        /// <summary>
        /// Dispose resources / 释放资源
        /// </summary>
        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            _nodeHealthCheckTimer?.Dispose();
            _connectionRouteRefreshTimer?.Dispose();
        }
    }

    /// <summary>
    /// Connection metadata / 连接元数据
    /// </summary>
    public class ConnectionMetadata
    {
        /// <summary>
        /// Remote IP address / 远程 IP 地址
        /// </summary>
        public string RemoteIpAddress { get; set; }

        /// <summary>
        /// Remote port / 远程端口
        /// </summary>
        public int RemotePort { get; set; }

        /// <summary>
        /// Connection time / 连接时间
        /// </summary>
        public DateTime ConnectedAt { get; set; }
    }
}

