using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure.Cluster
{
    /// <summary>
    /// WebSocket channel handler specifically for cluster communication
    /// 专门用于集群通信的 WebSocket 通道处理器
    /// </summary>
    public class ClusterChannelHandler
    {
        /// <summary>
        /// Store incoming server-side cluster connections
        /// 存储传入的服务器端集群连接
        /// </summary>
        public static ConcurrentDictionary<string, WebSocket> IncomingConnections { get; } = new ConcurrentDictionary<string, WebSocket>();

        /// <summary>
        /// Handle incoming cluster WebSocket connections
        /// 处理传入的集群 WebSocket 连接
        /// </summary>
        public static async Task ConnectionEntry(HttpContext context, ILogger<WebSocketRouteMiddleware> logger, WebSocketRouteOption webSocketOptions)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var remoteAddress = $"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort}";
            // 某些宿主（如 TestServer）不分配连接 ID，为空时补一个，避免以 null 作字典键崩溃
            // Some hosts (e.g. TestServer) don't assign a connection id; generate one so the
            // IncomingConnections dictionary never receives a null key
            var connectionId = string.IsNullOrEmpty(context.Connection.Id)
                ? Guid.NewGuid().ToString("N")
                : context.Connection.Id;

            logger.LogInformation($"Cluster WebSocket connection accepted from {remoteAddress} (ConnectionId: {connectionId})");

            // Store the connection
            IncomingConnections.TryAdd(connectionId, webSocket);

            try
            {
                await HandleClusterConnection(webSocket, context, logger, connectionId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Error handling cluster connection from {remoteAddress}");
            }
            finally
            {
                IncomingConnections.TryRemove(connectionId, out _);
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection closed", CancellationToken.None);
                }
                logger.LogInformation($"Cluster WebSocket connection closed from {remoteAddress}");
            }
        }

        /// <summary>
        /// Receive buffer size in bytes for a single WebSocket frame / 单个 WebSocket 帧的接收缓冲区大小（字节）
        /// </summary>
        private const int ReceiveBufferSize = 16 * 1024;

        /// <summary>
        /// Maximum allowed size of a single inbound cluster message in bytes (default 64MB).
        /// Messages larger than this cause the connection to be closed to protect memory.
        /// 单个入站集群消息允许的最大大小（字节，默认 64MB）。
        /// 超过此大小的消息将导致连接关闭以保护内存。
        /// </summary>
        public static int MaxMessageSize { get; set; } = 64 * 1024 * 1024;

        /// <summary>
        /// Handle cluster connection and forward messages to transport layer.
        /// Supports multi-frame message reassembly: frames are accumulated until EndOfMessage,
        /// so messages larger than the receive buffer are no longer dropped or truncated.
        /// 处理集群连接并将消息转发到传输层。
        /// 支持多帧消息重组：帧会被累积直到 EndOfMessage，因此大于接收缓冲区的消息不会再被丢弃或截断。
        /// </summary>
        private static async Task HandleClusterConnection(WebSocket webSocket, HttpContext context, ILogger<WebSocketRouteMiddleware> logger, string connectionId)
        {
            var buffer = new byte[ReceiveBufferSize];
            string identifiedNodeId = null;
            // Accumulate frames until EndOfMessage to support messages larger than the buffer
            // 累积帧直到 EndOfMessage，以支持大于缓冲区的消息
            using var messageStream = new System.IO.MemoryStream();

            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.Count > 0)
                {
                    // Guard against oversized messages / 防止超大消息
                    if (messageStream.Length + result.Count > MaxMessageSize)
                    {
                        logger.LogError($"Inbound cluster message exceeds MaxMessageSize ({MaxMessageSize} bytes), closing connection {connectionId}");
                        await webSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Cluster message too big", CancellationToken.None);
                        break;
                    }

                    messageStream.Write(buffer, 0, result.Count);
                }

                if (!result.EndOfMessage)
                {
                    // Wait for remaining frames of this message / 等待此消息的剩余帧
                    continue;
                }

                if (messageStream.Length == 0)
                {
                    continue;
                }

                var messageJson = Encoding.UTF8.GetString(messageStream.GetBuffer(), 0, (int)messageStream.Length);
                messageStream.SetLength(0);

                try
                {
                    var message = JsonSerializer.Deserialize<ClusterMessage>(messageJson);
                    if (message == null)
                    {
                        logger.LogWarning("Received null or invalid cluster message");
                        continue;
                    }

                    // Identify the node from the message
                    // 从消息中识别节点
                    if (string.IsNullOrEmpty(identifiedNodeId) && !string.IsNullOrEmpty(message.FromNodeId))
                    {
                        identifiedNodeId = message.FromNodeId;
                        logger.LogInformation($"Identified incoming cluster connection from node {identifiedNodeId} (ConnectionId: {connectionId})");
                    }

                    // Forward message to cluster manager's transport
                    // 将消息转发到集群管理器的传输层
                    var clusterManager = GlobalClusterCenter.ClusterManager;
                    if (clusterManager != null)
                    {
                        if (clusterManager.Transport is Transports.WebSocketClusterTransport wsTransport)
                        {
                            // Inject the inbound message through the official injection point
                            // 通过正式注入点注入入站消息
                            wsTransport.OnPeerMessage(message, message.FromNodeId ?? identifiedNodeId);
                            logger.LogDebug($"Forwarded cluster message {message.Type} from node {message.FromNodeId ?? identifiedNodeId}");
                        }
                        else
                        {
                            logger.LogWarning($"Cluster transport does not accept inbound WebSocket cluster messages (transport: {clusterManager.Transport?.GetType().Name ?? "null"})");
                        }
                    }
                    else
                    {
                        logger.LogWarning("Cluster manager not available, cannot process cluster message");
                    }
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, $"Failed to deserialize cluster message: {messageJson.Substring(0, Math.Min(100, messageJson.Length))}");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Error processing cluster message: {ex.Message}");
                }
            }
        }
    }
}
