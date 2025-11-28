# 客户端 SDK

Cyaim.WebSocketServer 提供了多语言客户端 SDK，支持自动 endpoint 发现和接口契约式调用。

## 支持的语言

### ✅ C# (.NET)

- **包名**: `Cyaim.WebSocketServer.Client`
- **目标框架**: .NET 8.0, 9.0, 10.0
- **特性**:
  - 使用 `DispatchProxy` 实现动态代理
  - 支持 `[WebSocketEndpoint]` 特性
  - 支持延迟加载和自定义选项
- **位置**: `Clients/Cyaim.WebSocketServer.Client/`
- **文档**: [README.md](../../Clients/Cyaim.WebSocketServer.Client/README.md)

### ✅ TypeScript/JavaScript

- **包名**: `@cyaim/websocket-client`
- **特性**:
  - 完整的 TypeScript 支持
  - 装饰器支持指定 endpoint
  - 基于 `ws` 库
- **位置**: `Clients/cyaim-websocket-client-js/`
- **文档**: [README.md](../../Clients/cyaim-websocket-client-js/README.md)

### ✅ Rust

- **包名**: `cyaim-websocket-client`
- **特性**:
  - 基于 `tokio-tungstenite`
  - 异步支持
  - 类型安全
- **位置**: `Clients/cyaim-websocket-client-rs/`
- **文档**: [README.md](../../Clients/cyaim-websocket-client-rs/README.md)

### ✅ Java

- **包名**: `com.cyaim:websocket-client`
- **特性**:
  - Maven 支持
  - 基于 `Java-WebSocket`
  - 支持 Java 11+
- **位置**: `Clients/cyaim-websocket-client-java/`
- **文档**: [README.md](../../Clients/cyaim-websocket-client-java/README.md)

### ✅ Dart

- **包名**: `cyaim_websocket_client`
- **特性**:
  - Flutter/Dart 支持
  - 基于 `web_socket_channel`
  - 异步支持
- **位置**: `Clients/cyaim-websocket-client-dart/`
- **文档**: [README.md](../../Clients/cyaim-websocket-client-dart/README.md)

### ✅ Python

- **包名**: `cyaim-websocket-client`
- **特性**:
  - 基于 `websockets` 和 `aiohttp`
  - 异步支持 (asyncio)
  - Python 3.8+
- **位置**: `Clients/cyaim-websocket-client-python/`
- **文档**: [README.md](../../Clients/cyaim-websocket-client-python/README.md)

## 核心功能

所有客户端 SDK 都实现了以下核心功能：

### 1. 自动 Endpoint 发现

客户端自动从服务器的 `/ws_server/api/endpoints` API 获取可用的 endpoint：

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
  ]
}
```

### 2. 接口契约式调用

定义接口/类型，直接调用方法，无需手动构造请求：

**C# 示例:**
```csharp
public interface IWeatherService
{
    Task<WeatherForecast[]> GetForecastsAsync();
    Task<WeatherForecast> GetForecastAsync(string city);
}

var factory = new WebSocketClientFactory("http://localhost:5000", "/ws");
var client = await factory.CreateClientAsync<IWeatherService>();
var forecasts = await client.GetForecastsAsync();
```

**TypeScript 示例:**
```typescript
interface IWeatherService {
  getForecasts(): Promise<WeatherForecast[]>;
  getForecast(city: string): Promise<WeatherForecast>;
}

const factory = new WebSocketClientFactory('http://localhost:5000', '/ws');
const client = await factory.createClient<IWeatherService>({
  getForecasts: async () => {},
  getForecast: async (city: string) => {}
});
const forecasts = await client.getForecasts();
```

### 3. 类型安全

所有客户端都提供强类型的请求和响应：

- 编译时类型检查（如果支持）
- 运行时类型验证
- 自动序列化/反序列化

### 4. 灵活的配置

所有客户端都支持灵活的配置选项：

- **延迟加载**: 按需加载 endpoint，而不是预先加载
- **验证**: 可选验证所有方法都有对应的 endpoint
- **错误处理**: 可配置的错误处理行为

## 快速开始示例

### C#

```csharp
using Cyaim.WebSocketServer.Client;

var factory = new WebSocketClientFactory("http://localhost:5000", "/ws");
var client = await factory.CreateClientAsync<IWeatherService>();
var forecasts = await client.GetForecastsAsync();
```

### TypeScript

```typescript
import { WebSocketClientFactory } from '@cyaim/websocket-client';

const factory = new WebSocketClientFactory('http://localhost:5000', '/ws');
const client = await factory.createClient<IWeatherService>({
  getForecasts: async () => {}
});
const forecasts = await client.getForecasts();
```

### Rust

```rust
use cyaim_websocket_client::WebSocketClientFactory;

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
import com.cyaim.websocket.*;

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
import 'package:cyaim_websocket_client/cyaim_websocket_client.dart';

final factory = WebSocketClientFactory('http://localhost:5000', '/ws');
final client = factory.createClient();
await client.connect();
final forecasts = await client.sendRequest<List<Map<String, dynamic>>>(
  'weatherforecast.get',
);
```

### Python

```python
from cyaim_websocket_client import WebSocketClientFactory

factory = WebSocketClientFactory('http://localhost:5000', '/ws')
client = factory.create_client()
await client.connect()
forecasts = await client.send_request('weatherforecast.get')
```

## 服务端要求

所有客户端都需要服务端提供以下 API 端点：

- **GET** `/ws_server/api/endpoints` - 返回所有可用的 WebSocket endpoint 列表

此端点由 Dashboard 模块自动提供。确保在服务器中配置了 Dashboard：

```csharp
builder.Services.AddWebSocketDashboard();
app.UseWebSocketDashboard("/dashboard");
```

## 高级用法

### 自定义 Endpoint 映射

如果方法名与服务端 endpoint 不匹配，可以指定自定义映射：

**C#:**
```csharp
public interface IUserService
{
    [WebSocketEndpoint("user.getbyid")]
    Task<User> GetUserByIdAsync(int id);
}
```

**TypeScript:**
```typescript
import { endpoint } from '@cyaim/websocket-client';

const client = await factory.createClient<IUserService>({
  [endpoint('user.getbyid')]: async (id: number) => {}
});
```

### 延迟加载

仅在需要时加载 endpoint：

**C#:**
```csharp
var options = new WebSocketClientOptions
{
    LazyLoadEndpoints = true
};
var factory = new WebSocketClientFactory("http://localhost:5000", "/ws", options);
```

**TypeScript:**
```typescript
const options = new WebSocketClientOptions();
options.lazyLoadEndpoints = true;
const factory = new WebSocketClientFactory('http://localhost:5000', '/ws', options);
```

## 最佳实践

1. **定义最小接口**: 只定义需要的方法，而不是所有可用的 endpoint
2. **使用类型安全**: 利用语言的类型系统实现编译时安全
3. **处理错误**: 始终处理 WebSocket 操作的潜在错误
4. **连接管理**: 让客户端自动管理连接
5. **Endpoint 缓存**: 客户端默认缓存 endpoint 以提高性能

## 故障排除

### Endpoint 未找到

如果遇到 "endpoint not found" 错误：

1. 检查服务器是否正在运行且可访问
2. 验证 Dashboard API 已启用 (`/ws_server/api/endpoints`)
3. 使用 `[WebSocketEndpoint]` 特性或装饰器指定确切的 endpoint 目标
4. 检查方法名是否匹配服务器 action 名称（不区分大小写）

### 连接问题

如果遇到连接问题：

1. 验证服务器 URL 和通道路径是否正确
2. 检查防火墙和网络设置
3. 确保服务器上启用了 WebSocket
4. 检查服务器日志中的错误

## 相关文档

- [核心库文档](./CORE.md) - 服务端实现
- [Dashboard](./DASHBOARD.md) - Dashboard 和 API 端点
- [快速开始](./QUICK_START.md) - 入门指南

## 许可证

所有客户端 SDK 均采用 MIT 许可证。

Copyright © Cyaim Studio

