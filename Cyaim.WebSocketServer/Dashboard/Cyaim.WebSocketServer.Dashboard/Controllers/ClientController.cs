using System;
using System.Collections.Generic;
using System.Linq;
using Cyaim.WebSocketServer.Dashboard.Models;
using Cyaim.WebSocketServer.Dashboard.Services;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Dashboard.Controllers
{
    /// <summary>
    /// Client connection management controller
    /// 客户端连接管理控制器
    /// </summary>
    [ApiController]
    [Route("ws_server/api/dashboard/client")]
    public class ClientController : ControllerBase
    {
        private readonly ILogger<ClientController> _logger;
        private readonly DashboardStatisticsService _statisticsService;
        private readonly DashboardHelperService _helperService;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        public ClientController(
            ILogger<ClientController> logger,
            DashboardStatisticsService statisticsService,
            DashboardHelperService helperService)
        {
            _logger = logger;
            _statisticsService = statisticsService;
            _helperService = helperService;
        }

        /// <summary>
        /// Get all client connections from cluster / 获取集群中全部客户端连接
        /// </summary>
        /// <param name="nodeId">Optional node ID filter / 可选的节点 ID 过滤器</param>
        /// <returns>Client connections / 客户端连接</returns>
        [HttpGet]
        public ActionResult<ApiResponse<List<ClientConnectionInfo>>> GetAll([FromQuery] string? nodeId = null)
        {
            try
            {
                var clients = new List<ClientConnectionInfo>();
                var clusterManager = GlobalClusterCenter.ClusterManager;
                var currentNodeId = GlobalClusterCenter.ClusterContext?.NodeId ?? "unknown";

                // Get all connections from cluster routing table / 从集群路由表获取所有连接
                var allConnections = _helperService.GetAllClusterConnections();

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
        /// Get client connections for specified node / 获取指定节点的客户端连接
        /// </summary>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        /// <returns>Client connections / 客户端连接</returns>
        [HttpGet("node/{nodeId}")]
        public ActionResult<ApiResponse<List<ClientConnectionInfo>>> GetByNode(string nodeId)
        {
            return GetAll(nodeId);
        }

        /// <summary>
        /// Get local node client connections / 获取本地节点的客户端连接
        /// </summary>
        /// <returns>Client connections / 客户端连接</returns>
        [HttpGet("local")]
        public ActionResult<ApiResponse<List<ClientConnectionInfo>>> GetLocal()
        {
            var currentNodeId = GlobalClusterCenter.ClusterContext?.NodeId;
            return GetAll(currentNodeId);
        }

        /// <summary>
        /// Get specified connection information / 获取指定连接的信息
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <returns>Client connection information / 客户端连接信息</returns>
        [HttpGet("{connectionId}")]
        public ActionResult<ApiResponse<ClientConnectionInfo>> Get(string connectionId)
        {
            try
            {
                var clientsResult = GetAll();
                if (!clientsResult.Value?.Success ?? true || clientsResult.Value?.Data == null)
                {
                    return NotFound(new ApiResponse<ClientConnectionInfo>
                    {
                        Success = false,
                        Error = "Failed to get clients"
                    });
                }

                var client = clientsResult.Value.Data.FirstOrDefault(c => c.ConnectionId == connectionId);
                if (client == null)
                {
                    return NotFound(new ApiResponse<ClientConnectionInfo>
                    {
                        Success = false,
                        Error = $"Connection {connectionId} not found"
                    });
                }

                return Ok(new ApiResponse<ClientConnectionInfo>
                {
                    Success = true,
                    Data = client
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting client");
                return StatusCode(500, new ApiResponse<ClientConnectionInfo>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// Get connection count statistics / 获取连接数统计
        /// </summary>
        /// <returns>Connection count information / 连接数信息</returns>
        [HttpGet("count")]
        public ActionResult<ApiResponse<ConnectionCountInfo>> GetCount()
        {
            try
            {
                var clusterManager = GlobalClusterCenter.ClusterManager;
                var total = clusterManager?.GetTotalConnectionCount() ?? 0;
                var local = clusterManager?.GetLocalConnectionCount() ?? 0;

                var result = new ConnectionCountInfo
                {
                    Total = total,
                    Local = local
                };

                return Ok(new ApiResponse<ConnectionCountInfo>
                {
                    Success = true,
                    Data = result
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting connection counts");
                return StatusCode(500, new ApiResponse<ConnectionCountInfo>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }
    }
}

