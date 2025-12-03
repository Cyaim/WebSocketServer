# 核心库文档

本文档详细介绍 Cyaim.WebSocketServer 核心库的功能和使用方法。

## 目录

- [概述](#概述)
- [快速开始](#快速开始)
- [路由系统](#路由系统)
- [处理器](#处理器)
- [中间件](#中间件)
- [配置选项](#配置选项)
- [请求和响应](#请求和响应)
- [向客户端发送数据](#向客户端发送数据)
- [高级功能](#高级功能)

## 概述

Cyaim.WebSocketServer 是一个轻量级、高性能的 WebSocket 服务端库，提供了类似 ASP.NET Core MVC 的路由机制，支持全双工通信、多路复用和管道处理。

### 核心特性

- ✅ **轻量级高性能** - 基于 ASP.NET Core，性能优异
- ✅ **路由系统** - 类似 MVC 的路由机制，支持 RESTful API
- ✅ **全双工通信** - 支持客户端和服务器双向通信
- ✅ **多路复用** - 单个连接支持多个请求/响应
- ✅ **管道处理** - 支持中间件管道模式
- ✅ **类型安全** - 强类型参数绑定和响应

## 快速开始

### 1. 安装 NuGet 包

```bash
dotnet add package Cyaim.WebSocketServer
```

### 2. 配置服务

```csharp
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Infrastructure.Configures;

var builder = WebApplication.CreateBuilder(args);

// 配置 WebSocket 路由
builder.Services.ConfigureWebSocketRoute(x =>
{
    // 定义通道
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", new MvcChannelHandler(4 * 1024).ConnectionEntry }
    };
    x.ApplicationServiceCollection = builder.Services;
});
```

### 3. 配置中间件

```csharp
var app = builder.Build();

// 配置 WebSocket 选项
var webSocketOptions = new WebSocketOptions()
{
    KeepAliveInterval = TimeSpan.FromSeconds(120)
};

app.UseWebSockets(webSocketOptions);
app.UseWebSocketServer();
```

### 4. 创建控制器

```csharp
using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("[controller]")]
public class WeatherForecastController : ControllerBase
{
    [WebSocket]
    [HttpGet]
    public IEnumerable<WeatherForecast> Get()
    {
        return new[]
        {
            new WeatherForecast { Date = DateTime.Now, TemperatureC = 25 }
        };
    }
}
```

## 路由系统

### 通道配置

通道是 WebSocket 连接的入口点，每个通道对应一个处理器。

```csharp
x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
{
    { "/ws", mvcHandler.ConnectionEntry },      // MVC 处理器
    { "/api", customHandler.ConnectionEntry }    // 自定义处理器
};
```

### 端点标记

使用 `[WebSocket]` 特性标记 WebSocket 端点：

```csharp
[WebSocket]
[HttpGet]
public string GetMessage()
{
    return "Hello, WebSocket!";
}
```

**注意**: `[WebSocket]` 特性的 `method` 参数忽略大小写。

## 处理器

### MvcChannelHandler

`MvcChannelHandler` 是默认的 MVC 风格处理器，支持控制器和动作方法的路由。

#### 构造函数参数

```csharp
// 接收和发送缓冲区大小（默认 4KB）
var handler = new MvcChannelHandler(
    receiveBufferSize: 4 * 1024,
    sendBufferSize: 4 * 1024
);
```

#### 配置选项

```csharp
var handler = new MvcChannelHandler();

// 接收缓冲区大小
handler.ReceiveTextBufferSize = 8 * 1024;
handler.ReceiveBinaryBufferSize = 8 * 1024;

// 发送缓冲区大小
handler.SendTextBufferSize = 8 * 1024;
handler.SendBinaryBufferSize = 8 * 1024;

// 响应发送超时
handler.ResponseSendTimeout = TimeSpan.FromSeconds(30);
```

### 自定义处理器

实现 `IWebSocketHandler` 接口创建自定义处理器：

```csharp
public class CustomHandler : IWebSocketHandler
{
    public WebSocketHandlerMetadata Metadata { get; } = new WebSocketHandlerMetadata
    {
        Describe = "Custom handler",
        CanHandleBinary = true,
        CanHandleText = true
    };

    public async Task ConnectionEntry(HttpContext context, WebSocket webSocket)
    {
        // 处理连接逻辑
    }
}
```

## 中间件

### WebSocketRouteMiddleware

`WebSocketRouteMiddleware` 是核心中间件，负责：

- 路由 WebSocket 请求到对应的通道
- 管理 WebSocket 连接生命周期
- 处理连接升级

### 使用方式

```csharp
app.UseWebSockets(webSocketOptions);
app.UseWebSocketServer();
```

## 配置选项

### WebSocketRouteOption

```csharp
builder.Services.ConfigureWebSocketRoute(x =>
{
    // 通道配置
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>();
    
    // 服务集合（用于依赖注入）
    x.ApplicationServiceCollection = builder.Services;
    
    // 带宽限制策略（可选）
    x.BandwidthLimitPolicy = new BandwidthLimitPolicy
    {
        Enabled = true,
        // ... 配置
    };
    
    // 集群配置（可选）
    x.EnableCluster = false;
});
```

### WebSocketOptions

```csharp
var webSocketOptions = new WebSocketOptions()
{
    // Keep-Alive 间隔
    KeepAliveInterval = TimeSpan.FromSeconds(120),
    
    // 接收缓冲区大小（已弃用，在处理器中配置）
    // ReceiveBufferSize = 4 * 1024
};
```

## 请求和响应

### 请求格式

请求使用 JSON 格式，遵循 `MvcRequestScheme` 结构：

```json
{
    "target": "ControllerName.ActionName",
    "body": {
        "param1": "value1",
        "param2": 123
    }
}
```

**注意**: `target` 参数忽略大小写。

#### 无参数请求

```json
{
    "target": "WeatherForecast.Get",
    "body": {}
}
```

#### 带参数请求

```json
{
    "target": "WeatherForecast.Get",
    "body": {
        "city": "Beijing",
        "date": "2024-01-01"
    }
}
```

### 响应格式

响应使用 JSON 格式，遵循 `MvcResponseScheme` 结构：

```json
{
    "Target": "WeatherForecast.Get",
    "Status": 0,
    "Msg": null,
    "RequestTime": 637395762382112345,
    "CompleteTime": 637395762382134526,
    "Body": {
        // 方法返回值
    }
}
```

#### 响应字段说明

- `Target`: 请求的目标方法
- `Status`: 状态码（0 表示成功）
- `Msg`: 错误消息（如果有）
- `RequestTime`: 请求时间（Ticks）
- `CompleteTime`: 完成时间（Ticks）
- `Body`: 方法返回值

### 消息类型

支持两种消息类型：

- **Text**: 文本消息（JSON 格式）
- **Binary**: 二进制消息

## 向客户端发送数据

`WebSocketManager` 提供了统一的发送方法，自动适配单机和集群模式，支持多种数据类型和发送方式。

### 概述

`WebSocketManager` 的核心特性：

- ✅ **自动适配** - 自动检测单机或集群模式，无需手动判断
- ✅ **统一接口** - 提供一致的 API，无论单机还是集群
- ✅ **多种数据类型** - 支持文本、JSON 对象、字节数组、流等
- ✅ **批量发送** - 支持同时向多个连接发送数据
- ✅ **扩展方法** - 提供便捷的扩展方法，代码更简洁
- ✅ **流支持** - 支持大文件、网络流等，自动分块传输

### 获取连接 ID

在发送数据前，需要获取客户端的连接 ID。连接 ID 通常从 `HttpContext` 中获取：

```csharp
// 在控制器或处理器中
public class MyController : ControllerBase
{
    [WebSocket]
    [HttpGet]
    public string GetMessage()
    {
        // 获取当前连接的 ID
        var connectionId = HttpContext.Connection.Id;
        
        // 发送消息
        await WebSocketManager.SendAsync(connectionId, "Hello from server!");
        
        return "Message sent";
    }
}
```

### 发送文本消息

#### 单个连接

```csharp
using Cyaim.WebSocketServer.Infrastructure;

// 方式1：使用静态方法
await WebSocketManager.SendAsync("connectionId", "Hello World!");

// 方式2：使用扩展方法（推荐，更简洁）
await "Hello World!".SendTextAsync("connectionId");

// 指定编码
await "你好世界".SendTextAsync("connectionId", Encoding.UTF8);
```

#### 批量发送

```csharp
var connectionIds = new[] { "conn1", "conn2", "conn3" };

// 方式1：使用静态方法
var results = await WebSocketManager.SendAsync(connectionIds, "Hello from server!");

// 方式2：使用扩展方法
var results = await "Hello from server!".SendTextAsync(connectionIds);

// 检查发送结果
foreach (var result in results)
{
    if (result.Value)
    {
        Console.WriteLine($"成功发送到 {result.Key}");
    }
    else
    {
        Console.WriteLine($"发送到 {result.Key} 失败");
    }
}
```

### 发送 JSON 对象

#### 单个连接

```csharp
// 定义数据模型
var user = new
{
    Id = 1,
    Name = "Alice",
    Email = "alice@example.com"
};

// 方式1：使用静态方法
await WebSocketManager.SendAsync("connectionId", user);

// 方式2：使用扩展方法（推荐）
await user.SendJsonAsync("connectionId");

// 自定义 JSON 序列化选项
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};
await user.SendJsonAsync("connectionId", options);
```

#### 批量发送

```csharp
var notification = new
{
    Type = "SystemNotification",
    Message = "系统维护将在 10 分钟后开始",
    Timestamp = DateTime.UtcNow
};

var connectionIds = new[] { "conn1", "conn2", "conn3" };
var results = await notification.SendJsonAsync(connectionIds);
```

### 发送字节数组

#### 单个连接

```csharp
// 准备数据
var data = Encoding.UTF8.GetBytes("Binary data");
var imageData = File.ReadAllBytes("image.png");

// 方式1：使用静态方法
await WebSocketManager.SendAsync("connectionId", data, WebSocketMessageType.Binary);

// 方式2：使用扩展方法
await data.SendAsync("connectionId", WebSocketMessageType.Binary);
await imageData.SendAsync("connectionId", WebSocketMessageType.Binary);
```

#### 批量发送

```csharp
var binaryData = Encoding.UTF8.GetBytes("Binary message");
var connectionIds = new[] { "conn1", "conn2", "conn3" };

var results = await binaryData.SendAsync(connectionIds, WebSocketMessageType.Binary);
```

### 发送流数据（大文件、网络流等）

流发送功能特别适合传输大文件、网络流等大数据量场景，系统会自动分块传输，避免内存溢出。

#### 单个连接

```csharp
// 发送文件流
using var fileStream = File.OpenRead("largefile.zip");
await fileStream.SendAsync("connectionId");

// 发送网络流
using var networkStream = new NetworkStream(socket);
await networkStream.SendAsync("connectionId", WebSocketMessageType.Binary);

// 发送内存流
using var memoryStream = new MemoryStream(data);
await memoryStream.SendAsync("connectionId");

// 自定义块大小（默认 64KB）
await fileStream.SendAsync("connectionId", chunkSize: 128 * 1024);
```

#### 批量发送

```csharp
using var fileStream = File.OpenRead("document.pdf");
var connectionIds = new[] { "conn1", "conn2", "conn3" };

// 批量发送流（系统会自动缓冲并发送到所有连接）
var results = await fileStream.SendAsync(connectionIds);
```

#### 流发送特性

- **自动分块**: 大流自动分块传输（默认 64KB），避免内存溢出
- **自动重组**: 接收端自动重组分块数据，确保数据完整性
- **进度跟踪**: 可选的总大小参数用于进度跟踪
- **内存高效**: 不会一次性加载整个流到内存

### 单机模式 vs 集群模式

`WebSocketManager` 会自动检测运行模式：

#### 单机模式

当未启用集群时，消息直接发送到本地 WebSocket 连接：

```csharp
// 自动使用本地 WebSocket 发送
await "Hello".SendTextAsync("connectionId");
```

#### 集群模式

当启用集群时，消息会自动路由到正确的节点：

```csharp
// 自动路由到正确的节点（本地或远程）
await "Hello".SendTextAsync("connectionId");

// 支持跨节点批量发送
var connectionIds = new[] { "node1-conn1", "node2-conn1", "node3-conn1" };
await "Hello from cluster".SendTextAsync(connectionIds);
```

**注意**: 无需手动判断是单机还是集群模式，`WebSocketManager` 会自动处理。

### 实际使用场景

#### 场景1：实时通知

```csharp
public class NotificationService
{
    public async Task NotifyUserAsync(string userId, string message)
    {
        // 假设 userId 对应 connectionId
        var notification = new
        {
            Type = "Notification",
            Message = message,
            Timestamp = DateTime.UtcNow
        };
        
        await notification.SendJsonAsync(userId);
    }
    
    public async Task BroadcastAsync(string message)
    {
        // 获取所有连接 ID
        var connectionIds = MvcChannelHandler.Clients.Keys;
        
        var notification = new
        {
            Type = "Broadcast",
            Message = message,
            Timestamp = DateTime.UtcNow
        };
        
        await notification.SendJsonAsync(connectionIds);
    }
}
```

#### 场景2：文件传输

```csharp
public class FileService
{
    public async Task SendFileAsync(string connectionId, string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException();
        }
        
        using var fileStream = File.OpenRead(filePath);
        var success = await fileStream.SendAsync(connectionId);
        
        if (success)
        {
            Console.WriteLine($"文件 {filePath} 发送成功");
        }
    }
    
    public async Task SendFileToMultipleAsync(IEnumerable<string> connectionIds, string filePath)
    {
        using var fileStream = File.OpenRead(filePath);
        var results = await fileStream.SendAsync(connectionIds);
        
        var successCount = results.Count(r => r.Value);
        Console.WriteLine($"成功发送到 {successCount}/{results.Count} 个连接");
    }
}
```

#### 场景3：实时数据推送

```csharp
public class DataPushService
{
    public async Task PushDataAsync(string connectionId, object data)
    {
        // 使用扩展方法发送 JSON
        await data.SendJsonAsync(connectionId);
    }
    
    public async Task PushToGroupAsync(IEnumerable<string> connectionIds, object data)
    {
        // 批量发送到组内所有连接
        await data.SendJsonAsync(connectionIds);
    }
}
```

### 错误处理

发送方法返回 `bool` 或 `Dictionary<string, bool>`，可以检查发送是否成功：

```csharp
// 单个连接
var success = await "Hello".SendTextAsync("connectionId");
if (!success)
{
    Console.WriteLine("发送失败，连接可能已断开");
}

// 批量发送
var results = await "Hello".SendTextAsync(connectionIds);
var failedConnections = results.Where(r => !r.Value).Select(r => r.Key);
if (failedConnections.Any())
{
    Console.WriteLine($"以下连接发送失败: {string.Join(", ", failedConnections)}");
}
```

### 性能建议

1. **批量发送**: 需要向多个连接发送相同数据时，使用批量方法更高效
2. **流传输**: 大文件使用流发送，避免加载到内存
3. **合理分块**: 流传输时根据网络情况调整块大小
4. **异步处理**: 所有发送方法都是异步的，避免阻塞

### 方法对比

| 方法 | 适用场景 | 返回值 |
|------|---------|--------|
| `SendAsync(string, ...)` | 单个连接 | `Task<bool>` |
| `SendAsync(IEnumerable<string>, ...)` | 多个连接 | `Task<Dictionary<string, bool>>` |
| 扩展方法 | 代码更简洁 | 同上 |

### 相关 API

详细 API 参考请查看 [API 参考文档](./API_REFERENCE.md#websocketmanager)。

集群模式下的路由功能请查看 [集群模块文档](./CLUSTER.md#消息路由)。

## 高级功能

### 请求管道

`MvcChannelHandler` 支持请求管道，可以在请求处理的不同阶段插入自定义逻辑：

```csharp
var handler = new MvcChannelHandler();

// 在请求解析前执行
handler.RequestPipeline.TryAdd(
    RequestPipelineStage.BeforeParse,
    new ConcurrentQueue<PipelineItem>()
);

// 添加管道项
handler.RequestPipeline[RequestPipelineStage.BeforeParse].Enqueue(
    new PipelineItem
    {
        Name = "CustomMiddleware",
        Handler = async (context, next) =>
        {
            // 自定义逻辑
            await next();
        }
    }
);
```

### 管道阶段

- `BeforeParse`: 解析请求前
- `AfterParse`: 解析请求后
- `BeforeInvoke`: 调用方法前
- `AfterInvoke`: 调用方法后
- `BeforeSend`: 发送响应前
- `AfterSend`: 发送响应后

### 依赖注入

控制器支持完整的依赖注入：

```csharp
public class WeatherForecastController : ControllerBase
{
    private readonly ILogger<WeatherForecastController> _logger;
    private readonly IWeatherService _weatherService;

    public WeatherForecastController(
        ILogger<WeatherForecastController> logger,
        IWeatherService weatherService)
    {
        _logger = logger;
        _weatherService = weatherService;
    }

    [WebSocket]
    [HttpGet]
    public async Task<WeatherForecast> Get(string city)
    {
        return await _weatherService.GetWeatherAsync(city);
    }
}
```

### 参数绑定

支持多种参数绑定方式：

#### 简单类型

```csharp
[WebSocket]
public string GetMessage(string message)
{
    return $"Received: {message}";
}
```

请求：
```json
{
    "target": "Controller.GetMessage",
    "body": {
        "message": "Hello"
    }
}
```

#### 复杂类型

```csharp
public class RequestModel
{
    public string Name { get; set; }
    public int Age { get; set; }
}

[WebSocket]
public string Process(RequestModel model)
{
    return $"Name: {model.Name}, Age: {model.Age}";
}
```

请求：
```json
{
    "target": "Controller.Process",
    "body": {
        "name": "John",
        "age": 30
    }
}
```

#### 数组和集合

```csharp
[WebSocket]
public string ProcessList(List<string> items)
{
    return string.Join(", ", items);
}
```

请求：
```json
{
    "target": "Controller.ProcessList",
    "body": {
        "items": ["item1", "item2", "item3"]
    }
}
```

### 异步方法

完全支持异步方法：

```csharp
[WebSocket]
public async Task<string> GetAsync(string id)
{
    var data = await _repository.GetAsync(id);
    return data;
}
```

### 错误处理

#### 异常处理

方法抛出的异常会被捕获并返回错误响应：

```json
{
    "Target": "Controller.Action",
    "Status": 1,
    "Msg": "Exception message",
    "RequestTime": 123456789,
    "CompleteTime": 123456790,
    "Body": null
}
```

#### 自定义错误处理

可以在管道中自定义错误处理：

```csharp
handler.RequestPipeline[RequestPipelineStage.AfterInvoke].Enqueue(
    new PipelineItem
    {
        Name = "ErrorHandler",
        Handler = async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                // 自定义错误处理
                context.Response.Status = 1;
                context.Response.Msg = ex.Message;
            }
        }
    }
);
```

## 性能优化

### 缓冲区大小

根据消息大小调整缓冲区：

```csharp
// 小消息（< 4KB）
var handler = new MvcChannelHandler(4 * 1024, 4 * 1024);

// 中等消息（4KB - 64KB）
var handler = new MvcChannelHandler(64 * 1024, 64 * 1024);

// 大消息（> 64KB）
var handler = new MvcChannelHandler(256 * 1024, 256 * 1024);
```

### 连接管理

使用静态字典管理连接：

```csharp
// 获取所有连接
var connections = MvcChannelHandler.Clients;

// 检查连接是否存在
if (MvcChannelHandler.Clients.TryGetValue(connectionId, out var webSocket))
{
    // 连接存在
}
```

### 并发处理

`MvcChannelHandler` 支持并发处理多个请求，每个连接独立处理。

## 最佳实践

1. **使用依赖注入** - 充分利用 ASP.NET Core 的依赖注入系统
2. **异步方法** - 对于 I/O 操作，使用异步方法提高性能
3. **错误处理** - 适当处理异常，返回有意义的错误消息
4. **参数验证** - 在方法中验证参数，确保数据有效性
5. **日志记录** - 使用日志记录重要操作和错误

## 示例代码

完整示例请参考：
- [Sample.Dashboard](../../Sample/Cyaim.WebSocketServer.Sample.Dashboard/)
- [Example.Wpf](../../Sample/Cyaim.WebSocketServer.Example.Wpf/)

## 相关文档

- [配置指南](./CONFIGURATION.md)
- [API 参考](./API_REFERENCE.md)
- [集群模块](./CLUSTER.md)
- [指标统计](./METRICS.md)

