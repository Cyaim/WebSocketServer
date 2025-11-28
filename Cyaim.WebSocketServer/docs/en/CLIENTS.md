# Client SDKs

Cyaim.WebSocketServer provides multi-language client SDKs that support automatic endpoint discovery and interface contract-based calling.

## Supported Languages

### ✅ C# (.NET)

- **Package**: `Cyaim.WebSocketServer.Client`
- **Target Frameworks**: .NET 8.0, 9.0, 10.0
- **Features**:
  - Dynamic proxy using `DispatchProxy`
  - `[WebSocketEndpoint]` attribute support
  - Lazy loading and custom options
- **Location**: `Clients/Cyaim.WebSocketServer.Client/`
- **Documentation**: [README.md](../../Clients/Cyaim.WebSocketServer.Client/README.md)

### ✅ TypeScript/JavaScript

- **Package**: `@cyaim/websocket-client`
- **Features**:
  - Full TypeScript support
  - Decorator support for endpoint specification
  - Based on `ws` library
- **Location**: `Clients/cyaim-websocket-client-js/`
- **Documentation**: [README.md](../../Clients/cyaim-websocket-client-js/README.md)

### ✅ Rust

- **Package**: `cyaim-websocket-client`
- **Features**:
  - Based on `tokio-tungstenite`
  - Async support
  - Type safety
- **Location**: `Clients/cyaim-websocket-client-rs/`
- **Documentation**: [README.md](../../Clients/cyaim-websocket-client-rs/README.md)

### ✅ Java

- **Package**: `com.cyaim:websocket-client`
- **Features**:
  - Maven support
  - Based on `Java-WebSocket`
  - Java 11+ support
- **Location**: `Clients/cyaim-websocket-client-java/`
- **Documentation**: [README.md](../../Clients/cyaim-websocket-client-java/README.md)

### ✅ Dart

- **Package**: `cyaim_websocket_client`
- **Features**:
  - Flutter/Dart support
  - Based on `web_socket_channel`
  - Async support
- **Location**: `Clients/cyaim-websocket-client-dart/`
- **Documentation**: [README.md](../../Clients/cyaim-websocket-client-dart/README.md)

### ✅ Python

- **Package**: `cyaim-websocket-client`
- **Features**:
  - Based on `websockets` and `aiohttp`
  - Async support (asyncio)
  - Python 3.8+
- **Location**: `Clients/cyaim-websocket-client-python/`
- **Documentation**: [README.md](../../Clients/cyaim-websocket-client-python/README.md)

## Core Features

All client SDKs implement the following core features:

### 1. Automatic Endpoint Discovery

Clients automatically fetch available endpoints from the server's `/ws_server/api/endpoints` API:

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

### 2. Interface Contract-Based Calling

Define interfaces/types and call methods directly, without manually constructing requests:

**C# Example:**
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

**TypeScript Example:**
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

### 3. Type Safety

All clients provide strong typing for requests and responses:

- Compile-time type checking (where supported)
- Runtime type validation
- Automatic serialization/deserialization

### 4. Flexible Configuration

All clients support flexible configuration options:

- **Lazy Loading**: Load endpoints on-demand instead of upfront
- **Validation**: Optional validation of all methods have corresponding endpoints
- **Error Handling**: Configurable error handling behavior

## Quick Start Examples

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

## Server Requirements

All clients require the server to provide the following API endpoint:

- **GET** `/ws_server/api/endpoints` - Returns all available WebSocket endpoints

This endpoint is automatically provided by the Dashboard module. Make sure the Dashboard is configured in your server:

```csharp
builder.Services.AddWebSocketDashboard();
app.UseWebSocketDashboard("/dashboard");
```

## Advanced Usage

### Custom Endpoint Mapping

If method names don't match server endpoints, you can specify custom mappings:

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

### Lazy Loading

Load endpoints only when needed:

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

## Best Practices

1. **Define Minimal Interfaces**: Only define the methods you need, not all available endpoints
2. **Use Type Safety**: Leverage your language's type system for compile-time safety
3. **Handle Errors**: Always handle potential errors from WebSocket operations
4. **Connection Management**: Let the client manage connections automatically
5. **Endpoint Caching**: Clients cache endpoints by default for better performance

## Troubleshooting

### Endpoint Not Found

If you get an "endpoint not found" error:

1. Check that the server is running and accessible
2. Verify the Dashboard API is enabled (`/ws_server/api/endpoints`)
3. Use `[WebSocketEndpoint]` attribute or decorator to specify the exact endpoint target
4. Check method name matches server action name (case-insensitive)

### Connection Issues

If you have connection problems:

1. Verify the server URL and channel path are correct
2. Check firewall and network settings
3. Ensure WebSocket is enabled on the server
4. Check server logs for errors

## Related Documentation

- [Core Library](./CORE.md) - Server-side implementation
- [Dashboard](./DASHBOARD.md) - Dashboard and API endpoints
- [Quick Start](./QUICK_START.md) - Getting started guide

## License

All client SDKs are licensed under MIT License.

Copyright © Cyaim Studio

