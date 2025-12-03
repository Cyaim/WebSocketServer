using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;

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
        private readonly WebSocketClientOptions _options;
        private ClientWebSocket? _webSocket;
        private bool _disposed = false;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="serverUri">Server URI (e.g., "ws://localhost:5000/ws") / 服务器 URI（例如："ws://localhost:5000/ws"）</param>
        /// <param name="channel">WebSocket channel (default: "/ws") / WebSocket 通道（默认："/ws"）</param>
        /// <param name="options">Client options / 客户端选项</param>
        public WebSocketClient(string serverUri, string channel = "/ws", WebSocketClientOptions? options = null)
        {
            _serverUri = serverUri ?? throw new ArgumentNullException(nameof(serverUri));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _options = options ?? new WebSocketClientOptions();
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

            byte[] requestBytes;
            WebSocketMessageType messageType;

            if (_options.Protocol == SerializationProtocol.MessagePack)
            {
                // 使用 MessagePack 序列化
                var request = new MessagePackRequestScheme
                {
                    Id = requestId,
                    Target = target,
                    Body = requestBody
                };
                requestBytes = MessagePackSerializer.Serialize(request);
                messageType = WebSocketMessageType.Binary;
            }
            else
            {
                // 使用 JSON 序列化
                var request = new MvcRequestScheme
                {
                    Id = requestId,
                    Target = target,
                    Body = requestBody
                };
                var requestJson = JsonSerializer.Serialize(request);
                requestBytes = Encoding.UTF8.GetBytes(requestJson);
                messageType = WebSocketMessageType.Text;
            }

            // Send request / 发送请求
            await _webSocket.SendAsync(
                new ArraySegment<byte>(requestBytes),
                messageType,
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
            if (_options.Protocol == SerializationProtocol.MessagePack)
            {
                // MessagePack 响应体已经是反序列化的对象，直接转换
                if (response.Body == null)
                {
                    return default(TResponse)!;
                }
                // 如果 Body 已经是目标类型，直接返回
                if (response.Body is TResponse directResponse)
                {
                    return directResponse;
                }
                // 否则通过 JSON 序列化/反序列化进行转换
                var jsonString = JsonSerializer.Serialize(response.Body);
                return JsonSerializer.Deserialize<TResponse>(jsonString)!;
            }
            else
            {
                // JSON 反序列化
                if (response.Body is JsonElement jsonElement)
                {
                    return JsonSerializer.Deserialize<TResponse>(jsonElement.GetRawText())!;
                }
                return JsonSerializer.Deserialize<TResponse>(JsonSerializer.Serialize(response.Body))!;
            }
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

            MvcResponseScheme response;

            if (_options.Protocol == SerializationProtocol.MessagePack)
            {
                // 使用 MessagePack 反序列化
                MessagePackResponseScheme messagePackResponse = await ReceiveMessagePackResponseAsync(requestId, result, buffer, cancellationToken);

                // 转换为 MvcResponseScheme 格式
                response = new MvcResponseScheme
                {
                    Id = messagePackResponse.Id,
                    Target = messagePackResponse.Target ?? string.Empty,
                    Status = messagePackResponse.Status,
                    Msg = messagePackResponse.Msg,
                    Body = messagePackResponse.Body
                };
            }
            else
            {
                // 使用 JSON 反序列化
                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                response = JsonSerializer.Deserialize<MvcResponseScheme>(
                    message,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                ) ?? throw new Exception("Failed to deserialize response");

                // Wait for matching response ID / 等待匹配的响应 ID
                while (response.Id != requestId)
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    response = JsonSerializer.Deserialize<MvcResponseScheme>(
                        message,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    ) ?? throw new Exception("Failed to deserialize response");
                }
            }

            return response;
        }

        /// <summary>
        /// Receive MessagePack response / 接收 MessagePack 响应
        /// </summary>
        private async Task<MessagePackResponseScheme> ReceiveMessagePackResponseAsync(
            string requestId,
            WebSocketReceiveResult initialResult,
            byte[] buffer,
            CancellationToken cancellationToken)
        {
            using var ms = new MemoryStream();

            // 接收完整消息（可能分片）
            do
            {
                ms.Write(buffer, 0, initialResult.Count);
                if (initialResult.EndOfMessage)
                {
                    break;
                }
                initialResult = await _webSocket!.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            } while (initialResult.MessageType != WebSocketMessageType.Close);

            var messageBytes = ms.ToArray();
            var response = MessagePackSerializer.Deserialize<MessagePackResponseScheme>(messageBytes);

            // Wait for matching response ID / 等待匹配的响应 ID
            while (response.Id != requestId)
            {
                var result = await _webSocket!.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new Exception("WebSocket connection closed");
                }

                using var nextMs = new MemoryStream();
                do
                {
                    nextMs.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                    {
                        break;
                    }
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                } while (result.MessageType != WebSocketMessageType.Close);

                response = MessagePackSerializer.Deserialize<MessagePackResponseScheme>(nextMs.ToArray());
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

