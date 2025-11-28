use serde::{Deserialize, Serialize};

/// WebSocket endpoint information / WebSocket 端点信息
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WebSocketEndpointInfo {
    pub controller: String,
    pub action: String,
    pub method_path: String,
    #[serde(default)]
    pub methods: Vec<String>,
    pub full_name: String,
    pub target: String,
}

/// API response wrapper / API 响应包装器
#[derive(Debug, Serialize, Deserialize)]
pub struct ApiResponse<T> {
    pub success: bool,
    pub data: Option<T>,
    pub error: Option<String>,
}

/// MVC request scheme / MVC 请求方案
#[derive(Debug, Serialize, Deserialize)]
pub struct MvcRequestScheme {
    pub id: String,
    pub target: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub body: Option<serde_json::Value>,
}

/// MVC response scheme / MVC 响应方案
#[derive(Debug, Serialize, Deserialize)]
pub struct MvcResponseScheme {
    pub id: String,
    pub target: String,
    pub status: i32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub msg: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub body: Option<serde_json::Value>,
}

