# Cyaim.WebSocketServer Clients

多语言 WebSocket 客户端实现，支持自动从服务端获取 endpoint 列表并实现接口契约式调用。

## 支持的客户端

### ✅ C# (.NET)
- **位置**: `Cyaim.WebSocketServer.Client/`
- **目标框架**: .NET 8.0, 9.0, 10.0
- **特性**: 
  - 使用 `DispatchProxy` 实现动态代理
  - 支持 `[WebSocketEndpoint]` 特性指定 endpoint
  - 支持延迟加载和自定义选项
- **文档**: [README.md](Cyaim.WebSocketServer.Client/README.md)

### ✅ JavaScript/TypeScript
- **位置**: `cyaim-websocket-client-js/`
- **包名**: `@cyaim/websocket-client`
- **特性**:
  - TypeScript 支持
  - 装饰器支持指定 endpoint
  - 基于 `ws` 库
- **文档**: [README.md](cyaim-websocket-client-js/README.md)

### ✅ Rust
- **位置**: `cyaim-websocket-client-rs/`
- **包名**: `cyaim-websocket-client`
- **特性**:
  - 基于 `tokio-tungstenite`
  - 异步支持
  - 类型安全
- **文档**: [README.md](cyaim-websocket-client-rs/README.md)

### ✅ Java
- **位置**: `cyaim-websocket-client-java/`
- **包名**: `com.cyaim:websocket-client`
- **特性**:
  - Maven 支持
  - 基于 `Java-WebSocket`
  - 支持 Java 11+
- **文档**: [README.md](cyaim-websocket-client-java/README.md)

### ✅ Dart
- **位置**: `cyaim-websocket-client-dart/`
- **包名**: `cyaim_websocket_client`
- **特性**:
  - Flutter/Dart 支持
  - 基于 `web_socket_channel`
  - 异步支持
- **文档**: [README.md](cyaim-websocket-client-dart/README.md)

### ✅ Python
- **位置**: `cyaim-websocket-client-python/`
- **包名**: `cyaim-websocket-client`
- **特性**:
  - 基于 `websockets` 和 `aiohttp`
  - 异步支持 (asyncio)
  - Python 3.8+
- **文档**: [README.md](cyaim-websocket-client-python/README.md)

## 核心功能

所有客户端都实现了以下核心功能：

1. **自动 Endpoint 发现**
   - 从服务器 `/ws_server/api/endpoints` 获取 endpoint 列表
   - 支持缓存和延迟加载

2. **接口契约式调用**
   - 定义接口/类型，直接调用方法
   - 自动匹配 endpoint 或使用装饰器/特性指定

3. **类型安全**
   - 强类型请求和响应
   - 编译时/运行时类型检查

4. **灵活的配置**
   - 支持延迟加载 endpoint
   - 可配置验证选项
   - 自定义错误处理

## 使用示例

### C#

```csharp
var factory = new WebSocketClientFactory("http://localhost:5000", "/ws");
var client = await factory.CreateClientAsync<IWeatherService>();
var forecasts = await client.GetForecastsAsync();
```

### TypeScript

```typescript
const factory = new WebSocketClientFactory('http://localhost:5000', '/ws');
const client = await factory.createClient<IWeatherService>({
  getForecasts: async () => {}
});
const forecasts = await client.getForecasts();
```

### Rust

```rust
let mut factory = WebSocketClientFactory::new(
    "http://localhost:5000".to_string(),
    "/ws".to_string(),
    None,
);
let client = factory.create_client();
let forecasts: Vec<WeatherForecast> = client
    .send_request("weatherforecast.get", None::<()>)
    .await?;
```

### Java

```java
WebSocketClientFactory factory = new WebSocketClientFactory(
    "http://localhost:5000", "/ws", new WebSocketClientOptions());
WebSocketClient client = factory.createClient();
client.connect().get();
List<WeatherForecast> forecasts = client.sendRequest(
    "weatherforecast.get", null, new TypeToken<List<WeatherForecast>>(){}.getType()
).get();
```

### Dart

```dart
final factory = WebSocketClientFactory('http://localhost:5000', '/ws');
final client = factory.createClient();
await client.connect();
final forecasts = await client.sendRequest<List<Map<String, dynamic>>>(
  'weatherforecast.get',
);
```

### Python

```python
factory = WebSocketClientFactory('http://localhost:5000', '/ws')
client = factory.create_client()
await client.connect()
forecasts = await client.send_request('weatherforecast.get')
```

## 服务端要求

所有客户端都需要服务端提供以下 API：

- **GET** `/ws_server/api/endpoints` - 返回所有可用的 WebSocket endpoint 列表

响应格式：

```json
{
  "success": true,
  "data": [
    {
      "controller": "WeatherForecast",
      "action": "Get",
      "methodPath": "weatherforecast.get",
      "methods": ["GET"],
      "fullName": "WeatherForecast.Get",
      "target": "weatherforecast.get"
    }
  ],
  "error": null
}
```

## 许可证

MIT License

Copyright © Cyaim Studio

