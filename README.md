# WebSocketServer
| 996ICU | Version | NuGet | Build | Code Size | License |
|--|--|--|--|--|--|
[![996.icu](https://img.shields.io/badge/link-996.icu-red.svg)](https://996.icu)|[![](https://img.shields.io/badge/.NET%20Standard-2.1-violet.svg)![](https://img.shields.io/badge/.NET%206+-black%20.svg)](https://www.nuget.org/packages/Cyaim.WebSocketServer)|[![](https://img.shields.io/nuget/v/Cyaim.WebSocketServer.svg)](https://www.nuget.org/packages/Cyaim.WebSocketServer)[![NuGet](https://img.shields.io/nuget/dt/Cyaim.WebSocketServer.svg)](https://www.nuget.org/packages/Cyaim.WebSocketServer)|[![](https://github.com/Cyaim/WebSocketServer/workflows/.NET%20Core/badge.svg)](https://github.com/Cyaim/WebSocketServer)|[![Code size](https://img.shields.io/github/languages/code-size/Cyaim/WebSocketServer?logo=github&logoColor=white)](https://github.com/Cyaim/WebSocketServer)|[![License](https://img.shields.io/github/license/Cyaim/WebSocketServer?logo=open-source-initiative&logoColor=green)](https://github.com/Cyaim/WebSocketServer/blob/master/LICENSE)[![LICENSE](https://img.shields.io/badge/license-Anti%20996-blue.svg)](https://github.com/996icu/996.ICU/blob/master/LICENSE)

> WebSocketServer is a lightweight and high-performance WebSocket library. Supports routing, full-duplex communication, clustering, and multi-language client SDKs.

## 📚 Documentation Center / 文档中心

- **[English Documentation](./docs/en/README.md)** - Complete English documentation
- **[中文文档](./docs/zh-cn/README.md)** - 完整的中文文档

## ✨ Features / 特性

- ✅ **Lightweight & High Performance** - Based on ASP.NET Core
- ✅ **Routing System** - MVC-like routing mechanism
- ✅ **Full Duplex Communication** - Bidirectional communication support
- ✅ **Multi-node Cluster** - Raft-based consensus protocol
- ✅ **Multi-language Clients** - C#, TypeScript, Rust, Java, Dart, Python
- ✅ **Automatic Endpoint Discovery** - Client SDKs auto-discover server endpoints
- ✅ **Dashboard** - Real-time monitoring and statistics

# QuickStart

## 1. Install Library / 安装库

```bash
# Package Manager
Install-Package Cyaim.WebSocketServer

# .NET CLI
dotnet add package Cyaim.WebSocketServer

# PackageReference
<PackageReference Include="Cyaim.WebSocketServer" Version="1.7.8" />
```

## 2. Configure WebSocket Server / 配置 WebSocket 服务器

### Using Minimal API / 使用 Minimal API

```csharp
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Middlewares;

var builder = WebApplication.CreateBuilder(args);

// Configure WebSocket route / 配置 WebSocket 路由
builder.Services.ConfigureWebSocketRoute(x =>
{
    var mvcHandler = new MvcChannelHandler();
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", mvcHandler.ConnectionEntry }
    };
    x.ApplicationServiceCollection = builder.Services;
});

var app = builder.Build();

// Configure WebSocket options / 配置 WebSocket 选项
var webSocketOptions = new WebSocketOptions()
{
    KeepAliveInterval = TimeSpan.FromSeconds(120),
};
app.UseWebSockets(webSocketOptions);
app.UseWebSocketServer();

app.Run();
```

### Using Startup.cs / 使用 Startup.cs

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.ConfigureWebSocketRoute(x =>
    {
        var mvcHandler = new MvcChannelHandler();
        x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
        {
            { "/ws", mvcHandler.ConnectionEntry }
        };
        x.ApplicationServiceCollection = services;
    });
}

public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    var webSocketOptions = new WebSocketOptions()
    {
        KeepAliveInterval = TimeSpan.FromSeconds(120),
    };
    app.UseWebSockets(webSocketOptions);
    app.UseWebSocketServer();
}
```

## 3. Mark WebSocket Endpoints / 标记 WebSocket 端点

Add `[WebSocket]` attribute to your controller actions:

```csharp
[ApiController]
[Route("[controller]")]
public class WeatherForecastController : ControllerBase
{
    [WebSocket]  // Mark as WebSocket endpoint / 标记为 WebSocket 端点
    [HttpGet]
    public IEnumerable<WeatherForecast> Get()
    {
        var rng = new Random();
        return Enumerable.Range(1, 5).Select(index => new WeatherForecast
        {
            Date = DateTime.Now.AddDays(index),
            TemperatureC = rng.Next(-20, 55),
            Summary = Summaries[rng.Next(Summaries.Length)]
        }).ToArray();
    }
}
```

> **Note**: The `target` parameter in requests is case-insensitive.  
> **注意**: 请求中的 `target` 参数不区分大小写。

## Request and Response

> Scheme namespace 👇  
> Request Cyaim.WebSocketServer.Infrastructure.Handlers.MvcRequestScheme  
> Response Cyaim.WebSocketServer.Infrastructure.Handlers.MvcResponseScheme  

> Request target ignore case

> Request scheme  
### 1. Nonparametric method request
```json
{
	"target": "WeatherForecast.Get",
	"body": {}
}
```
This request will be located at "WeatherForecastController" -> "Get" Method.  

> Response to this request  
```json
{
	"Target": "WeatherForecast.Get"
	"Status": 0,
	"Msg": null,
	"RequestTime": 637395762382112345,
	"CompleteTime": 637395762382134526,
	"Body": [{
		"Date": "2020-10-30T13:50:38.2133285+08:00",
		"TemperatureC": 43,
		"TemperatureF": 109,
		"Summary": "Scorching"
	}, {
		"Date": "2020-10-31T13:50:38.213337+08:00",
		"TemperatureC": 1,
		"TemperatureF": 33,
		"Summary": "Chilly"
	}]
}
```
Forward invoke method return content will write MvcResponseScheme.Body.  

### 2. Request with parameters  
Example Code:
1. Change method code to:
```C#
[WebSocket]
[HttpGet]
public IEnumerable<WeatherForecast> Get(Test a)
{
    var rng = new Random();
    return Enumerable.Range(1, 2).Select(index => new WeatherForecast
    {
         TemperatureC = a.PreTemperatureC + rng.Next(-20, 55),
         Summary = a.PreSummary + Summaries[rng.Next(Summaries.Length)]
    }).ToArray();
}
```

2. Define parameter class
```C#
public class Test
{
    public string PreSummary { get; set; }
    public int PreTemperatureC { get; set; }
}
```
 
> Request parameter  
```json
{
	"target": "WeatherForecast.Get",
	"body": {
	    "PreSummary":"Cyaim_",
	    "PreTemperatureC":233
	}
}
```
Request body will be deserialized and passed to the method parameter.

> Response to this request  
```json
{
	"Target": "WeatherForecast.Get",
	"Status": 0,
	"Msg": null,
	"RequestTime": 0,
	"CompleteTime": 637395922139434966,
	"Body": [{
		"Date": "0001-01-01T00:00:00",
		"TemperatureC": 282,
		"TemperatureF": 539,
		"Summary": "Cyaim_Warm"
	}, {
		"Date": "0001-01-01T00:00:00",
		"TemperatureC": 285,
		"TemperatureF": 544,
		"Summary": "Cyaim_Sweltering"
	}]
}
```

## Client SDKs / 客户端 SDK

We provide multi-language client SDKs with automatic endpoint discovery:

- **C#** - [Cyaim.WebSocketServer.Client](./Clients/Cyaim.WebSocketServer.Client/README.md)
- **TypeScript/JavaScript** - [@cyaim/websocket-client](./Clients/cyaim-websocket-client-js/README.md)
- **Rust** - [cyaim-websocket-client](./Clients/cyaim-websocket-client-rs/README.md)
- **Java** - [websocket-client](./Clients/cyaim-websocket-client-java/README.md)
- **Dart** - [cyaim_websocket_client](./Clients/cyaim-websocket-client-dart/README.md)
- **Python** - [cyaim-websocket-client](./Clients/cyaim-websocket-client-python/README.md)

### Quick Example / 快速示例

**C# Client:**
```csharp
using Cyaim.WebSocketServer.Client;

var factory = new WebSocketClientFactory("http://localhost:5000", "/ws");
var client = await factory.CreateClientAsync<IWeatherService>();
var forecasts = await client.GetForecastsAsync();
```

**TypeScript Client:**
```typescript
import { WebSocketClientFactory } from '@cyaim/websocket-client';

const factory = new WebSocketClientFactory('http://localhost:5000', '/ws');
const client = await factory.createClient<IWeatherService>({
  getForecasts: async () => {}
});
const forecasts = await client.getForecasts();
```

> **For more details, see**: [Clients Documentation](./docs/zh-cn/CLIENTS.md) | [客户端文档](./docs/zh-cn/CLIENTS.md)





## Cluster / 集群

Cyaim.WebSocketServer supports multi-node clustering with Raft consensus protocol. You can use WebSocket, Redis, or RabbitMQ for inter-node communication.

### Basic Cluster Setup / 基础集群配置

```csharp
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;

var builder = WebApplication.CreateBuilder(args);

// Configure WebSocket route / 配置 WebSocket 路由
builder.Services.ConfigureWebSocketRoute(x =>
{
    var mvcHandler = new MvcChannelHandler();
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", mvcHandler.ConnectionEntry }
    };
    x.ApplicationServiceCollection = builder.Services;
});

var app = builder.Build();

// Configure WebSocket / 配置 WebSocket
app.UseWebSockets();
app.UseWebSocketServer(serviceProvider =>
{
    // Configure cluster / 配置集群
    var clusterOption = new ClusterOption
    {
        NodeId = "node1",
        NodeAddress = "localhost",
        NodePort = 5000,
        TransportType = "ws", // or "redis" or "rabbitmq"
        ChannelName = "/cluster",
        Nodes = new[]
        {
            "ws://localhost:5001/node2",
            "ws://localhost:5002/node3"
        }
    };
    
    return clusterOption;
});

app.Run();
```

### Using Redis Transport / 使用 Redis 传输

```bash
# Install Redis transport package / 安装 Redis 传输包
dotnet add package Cyaim.WebSocketServer.Cluster.StackExchangeRedis
```

```csharp
var clusterOption = new ClusterOption
{
    NodeId = "node1",
    TransportType = "redis",
    RedisConnectionString = "localhost:6379",
    ChannelName = "/cluster",
    Nodes = new[] { "node1", "node2", "node3" }
};
```

### Using RabbitMQ Transport / 使用 RabbitMQ 传输

```bash
# Install RabbitMQ transport package / 安装 RabbitMQ 传输包
dotnet add package Cyaim.WebSocketServer.Cluster.RabbitMQ
```

```csharp
var clusterOption = new ClusterOption
{
    NodeId = "node1",
    TransportType = "rabbitmq",
    RabbitMQConnectionString = "amqp://guest:guest@localhost:5672/",
    ChannelName = "/cluster",
    Nodes = new[] { "node1", "node2", "node3" }
};
```

> **For more details, see**: [Cluster Documentation](./docs/zh-cn/CLUSTER.md) | [集群文档](./docs/zh-cn/CLUSTER.md)

## 📖 More Documentation / 更多文档

- **[Quick Start Guide](./docs/zh-cn/QUICK_START.md)** - Get started in 5 minutes / 5 分钟快速上手
- **[Core Library](./docs/zh-cn/CORE.md)** - Core features and routing / 核心功能和路由
- **[Configuration Guide](./docs/zh-cn/CONFIGURATION.md)** - Configuration options / 配置选项
- **[API Reference](./docs/zh-cn/API_REFERENCE.md)** - Complete API documentation / 完整 API 文档
- **[Dashboard](./docs/zh-cn/DASHBOARD.md)** - Monitoring and statistics / 监控和统计
- **[Metrics](./docs/zh-cn/METRICS.md)** - OpenTelemetry integration / OpenTelemetry 集成

## 🔗 Related Links / 相关链接

- [GitHub Repository](https://github.com/Cyaim/WebSocketServer)
- [NuGet Package](https://www.nuget.org/packages/Cyaim.WebSocketServer)
- [Issue Tracker](https://github.com/Cyaim/WebSocketServer/issues)

## 📄 License / 许可证

This project is licensed under [MIT License](./LICENSE).

Copyright © Cyaim Studio