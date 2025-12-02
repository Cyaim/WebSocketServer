using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Client
{
    /// <summary>
    /// Factory for creating WebSocket client proxies based on server endpoints
    /// 基于服务器端点创建 WebSocket 客户端代理的工厂
    /// </summary>
    public class WebSocketClientFactory
    {
        private readonly string _serverBaseUrl;
        private readonly string _channel;
        private readonly HttpClient _httpClient;
        private List<WebSocketEndpointInfo>? _cachedEndpoints;
        private readonly WebSocketClientOptions _options;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="serverBaseUrl">Server base URL (e.g., "http://localhost:5000") / 服务器基础 URL（例如："http://localhost:5000"）</param>
        /// <param name="channel">WebSocket channel (default: "/ws") / WebSocket 通道（默认："/ws"）</param>
        /// <param name="options">Client creation options / 客户端创建选项</param>
        public WebSocketClientFactory(string serverBaseUrl, string channel = "/ws", WebSocketClientOptions? options = null)
        {
            _serverBaseUrl = serverBaseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(serverBaseUrl));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _options = options ?? new WebSocketClientOptions();
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// Get endpoints from server / 从服务器获取端点
        /// </summary>
        public async Task<List<WebSocketEndpointInfo>> GetEndpointsAsync()
        {
            if (_cachedEndpoints != null)
            {
                return _cachedEndpoints;
            }

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
                    _cachedEndpoints = apiResponse.Data;
                    return _cachedEndpoints;
                }

                throw new Exception(apiResponse?.Error ?? "Failed to fetch endpoints");
            }
            catch (Exception ex)
            {
                throw new Exception($"Error fetching WebSocket endpoints from server: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Create a client proxy for a specific interface / 为特定接口创建客户端代理
        /// </summary>
        /// <typeparam name="T">Interface type / 接口类型</typeparam>
        /// <returns>Client proxy instance / 客户端代理实例</returns>
        public async Task<T> CreateClientAsync<T>() where T : class
        {
            var interfaceType = typeof(T);

            if (!interfaceType.IsInterface)
            {
                throw new ArgumentException($"Type {interfaceType.Name} must be an interface");
            }

            // Create WebSocket client / 创建 WebSocket 客户端
            var wsUri = _serverBaseUrl.Replace("http://", "ws://").Replace("https://", "wss://");
            var client = new WebSocketClient(wsUri, _channel, _options);

            // Get endpoints if not lazy loading / 如果不是延迟加载，则获取端点
            List<WebSocketEndpointInfo>? endpoints = null;
            if (!_options.LazyLoadEndpoints)
            {
                endpoints = await GetEndpointsAsync();
            }

            // Create proxy using reflection / 使用反射创建代理
            return CreateProxy<T>(client, endpoints);
        }

        /// <summary>
        /// Create proxy instance / 创建代理实例
        /// </summary>
        private T CreateProxy<T>(WebSocketClient client, List<WebSocketEndpointInfo>? endpoints) where T : class
        {
            var interfaceType = typeof(T);
            var methods = interfaceType.GetMethods();

            // Create a dictionary to map method names to endpoints / 创建方法名到端点的映射
            var methodEndpointMap = new Dictionary<MethodInfo, WebSocketEndpointInfo?>();
            var missingMethods = new List<string>();

            foreach (var method in methods)
            {
                WebSocketEndpointInfo? endpoint = null;

                // First, check for WebSocketEndpointAttribute / 首先检查 WebSocketEndpointAttribute
                var endpointAttr = method.GetCustomAttribute<WebSocketEndpointAttribute>();
                if (endpointAttr != null)
                {
                    // If endpoints are available, validate the target / 如果端点可用，验证目标
                    if (endpoints != null)
                    {
                        endpoint = endpoints.FirstOrDefault(ep =>
                            ep.Target.Equals(endpointAttr.Target, StringComparison.OrdinalIgnoreCase) ||
                            ep.MethodPath.Equals(endpointAttr.Target, StringComparison.OrdinalIgnoreCase) ||
                            ep.FullName.Equals(endpointAttr.Target, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        // If lazy loading, store the target from attribute / 如果延迟加载，存储特性中的目标
                        endpoint = new WebSocketEndpointInfo { Target = endpointAttr.Target };
                    }
                }
                else if (endpoints != null)
                {
                    // Try to find matching endpoint by method name / 尝试通过方法名查找匹配的端点
                    endpoint = endpoints.FirstOrDefault(ep =>
                        ep.Action.Equals(method.Name, StringComparison.OrdinalIgnoreCase) ||
                        ep.MethodPath.Equals(method.Name, StringComparison.OrdinalIgnoreCase));
                }

                if (endpoint != null)
                {
                    methodEndpointMap[method] = endpoint;
                }
                else
                {
                    methodEndpointMap[method] = null;
                    if (_options.ValidateAllMethods)
                    {
                        missingMethods.Add(method.Name);
                    }
                }
            }

            // Validate all methods if required / 如果需要，验证所有方法
            if (_options.ValidateAllMethods && missingMethods.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The following methods do not have corresponding endpoints: {string.Join(", ", missingMethods)}. " +
                    $"You can use [WebSocketEndpoint(\"target\")] attribute to specify the endpoint, or set ValidateAllMethods to false.");
            }

            // Create proxy using DispatchProxy / 使用 DispatchProxy 创建代理
            var proxy = DispatchProxy.Create<T, WebSocketClientProxy<T>>();
            if (proxy is WebSocketClientProxy<T> typedProxy)
            {
                typedProxy.Initialize(client, methodEndpointMap, this, _options);
            }

            return proxy;
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
    /// WebSocket client proxy implementation / WebSocket 客户端代理实现
    /// </summary>
    internal class WebSocketClientProxy<T> : DispatchProxy where T : class
    {
        private WebSocketClient? _client;
        private Dictionary<MethodInfo, WebSocketEndpointInfo?>? _methodEndpointMap;
        private WebSocketClientFactory? _factory;
        private WebSocketClientOptions? _options;

        /// <summary>
        /// Initialize proxy / 初始化代理
        /// </summary>
        public void Initialize(
            WebSocketClient client,
            Dictionary<MethodInfo, WebSocketEndpointInfo?> methodEndpointMap,
            WebSocketClientFactory factory,
            WebSocketClientOptions options)
        {
            _client = client;
            _methodEndpointMap = methodEndpointMap;
            _factory = factory;
            _options = options;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null || _client == null || _methodEndpointMap == null || _factory == null || _options == null)
            {
                throw new InvalidOperationException("Proxy not properly initialized");
            }

            // Get endpoint, lazy load if needed / 获取端点，如果需要则延迟加载
            if (!_methodEndpointMap.TryGetValue(targetMethod, out var endpoint) || endpoint == null)
            {
                // Try lazy loading / 尝试延迟加载
                if (_options.LazyLoadEndpoints)
                {
                    endpoint = LoadEndpointForMethodAsync(targetMethod).GetAwaiter().GetResult();
                    if (endpoint != null)
                    {
                        _methodEndpointMap[targetMethod] = endpoint;
                    }
                }

                if (endpoint == null)
                {
                    var errorMsg = $"Endpoint not found for method {targetMethod.Name}. " +
                        $"You can use [WebSocketEndpoint(\"target\")] attribute to specify the endpoint.";
                    
                    if (_options.ThrowOnEndpointNotFound)
                    {
                        throw new NotSupportedException(errorMsg);
                    }
                    else
                    {
                        throw new InvalidOperationException(errorMsg);
                    }
                }
            }

            // Build request body from method parameters / 从方法参数构建请求体
            object? requestBody = null;
            if (args != null && args.Length > 0)
            {
                if (args.Length == 1)
                {
                    requestBody = args[0];
                }
                else
                {
                    // Create anonymous object from parameters / 从参数创建匿名对象
                    var paramNames = targetMethod.GetParameters().Select(p => p.Name).ToArray();
                    var dict = new Dictionary<string, object?>();
                    for (int i = 0; i < args.Length && i < paramNames.Length; i++)
                    {
                        if (paramNames[i] != null)
                        {
                            dict[paramNames[i]!] = args[i];
                        }
                    }
                    requestBody = dict;
                }
            }

            // Get return type / 获取返回类型
            var returnType = targetMethod.ReturnType;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GetGenericArguments()[0];
                var method = typeof(WebSocketClientProxy<T>).GetMethod(
                    nameof(InvokeAsync),
                    BindingFlags.NonPublic | BindingFlags.Instance
                )!.MakeGenericMethod(resultType);

                return method.Invoke(this, new object[] { endpoint.Target, requestBody! });
            }
            else if (returnType == typeof(Task))
            {
                return InvokeAsync<object>(endpoint.Target, requestBody!);
            }

            throw new NotSupportedException($"Unsupported return type: {returnType}");
        }

        private async Task<TResult> InvokeAsync<TResult>(string target, object requestBody)
        {
            await _client!.ConnectAsync();
            return await _client.SendRequestAsync<object, TResult>(target, requestBody);
        }

        /// <summary>
        /// Load endpoint for a method (lazy loading) / 为方法加载端点（延迟加载）
        /// </summary>
        private async Task<WebSocketEndpointInfo?> LoadEndpointForMethodAsync(MethodInfo method)
        {
            if (_factory == null)
            {
                return null;
            }

            var endpoints = await _factory.GetEndpointsAsync();

            // Check attribute first / 首先检查特性
            var endpointAttr = method.GetCustomAttribute<WebSocketEndpointAttribute>();
            if (endpointAttr != null)
            {
                return endpoints.FirstOrDefault(ep =>
                    ep.Target.Equals(endpointAttr.Target, StringComparison.OrdinalIgnoreCase) ||
                    ep.MethodPath.Equals(endpointAttr.Target, StringComparison.OrdinalIgnoreCase) ||
                    ep.FullName.Equals(endpointAttr.Target, StringComparison.OrdinalIgnoreCase));
            }

            // Try by method name / 尝试通过方法名
            return endpoints.FirstOrDefault(ep =>
                ep.Action.Equals(method.Name, StringComparison.OrdinalIgnoreCase) ||
                ep.MethodPath.Equals(method.Name, StringComparison.OrdinalIgnoreCase));
        }
    }
}

