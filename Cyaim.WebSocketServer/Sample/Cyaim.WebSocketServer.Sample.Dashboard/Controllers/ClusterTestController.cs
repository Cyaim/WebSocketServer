using System;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Dashboard.Models;
using Cyaim.WebSocketServer.Sample.Dashboard.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Sample.Dashboard.Controllers
{
    /// <summary>
    /// WebSocket 集群自动化测试控制器
    /// WebSocket Cluster Automated Test Controller
    /// </summary>
    [ApiController]
    [Route("api/test/cluster")]
    public class ClusterTestController : ControllerBase
    {
        private readonly ILogger<ClusterTestController> _logger;
        private readonly ClusterTestService _testService;

        /// <summary>
        /// 构造函数 / Constructor
        /// </summary>
        public ClusterTestController(
            ILogger<ClusterTestController> logger,
            ClusterTestService testService)
        {
            _logger = logger;
            _testService = testService;
        }

        #region 自动化测试套件 / Automated Test Suite

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
}

