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
        /// Get client connections / 获取客户端连接
        /// </summary>
        /// <param name="nodeId">Optional node ID filter / 可选的节点 ID 过滤器</param>
        /// <returns>Client connections / 客户端连接</returns>
        [HttpGet("clients")]
        public ActionResult<ApiResponse<List<ClientConnectionInfo>>> GetClients([FromQuery] string nodeId = null)
        {
            try
            {
                var clients = new List<ClientConnectionInfo>();

                // Get local connections / 获取本地连接
                if (MvcChannelHandler.Clients != null)
                {
                    foreach (var kvp in MvcChannelHandler.Clients)
                    {
                        var connectionId = kvp.Key;
                        var webSocket = kvp.Value;

                        // Skip if node filter is specified and doesn't match / 如果指定了节点过滤器且不匹配则跳过
                        if (!string.IsNullOrEmpty(nodeId))
                        {
                            // In a real implementation, you'd check which node this connection belongs to
                            // 在实际实现中，您需要检查此连接属于哪个节点
                            continue;
                        }

                        var stats = _statisticsService.GetConnectionStats(connectionId);
                        var clientInfo = new ClientConnectionInfo
                        {
                            ConnectionId = connectionId,
                            NodeId = GlobalClusterCenter.ClusterContext?.NodeId ?? "unknown",
                            State = webSocket.State.ToString(),
                            BytesSent = stats?.BytesSent ?? 0,
                            BytesReceived = stats?.BytesReceived ?? 0,
                            MessagesSent = stats?.MessagesSent ?? 0,
                            MessagesReceived = stats?.MessagesReceived ?? 0
                        };

                        clients.Add(clientInfo);
                    }
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
        /// Send message to connection / 向连接发送消息
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

                // Try to get WebSocket connection / 尝试获取 WebSocket 连接
                if (MvcChannelHandler.Clients == null || !MvcChannelHandler.Clients.TryGetValue(request.ConnectionId, out var webSocket))
                {
                    return NotFound(new ApiResponse<bool>
                    {
                        Success = false,
                        Error = "Connection not found"
                    });
                }

                if (webSocket.State != WebSocketState.Open)
                {
                    return BadRequest(new ApiResponse<bool>
                    {
                        Success = false,
                        Error = "WebSocket is not open"
                    });
                }

                // Send message / 发送消息
                var messageType = request.MessageType == "Binary" 
                    ? WebSocketMessageType.Binary 
                    : WebSocketMessageType.Text;
                
                var data = Encoding.UTF8.GetBytes(request.Content);
                await webSocket.SendAsync(
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

