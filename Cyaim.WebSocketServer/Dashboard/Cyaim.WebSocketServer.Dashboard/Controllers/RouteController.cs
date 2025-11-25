using System;
using System.Collections.Generic;
using System.Linq;
using Cyaim.WebSocketServer.Dashboard.Models;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Dashboard.Controllers
{
    /// <summary>
    /// Connection routing controller
    /// 连接路由控制器
    /// </summary>
    [ApiController]
    [Route("ws_server/api/dashboard/routes")]
    public class RouteController : ControllerBase
    {
        private readonly ILogger<RouteController> _logger;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        public RouteController(ILogger<RouteController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Get connection routing table / 获取连接路由表
        /// </summary>
        /// <returns>Connection ID to Node ID mapping / 连接ID到节点ID的映射</returns>
        [HttpGet]
        public ActionResult<ApiResponse<Dictionary<string, string>>> GetAll()
        {
            try
            {
                var clusterManager = GlobalClusterCenter.ClusterManager;
                if (clusterManager == null || clusterManager.ConnectionRoutes == null)
                {
                    return Ok(new ApiResponse<Dictionary<string, string>>
                    {
                        Success = false,
                        Error = "Cluster manager or connection routes not available"
                    });
                }

                var routes = clusterManager.ConnectionRoutes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                return Ok(new ApiResponse<Dictionary<string, string>>
                {
                    Success = true,
                    Data = routes
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting connection routes");
                return StatusCode(500, new ApiResponse<Dictionary<string, string>>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// Query which node a connection is on / 查询连接所在的节点
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <returns>Node ID / 节点 ID</returns>
        [HttpGet("{connectionId}")]
        public ActionResult<ApiResponse<string>> QueryConnection(string connectionId)
        {
            try
            {
                var clusterManager = GlobalClusterCenter.ClusterManager;
                if (clusterManager == null || clusterManager.ConnectionRoutes == null)
                {
                    return NotFound(new ApiResponse<string>
                    {
                        Success = false,
                        Error = "Cluster manager or connection routes not available"
                    });
                }

                if (!clusterManager.ConnectionRoutes.TryGetValue(connectionId, out var nodeId))
                {
                    return NotFound(new ApiResponse<string>
                    {
                        Success = false,
                        Error = $"Connection {connectionId} not found in routing table"
                    });
                }

                return Ok(new ApiResponse<string>
                {
                    Success = true,
                    Data = nodeId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error querying connection node");
                return StatusCode(500, new ApiResponse<string>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// Get all connection IDs for specified node / 获取指定节点的所有连接ID
        /// </summary>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        /// <returns>Connection ID list / 连接ID列表</returns>
        [HttpGet("node/{nodeId}")]
        public ActionResult<ApiResponse<List<string>>> GetByNode(string nodeId)
        {
            try
            {
                var clusterManager = GlobalClusterCenter.ClusterManager;
                if (clusterManager == null || clusterManager.ConnectionRoutes == null)
                {
                    return Ok(new ApiResponse<List<string>>
                    {
                        Success = false,
                        Error = "Cluster manager or connection routes not available"
                    });
                }

                var connectionIds = clusterManager.ConnectionRoutes
                    .Where(kvp => kvp.Value == nodeId)
                    .Select(kvp => kvp.Key)
                    .ToList();

                return Ok(new ApiResponse<List<string>>
                {
                    Success = true,
                    Data = connectionIds
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting node connection IDs");
                return StatusCode(500, new ApiResponse<List<string>>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }
    }
}

