namespace Cyaim.WebSocketServer.Infrastructure.Configures
{
    /// <summary>
    /// Per-endpoint receive policy resolved from <see cref="Attributes.WebSocketAttribute"/>.
    /// 由 [WebSocket] 属性解析出的端点级接收策略。
    /// </summary>
    public readonly struct EndpointReceivePolicy
    {
        /// <summary>Streaming endpoint (方案 B): do not buffer, stream frames to the endpoint.</summary>
        public bool IsStream { get; }

        /// <summary>
        /// Size cap in bytes. 0 = use the global default. For buffered endpoints (方案 A) this bounds the
        /// in-memory buffer; for streaming endpoints (方案 B) it bounds the total streamed bytes.
        /// </summary>
        public long MaxBytes { get; }

        public EndpointReceivePolicy(bool isStream, long maxBytes)
        {
            IsStream = isStream;
            MaxBytes = maxBytes;
        }
    }
}
