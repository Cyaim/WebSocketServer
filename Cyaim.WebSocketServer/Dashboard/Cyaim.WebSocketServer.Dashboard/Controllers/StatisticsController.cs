using System;
using System.Collections.Generic;
using Cyaim.WebSocketServer.Dashboard.Models;
using Cyaim.WebSocketServer.Dashboard.Services;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Dashboard.Controllers
{
    /// <summary>
    /// Statistics controller
    /// 统计信息控制器
    /// </summary>
    [ApiController]
    [Route("ws_server/api/statistics")]
    public class StatisticsController : ControllerBase
    {
        private readonly ILogger<StatisticsController> _logger;
        private readonly DashboardStatisticsService _statisticsService;
        private readonly DashboardHelperService _helperService;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        public StatisticsController(
            ILogger<StatisticsController> logger,
            DashboardStatisticsService statisticsService,
            DashboardHelperService helperService)
        {
            _logger = logger;
            _statisticsService = statisticsService;
            _helperService = helperService;
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
        /// Get connection statistics / 获取连接统计信息
        /// </summary>
        /// <returns>Connection statistics / 连接统计信息</returns>
        [HttpGet("connections")]
        public ActionResult<ApiResponse<List<ClientConnectionInfo>>> GetConnections()
        {
            try
            {
                // Reuse ClientController logic / 重用 ClientController 的逻辑
                var clients = new List<ClientConnectionInfo>();
                var clusterManager = Infrastructure.Cluster.GlobalClusterCenter.ClusterManager;
                var currentNodeId = Infrastructure.Cluster.GlobalClusterCenter.ClusterContext?.NodeId ?? "unknown";

                // Get all connections from cluster routing table / 从集群路由表获取所有连接
                var allConnections = _helperService.GetAllClusterConnections();

                foreach (var connectionRoute in allConnections)
                {
                    var connectionId = connectionRoute.Key;
                    var targetNodeId = connectionRoute.Value;

                    // Get connection info / 获取连接信息
                    ClientConnectionInfo clientInfo = null;

                    // Get connection metadata from cluster manager / 从集群管理器获取连接元数据
                    ConnectionMetadata metadata = null;
                    string endpoint = null;
                    if (clusterManager != null)
                    {
                        metadata = clusterManager.GetConnectionMetadata(connectionId);
                        endpoint = clusterManager.GetConnectionEndpoint(connectionId);
                    }

                    // If connection is on current node, get detailed info / 如果连接在当前节点，获取详细信息
                    if (targetNodeId == currentNodeId && Infrastructure.Handlers.MvcHandler.MvcChannelHandler.Clients != null)
                    {
                        if (Infrastructure.Handlers.MvcHandler.MvcChannelHandler.Clients.TryGetValue(connectionId, out var webSocket))
                        {
                            var stats = _statisticsService.GetConnectionStats(connectionId);
                            clientInfo = new ClientConnectionInfo
                            {
                                ConnectionId = connectionId,
                                NodeId = targetNodeId,
                                RemoteIpAddress = metadata?.RemoteIpAddress,
                                RemotePort = metadata?.RemotePort ?? 0,
                                State = webSocket.State.ToString(),
                                ConnectedAt = metadata?.ConnectedAt,
                                Endpoint = endpoint,
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
                        // If connection is in routing table, assume it's Open (disconnected connections are unregistered)
                        // 如果连接在路由表中，假设它是 Open 状态（断开的连接会被注销）
                        var state = "Open"; // Assume open if in routing table / 如果在路由表中，假设为 Open
                        
                        clientInfo = new ClientConnectionInfo
                        {
                            ConnectionId = connectionId,
                            NodeId = targetNodeId,
                            RemoteIpAddress = metadata?.RemoteIpAddress,
                            RemotePort = metadata?.RemotePort ?? 0,
                            State = state,
                            ConnectedAt = metadata?.ConnectedAt,
                            Endpoint = endpoint,
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
                _logger.LogError(ex, "Error getting connection statistics");
                return StatusCode(500, new ApiResponse<List<ClientConnectionInfo>>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }
    }
}

