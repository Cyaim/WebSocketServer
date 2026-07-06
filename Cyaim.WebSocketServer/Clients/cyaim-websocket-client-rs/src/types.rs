use serde::{Deserialize, Serialize};

/// WebSocket endpoint information / WebSocket 端点信息
/// Server JSON is camelCase (methodPath/fullName). / 服务端 JSON 为 camelCase。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WebSocketEndpointInfo {
    #[serde(default)]
    pub controller: String,
    #[serde(default)]
    pub action: String,
    #[serde(default)]
    pub method_path: String,
    #[serde(default)]
    pub methods: Vec<String>,
    #[serde(default)]
    pub full_name: String,
    #[serde(default)]
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

/// MVC response scheme (JSON). The server serializes responses in PascalCase
/// (Status/Id/Msg/Body), so map field names accordingly; extra fields
/// (RequestTime/CompleteTime) are ignored.
/// MVC 响应（JSON）。服务端以 PascalCase 序列化，故按名映射；多余字段被忽略。
#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct MvcResponseScheme {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub target: String,
    pub status: i32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub msg: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub body: Option<serde_json::Value>,
}

/// MessagePack response scheme matching the server's integer [Key] layout, which
/// rmp-serde decodes positionally as a 7-element array:
/// [Status(0), Msg(1), RequestTime(2), CompleteTime(3), Id(4), Target(5), Body(6)].
/// 与服务端整数 [Key] 布局一致的 MessagePack 响应（rmp-serde 按位置解码为 7 元数组）。
#[derive(Debug, Deserialize)]
pub struct MessagePackResponseScheme {
    pub status: i32,
    pub msg: Option<String>,
    #[serde(default)]
    pub request_time: i64,
    #[serde(default)]
    pub complete_time: i64,
    pub id: String,
    pub target: String,
    pub body: Option<serde_json::Value>,
}

impl From<MessagePackResponseScheme> for MvcResponseScheme {
    fn from(m: MessagePackResponseScheme) -> Self {
        MvcResponseScheme {
            id: m.id,
            target: m.target,
            status: m.status,
            msg: m.msg,
            body: m.body,
        }
    }
}

