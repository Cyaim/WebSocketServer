use crate::types::{MvcRequestScheme, MvcResponseScheme};
use serde::{de::DeserializeOwned, Serialize};
use std::collections::HashMap;
use tokio::sync::oneshot;
use tokio_tungstenite::{connect_async, tungstenite::Message};
use futures_util::{SinkExt, StreamExt};

/// WebSocket client for connecting to Cyaim.WebSocketServer
/// 用于连接到 Cyaim.WebSocketServer 的 WebSocket 客户端
pub struct WebSocketClient {
    server_uri: String,
    channel: String,
    pending_responses: tokio::sync::Mutex<HashMap<String, oneshot::Sender<MvcResponseScheme>>>,
}

impl WebSocketClient {
    /// Constructor / 构造函数
    pub fn new(server_uri: String, channel: String) -> Self {
        Self {
            server_uri,
            channel,
            pending_responses: tokio::sync::Mutex::new(HashMap::new()),
        }
    }

    /// Send request and wait for response / 发送请求并等待响应
    pub async fn send_request<TRequest, TResponse>(
        &self,
        target: &str,
        request_body: Option<TRequest>,
    ) -> Result<TResponse, Box<dyn std::error::Error + Send + Sync>>
    where
        TRequest: Serialize,
        TResponse: DeserializeOwned,
    {
        let uri = format!("{}{}", self.server_uri.trim_end_matches('/'), self.channel);
        let (ws_stream, _) = connect_async(&uri).await?;
        let (mut write, mut read) = ws_stream.split();

        let request_id = uuid::Uuid::new_v4().to_string();
        let request = MvcRequestScheme {
            id: request_id.clone(),
            target: target.to_string(),
            body: request_body.map(|b| serde_json::to_value(b).unwrap()),
        };

        let request_json = serde_json::to_string(&request)?;
        write.send(Message::Text(request_json)).await?;

        // Create channel for response / 创建响应通道
        let (tx, rx) = oneshot::channel();
        {
            let mut pending = self.pending_responses.lock().await;
            pending.insert(request_id.clone(), tx);
        }

        // Spawn task to handle messages / 生成任务处理消息
        let pending_clone = self.pending_responses.clone();
        tokio::spawn(async move {
            while let Some(msg) = read.next().await {
                match msg {
                    Ok(Message::Text(text)) => {
                        if let Ok(response) = serde_json::from_str::<MvcResponseScheme>(&text) {
                            let mut pending = pending_clone.lock().await;
                            if let Some(tx) = pending.remove(&response.id) {
                                let _ = tx.send(response);
                            }
                        }
                    }
                    Ok(Message::Close(_)) => break,
                    Err(e) => {
                        eprintln!("WebSocket error: {}", e);
                        break;
                    }
                    _ => {}
                }
            }
        });

        // Wait for response / 等待响应
        match tokio::time::timeout(std::time::Duration::from_secs(30), rx).await {
            Ok(Ok(response)) => {
                if response.status != 0 {
                    return Err(format!("Request failed: {}", response.msg.unwrap_or_else(|| "Unknown error".to_string())).into());
                }

                if let Some(body) = response.body {
                    Ok(serde_json::from_value(body)?)
                } else {
                    Err("Response body is null".into())
                }
            }
            Ok(Err(_)) => Err("Response channel closed".into()),
            Err(_) => Err("Request timeout".into()),
        }
    }
}

