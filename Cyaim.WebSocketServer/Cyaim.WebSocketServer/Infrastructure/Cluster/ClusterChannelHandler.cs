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
            var connectionId = context.Connection.Id;

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
        /// Handle cluster connection and forward messages to transport layer
        /// 处理集群连接并将消息转发到传输层
        /// </summary>
        private static async Task HandleClusterConnection(WebSocket webSocket, HttpContext context, ILogger<WebSocketRouteMiddleware> logger, string connectionId)
        {
            var buffer = new byte[4096];
            string identifiedNodeId = null;

            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.EndOfMessage && result.Count > 0)
                {
                    var messageJson = Encoding.UTF8.GetString(buffer, 0, result.Count);

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

                        // Forward message to cluster manager
                        // 将消息转发到集群管理器
                        var clusterManager = GlobalClusterCenter.ClusterManager;
                        if (clusterManager != null)
                        {
                            // Get the transport and trigger MessageReceived event
                            // 获取传输层并触发 MessageReceived 事件
                            var transport = GetTransportFromManager(clusterManager);
                            if (transport is Transports.WebSocketClusterTransport wsTransport)
                            {
                                // Create event args and trigger the event
                                // 创建事件参数并触发事件
                                var eventArgs = new ClusterMessageEventArgs
                                {
                                    FromNodeId = message.FromNodeId ?? identifiedNodeId,
                                    Message = message
                                };

                                // Trigger MessageReceived event using reflection
                                // 使用反射触发 MessageReceived 事件
                                var eventField = typeof(Transports.WebSocketClusterTransport)
                                    .GetField("MessageReceived", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                                if (eventField?.GetValue(wsTransport) is EventHandler<ClusterMessageEventArgs> handler)
                                {
                                    handler.Invoke(wsTransport, eventArgs);
                                    logger.LogDebug($"Forwarded cluster message {message.Type} from node {eventArgs.FromNodeId}");
                                }
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

        /// <summary>
        /// Get transport instance from cluster manager using reflection
        /// 使用反射从集群管理器获取传输实例
        /// </summary>
        private static IClusterTransport GetTransportFromManager(ClusterManager manager)
        {
            var transportField = typeof(ClusterManager)
                .GetField("_transport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return transportField?.GetValue(manager) as IClusterTransport;
        }
    }
}
