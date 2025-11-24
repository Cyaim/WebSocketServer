using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Dashboard.Models;
using Cyaim.WebSocketServer.Sample.Dashboard.Interfaces;
using Cyaim.WebSocketServer.Sample.Dashboard.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Sample.Dashboard.Controllers
{
    /// <summary>
    /// WebSocket 集群测试控制器
    /// WebSocket Cluster Test Controller
    /// </summary>
    [ApiController]
    [Route("api/test/cluster")]
    public class ClusterTestController : ControllerBase
    {
        private readonly ILogger<ClusterTestController> _logger;
        private readonly IWebSocketClusterTestApi _testApi;
        private readonly ClusterTestService _testService;

        /// <summary>
        /// 构造函数 / Constructor
        /// </summary>
        public ClusterTestController(
            ILogger<ClusterTestController> logger,
            IWebSocketClusterTestApi testApi,
            ClusterTestService testService)
        {
            _logger = logger;
            _testApi = testApi;
            _testService = testService;
        }

        #region 集群信息测试 / Cluster Information Tests

        /// <summary>
        /// 测试获取集群概览
        /// Test getting cluster overview
        /// </summary>
        [HttpGet("overview")]
        public async Task<ActionResult<ApiResponse<ClusterOverview>>> TestGetClusterOverview()
        {
            try
            {
                var result = await _testApi.GetClusterOverviewAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing cluster overview");
                return StatusCode(500, new ApiResponse<ClusterOverview>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试获取所有节点
        /// Test getting all nodes
        /// </summary>
        [HttpGet("nodes")]
        public async Task<ActionResult<ApiResponse<List<NodeStatusInfo>>>> TestGetNodes()
        {
            try
            {
                var result = await _testApi.GetNodesAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing get nodes");
                return StatusCode(500, new ApiResponse<List<NodeStatusInfo>>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试获取指定节点
        /// Test getting specified node
        /// </summary>
        [HttpGet("nodes/{nodeId}")]
        public async Task<ActionResult<ApiResponse<NodeStatusInfo>>> TestGetNode(string nodeId)
        {
            try
            {
                var result = await _testApi.GetNodeAsync(nodeId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing get node");
                return StatusCode(500, new ApiResponse<NodeStatusInfo>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试检查是否为领导者
        /// Test checking if current node is leader
        /// </summary>
        [HttpGet("leader")]
        public async Task<ActionResult<ApiResponse<bool>>> TestIsLeader()
        {
            try
            {
                var result = await _testApi.IsLeaderAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing is leader");
                return StatusCode(500, new ApiResponse<bool>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试获取当前节点ID
        /// Test getting current node ID
        /// </summary>
        [HttpGet("current-node")]
        public async Task<ActionResult<ApiResponse<string>>> TestGetCurrentNodeId()
        {
            try
            {
                var result = await _testApi.GetCurrentNodeIdAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing get current node ID");
                return StatusCode(500, new ApiResponse<string>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试获取最优节点
        /// Test getting optimal node
        /// </summary>
        [HttpGet("optimal-node")]
        public async Task<ActionResult<ApiResponse<string>>> TestGetOptimalNode()
        {
            try
            {
                var result = await _testApi.GetOptimalNodeAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing get optimal node");
                return StatusCode(500, new ApiResponse<string>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        #endregion

        #region 客户端连接测试 / Client Connection Tests

        /// <summary>
        /// 测试获取所有客户端
        /// Test getting all clients
        /// </summary>
        [HttpGet("clients")]
        public async Task<ActionResult<ApiResponse<List<ClientConnectionInfo>>>> TestGetAllClients([FromQuery] string? nodeId = null)
        {
            try
            {
                var result = await _testApi.GetAllClientsAsync(nodeId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing get all clients");
                return StatusCode(500, new ApiResponse<List<ClientConnectionInfo>>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试获取指定节点的客户端
        /// Test getting clients by node
        /// </summary>
        [HttpGet("clients/node/{nodeId}")]
        public async Task<ActionResult<ApiResponse<List<ClientConnectionInfo>>>> TestGetClientsByNode(string nodeId)
        {
            try
            {
                var result = await _testApi.GetClientsByNodeAsync(nodeId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing get clients by node");
                return StatusCode(500, new ApiResponse<List<ClientConnectionInfo>>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试获取本地客户端
        /// Test getting local clients
        /// </summary>
        [HttpGet("clients/local")]
        public async Task<ActionResult<ApiResponse<List<ClientConnectionInfo>>>> TestGetLocalClients()
        {
            try
            {
                var result = await _testApi.GetLocalClientsAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing get local clients");
                return StatusCode(500, new ApiResponse<List<ClientConnectionInfo>>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试获取指定连接信息
        /// Test getting client by connection ID
        /// </summary>
        [HttpGet("clients/{connectionId}")]
        public async Task<ActionResult<ApiResponse<ClientConnectionInfo>>> TestGetClient(string connectionId)
        {
            try
            {
                var result = await _testApi.GetClientAsync(connectionId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing get client");
                return StatusCode(500, new ApiResponse<ClientConnectionInfo>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试获取连接数统计
        /// Test getting connection counts
        /// </summary>
        [HttpGet("connections/count")]
        public async Task<ActionResult<ApiResponse<ConnectionCountInfo>>> TestGetConnectionCounts()
        {
            try
            {
                var totalTask = _testApi.GetTotalConnectionCountAsync();
                var localTask = _testApi.GetLocalConnectionCountAsync();

                await Task.WhenAll(totalTask, localTask);

                var result = new ConnectionCountInfo
                {
                    Total = totalTask.Result.Success ? totalTask.Result.Data : 0,
                    Local = localTask.Result.Success ? localTask.Result.Data : 0
                };

                return Ok(new ApiResponse<ConnectionCountInfo>
                {
                    Success = true,
                    Data = result
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing get connection counts");
                return StatusCode(500, new ApiResponse<ConnectionCountInfo>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        #endregion

        #region 消息发送测试 / Message Sending Tests

        /// <summary>
        /// 测试发送文本消息
        /// Test sending text message
        /// </summary>
        [HttpPost("send/text")]
        public async Task<ActionResult<ApiResponse<bool>>> TestSendTextMessage([FromBody] SendMessageRequest request)
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

                var result = await _testApi.SendTextMessageAsync(request.ConnectionId, request.Content);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing send text message");
                return StatusCode(500, new ApiResponse<bool>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试发送二进制消息
        /// Test sending binary message
        /// </summary>
        [HttpPost("send/binary")]
        public async Task<ActionResult<ApiResponse<bool>>> TestSendBinaryMessage([FromBody] SendMessageRequest request)
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

                var result = await _testApi.SendBinaryMessageAsync(request.ConnectionId, request.Content);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing send binary message");
                return StatusCode(500, new ApiResponse<bool>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试广播消息
        /// Test broadcasting message
        /// </summary>
        [HttpPost("broadcast")]
        public async Task<ActionResult<ApiResponse<int>>> TestBroadcastMessage([FromBody] BroadcastMessageRequest request)
        {
            try
            {
                var result = await _testApi.BroadcastMessageAsync(
                    request.Content,
                    request.MessageType ?? "Text");
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing broadcast message");
                return StatusCode(500, new ApiResponse<int>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试向指定节点广播消息
        /// Test broadcasting message to node
        /// </summary>
        [HttpPost("broadcast/node/{nodeId}")]
        public async Task<ActionResult<ApiResponse<int>>> TestBroadcastMessageToNode(string nodeId, [FromBody] BroadcastMessageRequest request)
        {
            try
            {
                var result = await _testApi.BroadcastMessageToNodeAsync(
                    nodeId,
                    request.Content,
                    request.MessageType ?? "Text");
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing broadcast message to node");
                return StatusCode(500, new ApiResponse<int>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        #endregion

        #region 统计信息测试 / Statistics Tests

        /// <summary>
        /// 测试获取带宽统计
        /// Test getting bandwidth statistics
        /// </summary>
        [HttpGet("statistics/bandwidth")]
        public async Task<ActionResult<ApiResponse<BandwidthStatistics>>> TestGetBandwidthStatistics()
        {
            try
            {
                var result = await _testApi.GetBandwidthStatisticsAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing get bandwidth statistics");
                return StatusCode(500, new ApiResponse<BandwidthStatistics>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试获取连接统计
        /// Test getting connection statistics
        /// </summary>
        [HttpGet("statistics/connections")]
        public async Task<ActionResult<ApiResponse<List<ClientConnectionInfo>>>> TestGetConnectionStatistics()
        {
            try
            {
                var result = await _testApi.GetAllConnectionStatisticsAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing get connection statistics");
                return StatusCode(500, new ApiResponse<List<ClientConnectionInfo>>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        #endregion

        #region 路由测试 / Routing Tests

        /// <summary>
        /// 测试获取连接路由表
        /// Test getting connection routes
        /// </summary>
        [HttpGet("routes")]
        public async Task<ActionResult<ApiResponse<Dictionary<string, string>>>> TestGetConnectionRoutes()
        {
            try
            {
                var result = await _testApi.GetConnectionRoutesAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing get connection routes");
                return StatusCode(500, new ApiResponse<Dictionary<string, string>>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试查询连接所在节点
        /// Test querying connection node
        /// </summary>
        [HttpGet("routes/{connectionId}")]
        public async Task<ActionResult<ApiResponse<string>>> TestQueryConnectionNode(string connectionId)
        {
            try
            {
                var result = await _testApi.QueryConnectionNodeAsync(connectionId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing query connection node");
                return StatusCode(500, new ApiResponse<string>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        #endregion

        #region 健康检查测试 / Health Check Tests

        /// <summary>
        /// 测试集群健康检查
        /// Test cluster health check
        /// </summary>
        [HttpGet("health")]
        public async Task<ActionResult<ApiResponse<ClusterHealthStatus>>> TestClusterHealth()
        {
            try
            {
                var result = await _testApi.GetClusterHealthAsync();
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing cluster health");
                return StatusCode(500, new ApiResponse<ClusterHealthStatus>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 测试节点健康检查
        /// Test node health check
        /// </summary>
        [HttpGet("health/node/{nodeId}")]
        public async Task<ActionResult<ApiResponse<NodeHealthStatus>>> TestNodeHealth(string nodeId)
        {
            try
            {
                var result = await _testApi.GetNodeHealthAsync(nodeId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing node health");
                return StatusCode(500, new ApiResponse<NodeHealthStatus>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        #endregion

        #region 综合测试 / Comprehensive Tests

        /// <summary>
        /// 运行所有测试
        /// Run all tests
        /// </summary>
        [HttpPost("run-all")]
        public async Task<ActionResult<ApiResponse<TestResults>>> RunAllTests()
        {
            try
            {
                var results = await _testService.RunAllTestsAsync();
                return Ok(new ApiResponse<TestResults>
                {
                    Success = true,
                    Data = results
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running all tests");
                return StatusCode(500, new ApiResponse<TestResults>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 运行集群信息测试
        /// Run cluster information tests
        /// </summary>
        [HttpPost("run/cluster-info")]
        public async Task<ActionResult<ApiResponse<TestResults>>> RunClusterInfoTests()
        {
            try
            {
                var results = await _testService.RunClusterInfoTestsAsync();
                return Ok(new ApiResponse<TestResults>
                {
                    Success = true,
                    Data = results
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running cluster info tests");
                return StatusCode(500, new ApiResponse<TestResults>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 运行连接管理测试
        /// Run connection management tests
        /// </summary>
        [HttpPost("run/connections")]
        public async Task<ActionResult<ApiResponse<TestResults>>> RunConnectionTests()
        {
            try
            {
                var results = await _testService.RunConnectionTestsAsync();
                return Ok(new ApiResponse<TestResults>
                {
                    Success = true,
                    Data = results
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running connection tests");
                return StatusCode(500, new ApiResponse<TestResults>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        /// <summary>
        /// 运行消息发送测试
        /// Run message sending tests
        /// </summary>
        [HttpPost("run/messages")]
        public async Task<ActionResult<ApiResponse<TestResults>>> RunMessageTests()
        {
            try
            {
                var results = await _testService.RunMessageTestsAsync();
                return Ok(new ApiResponse<TestResults>
                {
                    Success = true,
                    Data = results
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running message tests");
                return StatusCode(500, new ApiResponse<TestResults>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        #endregion
    }

    /// <summary>
    /// 连接数信息 / Connection Count Information
    /// </summary>
    public class ConnectionCountInfo
    {
        /// <summary>
        /// 总连接数 / Total connections
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// 本地连接数 / Local connections
        /// </summary>
        public int Local { get; set; }
    }

    /// <summary>
    /// 广播消息请求 / Broadcast Message Request
    /// </summary>
    public class BroadcastMessageRequest
    {
        /// <summary>
        /// 消息内容 / Message content
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// 消息类型 / Message type
        /// </summary>
        public string? MessageType { get; set; }
    }
}

