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
    /// Message sending controller
    /// 消息发送控制器
    /// </summary>
    [ApiController]
    [Route("ws_server/api/dashboard/messages")]
    public class MessageController : ControllerBase
    {
        private readonly ILogger<MessageController> _logger;
        private readonly DashboardStatisticsService _statisticsService;
        private readonly DashboardHelperService _helperService;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        public MessageController(
            ILogger<MessageController> logger,
            DashboardStatisticsService statisticsService,
            DashboardHelperService helperService)
        {
            _logger = logger;
            _statisticsService = statisticsService;
            _helperService = helperService;
        }

        /// <summary>
        /// Send message to connection / 向连接发送消息
        /// </summary>
        /// <param name="request">Send message request / 发送消息请求</param>
        /// <returns>Send result / 发送结果</returns>
        [HttpPost("send")]
        public async Task<ActionResult<ApiResponse<bool>>> Send([FromBody] SendMessageRequest request)
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
        /// Send text message to connection / 向连接发送文本消息
        /// </summary>
        /// <param name="request">Send message request / 发送消息请求</param>
        /// <returns>Send result / 发送结果</returns>
        [HttpPost("send/text")]
        public async Task<ActionResult<ApiResponse<bool>>> SendText([FromBody] SendMessageRequest request)
        {
            if (request == null)
            {
                return BadRequest(new ApiResponse<bool>
                {
                    Success = false,
                    Error = "Request is required"
                });
            }

            request.MessageType = "Text";
            return await Send(request);
        }

        /// <summary>
        /// Send binary message to connection / 向连接发送二进制消息
        /// </summary>
        /// <param name="request">Send message request / 发送消息请求</param>
        /// <returns>Send result / 发送结果</returns>
        [HttpPost("send/binary")]
        public async Task<ActionResult<ApiResponse<bool>>> SendBinary([FromBody] SendMessageRequest request)
        {
            if (request == null)
            {
                return BadRequest(new ApiResponse<bool>
                {
                    Success = false,
                    Error = "Request is required"
                });
            }

            request.MessageType = "Binary";
            return await Send(request);
        }

        /// <summary>
        /// Broadcast message to all connections / 广播消息到所有连接
        /// </summary>
        /// <param name="request">Broadcast message request / 广播消息请求</param>
        /// <returns>Number of successful sends / 发送成功的连接数</returns>
        [HttpPost("broadcast")]
        public async Task<ActionResult<ApiResponse<int>>> Broadcast([FromBody] BroadcastMessageRequest request)
        {
            try
            {
                // Get all clients directly / 直接获取所有客户端
                var allConnections = _helperService.GetAllClusterConnections();
                if (allConnections.Count == 0)
                {
                    return Ok(new ApiResponse<int>
                    {
                        Success = true,
                        Data = 0
                    });
                }

                var messageType = request.MessageType ?? "Text";
                var successCount = 0;

                foreach (var connection in allConnections)
                {
                    var sendRequest = new SendMessageRequest
                    {
                        ConnectionId = connection.Key,
                        Content = request.Content,
                        MessageType = messageType
                    };

                    var sendResult = await Send(sendRequest);
                    if (sendResult.Value?.Success == true && sendResult.Value.Data)
                    {
                        successCount++;
                    }
                }

                return Ok(new ApiResponse<int>
                {
                    Success = true,
                    Data = successCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting message");
                return StatusCode(500, new ApiResponse<int>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// Broadcast message to all connections on specified node / 广播消息到指定节点的所有连接
        /// </summary>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        /// <param name="request">Broadcast message request / 广播消息请求</param>
        /// <returns>Number of successful sends / 发送成功的连接数</returns>
        [HttpPost("broadcast/node/{nodeId}")]
        public async Task<ActionResult<ApiResponse<int>>> BroadcastToNode(string nodeId, [FromBody] BroadcastMessageRequest request)
        {
            try
            {
                // Get connections for specified node / 获取指定节点的连接
                var allConnections = _helperService.GetAllClusterConnections();
                var nodeConnections = allConnections
                    .Where(kvp => kvp.Value == nodeId)
                    .ToList();

                if (nodeConnections.Count == 0)
                {
                    return Ok(new ApiResponse<int>
                    {
                        Success = true,
                        Data = 0
                    });
                }

                var messageType = request.MessageType ?? "Text";
                var successCount = 0;

                foreach (var connection in nodeConnections)
                {
                    var sendRequest = new SendMessageRequest
                    {
                        ConnectionId = connection.Key,
                        Content = request.Content,
                        MessageType = messageType
                    };

                    var sendResult = await Send(sendRequest);
                    if (sendResult.Value?.Success == true && sendResult.Value.Data)
                    {
                        successCount++;
                    }
                }

                return Ok(new ApiResponse<int>
                {
                    Success = true,
                    Data = successCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting message to node");
                return StatusCode(500, new ApiResponse<int>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }
    }
}

