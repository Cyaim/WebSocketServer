namespace Cyaim.WebSocketServer.Client
{
    /// <summary>
    /// Options for creating WebSocket client
    /// 创建 WebSocket 客户端的选项
    /// </summary>
    public class WebSocketClientOptions
    {
        /// <summary>
        /// Whether to validate all methods have corresponding endpoints (default: false)
        /// 是否验证所有方法都有对应的端点（默认：false）
        /// </summary>
        public bool ValidateAllMethods { get; set; } = false;

        /// <summary>
        /// Whether to fetch endpoints immediately or lazily (default: true)
        /// 是否立即获取端点或延迟获取（默认：true）
        /// </summary>
        public bool LazyLoadEndpoints { get; set; } = false;

        /// <summary>
        /// Whether to throw exception if endpoint not found (default: true)
        /// 如果找不到端点是否抛出异常（默认：true）
        /// </summary>
        public bool ThrowOnEndpointNotFound { get; set; } = true;
    }
}

