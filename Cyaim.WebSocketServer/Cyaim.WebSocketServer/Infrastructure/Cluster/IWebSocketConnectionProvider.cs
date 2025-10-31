using System.Net.WebSockets;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure.Cluster
{
    /// <summary>
    /// WebSocket connection provider interface for cluster routing
    /// 用于集群路由的 WebSocket 连接提供者接口
    /// </summary>
    public interface IWebSocketConnectionProvider
    {
        /// <summary>
        /// Get WebSocket instance by connection ID
        /// 根据连接 ID 获取 WebSocket 实例
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <returns>WebSocket instance if found, null otherwise / 找到则返回 WebSocket 实例，否则返回 null</returns>
        WebSocket GetConnection(string connectionId);

        /// <summary>
        /// Send message to WebSocket connection
        /// 向 WebSocket 连接发送消息
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="data">Data to send / 要发送的数据</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <returns>True if sent successfully, false otherwise / 发送成功返回 true，否则返回 false</returns>
        Task<bool> SendAsync(string connectionId, byte[] data, WebSocketMessageType messageType, System.Threading.CancellationToken cancellationToken = default);
    }
}

