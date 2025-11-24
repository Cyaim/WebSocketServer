using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Dashboard.Models;
using Cyaim.WebSocketServer.Dashboard.Services;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Dashboard.Controllers
{
    /// <summary>
    /// Dashboard API controller
    /// Dashboard API 控制器
    /// </summary>
    [ApiController]
    [Route("api/dashboard")]
    public class DashboardApiController : ControllerBase
    {
        private readonly ILogger<DashboardApiController> _logger;
        private readonly DashboardStatisticsService _statisticsService;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="statisticsService">Statistics service / 统计服务</param>
        public DashboardApiController(
            ILogger<DashboardApiController> logger,
            DashboardStatisticsService statisticsService)
        {
            _logger = logger;
            _statisticsService = statisticsService;
        }

        /// <summary>
        /// Get cluster overview / 获取集群概览
        /// </summary>
        /// <returns>Cluster overview / 集群概览</returns>
        [HttpGet("cluster/overview")]
        public ActionResult<ApiResponse<ClusterOverview>> GetClusterOverview()
        {
            try
            {
                var overview = new ClusterOverview
                {
                    CurrentNodeId = GlobalClusterCenter.ClusterContext?.NodeId ?? "unknown",
                    IsCurrentNodeLeader = GlobalClusterCenter.ClusterManager?.IsLeader() ?? false,
                    Nodes = GetNodeStatusList()
                };

                overview.TotalNodes = overview.Nodes.Count;
                overview.ConnectedNodes = overview.Nodes.Count(n => n.IsConnected);
                overview.TotalConnections = GlobalClusterCenter.ClusterManager?.GetTotalConnectionCount() ?? 0;
                overview.LocalConnections = GlobalClusterCenter.ClusterManager?.GetLocalConnectionCount() ?? 0;

                return Ok(new ApiResponse<ClusterOverview>
                {
                    Success = true,
                    Data = overview
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting cluster overview");
                return StatusCode(500, new ApiResponse<ClusterOverview>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// Get node status list / 获取节点状态列表
        /// </summary>
        /// <returns>Node status list / 节点状态列表</returns>
        [HttpGet("cluster/nodes")]
        public ActionResult<ApiResponse<List<NodeStatusInfo>>> GetNodes()
        {
            try
            {
                var nodes = GetNodeStatusList();
                return Ok(new ApiResponse<List<NodeStatusInfo>>
                {
                    Success = true,
                    Data = nodes
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting nodes");
                return StatusCode(500, new ApiResponse<List<NodeStatusInfo>>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// Get all client connections from cluster / 获取集群中全部客户端连接
        /// </summary>
        /// <param name="nodeId">Optional node ID filter / 可选的节点 ID 过滤器</param>
        /// <returns>Client connections / 客户端连接</returns>
        [HttpGet("clients")]
        public ActionResult<ApiResponse<List<ClientConnectionInfo>>> GetClients([FromQuery] string nodeId = null)
        {
            try
            {
                var clients = new List<ClientConnectionInfo>();
                var clusterManager = GlobalClusterCenter.ClusterManager;
                var currentNodeId = GlobalClusterCenter.ClusterContext?.NodeId ?? "unknown";

                // Get all connections from cluster routing table / 从集群路由表获取所有连接
                var allConnections = GetAllClusterConnections();

                foreach (var connectionRoute in allConnections)
                {
                    var connectionId = connectionRoute.Key;
                    var targetNodeId = connectionRoute.Value;

                    // Apply node filter if specified / 如果指定了节点过滤器则应用
                    if (!string.IsNullOrEmpty(nodeId) && targetNodeId != nodeId)
                    {
                        continue;
                    }

                    // Get connection info / 获取连接信息
                    ClientConnectionInfo clientInfo = null;

                    // If connection is on current node, get detailed info / 如果连接在当前节点，获取详细信息
                    if (targetNodeId == currentNodeId && MvcChannelHandler.Clients != null)
                    {
                        if (MvcChannelHandler.Clients.TryGetValue(connectionId, out var webSocket))
                        {
                            var stats = _statisticsService.GetConnectionStats(connectionId);
                            clientInfo = new ClientConnectionInfo
                            {
                                ConnectionId = connectionId,
                                NodeId = targetNodeId,
                                State = webSocket.State.ToString(),
                                BytesSent = stats?.BytesSent ?? 0,
                                BytesReceived = stats?.BytesReceived ?? 0,
                                MessagesSent = stats?.MessagesSent ?? 0,
                                MessagesReceived = stats?.MessagesReceived ?? 0
                            };
                        }
                    }

                    // If not found locally or on remote node, create basic info / 如果未在本地找到或在远程节点，创建基本信息
                    if (clientInfo == null)
                    {
                        clientInfo = new ClientConnectionInfo
                        {
                            ConnectionId = connectionId,
                            NodeId = targetNodeId,
                            State = "Unknown", // Remote connections state is unknown / 远程连接状态未知
                            BytesSent = 0,
                            BytesReceived = 0,
                            MessagesSent = 0,
                            MessagesReceived = 0
                        };
                    }

                    clients.Add(clientInfo);
                }

                return Ok(new ApiResponse<List<ClientConnectionInfo>>
                {
                    Success = true,
                    Data = clients
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting clients");
                return StatusCode(500, new ApiResponse<List<ClientConnectionInfo>>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// Get bandwidth statistics / 获取带宽统计信息
        /// </summary>
        /// <returns>Bandwidth statistics / 带宽统计信息</returns>
        [HttpGet("bandwidth")]
        public ActionResult<ApiResponse<BandwidthStatistics>> GetBandwidth()
        {
            try
            {
                var stats = _statisticsService.GetBandwidthStatistics();
                return Ok(new ApiResponse<BandwidthStatistics>
                {
                    Success = true,
                    Data = stats
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting bandwidth statistics");
                return StatusCode(500, new ApiResponse<BandwidthStatistics>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// Send text message to connection in cluster / 向集群中的连接发送文本消息
        /// </summary>
        /// <param name="request">Send message request / 发送消息请求</param>
        /// <returns>Send result / 发送结果</returns>
        [HttpPost("send")]
        public async Task<ActionResult<ApiResponse<bool>>> SendMessage([FromBody] SendMessageRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.ConnectionId))
                {
                    return BadRequest(new ApiResponse<bool>
                    {
                        Success = false,
                        Error = "ConnectionId is required"
                    });
                }

                if (string.IsNullOrEmpty(request.Content))
                {
                    return BadRequest(new ApiResponse<bool>
                    {
                        Success = false,
                        Error = "Content is required"
                    });
                }

                var clusterManager = GlobalClusterCenter.ClusterManager;
                var currentNodeId = GlobalClusterCenter.ClusterContext?.NodeId ?? "unknown";

                // Prepare message data / 准备消息数据
                var messageType = request.MessageType == "Binary"
                    ? WebSocketMessageType.Binary
                    : WebSocketMessageType.Text;

                var data = Encoding.UTF8.GetBytes(request.Content);
                var messageTypeInt = (int)messageType;

                // Try local connection first / 首先尝试本地连接
                if (MvcChannelHandler.Clients != null && MvcChannelHandler.Clients.TryGetValue(request.ConnectionId, out var localWebSocket))
                {
                    if (localWebSocket.State == WebSocketState.Open)
                    {
                        // Send to local connection / 发送到本地连接
                        await localWebSocket.SendAsync(
                            new ArraySegment<byte>(data),
                            messageType,
                            true,
                            System.Threading.CancellationToken.None);

                        // Record statistics / 记录统计信息
                        _statisticsService.RecordBytesSent(request.ConnectionId, data.Length);

                        return Ok(new ApiResponse<bool>
                        {
                            Success = true,
                            Data = true
                        });
                    }
                    else
                    {
                        return BadRequest(new ApiResponse<bool>
                        {
                            Success = false,
                            Error = "WebSocket is not open"
                        });
                    }
                }

                // If not found locally, try to route through cluster / 如果本地未找到，尝试通过集群路由
                if (clusterManager != null)
                {
                    var routed = await clusterManager.RouteMessageAsync(request.ConnectionId, data, messageTypeInt);

                    if (routed)
                    {
                        // Record statistics if connection is tracked / 如果连接被跟踪则记录统计信息
                        _statisticsService.RecordBytesSent(request.ConnectionId, data.Length);

                        return Ok(new ApiResponse<bool>
                        {
                            Success = true,
                            Data = true
                        });
                    }
                    else
                    {
                        return NotFound(new ApiResponse<bool>
                        {
                            Success = false,
                            Error = "Connection not found in cluster"
                        });
                    }
                }
                else
                {
                    // No cluster manager, connection not found / 没有集群管理器，连接未找到
                    return NotFound(new ApiResponse<bool>
                    {
                        Success = false,
                        Error = "Connection not found"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message");
                return StatusCode(500, new ApiResponse<bool>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// Get all connections from cluster routing table / 从集群路由表获取所有连接
        /// </summary>
        /// <returns>Dictionary of connection ID to node ID / 连接ID到节点ID的字典</returns>
        private Dictionary<string, string> GetAllClusterConnections()
        {
            var connections = new Dictionary<string, string>();
            var clusterManager = GlobalClusterCenter.ClusterManager;
            var currentNodeId = GlobalClusterCenter.ClusterContext?.NodeId ?? "unknown";

            // Get local connections / 获取本地连接
            if (MvcChannelHandler.Clients != null)
            {
                foreach (var kvp in MvcChannelHandler.Clients)
                {
                    connections[kvp.Key] = currentNodeId;
                }
            }

            // Get connections from cluster routing table / 从集群路由表获取连接
            if (clusterManager != null && clusterManager.ConnectionRoutes != null)
            {
                foreach (var kvp in clusterManager.ConnectionRoutes)
                {
                    // Merge with local connections, cluster routes take precedence / 与本地连接合并，集群路由优先
                    connections[kvp.Key] = kvp.Value;
                }
            }

            return connections;
        }

        /// <summary>
        /// Get node status list / 获取节点状态列表
        /// </summary>
        /// <returns>Node status list / 节点状态列表</returns>
        private List<NodeStatusInfo> GetNodeStatusList()
        {
            var nodes = new List<NodeStatusInfo>();

            try
            {
                var clusterManager = GlobalClusterCenter.ClusterManager;
                var clusterContext = GlobalClusterCenter.ClusterContext;

                if (clusterManager == null || clusterContext == null)
                {
                    // Return current node only / 仅返回当前节点
                    return new List<NodeStatusInfo>
                    {
                        new NodeStatusInfo
                        {
                            NodeId = "unknown",
                            State = "Unknown",
                            IsConnected = false,
                            ConnectionCount = MvcChannelHandler.Clients?.Count ?? 0
                        }
                    };
                }

                // Get current node info / 获取当前节点信息
                var currentNode = new NodeStatusInfo
                {
                    NodeId = clusterContext.NodeId,
                    Address = clusterContext.NodeAddress,
                    Port = clusterContext.NodePort,
                    State = "Unknown",
                    IsLeader = clusterManager.IsLeader(),
                    IsConnected = true,
                    ConnectionCount = clusterManager.GetLocalConnectionCount(),
                    CurrentTerm = 0,
                    LogLength = 0
                };

                // Try to get Raft node info if available / 如果可用，尝试获取 Raft 节点信息
                // Note: This would require exposing RaftNode properties or using reflection
                // 注意：这需要公开 RaftNode 属性或使用反射

                nodes.Add(currentNode);

                // Add other nodes from cluster context / 从集群上下文添加其他节点
                if (clusterContext.Nodes != null)
                {
                    foreach (var nodeConfig in clusterContext.Nodes)
                    {
                        if (string.IsNullOrEmpty(nodeConfig))
                            continue;

                        // Parse node config / 解析节点配置
                        var parts = nodeConfig.Split('@');
                        if (parts.Length >= 2)
                        {
                            var nodeId = parts[0];
                            var addressParts = parts[1].Split(':');
                            var address = addressParts[0];
                            var port = addressParts.Length > 1 && int.TryParse(addressParts[1], out var p) ? p : 0;

                            if (nodeId != currentNode.NodeId)
                            {
                                nodes.Add(new NodeStatusInfo
                                {
                                    NodeId = nodeId,
                                    Address = address,
                                    Port = port,
                                    State = "Unknown",
                                    IsConnected = false,
                                    ConnectionCount = 0
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting node status list");
            }

            return nodes;
        }
    }
}

