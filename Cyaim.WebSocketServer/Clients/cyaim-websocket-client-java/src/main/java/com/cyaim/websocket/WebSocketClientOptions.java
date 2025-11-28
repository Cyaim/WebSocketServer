package com.cyaim.websocket;

/**
 * Options for creating WebSocket client
 * 创建 WebSocket 客户端的选项
 */
public class WebSocketClientOptions {
    /**
     * Whether to validate all methods have corresponding endpoints (default: false)
     * 是否验证所有方法都有对应的端点（默认：false）
     */
    public boolean validateAllMethods = false;

    /**
     * Whether to fetch endpoints immediately or lazily (default: false)
     * 是否立即获取端点或延迟获取（默认：false）
     */
    public boolean lazyLoadEndpoints = false;

    /**
     * Whether to throw exception if endpoint not found (default: true)
     * 如果找不到端点是否抛出异常（默认：true）
     */
    public boolean throwOnEndpointNotFound = true;
}

