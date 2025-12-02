# Cyaim.WebSocketServer.Client

WebSocket 客户端生成器，支持自动从服务端获取 endpoint 列表并生成接口契约式客户端。

## 功能特性

- ✅ 自动从服务端获取 WebSocket endpoint 列表
- ✅ 接口契约式调用服务端 endpoint
- ✅ 类型安全的请求和响应
- ✅ 自动连接管理和错误处理
- ✅ 支持自定义契约接口，只需定义需要的方法
- ✅ 支持通过特性指定 endpoint 映射
- ✅ 支持延迟加载 endpoint，按需获取
- ✅ 灵活的验证选项，支持部分匹配

## 快速开始

### 1. 定义接口契约

根据实际需求定义接口，**只需定义需要的方法**，不需要一次性获取所有 endpoint：

```csharp
public interface IWeatherService
{
    Task<WeatherForecast[]> GetForecastsAsync();
    Task<WeatherForecast> GetForecastAsync(string city);
}
```

### 2. 创建客户端工厂

#### 基本用法

```csharp
using Cyaim.WebSocketServer.Client;

var factory = new WebSocketClientFactory("http://localhost:5000", "/ws");
var client = await factory.CreateClientAsync<IWeatherService>();
```

#### 使用自定义选项

```csharp
var options = new WebSocketClientOptions
{
    Protocol = SerializationProtocol.Json,  // 序列化协议：Json（默认）或 MessagePack
    ValidateAllMethods = false,             // 不验证所有方法（默认）
    LazyLoadEndpoints = true,                // 延迟加载 endpoint
    ThrowOnEndpointNotFound = true           // 找不到 endpoint 时抛出异常
};

var factory = new WebSocketClientFactory("http://localhost:5000", "/ws", options);
var client = await factory.CreateClientAsync<IWeatherService>();
```

#### 使用 MessagePack 协议

MessagePack 是二进制序列化格式，相比 JSON 具有更小的数据体积和更快的序列化速度，适合对性能要求较高的场景。

```csharp
var options = new WebSocketClientOptions
{
    Protocol = SerializationProtocol.MessagePack  // 使用 MessagePack 协议
};

var factory = new WebSocketClientFactory("http://localhost:5000", "/ws", options);
var client = await factory.CreateClientAsync<IWeatherService>();
var forecasts = await client.GetForecastsAsync();
```

**注意**：使用 MessagePack 时，服务端也需要配置 MessagePack 处理器。请参考服务端 MessagePack 扩展文档。

### 3. 调用服务端方法

```csharp
// 调用无参数方法
var forecasts = await client.GetForecastsAsync();

// 调用带参数方法
var forecast = await client.GetForecastAsync("Beijing");
```

### 4. 使用特性指定 endpoint（可选）

如果方法名与服务端 endpoint 不匹配，可以使用 `[WebSocketEndpoint]` 特性：

```csharp
public interface IWeatherService
{
    // 自动匹配：方法名 "GetForecasts" 会匹配服务端的 "GetForecasts" action
    Task<WeatherForecast[]> GetForecastsAsync();
    
    // 使用特性指定：明确指定 endpoint target
    [WebSocketEndpoint("weatherforecast.getbycity")]
    Task<WeatherForecast> GetForecastAsync(string city);
}
```

## API 说明

### WebSocketClientFactory

客户端工厂类，用于创建 WebSocket 客户端代理。

#### 构造函数

```csharp
public WebSocketClientFactory(
    string serverBaseUrl, 
    string channel = "/ws", 
    WebSocketClientOptions? options = null)
```

- `serverBaseUrl`: 服务器基础 URL（例如："http://localhost:5000"）
- `channel`: WebSocket 通道（默认："/ws"）
- `options`: 客户端创建选项（可选）

#### 方法

##### CreateClientAsync<T>()

创建指定接口的客户端代理。

```csharp
var client = await factory.CreateClientAsync<IWeatherService>();
```

##### GetEndpointsAsync()

从服务器获取所有 WebSocket endpoint 列表。

```csharp
var endpoints = await factory.GetEndpointsAsync();
```

### WebSocketClientOptions

客户端创建选项。

#### 属性

- `Protocol` (SerializationProtocol, 默认: Json): 序列化协议
  - `SerializationProtocol.Json`: JSON 协议（文本消息，默认）
  - `SerializationProtocol.MessagePack`: MessagePack 协议（二进制消息）
- `ValidateAllMethods` (bool, 默认: false): 是否验证所有方法都有对应的端点
- `LazyLoadEndpoints` (bool, 默认: false): 是否延迟加载端点（在调用方法时才获取）
- `ThrowOnEndpointNotFound` (bool, 默认: true): 如果找不到端点是否抛出异常

### WebSocketEndpointAttribute

用于指定方法的 WebSocket endpoint 目标。

```csharp
[WebSocketEndpoint("controller.action")]
Task<Result> MethodAsync();
```

- `Target`: 目标端点路径（例如："weatherforecast.get" 或 "controller.action"）

### WebSocketClient

底层 WebSocket 客户端，用于连接到服务器并发送请求。

#### 构造函数

```csharp
public WebSocketClient(string serverUri, string channel = "/ws")
```

- `serverUri`: 服务器 URI（例如："ws://localhost:5000/ws"）
- `channel`: WebSocket 通道（默认："/ws"）

#### 方法

##### ConnectAsync()

连接到服务器。

```csharp
await client.ConnectAsync();
```

##### SendRequestAsync<TRequest, TResponse>()

发送请求并等待响应。

```csharp
var response = await client.SendRequestAsync<RequestType, ResponseType>(
    "Controller.Action",
    requestBody
);
```

##### DisconnectAsync()

断开服务器连接。

```csharp
await client.DisconnectAsync();
```

## 服务端要求

服务端需要提供以下 API 端点来支持客户端自动发现：

### GET /ws_server/api/endpoints

返回所有可用的 WebSocket endpoint 列表。

响应格式：

```json
{
  "Success": true,
  "Data": [
    {
      "Controller": "WeatherForecast",
      "Action": "Get",
      "MethodPath": "weatherforecast.get",
      "Methods": ["GET"],
      "FullName": "WeatherForecast.Get",
      "Target": "weatherforecast.get"
    }
  ],
  "Error": null
}
```

## 完整示例

```csharp
using Cyaim.WebSocketServer.Client;
using System;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        // 创建客户端工厂
        var factory = new WebSocketClientFactory("http://localhost:5000", "/ws");
        
        try
        {
            // 创建客户端代理
            var weatherService = await factory.CreateClientAsync<IWeatherService>();
            
            // 调用服务端方法
            var forecasts = await weatherService.GetForecastsAsync();
            
            foreach (var forecast in forecasts)
            {
                Console.WriteLine($"{forecast.Date}: {forecast.TemperatureC}°C");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            factory.Dispose();
        }
    }
}

// 定义接口契约
public interface IWeatherService
{
    Task<WeatherForecast[]> GetForecastsAsync();
}

public class WeatherForecast
{
    public DateTime Date { get; set; }
    public int TemperatureC { get; set; }
    public string Summary { get; set; } = string.Empty;
}
```

## 使用场景

### 场景 1：只使用部分 endpoint

你只需要使用服务端的部分功能，可以只定义需要的接口方法：

```csharp
// 服务端可能有 100 个 endpoint，但你只需要 2 个
public interface IUserService
{
    Task<User> GetUserAsync(int userId);
    Task<bool> UpdateUserAsync(User user);
}

var factory = new WebSocketClientFactory("http://localhost:5000");
var client = await factory.CreateClientAsync<IUserService>();
```

### 场景 2：自定义契约接口

你可以完全自定义接口，使用 `[WebSocketEndpoint]` 特性指定映射：

```csharp
public interface IMyCustomService
{
    [WebSocketEndpoint("user.getbyid")]
    Task<User> GetUserByIdAsync(int id);
    
    [WebSocketEndpoint("user.update")]
    Task<bool> UpdateUserAsync(User user);
}
```

### 场景 3：延迟加载 endpoint

如果不想在创建客户端时立即获取所有 endpoint，可以使用延迟加载：

```csharp
var options = new WebSocketClientOptions
{
    LazyLoadEndpoints = true  // 在调用方法时才获取 endpoint
};

var factory = new WebSocketClientFactory("http://localhost:5000", "/ws", options);
var client = await factory.CreateClientAsync<IUserService>();

// 第一次调用时会自动获取并匹配 endpoint
var user = await client.GetUserAsync(1);
```

## 注意事项

1. **接口方法命名**：接口方法名会自动匹配服务端的 Action 名称（不区分大小写），如果不匹配可以使用 `[WebSocketEndpoint]` 特性
2. **参数映射**：方法参数会自动映射到请求的 Body 中
3. **返回类型**：方法必须返回 `Task` 或 `Task<T>`
4. **连接管理**：客户端会自动管理 WebSocket 连接，无需手动连接
5. **错误处理**：如果服务端返回错误（Status != 0），会抛出异常
6. **部分匹配**：默认情况下，不会验证所有方法都有对应的 endpoint，只在使用时才会检查
7. **自定义契约**：你可以完全自定义接口，只定义需要的方法，不需要一次性获取所有 endpoint

## 许可证

Copyright © Cyaim Studio

