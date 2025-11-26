using System;
using System.Collections.Generic;
using System.Linq;
using Cyaim.WebSocketServer.Dashboard.Models;
using Cyaim.WebSocketServer.Dashboard.Services;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Dashboard.Controllers
{
    /// <summary>
    /// Cluster management controller
    /// 集群管理控制器
    /// </summary>
    [ApiController]
    [Route("ws_server/api/cluster")]
    public class ClusterController : ControllerBase
    {
        private readonly ILogger<ClusterController> _logger;
        private readonly DashboardHelperService _helperService;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        public ClusterController(
            ILogger<ClusterController> logger,
            DashboardHelperService helperService)
        {
            _logger = logger;
            _helperService = helperService;
        }

        /// <summary>
        /// Get cluster overview / 获取集群概览
        /// </summary>
        /// <returns>Cluster overview / 集群概览</returns>
        [HttpGet("overview")]
        public ActionResult<ApiResponse<ClusterOverview>> GetOverview()
        {
            try
            {
                var overview = new ClusterOverview
                {
                    CurrentNodeId = GlobalClusterCenter.ClusterContext?.NodeId ?? "unknown",
                    IsCurrentNodeLeader = GlobalClusterCenter.ClusterManager?.IsLeader() ?? false,
                    Nodes = _helperService.GetNodeStatusList()
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
        [HttpGet("nodes")]
        public ActionResult<ApiResponse<List<NodeStatusInfo>>> GetNodes()
        {
            try
            {
                var nodes = _helperService.GetNodeStatusList();
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
        /// Get specified node information / 获取指定节点信息
        /// </summary>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        /// <returns>Node status information / 节点状态信息</returns>
        [HttpGet("nodes/{nodeId}")]
        public ActionResult<ApiResponse<NodeStatusInfo?>> GetNode(string nodeId)
        {
            try
            {
                var nodes = _helperService.GetNodeStatusList();
                var node = nodes.FirstOrDefault(n => n.NodeId == nodeId);
                if (node == null)
                {
                    return NotFound(new ApiResponse<NodeStatusInfo?>
                    {
                        Success = false,
                        Error = $"Node {nodeId} not found"
                    });
                }

                return Ok(new ApiResponse<NodeStatusInfo?>
                {
                    Success = true,
                    Data = node
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting node");
                return StatusCode(500, new ApiResponse<NodeStatusInfo?>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// Check if current node is leader / 检查当前节点是否为领导者
        /// </summary>
        /// <returns>Is leader / 是否为领导者</returns>
        [HttpGet("leader")]
        public ActionResult<ApiResponse<bool>> IsLeader()
        {
            try
            {
                var isLeader = GlobalClusterCenter.ClusterManager?.IsLeader() ?? false;
                return Ok(new ApiResponse<bool>
                {
                    Success = true,
                    Data = isLeader
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking leader status");
                return StatusCode(500, new ApiResponse<bool>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// Get current node ID / 获取当前节点ID
        /// </summary>
        /// <returns>Node ID / 节点 ID</returns>
        [HttpGet("current-node")]
        public ActionResult<ApiResponse<string>> GetCurrentNodeId()
        {
            try
            {
                var nodeId = GlobalClusterCenter.ClusterContext?.NodeId ?? "unknown";
                return Ok(new ApiResponse<string>
                {
                    Success = true,
                    Data = nodeId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting current node ID");
                return StatusCode(500, new ApiResponse<string>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// Get optimal node for load balancing / 获取用于负载均衡的最优节点
        /// </summary>
        /// <returns>Optimal node ID / 最优节点 ID</returns>
        [HttpGet("optimal-node")]
        public ActionResult<ApiResponse<string>> GetOptimalNode()
        {
            try
            {
                var clusterManager = GlobalClusterCenter.ClusterManager;
                if (clusterManager == null)
                {
                    return Ok(new ApiResponse<string>
                    {
                        Success = false,
                        Error = "Cluster manager not available"
                    });
                }

                var optimalNode = clusterManager.GetOptimalNode();
                return Ok(new ApiResponse<string>
                {
                    Success = true,
                    Data = optimalNode
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting optimal node");
                return StatusCode(500, new ApiResponse<string>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }
    }
}

