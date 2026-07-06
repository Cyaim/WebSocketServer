namespace Cyaim.WebSocketServer.Infrastructure.Configures
{
    /// <summary>
    /// Per-endpoint receive policy resolved from <see cref="Attributes.WebSocketAttribute"/>.
    /// 由 [WebSocket] 属性解析出的端点级接收策略。
    /// </summary>
    public readonly struct EndpointReceivePolicy
    {
        /// <summary>Streaming endpoint: do not buffer, stream frames to the endpoint.</summary>
        public bool IsStream { get; }

        /// <summary>
        /// Size cap in bytes. 0 = use the global default. For buffered endpoints this bounds the
        /// in-memory buffer; for streaming endpoints it bounds the total streamed bytes.
        /// </summary>
        public long MaxBytes { get; }

        public EndpointReceivePolicy(bool isStream, long maxBytes)
        {
            IsStream = isStream;
            MaxBytes = maxBytes;
        }
    }
}
