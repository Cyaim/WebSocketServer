#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Sample.Dashboard.Interfaces;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Sample.Dashboard.Services
{
    /// <summary>
    /// 集群测试服务
    /// Cluster Test Service
    /// </summary>
    public class ClusterTestService
    {
        private readonly ILogger<ClusterTestService> _logger;
        private readonly IWebSocketClusterTestApi _testApi;

        /// <summary>
        /// 构造函数 / Constructor
        /// </summary>
        public ClusterTestService(
            ILogger<ClusterTestService> logger,
            IWebSocketClusterTestApi testApi)
        {
            _logger = logger;
            _testApi = testApi;
        }

        /// <summary>
        /// 运行所有测试
        /// Run all tests
        /// </summary>
        public async Task<TestResults> RunAllTestsAsync()
        {
            var results = new TestResults
            {
                TestName = "All Tests",
                StartTime = DateTime.UtcNow
            };

            try
            {
                // 运行集群信息测试
                var clusterInfoResults = await RunClusterInfoTestsAsync();
                results.Tests.AddRange(clusterInfoResults.Tests);

                // 运行连接管理测试
                var connectionResults = await RunConnectionTestsAsync();
                results.Tests.AddRange(connectionResults.Tests);

                // 运行消息发送测试
                var messageResults = await RunMessageTestsAsync();
                results.Tests.AddRange(messageResults.Tests);

                // 运行路由测试
                var routingResults = await RunRoutingTestsAsync();
                results.Tests.AddRange(routingResults.Tests);

                // 运行健康检查测试
                var healthResults = await RunHealthCheckTestsAsync();
                results.Tests.AddRange(healthResults.Tests);

                results.EndTime = DateTime.UtcNow;
                results.Duration = results.EndTime - results.StartTime;
                results.Success = results.Tests.All(t => t.Success);
                results.PassedCount = results.Tests.Count(t => t.Success);
                results.FailedCount = results.Tests.Count(t => !t.Success);

                _logger.LogInformation($"All tests completed. Passed: {results.PassedCount}, Failed: {results.FailedCount}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running all tests");
                results.Success = false;
                results.ErrorMessage = ex.Message;
            }

            return results;
        }

        /// <summary>
        /// 运行集群信息测试
        /// Run cluster information tests
        /// </summary>
        public async Task<TestResults> RunClusterInfoTestsAsync()
        {
            var results = new TestResults
            {
                TestName = "Cluster Information Tests",
                StartTime = DateTime.UtcNow
            };

            try
            {
                // 测试获取集群概览
                var overviewTest = await RunTestAsync("GetClusterOverview", async () =>
                {
                    var result = await _testApi.GetClusterOverviewAsync();
                    return result.Success && result.Data != null;
                });
                results.Tests.Add(overviewTest);

                // 测试获取节点列表
                var nodesTest = await RunTestAsync("GetNodes", async () =>
                {
                    var result = await _testApi.GetNodesAsync();
                    return result.Success && result.Data != null;
                });
                results.Tests.Add(nodesTest);

                // 测试获取当前节点ID
                var currentNodeTest = await RunTestAsync("GetCurrentNodeId", async () =>
                {
                    var result = await _testApi.GetCurrentNodeIdAsync();
                    return result.Success && !string.IsNullOrEmpty(result.Data);
                });
                results.Tests.Add(currentNodeTest);

                // 测试检查是否为领导者
                var leaderTest = await RunTestAsync("IsLeader", async () =>
                {
                    var result = await _testApi.IsLeaderAsync();
                    return result.Success;
                });
                results.Tests.Add(leaderTest);

                // 测试获取最优节点
                var optimalNodeTest = await RunTestAsync("GetOptimalNode", async () =>
                {
                    var result = await _testApi.GetOptimalNodeAsync();
                    return result.Success && !string.IsNullOrEmpty(result.Data);
                });
                results.Tests.Add(optimalNodeTest);

                results.EndTime = DateTime.UtcNow;
                results.Duration = results.EndTime - results.StartTime;
                results.Success = results.Tests.All(t => t.Success);
                results.PassedCount = results.Tests.Count(t => t.Success);
                results.FailedCount = results.Tests.Count(t => !t.Success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running cluster info tests");
                results.Success = false;
                results.ErrorMessage = ex.Message;
            }

            return results;
        }

        /// <summary>
        /// 运行连接管理测试
        /// Run connection management tests
        /// </summary>
        public async Task<TestResults> RunConnectionTestsAsync()
        {
            var results = new TestResults
            {
                TestName = "Connection Management Tests",
                StartTime = DateTime.UtcNow
            };

            try
            {
                // 测试获取所有客户端
                var allClientsTest = await RunTestAsync("GetAllClients", async () =>
                {
                    var result = await _testApi.GetAllClientsAsync();
                    return result.Success && result.Data != null;
                });
                results.Tests.Add(allClientsTest);

                // 测试获取本地客户端
                var localClientsTest = await RunTestAsync("GetLocalClients", async () =>
                {
                    var result = await _testApi.GetLocalClientsAsync();
                    return result.Success && result.Data != null;
                });
                results.Tests.Add(localClientsTest);

                // 测试获取总连接数
                var totalCountTest = await RunTestAsync("GetTotalConnectionCount", async () =>
                {
                    var result = await _testApi.GetTotalConnectionCountAsync();
                    return result.Success;
                });
                results.Tests.Add(totalCountTest);

                // 测试获取本地连接数
                var localCountTest = await RunTestAsync("GetLocalConnectionCount", async () =>
                {
                    var result = await _testApi.GetLocalConnectionCountAsync();
                    return result.Success;
                });
                results.Tests.Add(localCountTest);

                // 测试获取连接路由表
                var routesTest = await RunTestAsync("GetConnectionRoutes", async () =>
                {
                    var result = await _testApi.GetConnectionRoutesAsync();
                    return result.Success && result.Data != null;
                });
                results.Tests.Add(routesTest);

                results.EndTime = DateTime.UtcNow;
                results.Duration = results.EndTime - results.StartTime;
                results.Success = results.Tests.All(t => t.Success);
                results.PassedCount = results.Tests.Count(t => t.Success);
                results.FailedCount = results.Tests.Count(t => !t.Success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running connection tests");
                results.Success = false;
                results.ErrorMessage = ex.Message;
            }

            return results;
        }

        /// <summary>
        /// 运行消息发送测试
        /// Run message sending tests
        /// </summary>
        public async Task<TestResults> RunMessageTestsAsync()
        {
            var results = new TestResults
            {
                TestName = "Message Sending Tests",
                StartTime = DateTime.UtcNow
            };

            try
            {
                // 先获取一个连接ID用于测试
                var clientsResult = await _testApi.GetAllClientsAsync();
                var hasConnections = clientsResult.Success && clientsResult.Data != null && clientsResult.Data.Count > 0;

                if (hasConnections)
                {
                    var testConnectionId = clientsResult.Data!.First().ConnectionId;

                    // 测试发送文本消息
                    var sendTextTest = await RunTestAsync("SendTextMessage", async () =>
                    {
                        var result = await _testApi.SendTextMessageAsync(testConnectionId, "Test message");
                        return result.Success;
                    });
                    results.Tests.Add(sendTextTest);
                }
                else
                {
                    results.Tests.Add(new TestCase
                    {
                        Name = "SendTextMessage",
                        Success = false,
                        ErrorMessage = "No connections available for testing"
                    });
                }

                // 测试获取带宽统计
                var bandwidthTest = await RunTestAsync("GetBandwidthStatistics", async () =>
                {
                    var result = await _testApi.GetBandwidthStatisticsAsync();
                    return result.Success && result.Data != null;
                });
                results.Tests.Add(bandwidthTest);

                results.EndTime = DateTime.UtcNow;
                results.Duration = results.EndTime - results.StartTime;
                results.Success = results.Tests.All(t => t.Success);
                results.PassedCount = results.Tests.Count(t => t.Success);
                results.FailedCount = results.Tests.Count(t => !t.Success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running message tests");
                results.Success = false;
                results.ErrorMessage = ex.Message;
            }

            return results;
        }

        /// <summary>
        /// 运行路由测试
        /// Run routing tests
        /// </summary>
        public async Task<TestResults> RunRoutingTestsAsync()
        {
            var results = new TestResults
            {
                TestName = "Routing Tests",
                StartTime = DateTime.UtcNow
            };

            try
            {
                // 测试获取连接路由表
                var routesTest = await RunTestAsync("GetConnectionRoutes", async () =>
                {
                    var result = await _testApi.GetConnectionRoutesAsync();
                    return result.Success && result.Data != null;
                });
                results.Tests.Add(routesTest);

                // 如果有连接，测试查询连接节点
                var clientsResult = await _testApi.GetAllClientsAsync();
                if (clientsResult.Success && clientsResult.Data != null && clientsResult.Data.Count > 0)
                {
                    var testConnectionId = clientsResult.Data.First().ConnectionId;
                    var queryNodeTest = await RunTestAsync("QueryConnectionNode", async () =>
                    {
                        var result = await _testApi.QueryConnectionNodeAsync(testConnectionId);
                        return result.Success && !string.IsNullOrEmpty(result.Data);
                    });
                    results.Tests.Add(queryNodeTest);
                }

                results.EndTime = DateTime.UtcNow;
                results.Duration = results.EndTime - results.StartTime;
                results.Success = results.Tests.All(t => t.Success);
                results.PassedCount = results.Tests.Count(t => t.Success);
                results.FailedCount = results.Tests.Count(t => !t.Success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running routing tests");
                results.Success = false;
                results.ErrorMessage = ex.Message;
            }

            return results;
        }

        /// <summary>
        /// 运行健康检查测试
        /// Run health check tests
        /// </summary>
        public async Task<TestResults> RunHealthCheckTestsAsync()
        {
            var results = new TestResults
            {
                TestName = "Health Check Tests",
                StartTime = DateTime.UtcNow
            };

            try
            {
                // 测试集群健康检查
                var clusterHealthTest = await RunTestAsync("GetClusterHealth", async () =>
                {
                    var result = await _testApi.GetClusterHealthAsync();
                    return result.Success && result.Data != null;
                });
                results.Tests.Add(clusterHealthTest);

                // 测试获取当前节点并检查其健康状态
                var currentNodeResult = await _testApi.GetCurrentNodeIdAsync();
                if (currentNodeResult.Success && !string.IsNullOrEmpty(currentNodeResult.Data))
                {
                    var nodeHealthTest = await RunTestAsync("GetNodeHealth", async () =>
                    {
                        var result = await _testApi.GetNodeHealthAsync(currentNodeResult.Data);
                        return result.Success && result.Data != null;
                    });
                    results.Tests.Add(nodeHealthTest);
                }

                results.EndTime = DateTime.UtcNow;
                results.Duration = results.EndTime - results.StartTime;
                results.Success = results.Tests.All(t => t.Success);
                results.PassedCount = results.Tests.Count(t => t.Success);
                results.FailedCount = results.Tests.Count(t => !t.Success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running health check tests");
                results.Success = false;
                results.ErrorMessage = ex.Message;
            }

            return results;
        }

        /// <summary>
        /// 运行单个测试
        /// Run a single test
        /// </summary>
        private async Task<TestCase> RunTestAsync(string testName, Func<Task<bool>> testAction)
        {
            var testCase = new TestCase
            {
                Name = testName,
                StartTime = DateTime.UtcNow
            };

            try
            {
                var result = await testAction();
                testCase.Success = result;
                if (!result)
                {
                    testCase.ErrorMessage = "Test returned false";
                }
            }
            catch (Exception ex)
            {
                testCase.Success = false;
                testCase.ErrorMessage = ex.Message;
                _logger.LogError(ex, "Test {TestName} failed", testName);
            }
            finally
            {
                testCase.EndTime = DateTime.UtcNow;
                testCase.Duration = testCase.EndTime - testCase.StartTime;
            }

            return testCase;
        }
    }

    /// <summary>
    /// 测试结果 / Test Results
    /// </summary>
    public class TestResults
    {
        /// <summary>
        /// 测试名称 / Test name
        /// </summary>
        public string TestName { get; set; } = string.Empty;

        /// <summary>
        /// 是否成功 / Is successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 开始时间 / Start time
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// 结束时间 / End time
        /// </summary>
        public DateTime EndTime { get; set; }

        /// <summary>
        /// 持续时间 / Duration
        /// </summary>
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// 通过的测试数 / Passed test count
        /// </summary>
        public int PassedCount { get; set; }

        /// <summary>
        /// 失败的测试数 / Failed test count
        /// </summary>
        public int FailedCount { get; set; }

        /// <summary>
        /// 错误信息 / Error message
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 测试用例列表 / Test cases list
        /// </summary>
        public List<TestCase> Tests { get; set; } = new List<TestCase>();
    }

    /// <summary>
    /// 测试用例 / Test Case
    /// </summary>
    public class TestCase
    {
        /// <summary>
        /// 测试名称 / Test name
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 是否成功 / Is successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 开始时间 / Start time
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// 结束时间 / End time
        /// </summary>
        public DateTime EndTime { get; set; }

        /// <summary>
        /// 持续时间 / Duration
        /// </summary>
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// 错误信息 / Error message
        /// </summary>
        public string? ErrorMessage { get; set; }
    }
}

