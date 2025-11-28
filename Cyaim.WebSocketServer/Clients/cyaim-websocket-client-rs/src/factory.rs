use crate::client::WebSocketClient;
use crate::options::WebSocketClientOptions;
use crate::types::{ApiResponse, WebSocketEndpointInfo};
use async_trait::async_trait;
use std::collections::HashMap;

/// Factory for creating WebSocket client proxies based on server endpoints
/// 基于服务器端点创建 WebSocket 客户端代理的工厂
pub struct WebSocketClientFactory {
    server_base_url: String,
    channel: String,
    cached_endpoints: Option<Vec<WebSocketEndpointInfo>>,
    options: WebSocketClientOptions,
}

impl WebSocketClientFactory {
    /// Constructor / 构造函数
    pub fn new(
        server_base_url: String,
        channel: String,
        options: Option<WebSocketClientOptions>,
    ) -> Self {
        Self {
            server_base_url: server_base_url.trim_end_matches('/').to_string(),
            channel,
            cached_endpoints: None,
            options: options.unwrap_or_default(),
        }
    }

    /// Get endpoints from server / 从服务器获取端点
    pub async fn get_endpoints(&mut self) -> Result<Vec<WebSocketEndpointInfo>, Box<dyn std::error::Error + Send + Sync>> {
        if let Some(ref endpoints) = self.cached_endpoints {
            return Ok(endpoints.clone());
        }

        let url = format!("{}/ws_server/api/endpoints", self.server_base_url);
        let response: ApiResponse<Vec<WebSocketEndpointInfo>> = reqwest::get(&url)
            .await?
            .json()
            .await?;

        if response.success {
            if let Some(data) = response.data {
                self.cached_endpoints = Some(data.clone());
                return Ok(data);
            }
        }

        Err(format!("Failed to fetch endpoints: {}", response.error.unwrap_or_else(|| "Unknown error".to_string())).into())
    }

    /// Create a client / 创建客户端
    pub fn create_client(&self) -> WebSocketClient {
        let ws_uri = self.server_base_url
            .replace("http://", "ws://")
            .replace("https://", "wss://");
        WebSocketClient::new(ws_uri, self.channel.clone())
    }

    /// Find endpoint by method name or target / 通过方法名或目标查找端点
    pub async fn find_endpoint(
        &mut self,
        method_name: &str,
        target: Option<&str>,
    ) -> Result<Option<WebSocketEndpointInfo>, Box<dyn std::error::Error + Send + Sync>> {
        let endpoints = self.get_endpoints().await?;

        if let Some(t) = target {
            if let Some(ep) = endpoints.iter().find(|ep| {
                ep.target.eq_ignore_ascii_case(t)
                    || ep.method_path.eq_ignore_ascii_case(t)
                    || ep.full_name.eq_ignore_ascii_case(t)
            }) {
                return Ok(Some(ep.clone()));
            }
        }

        if let Some(ep) = endpoints.iter().find(|ep| {
            ep.action.eq_ignore_ascii_case(method_name)
                || ep.method_path.eq_ignore_ascii_case(method_name)
        }) {
            return Ok(Some(ep.clone()));
        }

        Ok(None)
    }
}

/// Trait for WebSocket service interfaces / WebSocket 服务接口的 trait
#[async_trait]
pub trait WebSocketService {
    /// Get the endpoint target for a method / 获取方法的端点目标
    fn endpoint_target(&self, method_name: &str) -> Option<String>;
}

