namespace Cyaim.WebSocketServer.Infrastructure.Metrics
{
    /// <summary>
    /// Interface for recording WebSocket connection statistics
    /// 用于记录 WebSocket 连接统计信息的接口
    /// </summary>
    public interface IWebSocketStatisticsRecorder
    {
        /// <summary>
        /// Record bytes sent / 记录发送的字节数
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="bytes">Bytes sent / 发送的字节数</param>
        void RecordBytesSent(string connectionId, int bytes);

        /// <summary>
        /// Record bytes received / 记录接收的字节数
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="bytes">Bytes received / 接收的字节数</param>
        void RecordBytesReceived(string connectionId, int bytes);
    }
}

