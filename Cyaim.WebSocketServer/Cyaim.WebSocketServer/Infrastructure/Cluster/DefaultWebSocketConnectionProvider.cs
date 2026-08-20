using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;

namespace Cyaim.WebSocketServer.Infrastructure.Cluster
{
    /// <summary>
    /// Default WebSocket connection provider using MvcChannelHandler.Clients
    /// 使用 MvcChannelHandler.Clients 的默认 WebSocket 连接提供者
    /// </summary>
    public class DefaultWebSocketConnectionProvider : IWebSocketConnectionProvider
    {
        /// <summary>
        /// Get WebSocket instance by connection ID
        /// 根据连接 ID 获取 WebSocket 实例
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <returns>WebSocket instance or null / WebSocket 实例或 null</returns>
        public WebSocket GetConnection(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId))
                return null;

            if (MvcChannelHandler.Clients.TryGetValue(connectionId, out var webSocket))
            {
                return webSocket;
            }

            return null;
        }

        /// <summary>
        /// Send message to WebSocket connection
        /// 向 WebSocket 连接发送消息
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="data">Data to send / 要发送的数据</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <returns>True if sent successfully, false otherwise / 发送成功返回 true，否则返回 false</returns>
        public async Task<bool> SendAsync(string connectionId, byte[] data, WebSocketMessageType messageType, CancellationToken cancellationToken = default)
        {
            try
            {
                var webSocket = GetConnection(connectionId);
                if (webSocket == null)
                {
                    return false;
                }

                if (webSocket.State != WebSocketState.Open)
                {
                    return false;
                }

                // 必须走 WebSocketManager：直接 SendAsync 会绕过 per-socket 发送门闩，插进别人正在发的多帧消息中间，
                // 造出一条带结束标志、内容却是别人前半截的消息——正是发送不变式要消灭的东西。
                // Must go through WebSocketManager: a raw SendAsync bypasses the per-socket send gate and can inject
                // itself into someone else's multi-frame message, producing one that carries the end flag while holding
                // another message's opening bytes — exactly what the send invariant exists to prevent.
                await WebSocketManager.SendLocalAsync(
                    data.AsMemory(), messageType, sendAtOnce: true,
                    cancellationToken, timeout: null, sockets: webSocket);

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}

