/// Options for creating WebSocket client
/// 创建 WebSocket 客户端的选项
#[derive(Debug, Clone)]
pub struct WebSocketClientOptions {
    /// Whether to validate all methods have corresponding endpoints (default: false)
    /// 是否验证所有方法都有对应的端点（默认：false）
    pub validate_all_methods: bool,
    
    /// Whether to fetch endpoints immediately or lazily (default: false)
    /// 是否立即获取端点或延迟获取（默认：false）
    pub lazy_load_endpoints: bool,
    
    /// Whether to throw exception if endpoint not found (default: true)
    /// 如果找不到端点是否抛出异常（默认：true）
    pub throw_on_endpoint_not_found: bool,
}

impl Default for WebSocketClientOptions {
    fn default() -> Self {
        Self {
            validate_all_methods: false,
            lazy_load_endpoints: false,
            throw_on_endpoint_not_found: true,
        }
    }
}

