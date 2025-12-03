# 快速开始

5 分钟快速上手 Cyaim.WebSocketServer。

## 安装

```bash
dotnet add package Cyaim.WebSocketServer
```

## 最小示例

### 1. 创建项目

```bash
dotnet new web -n WebSocketServerApp
cd WebSocketServerApp
```

### 2. 安装包

```bash
dotnet add package Cyaim.WebSocketServer
```

### 3. 配置 Program.cs

```csharp
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Infrastructure.Configures;

var builder = WebApplication.CreateBuilder(args);

// 配置 WebSocket 路由
builder.Services.ConfigureWebSocketRoute(x =>
{
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", new MvcChannelHandler().ConnectionEntry }
    };
    x.ApplicationServiceCollection = builder.Services;
});

builder.Services.AddControllers();

var app = builder.Build();

app.UseWebSockets();
app.UseWebSocketServer();
app.MapControllers();

app.Run();
```

### 4. 创建控制器

```csharp
using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("[controller]")]
public class EchoController : ControllerBase
{
    [WebSocket]
    [HttpGet]
    public string Echo(string message)
    {
        return $"Echo: {message}";
    }
}
```

### 5. 运行

```bash
dotnet run
```

### 6. 测试

使用 WebSocket 客户端连接到 `ws://localhost:5000/ws`，发送：

```json
{
    "target": "Echo.Echo",
    "body": {
        "message": "Hello, WebSocket!"
    }
}
```

### 7. 向客户端发送数据

服务器可以向客户端主动发送数据：

```csharp
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("[controller]")]
public class MessageController : ControllerBase
{
    [WebSocket]
    [HttpGet]
    public string SendMessage()
    {
        // 获取当前连接 ID
        var connectionId = HttpContext.Connection.Id;
        
        // 发送文本消息（使用扩展方法）
        _ = "Hello from server!".SendTextAsync(connectionId);
        
        return "Message sent";
    }
    
    [WebSocket]
    [HttpGet]
    public string SendNotification()
    {
        var connectionId = HttpContext.Connection.Id;
        
        // 发送 JSON 对象
        var notification = new
        {
            Type = "Notification",
            Message = "You have a new message",
            Timestamp = DateTime.UtcNow
        };
        
        _ = notification.SendJsonAsync(connectionId);
        
        return "Notification sent";
    }
}
```

**提示**: 更多发送数据的方法和示例，请查看 [向客户端发送数据](./CORE.md#向客户端发送数据) 章节。

## 下一步

- 阅读 [核心库文档](./CORE.md) 了解详细功能
- 查看 [向客户端发送数据](./CORE.md#向客户端发送数据) 了解如何发送消息
- 查看 [配置指南](./CONFIGURATION.md) 了解配置选项
- 参考示例项目了解实际应用

