using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Client
{
    /// <summary>
    /// WebSocket client for connecting to Cyaim.WebSocketServer
    /// 用于连接到 Cyaim.WebSocketServer 的 WebSocket 客户端
    /// </summary>
    public class WebSocketClient : IDisposable
    {
        private readonly string _serverUri;
        private readonly string _channel;
        private ClientWebSocket? _webSocket;
        private bool _disposed = false;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="serverUri">Server URI (e.g., "ws://localhost:5000/ws") / 服务器 URI（例如："ws://localhost:5000/ws"）</param>
        /// <param name="channel">WebSocket channel (default: "/ws") / WebSocket 通道（默认："/ws"）</param>
        public WebSocketClient(string serverUri, string channel = "/ws")
        {
            _serverUri = serverUri ?? throw new ArgumentNullException(nameof(serverUri));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        }

        /// <summary>
        /// Connect to server / 连接到服务器
        /// </summary>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                return;
            }

            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();

            var uri = new Uri(_serverUri.TrimEnd('/') + _channel);
            await _webSocket.ConnectAsync(uri, cancellationToken);
        }

        /// <summary>
        /// Send request and wait for response / 发送请求并等待响应
        /// </summary>
        /// <typeparam name="TRequest">Request body type / 请求体类型</typeparam>
        /// <typeparam name="TResponse">Response body type / 响应体类型</typeparam>
        /// <param name="target">Target endpoint (e.g., "Controller.Action") / 目标端点（例如："Controller.Action"）</param>
        /// <param name="requestBody">Request body / 请求体</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <returns>Response body / 响应体</returns>
        public async Task<TResponse> SendRequestAsync<TRequest, TResponse>(
            string target,
            TRequest requestBody,
            CancellationToken cancellationToken = default)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("WebSocket is not connected. Call ConnectAsync first.");
            }

            var requestId = Guid.NewGuid().ToString();
            var request = new MvcRequestScheme
            {
                Id = requestId,
                Target = target,
                Body = requestBody
            };

            var requestJson = JsonSerializer.Serialize(request);
            var requestBytes = Encoding.UTF8.GetBytes(requestJson);

            // Send request / 发送请求
            await _webSocket.SendAsync(
                new ArraySegment<byte>(requestBytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken);

            // Wait for response / 等待响应
            var response = await ReceiveResponseAsync(requestId, cancellationToken);
            
            if (response.Status != 0)
            {
                throw new Exception($"Request failed: {response.Msg ?? "Unknown error"}");
            }

            if (response.Body == null)
            {
                return default(TResponse)!;
            }

            // Deserialize response body / 反序列化响应体
            if (response.Body is JsonElement jsonElement)
            {
                return JsonSerializer.Deserialize<TResponse>(jsonElement.GetRawText())!;
            }

            return JsonSerializer.Deserialize<TResponse>(JsonSerializer.Serialize(response.Body))!;
        }

        /// <summary>
        /// Receive response from server / 从服务器接收响应
        /// </summary>
        private async Task<MvcResponseScheme> ReceiveResponseAsync(string requestId, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            var result = await _webSocket!.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new Exception("WebSocket connection closed");
            }

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var response = JsonSerializer.Deserialize<MvcResponseScheme>(
                message,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            if (response == null)
            {
                throw new Exception("Failed to deserialize response");
            }

            // Wait for matching response ID / 等待匹配的响应 ID
            while (response.Id != requestId)
            {
                result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                response = JsonSerializer.Deserialize<MvcResponseScheme>(
                    message,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );

                if (response == null)
                {
                    throw new Exception("Failed to deserialize response");
                }
            }

            return response;
        }

        /// <summary>
        /// Disconnect from server / 断开服务器连接
        /// </summary>
        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", cancellationToken);
            }
        }

        /// <summary>
        /// Dispose resources / 释放资源
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _webSocket?.Dispose();
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// MVC request scheme / MVC 请求方案
    /// </summary>
    internal class MvcRequestScheme
    {
        public string Id { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public object? Body { get; set; }
    }

    /// <summary>
    /// MVC response scheme / MVC 响应方案
    /// </summary>
    internal class MvcResponseScheme
    {
        public string Id { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public int Status { get; set; }
        public string? Msg { get; set; }
        public object? Body { get; set; }
    }
}

