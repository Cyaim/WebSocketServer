using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Dashboard.Models;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Sample.Dashboard.Interfaces
{
    /// <summary>
    /// WebSocket 集群测试 API 实现
    /// WebSocket Cluster Test API Implementation
    /// </summary>
    public class WebSocketClusterTestApi : IWebSocketClusterTestApi
    {
        private readonly ILogger<WebSocketClusterTestApi> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _apiBaseUrl;

        /// <summary>
        /// 构造函数 / Constructor
        /// </summary>
        /// <param name="logger">日志记录器 / Logger</param>
        /// <param name="httpClient">HTTP 客户端 / HTTP client</param>
        /// <param name="apiBaseUrl">API 基础URL，默认为 "/api/dashboard" / API base URL, default is "/api/dashboard"</param>
        public WebSocketClusterTestApi(
            ILogger<WebSocketClusterTestApi> logger,
            HttpClient httpClient,
            string apiBaseUrl = "/api/dashboard")
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _apiBaseUrl = apiBaseUrl;
        }

        #region 集群管理 / Cluster Management

        public async Task<ApiResponse<ClusterOverview>> GetClusterOverviewAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_apiBaseUrl}/cluster/overview");
                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<ApiResponse<ClusterOverview>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new ApiResponse<ClusterOverview> { Success = false, Error = "Failed to deserialize response" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting cluster overview");
                return new ApiResponse<ClusterOverview> { Success = false, Error = ex.Message };
            }
        }

        public async Task<ApiResponse<List<NodeStatusInfo>>> GetNodesAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_apiBaseUrl}/cluster/nodes");
                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<ApiResponse<List<NodeStatusInfo>>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new ApiResponse<List<NodeStatusInfo>> { Success = false, Error = "Failed to deserialize response" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting nodes");
                return new ApiResponse<List<NodeStatusInfo>> { Success = false, Error = ex.Message };
            }
        }

        public async Task<ApiResponse<NodeStatusInfo?>> GetNodeAsync(string nodeId)
        {
            try
            {
                var nodesResponse = await GetNodesAsync();
                if (!nodesResponse.Success || nodesResponse.Data == null)
                {
                    return new ApiResponse<NodeStatusInfo?> { Success = false, Error = "Failed to get nodes" };
                }

                var node = nodesResponse.Data.FirstOrDefault(n => n.NodeId == nodeId);
                if (node == null)
                {
                    return new ApiResponse<NodeStatusInfo?> { Success = false, Error = $"Node {nodeId} not found" };
                }

                return new ApiResponse<NodeStatusInfo?> { Success = true, Data = node };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting node");
                return new ApiResponse<NodeStatusInfo?> { Success = false, Error = ex.Message };
            }
        }

        public async Task<ApiResponse<bool>> IsLeaderAsync()
        {
            try
            {
                var overview = await GetClusterOverviewAsync();
                if (!overview.Success || overview.Data == null)
                {
                    return new ApiResponse<bool> { Success = false, Error = "Failed to get cluster overview" };
                }

                return new ApiResponse<bool> { Success = true, Data = overview.Data.IsCurrentNodeLeader };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking leader status");
                return new ApiResponse<bool> { Success = false, Error = ex.Message };
            }
        }

        public async Task<ApiResponse<string>> GetCurrentNodeIdAsync()
        {
            try
            {
                var overview = await GetClusterOverviewAsync();
                if (!overview.Success || overview.Data == null)
                {
                    return new ApiResponse<string> { Success = false, Error = "Failed to get cluster overview" };
                }

                return new ApiResponse<string> { Success = true, Data = overview.Data.CurrentNodeId };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting current node ID");
                return new ApiResponse<string> { Success = false, Error = ex.Message };
            }
        }

        public Task<ApiResponse<string>> GetOptimalNodeAsync()
        {
            try
            {
                var clusterManager = GlobalClusterCenter.ClusterManager;
                if (clusterManager == null)
                {
                    return Task.FromResult(new ApiResponse<string> { Success = false, Error = "Cluster manager not available" });
                }

                var optimalNode = clusterManager.GetOptimalNode();
                return Task.FromResult(new ApiResponse<string> { Success = true, Data = optimalNode });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting optimal node");
                return Task.FromResult(new ApiResponse<string> { Success = false, Error = ex.Message });
            }
        }

        #endregion

        #region 客户端连接管理 / Client Connection Management

        public async Task<ApiResponse<List<ClientConnectionInfo>>> GetAllClientsAsync(string? nodeId = null)
        {
            try
            {
                var url = $"{_apiBaseUrl}/clients";
                if (!string.IsNullOrEmpty(nodeId))
                {
                    url += $"?nodeId={Uri.EscapeDataString(nodeId)}";
                }

                var response = await _httpClient.GetAsync(url);
                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<ApiResponse<List<ClientConnectionInfo>>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new ApiResponse<List<ClientConnectionInfo>> { Success = false, Error = "Failed to deserialize response" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all clients");
                return new ApiResponse<List<ClientConnectionInfo>> { Success = false, Error = ex.Message };
            }
        }

        public async Task<ApiResponse<List<ClientConnectionInfo>>> GetClientsByNodeAsync(string nodeId)
        {
            return await GetAllClientsAsync(nodeId);
        }

        public async Task<ApiResponse<List<ClientConnectionInfo>>> GetLocalClientsAsync()
        {
            try
            {
                var currentNodeId = GlobalClusterCenter.ClusterContext?.NodeId;
                return await GetAllClientsAsync(currentNodeId ?? string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting local clients");
                return new ApiResponse<List<ClientConnectionInfo>> { Success = false, Error = ex.Message };
            }
        }

        public async Task<ApiResponse<ClientConnectionInfo>> GetClientAsync(string connectionId)
        {
            try
            {
                var clientsResponse = await GetAllClientsAsync();
                if (!clientsResponse.Success || clientsResponse.Data == null)
                {
                    return new ApiResponse<ClientConnectionInfo> { Success = false, Error = "Failed to get clients" };
                }

                var client = clientsResponse.Data.FirstOrDefault(c => c.ConnectionId == connectionId);
                if (client == null)
                {
                    return new ApiResponse<ClientConnectionInfo> { Success = false, Error = $"Connection {connectionId} not found" };
                }

                return new ApiResponse<ClientConnectionInfo> { Success = true, Data = client };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting client");
                return new ApiResponse<ClientConnectionInfo> { Success = false, Error = ex.Message };
            }
        }

        public Task<ApiResponse<int>> GetTotalConnectionCountAsync()
        {
            try
            {
                var clusterManager = GlobalClusterCenter.ClusterManager;
                if (clusterManager == null)
                {
                    return Task.FromResult(new ApiResponse<int> { Success = false, Error = "Cluster manager not available" });
                }

                var count = clusterManager.GetTotalConnectionCount();
                return Task.FromResult(new ApiResponse<int> { Success = true, Data = count });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting total connection count");
                return Task.FromResult(new ApiResponse<int> { Success = false, Error = ex.Message });
            }
        }

        public Task<ApiResponse<int>> GetLocalConnectionCountAsync()
        {
            try
            {
                var clusterManager = GlobalClusterCenter.ClusterManager;
                if (clusterManager == null)
                {
                    return Task.FromResult(new ApiResponse<int> { Success = false, Error = "Cluster manager not available" });
                }

                var count = clusterManager.GetLocalConnectionCount();
                return Task.FromResult(new ApiResponse<int> { Success = true, Data = count });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting local connection count");
                return Task.FromResult(new ApiResponse<int> { Success = false, Error = ex.Message });
            }
        }

        public async Task<ApiResponse<int>> GetNodeConnectionCountAsync(string nodeId)
        {
            try
            {
                var clientsResponse = await GetClientsByNodeAsync(nodeId);
                if (!clientsResponse.Success || clientsResponse.Data == null)
                {
                    return new ApiResponse<int> { Success = false, Error = "Failed to get node clients" };
                }

                return new ApiResponse<int> { Success = true, Data = clientsResponse.Data.Count };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting node connection count");
                return new ApiResponse<int> { Success = false, Error = ex.Message };
            }
        }

        #endregion

        #region 消息发送 / Message Sending

        public async Task<ApiResponse<bool>> SendTextMessageAsync(string connectionId, string content)
        {
            var request = new SendMessageRequest
            {
                ConnectionId = connectionId,
                Content = content,
                MessageType = "Text"
            };
            return await SendMessageAsync(request);
        }

        public async Task<ApiResponse<bool>> SendBinaryMessageAsync(string connectionId, string content)
        {
            var request = new SendMessageRequest
            {
                ConnectionId = connectionId,
                Content = content,
                MessageType = "Binary"
            };
            return await SendMessageAsync(request);
        }

        public async Task<ApiResponse<bool>> SendMessageAsync(SendMessageRequest request)
        {
            try
            {
                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync($"{_apiBaseUrl}/send", content);
                var responseContent = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<ApiResponse<bool>>(responseContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new ApiResponse<bool> { Success = false, Error = "Failed to deserialize response" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message");
                return new ApiResponse<bool> { Success = false, Error = ex.Message };
            }
        }

        public async Task<ApiResponse<List<KeyValuePair<string, bool>>>> SendMessagesAsync(List<SendMessageRequest> requests)
        {
            try
            {
                var results = new List<KeyValuePair<string, bool>>();
                var tasks = requests.Select(async req =>
                {
                    var result = await SendMessageAsync(req);
                    return new KeyValuePair<string, bool>(req.ConnectionId, result.Success && result.Data);
                });

                var taskResults = await Task.WhenAll(tasks);
                results.AddRange(taskResults);

                return new ApiResponse<List<KeyValuePair<string, bool>>>
                {
                    Success = true,
                    Data = results
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending messages");
                return new ApiResponse<List<KeyValuePair<string, bool>>> { Success = false, Error = ex.Message };
            }
        }

        public async Task<ApiResponse<int>> BroadcastMessageAsync(string content, string messageType = "Text")
        {
            try
            {
                var clientsResponse = await GetAllClientsAsync();
                if (!clientsResponse.Success || clientsResponse.Data == null || clientsResponse.Data.Count == 0)
                {
                    return new ApiResponse<int> { Success = true, Data = 0 };
                }

                var requests = clientsResponse.Data.Select(c => new SendMessageRequest
                {
                    ConnectionId = c.ConnectionId,
                    Content = content,
                    MessageType = messageType
                }).ToList();

                var results = await SendMessagesAsync(requests);
                if (!results.Success || results.Data == null)
                {
                    return new ApiResponse<int> { Success = false, Error = "Failed to send messages" };
                }

                var successCount = results.Data.Count(kvp => kvp.Value);
                return new ApiResponse<int> { Success = true, Data = successCount };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting message");
                return new ApiResponse<int> { Success = false, Error = ex.Message };
            }
        }

        public async Task<ApiResponse<int>> BroadcastMessageToNodeAsync(string nodeId, string content, string messageType = "Text")
        {
            try
            {
                var clientsResponse = await GetClientsByNodeAsync(nodeId);
                if (!clientsResponse.Success || clientsResponse.Data == null || clientsResponse.Data.Count == 0)
                {
                    return new ApiResponse<int> { Success = true, Data = 0 };
                }

                var requests = clientsResponse.Data.Select(c => new SendMessageRequest
                {
                    ConnectionId = c.ConnectionId,
                    Content = content,
                    MessageType = messageType
                }).ToList();

                var results = await SendMessagesAsync(requests);
                if (!results.Success || results.Data == null)
                {
                    return new ApiResponse<int> { Success = false, Error = "Failed to send messages" };
                }

                var successCount = results.Data.Count(kvp => kvp.Value);
                return new ApiResponse<int> { Success = true, Data = successCount };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting message to node");
                return new ApiResponse<int> { Success = false, Error = ex.Message };
            }
        }

        #endregion

        #region 统计信息 / Statistics

        public async Task<ApiResponse<BandwidthStatistics>> GetBandwidthStatisticsAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_apiBaseUrl}/bandwidth");
                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<ApiResponse<BandwidthStatistics>>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new ApiResponse<BandwidthStatistics> { Success = false, Error = "Failed to deserialize response" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting bandwidth statistics");
                return new ApiResponse<BandwidthStatistics> { Success = false, Error = ex.Message };
            }
        }

        public async Task<ApiResponse<ClientConnectionInfo>> GetConnectionStatisticsAsync(string connectionId)
        {
            return await GetClientAsync(connectionId);
        }

        public async Task<ApiResponse<List<ClientConnectionInfo>>> GetAllConnectionStatisticsAsync()
        {
            return await GetAllClientsAsync();
        }

        #endregion

        #region 连接路由 / Connection Routing

        public Task<ApiResponse<Dictionary<string, string>>> GetConnectionRoutesAsync()
        {
            try
            {
                var clusterManager = GlobalClusterCenter.ClusterManager;
                if (clusterManager == null || clusterManager.ConnectionRoutes == null)
                {
                    return Task.FromResult(new ApiResponse<Dictionary<string, string>> { Success = false, Error = "Cluster manager or connection routes not available" });
                }

                var routes = clusterManager.ConnectionRoutes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                return Task.FromResult(new ApiResponse<Dictionary<string, string>> { Success = true, Data = routes });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting connection routes");
                return Task.FromResult(new ApiResponse<Dictionary<string, string>> { Success = false, Error = ex.Message });
            }
        }

        public async Task<ApiResponse<string>> QueryConnectionNodeAsync(string connectionId)
        {
            try
            {
                var routesResponse = await GetConnectionRoutesAsync();
                if (!routesResponse.Success || routesResponse.Data == null)
                {
                    return new ApiResponse<string> { Success = false, Error = "Failed to get connection routes" };
                }

                if (!routesResponse.Data.TryGetValue(connectionId, out var nodeId))
                {
                    return new ApiResponse<string> { Success = false, Error = $"Connection {connectionId} not found in routing table" };
                }

                return new ApiResponse<string> { Success = true, Data = nodeId };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error querying connection node");
                return new ApiResponse<string> { Success = false, Error = ex.Message };
            }
        }

        public async Task<ApiResponse<List<string>>> GetNodeConnectionIdsAsync(string nodeId)
        {
            try
            {
                var routesResponse = await GetConnectionRoutesAsync();
                if (!routesResponse.Success || routesResponse.Data == null)
                {
                    return new ApiResponse<List<string>> { Success = false, Error = "Failed to get connection routes" };
                }

                var connectionIds = routesResponse.Data
                    .Where(kvp => kvp.Value == nodeId)
                    .Select(kvp => kvp.Key)
                    .ToList();

                return new ApiResponse<List<string>> { Success = true, Data = connectionIds };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting node connection IDs");
                return new ApiResponse<List<string>> { Success = false, Error = ex.Message };
            }
        }

        #endregion

        #region 健康检查 / Health Check

        public async Task<ApiResponse<ClusterHealthStatus>> GetClusterHealthAsync()
        {
            try
            {
                var overview = await GetClusterOverviewAsync();
                if (!overview.Success || overview.Data == null)
                {
                    return new ApiResponse<ClusterHealthStatus>
                    {
                        Success = false,
                        Error = "Failed to get cluster overview"
                    };
                }

                var nodes = await GetNodesAsync();
                var healthyNodes = nodes.Success && nodes.Data != null
                    ? nodes.Data.Count(n => n.IsConnected)
                    : 0;

                var healthStatus = new ClusterHealthStatus
                {
                    IsHealthy = overview.Data.ConnectedNodes > 0 && overview.Data.TotalNodes > 0,
                    TotalNodes = overview.Data.TotalNodes,
                    HealthyNodes = healthyNodes,
                    UnhealthyNodes = overview.Data.TotalNodes - healthyNodes,
                    HasLeader = overview.Data.IsCurrentNodeLeader || nodes.Success && nodes.Data != null && nodes.Data.Any(n => n.IsLeader),
                    TotalConnections = overview.Data.TotalConnections,
                    Details = new Dictionary<string, object>
                    {
                        { "CurrentNodeId", overview.Data.CurrentNodeId },
                        { "IsCurrentNodeLeader", overview.Data.IsCurrentNodeLeader },
                        { "LocalConnections", overview.Data.LocalConnections }
                    }
                };

                return new ApiResponse<ClusterHealthStatus> { Success = true, Data = healthStatus };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting cluster health");
                return new ApiResponse<ClusterHealthStatus> { Success = false, Error = ex.Message };
            }
        }

        public async Task<ApiResponse<NodeHealthStatus>> GetNodeHealthAsync(string nodeId)
        {
            try
            {
                var nodeResponse = await GetNodeAsync(nodeId);
                if (!nodeResponse.Success || nodeResponse.Data == null)
                {
                    return new ApiResponse<NodeHealthStatus>
                    {
                        Success = false,
                        Error = nodeResponse.Error ?? $"Node {nodeId} not found"
                    };
                }

                var node = nodeResponse.Data;
                var startTime = DateTime.UtcNow;
                var connectionCount = await GetNodeConnectionCountAsync(nodeId);
                var responseTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

                var healthStatus = new NodeHealthStatus
                {
                    NodeId = node.NodeId,
                    IsHealthy = node.IsConnected,
                    IsConnected = node.IsConnected,
                    ConnectionCount = connectionCount.Success ? connectionCount.Data : 0,
                    IsLeader = node.IsLeader,
                    ResponseTimeMs = (long)responseTime
                };

                return new ApiResponse<NodeHealthStatus> { Success = true, Data = healthStatus };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting node health");
                return new ApiResponse<NodeHealthStatus>
                {
                    Success = false,
                    Error = ex.Message,
                    Data = new NodeHealthStatus
                    {
                        NodeId = nodeId,
                        IsHealthy = false,
                        ErrorMessage = ex.Message
                    }
                };
            }
        }

        #endregion
    }
}

