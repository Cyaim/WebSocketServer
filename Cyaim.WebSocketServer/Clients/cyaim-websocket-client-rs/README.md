# cyaim-websocket-client

WebSocket client for Cyaim.WebSocketServer with automatic endpoint discovery (Rust)

## Installation

Add to your `Cargo.toml`:

```toml
[dependencies]
cyaim-websocket-client = { path = "../cyaim-websocket-client-rs" }
# Or from git:
# cyaim-websocket-client = { git = "https://github.com/cyaim/websocket-server" }
```

## Usage

### Basic Example

```rust
use cyaim_websocket_client::{WebSocketClientFactory, WebSocketClientOptions};
use serde::{Deserialize, Serialize};

#[derive(Serialize, Deserialize)]
struct WeatherForecast {
    date: String,
    temperature_c: i32,
    summary: String,
}

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut factory = WebSocketClientFactory::new(
        "http://localhost:5000".to_string(),
        "/ws".to_string(),
        None,
    );

    let client = factory.create_client();
    
    // Get forecasts
    let forecasts: Vec<WeatherForecast> = client
        .send_request("weatherforecast.get", None::<()>)
        .await?;

    // Get forecast by city
    let request_body = serde_json::json!({ "city": "Beijing" });
    let forecast: WeatherForecast = client
        .send_request("weatherforecast.getbycity", Some(request_body))
        .await?;

    Ok(())
}
```

### With Options

```rust
use cyaim_websocket_client::WebSocketClientOptions;

let options = WebSocketClientOptions {
    lazy_load_endpoints: true,
    validate_all_methods: false,
    throw_on_endpoint_not_found: true,
};

let mut factory = WebSocketClientFactory::new(
    "http://localhost:5000".to_string(),
    "/ws".to_string(),
    Some(options),
);
```

## API

### WebSocketClientFactory

- `new(server_base_url: String, channel: String, options: Option<WebSocketClientOptions>)` - Create factory
- `get_endpoints() -> Result<Vec<WebSocketEndpointInfo>>` - Get all endpoints
- `create_client() -> WebSocketClient` - Create client
- `find_endpoint(method_name: &str, target: Option<&str>) -> Result<Option<WebSocketEndpointInfo>>` - Find endpoint

### WebSocketClient

- `send_request<TRequest, TResponse>(target: &str, request_body: Option<TRequest>) -> Result<TResponse>` - Send request

## License

MIT

