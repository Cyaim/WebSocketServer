using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Client
{
    /// <summary>
    /// WebSocket client generator that fetches endpoints from server and generates client proxies
    /// WebSocket 客户端生成器，从服务器获取端点并生成客户端代理
    /// </summary>
    public class WebSocketClientGenerator
    {
        private readonly HttpClient _httpClient;
        private readonly string _serverBaseUrl;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="serverBaseUrl">Server base URL (e.g., "http://localhost:5000") / 服务器基础 URL（例如："http://localhost:5000"）</param>
        public WebSocketClientGenerator(string serverBaseUrl)
        {
            _serverBaseUrl = serverBaseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(serverBaseUrl));
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// Fetch WebSocket endpoints from server / 从服务器获取 WebSocket 端点
        /// </summary>
        /// <returns>List of WebSocket endpoints / WebSocket 端点列表</returns>
        public async Task<List<WebSocketEndpointInfo>> FetchEndpointsAsync()
        {
            try
            {
                var url = $"{_serverBaseUrl}/ws_server/api/endpoints";
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var apiResponse = JsonSerializer.Deserialize<ApiResponse<List<WebSocketEndpointInfo>>>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );

                if (apiResponse?.Success == true && apiResponse.Data != null)
                {
                    return apiResponse.Data;
                }

                throw new Exception(apiResponse?.Error ?? "Failed to fetch endpoints");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error fetching WebSocket endpoints from server: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Dispose resources / 释放资源
        /// </summary>
        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    /// <summary>
    /// API response wrapper / API 响应包装器
    /// </summary>
    public class ApiResponse<T>
    {
        public bool Success { get; set; }
        public T? Data { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// WebSocket endpoint information / WebSocket 端点信息
    /// </summary>
    public class WebSocketEndpointInfo
    {
        public string Controller { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string MethodPath { get; set; } = string.Empty;
        public string[] Methods { get; set; } = Array.Empty<string>();
        public string FullName { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
    }
}

