#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Client connection management controller
    /// 客户端连接管理控制器
    /// </summary>
    [ApiController]
    [Route("ws_server/api/client")]
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
                // Must match the node id used by the routing table (GetAllClusterConnections) so that
                // local connections are recognised as on-node and enriched with live stats.
                // 需与路由表使用的节点 id 一致，本地连接才会被识别为本节点并附加实时统计。
                var currentNodeId = GlobalClusterCenter.ClusterContext?.NodeId ?? "standalone";

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
                    ClientConnectionInfo? clientInfo = null;

                    // Get connection metadata from cluster manager / 从集群管理器获取连接元数据
                    ConnectionMetadata? metadata = null;
                    string? endpoint = null;
                    if (clusterManager != null)
                    {
                        metadata = clusterManager.GetConnectionMetadata(connectionId);
                        endpoint = clusterManager.GetConnectionEndpoint(connectionId);
                    }

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
                                RemoteIpAddress = metadata?.RemoteIpAddress ?? string.Empty,
                                RemotePort = metadata?.RemotePort ?? 0,
                                State = webSocket.State.ToString(),
                                ConnectedAt = metadata?.ConnectedAt,
                                Endpoint = endpoint ?? string.Empty,
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
                            RemoteIpAddress = metadata?.RemoteIpAddress ?? string.Empty,
                            RemotePort = metadata?.RemotePort ?? 0,
                            State = state,
                            ConnectedAt = metadata?.ConnectedAt,
                            Endpoint = endpoint ?? string.Empty,
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

                var client = clientsResult.Value!.Data!.FirstOrDefault(c => c.ConnectionId == connectionId);
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
                // Fall back to the local connection dictionary in standalone (no-cluster) mode.
                // 单机（无集群）模式回退到本地连接字典。
                var local = clusterManager?.GetLocalConnectionCount() ?? MvcChannelHandler.Clients?.Count ?? 0;
                var total = clusterManager?.GetTotalConnectionCount() ?? local;

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

        /// <summary>
        /// Disconnect (close) a connection — management operation.
        /// 断开（关闭）一个连接——管理操作。
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        [HttpDelete("{connectionId}")]
        public async Task<ActionResult<ApiResponse<bool>>> Disconnect(string connectionId)
        {
            try
            {
                // Local connection: close the socket directly.
                // 本地连接：直接关闭 socket。
                if (MvcChannelHandler.Clients != null &&
                    MvcChannelHandler.Clients.TryGetValue(connectionId, out var socket) && socket != null)
                {
                    try
                    {
                        if (socket.State == System.Net.WebSockets.WebSocketState.Open ||
                            socket.State == System.Net.WebSockets.WebSocketState.CloseReceived)
                        {
                            await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                                "Closed by dashboard", System.Threading.CancellationToken.None);
                        }
                        else
                        {
                            socket.Abort();
                        }
                    }
                    catch
                    {
                        socket.Abort();
                    }
                    return Ok(new ApiResponse<bool> { Success = true, Data = true });
                }

                // Not a local connection. (Routing a disconnect to a remote cluster node is not yet
                // supported; the owning node's dashboard can close it.)
                // 非本地连接（跨节点断开暂未支持，可在归属节点的看板上关闭）。
                await Task.CompletedTask;
                return NotFound(new ApiResponse<bool>
                {
                    Success = false,
                    Data = false,
                    Error = $"Connection {connectionId} not found on this node"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disconnecting client");
                return StatusCode(500, new ApiResponse<bool> { Success = false, Error = ex.Message });
            }
        }
    }
}

