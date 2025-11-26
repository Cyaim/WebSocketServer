using System;
using System.Collections.Generic;
using System.Linq;
using Cyaim.WebSocketServer.Dashboard.Models;
using Cyaim.WebSocketServer.Dashboard.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Dashboard.Controllers
{
    /// <summary>
    /// Health check controller
    /// 健康检查控制器
    /// </summary>
    [ApiController]
    [Route("ws_server/api/health")]
    public class HealthController : ControllerBase
    {
        private readonly ILogger<HealthController> _logger;
        private readonly DashboardHelperService _helperService;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        public HealthController(
            ILogger<HealthController> logger,
            DashboardHelperService helperService)
        {
            _logger = logger;
            _helperService = helperService;
        }

        /// <summary>
        /// Check cluster health status / 检查集群健康状态
        /// </summary>
        /// <returns>Health status information / 健康状态信息</returns>
        [HttpGet]
        public ActionResult<ApiResponse<ClusterHealthStatus>> GetClusterHealth()
        {
            try
            {
                // Get cluster overview directly / 直接获取集群概览
                var nodes = _helperService.GetNodeStatusList();
                var clusterManager = Infrastructure.Cluster.GlobalClusterCenter.ClusterManager;
                var clusterContext = Infrastructure.Cluster.GlobalClusterCenter.ClusterContext;
                
                var currentNodeId = clusterContext?.NodeId ?? "unknown";
                var isCurrentNodeLeader = clusterManager?.IsLeader() ?? false;
                var totalNodes = nodes.Count;
                var connectedNodes = nodes.Count(n => n.IsConnected);
                var totalConnections = clusterManager?.GetTotalConnectionCount() ?? 0;
                var localConnections = clusterManager?.GetLocalConnectionCount() ?? 0;
                var healthyNodes = connectedNodes;
                var hasLeader = isCurrentNodeLeader || nodes.Any(n => n.IsLeader);

                var healthStatus = new ClusterHealthStatus
                {
                    IsHealthy = connectedNodes > 0 && totalNodes > 0,
                    TotalNodes = totalNodes,
                    HealthyNodes = healthyNodes,
                    UnhealthyNodes = totalNodes - healthyNodes,
                    HasLeader = hasLeader,
                    TotalConnections = totalConnections,
                    Details = new Dictionary<string, object>
                    {
                        { "CurrentNodeId", currentNodeId },
                        { "IsCurrentNodeLeader", isCurrentNodeLeader },
                        { "LocalConnections", localConnections }
                    }
                };

                return Ok(new ApiResponse<ClusterHealthStatus>
                {
                    Success = true,
                    Data = healthStatus
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting cluster health");
                return StatusCode(500, new ApiResponse<ClusterHealthStatus>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// Check node health status / 检查节点健康状态
        /// </summary>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        /// <returns>Health status information / 健康状态信息</returns>
        [HttpGet("node/{nodeId}")]
        public ActionResult<ApiResponse<NodeHealthStatus>> GetNodeHealth(string nodeId)
        {
            try
            {
                var startTime = DateTime.UtcNow;
                
                // Get node directly / 直接获取节点
                var nodes = _helperService.GetNodeStatusList();
                var node = nodes.FirstOrDefault(n => n.NodeId == nodeId);
                var responseTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

                if (node == null)
                {
                    return NotFound(new ApiResponse<NodeHealthStatus>
                    {
                        Success = false,
                        Error = $"Node {nodeId} not found"
                    });
                }

                var healthStatus = new NodeHealthStatus
                {
                    NodeId = node.NodeId,
                    IsHealthy = node.IsConnected,
                    IsConnected = node.IsConnected,
                    ConnectionCount = node.ConnectionCount,
                    IsLeader = node.IsLeader,
                    ResponseTimeMs = (long)responseTime
                };

                return Ok(new ApiResponse<NodeHealthStatus>
                {
                    Success = true,
                    Data = healthStatus
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting node health");
                return StatusCode(500, new ApiResponse<NodeHealthStatus>
                {
                    Success = false,
                    Error = ex.Message,
                    Data = new NodeHealthStatus
                    {
                        NodeId = nodeId,
                        IsHealthy = false,
                        ErrorMessage = ex.Message
                    }
                });
            }
        }
    }
}

