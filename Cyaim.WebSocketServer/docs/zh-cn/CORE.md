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

