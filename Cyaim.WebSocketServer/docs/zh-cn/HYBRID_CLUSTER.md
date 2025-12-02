# Hybrid 混合集群传输文档

本文档详细介绍 Cyaim.WebSocketServer 的 Hybrid 混合集群传输实现，该实现使用 **Redis 进行服务发现**，使用 **RabbitMQ 进行消息路由**。

## 目录

- [概述](#概述)
- [架构设计](#架构设计)
- [功能特性](#功能特性)
- [快速开始](#快速开始)
- [安装](#安装)
- [配置](#配置)
- [负载均衡策略](#负载均衡策略)
- [节点信息管理](#节点信息管理)
- [自定义实现](#自定义实现)
- [最佳实践](#最佳实践)
- [故障排除](#故障排除)

## 概述

Hybrid 混合集群传输是一种结合了 Redis 和 RabbitMQ 优势的集群传输方案：

- **Redis** - 用于服务发现和节点信息存储，支持自动节点注册和发现
- **RabbitMQ** - 用于消息路由，提供可靠的消息传递保证
- **负载均衡** - 支持多种负载均衡策略，智能选择最优节点

### 为什么选择 Hybrid 方案？

相比单一传输方式，Hybrid 方案具有以下优势：

1. **解耦服务发现和消息路由** - 服务发现使用 Redis 的轻量级特性，消息路由使用 RabbitMQ 的可靠性保证
2. **自动节点发现** - 无需手动配置节点列表，新节点自动加入集群
3. **智能负载均衡** - 基于连接数、CPU、内存等指标选择最优节点
4. **高可用性** - Redis 和 RabbitMQ 都支持集群模式，提供高可用保障

## 架构设计

```
┌─────────────────────────────────────────────────────────┐
│              HybridClusterTransport                     │
│  (Implements IClusterTransport)                        │
└──────────────┬──────────────────────────────────────────┘
               │
    ┌──────────┴──────────┐
    │                     │
┌───▼──────┐      ┌────────▼────────┐
│   Redis  │      │    RabbitMQ     │
│          │      │                 │
│ Service  │      │  Message Queue  │
│Discovery │      │    Service      │
│          │      │                 │
│ Node     │      │  Exchange &     │
│ Registry │      │  Queue Routing  │
└──────────┘      └─────────────────┘
```

### 组件职责

- **Redis**: 存储节点信息，实现自动发现和注册
- **RabbitMQ**: 在节点间路由 WebSocket 消息
- **LoadBalancer**: 根据策略选择最优节点

## 功能特性

- ✅ **基于 Redis 的服务发现** - 自动节点注册和发现
- ✅ **RabbitMQ 消息路由** - 节点间高效消息路由
- ✅ **自动负载均衡** - 多种负载均衡策略
- ✅ **抽象层设计** - 支持不同的 Redis 和 RabbitMQ 库
- ✅ **节点健康监控** - 自动检测离线节点
- ✅ **连接数跟踪** - 实时连接统计

## 快速开始

### 1. 安装包

**示例：StackExchange.Redis + RabbitMQ**

```bash
# 核心包
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid

# Redis 实现（选择一个）
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.Redis.StackExchange

# 消息队列实现
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ
```

**示例：FreeRedis + RabbitMQ**

```bash
# 核心包
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid

# Redis 实现
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.Redis.FreeRedis

# 消息队列实现
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ
```

### 2. 配置服务

**示例：StackExchange.Redis + RabbitMQ**

```csharp
using Cyaim.WebSocketServer.Cluster.Hybrid;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Cyaim.WebSocketServer.Cluster.Hybrid.Redis.StackExchange;
using Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// 注册 StackExchange.Redis 服务
builder.Services.AddSingleton<IRedisService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<StackExchangeRedisService>>();
    return new StackExchangeRedisService(logger, "localhost:6379");
});

// 注册 RabbitMQ 服务
builder.Services.AddSingleton<IMessageQueueService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<RabbitMQMessageQueueService>>();
    return new RabbitMQMessageQueueService(logger, "amqp://guest:guest@localhost:5672/");
});

// 注册混合集群传输
builder.Services.AddSingleton<IClusterTransport>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HybridClusterTransport>>();
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    var redisService = provider.GetRequiredService<IRedisService>();
    var messageQueueService = provider.GetRequiredService<IMessageQueueService>();
    
    var nodeInfo = new NodeInfo
    {
        NodeId = "node1",
        Address = "localhost",
        Port = 5001,
        Endpoint = "/ws",
        MaxConnections = 10000,
        Status = NodeStatus.Active
    };
    
    return new HybridClusterTransport(
        logger,
        loggerFactory,
        redisService,
        messageQueueService,
        nodeId: "node1",
        nodeInfo: nodeInfo,
        loadBalancingStrategy: LoadBalancingStrategy.LeastConnections
    );
});
```

**示例：FreeRedis + RabbitMQ**

```csharp
using Cyaim.WebSocketServer.Cluster.Hybrid;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Cyaim.WebSocketServer.Cluster.Hybrid.Redis.FreeRedis;
using Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// 注册 FreeRedis 服务
builder.Services.AddSingleton<IRedisService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<FreeRedisService>>();
    return new FreeRedisService(logger, "localhost:6379");
});

// 注册 RabbitMQ 服务
builder.Services.AddSingleton<IMessageQueueService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<RabbitMQMessageQueueService>>();
    return new RabbitMQMessageQueueService(logger, "amqp://guest:guest@localhost:5672/");
});

// 注册混合集群传输
builder.Services.AddSingleton<IClusterTransport>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HybridClusterTransport>>();
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    var redisService = provider.GetRequiredService<IRedisService>();
    var messageQueueService = provider.GetRequiredService<IMessageQueueService>();
    
    var nodeInfo = new NodeInfo
    {
        NodeId = "node1",
        Address = "localhost",
        Port = 5001,
        Endpoint = "/ws",
        MaxConnections = 10000,
        Status = NodeStatus.Active
    };
    
    return new HybridClusterTransport(
        logger,
        loggerFactory,
        redisService,
        messageQueueService,
        nodeId: "node1",
        nodeInfo: nodeInfo,
        loadBalancingStrategy: LoadBalancingStrategy.LeastConnections
    );
});
```

### 3. 配置 WebSocket 服务器（必需）

⚠️ **重要提示**: 使用集群传输时，您**必须**仍然使用 `ConfigureWebSocketRoute` 配置 WebSocket 服务器。集群传输（`IClusterTransport`）只处理节点间通信，不处理客户端连接。客户端 WebSocket 连接仍需要通过常规的 WebSocket 服务器通道。

```csharp
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;

// 配置 WebSocket 服务器通道
builder.Services.ConfigureWebSocketRoute(x =>
{
    var mvcHandler = new MvcChannelHandler();
    
    // 定义客户端连接的业务通道
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", mvcHandler.ConnectionEntry }  // ← 必需：客户端连接端点
    };
    x.ApplicationServiceCollection = builder.Services;
});
```

### 4. 启动集群传输

```csharp
var app = builder.Build();

// 配置 WebSocket 中间件
app.UseWebSockets();
app.UseWebSocketServer();

// 获取集群传输并启动
var clusterTransport = app.Services.GetRequiredService<IClusterTransport>();
await clusterTransport.StartAsync();

app.Run();
```

## ⚠️ 重要注意事项

### 单节点配置必须保留

**关键点**: `IClusterTransport`（包括 `HybridClusterTransport`）**仅**负责节点间通信（服务发现和消息路由）。它**不**处理客户端 WebSocket 连接。

#### 架构概览

```text
┌─────────────────────────────────────────────────────────┐
│         WebSocket Server (单节点配置)                   │
│  ┌──────────────────────────────────────────────────┐  │
│  │  ConfigureWebSocketRoute                          │  │
│  │  └── /ws (业务通道) ← 客户端连接这里               │  │
│  └──────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
                        │
                        │ 使用 IClusterTransport
                        ▼
┌─────────────────────────────────────────────────────────┐
│      HybridClusterTransport (集群传输)                   │
│  ┌──────────────┐         ┌──────────────┐            │
│  │ Redis        │         │ RabbitMQ    │            │
│  │ (服务发现)    │         │ (消息路由)   │            │
│  └──────────────┘         └──────────────┘            │
└─────────────────────────────────────────────────────────┘
```

#### 职责划分

| 组件 | 职责 | 必需 |
|------|------|------|
| `ConfigureWebSocketRoute` | 处理客户端 WebSocket 连接 | ✅ **是** |
| `IClusterTransport` | 节点间通信 | ✅ **是** |

#### 完整配置示例

以下是一个完整的示例，展示两种配置：

```csharp
using Cyaim.WebSocketServer.Cluster.Hybrid;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Cyaim.WebSocketServer.Cluster.Hybrid.Redis.FreeRedis;
using Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// ============================================
// 步骤 1：配置 Redis 服务
// ============================================
builder.Services.AddSingleton<IRedisService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<FreeRedisService>>();
    return new FreeRedisService(logger, "localhost:6379");
});

// ============================================
// 步骤 2：配置 RabbitMQ 服务
// ============================================
builder.Services.AddSingleton<IMessageQueueService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<RabbitMQMessageQueueService>>();
    return new RabbitMQMessageQueueService(logger, "amqp://guest:guest@localhost:5672/");
});

// ============================================
// 步骤 3：配置 WebSocket 服务器
// ⚠️ 这是必需的 - 不要跳过
// ============================================
builder.Services.ConfigureWebSocketRoute(x =>
{
    var mvcHandler = new MvcChannelHandler();
    
    // 定义客户端连接的业务通道
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", mvcHandler.ConnectionEntry }  // 客户端连接端点
    };
    x.ApplicationServiceCollection = builder.Services;
});

// ============================================
// 步骤 4：配置集群传输
// ============================================
builder.Services.AddSingleton<IClusterTransport>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HybridClusterTransport>>();
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    var redisService = provider.GetRequiredService<IRedisService>();
    var messageQueueService = provider.GetRequiredService<IMessageQueueService>();
    
    var nodeInfo = new NodeInfo
    {
        NodeId = "node1",
        Address = "localhost",
        Port = 5001,
        Endpoint = "/ws",  // 这仅用于节点信息，不能替代 ConfigureWebSocketRoute
        MaxConnections = 10000,
        Status = NodeStatus.Active
    };
    
    return new HybridClusterTransport(
        logger,
        loggerFactory,
        redisService,
        messageQueueService,
        nodeId: "node1",
        nodeInfo: nodeInfo,
        loadBalancingStrategy: LoadBalancingStrategy.LeastConnections
    );
});

var app = builder.Build();

// ============================================
// 步骤 5：配置中间件
// ============================================
app.UseWebSockets();
app.UseWebSocketServer();  // WebSocket 服务器必需

// ============================================
// 步骤 6：启动集群传输
// ============================================
var clusterTransport = app.Services.GetRequiredService<IClusterTransport>();
await clusterTransport.StartAsync();

app.Run();
```

#### 常见错误

❌ **错误**: 只配置 `IClusterTransport` 而不配置 `ConfigureWebSocketRoute`

```csharp
// ❌ 这不会工作 - 客户端无法连接
builder.Services.AddSingleton<IClusterTransport>(...);
// 缺少 ConfigureWebSocketRoute
```

✅ **正确**: 同时配置 `ConfigureWebSocketRoute` 和 `IClusterTransport`

```csharp
// ✅ 正确 - 两者都是必需的
builder.Services.ConfigureWebSocketRoute(...);  // 用于客户端连接
builder.Services.AddSingleton<IClusterTransport>(...);  // 用于节点间通信
```

#### 为什么两者都需要

1. **客户端连接**:
   - 客户端连接到通过 `ConfigureWebSocketRoute` 配置的 `/ws` 端点
   - 这由 WebSocket 服务器处理，而不是集群传输

2. **节点间通信**:
   - 当需要将消息发送到另一个节点上的客户端时，`IClusterTransport` 进行路由
   - 这使用 Redis 进行发现，使用 RabbitMQ 进行路由

#### 总结

- ✅ **始终配置** `ConfigureWebSocketRoute` 用于客户端连接
- ✅ **始终配置** `IClusterTransport` 用于节点间通信
- ❌ **永远不要跳过** 使用集群传输时的 `ConfigureWebSocketRoute`

## 安装

### 核心包

```bash
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid
```

### 实现包（模块化设计）

Hybrid 集群传输采用模块化设计，您可以根据需要选择实现包：

#### Redis 实现（服务发现）

选择一个 Redis 实现用于服务发现：

**选项 1: StackExchange.Redis**
```bash
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.Redis.StackExchange
```

**选项 2: FreeRedis**
```bash
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.Redis.FreeRedis
```

#### 消息队列实现（消息路由）

选择一个消息队列实现用于消息路由：

**RabbitMQ**
```bash
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ
```

**未来实现**:
- `Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.MQTT` - MQTT 支持

### ⚠️ 已弃用的包

旧的 `Cyaim.WebSocketServer.Cluster.Hybrid.Implementations` 包已弃用。请使用新的模块化包。

## 配置

### Redis 连接字符串

支持以下格式：

```
localhost:6379
redis://localhost:6379
redis://password@localhost:6379
```

### RabbitMQ 连接字符串

支持标准 AMQP 格式：

```
amqp://guest:guest@localhost:5672/
amqp://username:password@host:port/vhost
```

### 集群前缀

默认的 Redis 键前缀是 `websocket:cluster`。可以通过 `RedisNodeDiscoveryService` 构造函数自定义：

```csharp
var discoveryService = new RedisNodeDiscoveryService(
    logger,
    redisService,
    nodeId,
    nodeInfo,
    clusterPrefix: "myapp:cluster"  // 自定义前缀
);
```

## 负载均衡策略

Hybrid 集群传输支持以下负载均衡策略：

### LeastConnections（默认）

选择连接数最少的节点。

```csharp
LoadBalancingStrategy.LeastConnections
```

### RoundRobin

按轮换顺序选择节点。

```csharp
LoadBalancingStrategy.RoundRobin
```

### LeastResourceUsage

选择 CPU 和内存使用率最低的节点。

```csharp
LoadBalancingStrategy.LeastResourceUsage
```

### Random

随机选择一个节点。

```csharp
LoadBalancingStrategy.Random
```

## 节点信息管理

### NodeInfo 类

`NodeInfo` 类包含每个集群节点的信息：

```csharp
public class NodeInfo
{
    public string NodeId { get; set; }           // 节点 ID
    public string Address { get; set; }          // 节点地址
    public int Port { get; set; }                // 节点端口
    public string Endpoint { get; set; }         // WebSocket 端点
    public int ConnectionCount { get; set; }     // 当前连接数
    public int MaxConnections { get; set; }      // 最大连接数
    public double CpuUsage { get; set; }         // CPU 使用率 %
    public double MemoryUsage { get; set; }      // 内存使用率 %
    public DateTime LastHeartbeat { get; set; }  // 最后心跳时间
    public DateTime RegisteredAt { get; set; }   // 注册时间
    public NodeStatus Status { get; set; }       // 节点状态
    public Dictionary<string, string> Metadata { get; set; }  // 元数据
}
```

### ✅ 自动更新节点信息（默认启用）

**好消息**: `HybridClusterTransport` **默认会自动更新**节点信息！每次心跳时（每 5 秒），系统会自动：

1. **自动检测连接数** - 从 `MvcChannelHandler.Clients` 或 `ClusterManager` 获取
2. **自动获取 CPU 使用率** - 从进程信息计算
3. **自动获取内存使用率** - 从进程信息获取

**无需任何额外配置**，节点信息会自动保持最新！

如果您需要自定义更新逻辑，可以传入 `nodeInfoProvider` 参数。

### 自动更新机制

#### 默认自动更新（无需配置）

`HybridClusterTransport` **默认会自动更新**节点信息，无需任何额外配置：

```csharp
// 标准配置 - 自动更新已启用
builder.Services.AddSingleton<IClusterTransport>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HybridClusterTransport>>();
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    var redisService = provider.GetRequiredService<IRedisService>();
    var messageQueueService = provider.GetRequiredService<IMessageQueueService>();
    
    var nodeInfo = new NodeInfo
    {
        NodeId = "node1",
        Address = "localhost",
        Port = 5001,
        Endpoint = "/ws",
        MaxConnections = 10000,
        Status = NodeStatus.Active
    };
    
    // 无需额外配置，自动更新已启用！
    return new HybridClusterTransport(
        logger,
        loggerFactory,
        redisService,
        messageQueueService,
        nodeId: "node1",
        nodeInfo: nodeInfo,
        loadBalancingStrategy: LoadBalancingStrategy.LeastConnections
    );
});
```

**自动更新会**：
- ✅ 每 5 秒（心跳间隔）自动更新一次
- ✅ 自动从 `MvcChannelHandler.Clients` 获取连接数
- ✅ 自动从 `ClusterManager` 获取连接数（如果可用）
- ✅ 自动计算 CPU 使用率
- ✅ 自动获取内存使用率

#### 自定义更新逻辑

如果您需要自定义更新逻辑，可以传入 `nodeInfoProvider` 参数：

```csharp
builder.Services.AddSingleton<IClusterTransport>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HybridClusterTransport>>();
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    var redisService = provider.GetRequiredService<IRedisService>();
    var messageQueueService = provider.GetRequiredService<IMessageQueueService>();
    
    var nodeInfo = new NodeInfo
    {
        NodeId = "node1",
        Address = "localhost",
        Port = 5001,
        Endpoint = "/ws",
        MaxConnections = 10000,
        Status = NodeStatus.Active
    };
    
    // 自定义节点信息提供者
    return new HybridClusterTransport(
        logger,
        loggerFactory,
        redisService,
        messageQueueService,
        nodeId: "node1",
        nodeInfo: nodeInfo,
        loadBalancingStrategy: LoadBalancingStrategy.LeastConnections,
        nodeInfoProvider: async () =>
        {
            // 自定义逻辑：从您的业务系统获取信息
            return new NodeInfo
            {
                NodeId = "node1",
                Address = "localhost",
                Port = 5001,
                Endpoint = "/ws",
                ConnectionCount = YourCustomService.GetConnectionCount(),
                MaxConnections = 10000,
                CpuUsage = YourCustomService.GetCpuUsage(),
                MemoryUsage = YourCustomService.GetMemoryUsage(),
                Status = NodeStatus.Active
            };
        }
    );
});
```

#### 手动更新（可选）

如果您需要立即更新节点信息（不等待心跳），可以调用 `UpdateNodeInfoAsync`：

```csharp
var clusterTransport = app.Services.GetRequiredService<IClusterTransport>();
await clusterTransport.UpdateNodeInfoAsync(new NodeInfo
{
    NodeId = "node1",
    Address = "localhost",
    Port = 5001,
    Endpoint = "/ws",
    ConnectionCount = 100,
    MaxConnections = 10000,
    CpuUsage = 25.5,
    MemoryUsage = 512.0,
    Status = NodeStatus.Active
});
```

### 获取当前连接数

您需要自己实现获取当前 WebSocket 连接数的方法。以下是一些示例：

```csharp
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Infrastructure.Cluster;

private int GetCurrentConnectionCount()
{
    // 方法 1：从 MvcChannelHandler 的静态 Clients 集合获取（如果使用 MVC 处理器）
    // 这是最简单的方法，适用于使用 MvcChannelHandler 的场景
    return MvcChannelHandler.Clients?.Count ?? 0;
    
    // 方法 2：从 ClusterManager 获取（如果启用了集群）
    // var clusterManager = GlobalClusterCenter.ClusterManager;
    // if (clusterManager != null)
    // {
    //     return clusterManager.GetLocalConnectionCount();
    // }
    // return 0;
    
    // 方法 3：使用静态计数器（需要在连接建立/断开时更新）
    // return WebSocketConnectionCounter.CurrentCount;
    
    // 方法 4：从您的业务逻辑中获取
    // return YourService.GetActiveConnectionCount();
}

private double GetCpuUsage()
{
    // 注意：获取 CPU 使用率需要跨平台兼容性考虑
    // 这里提供一个简单的示例，实际使用时可能需要使用 PerformanceCounter 或其他库
    
    // Windows 平台可以使用 PerformanceCounter
    // #if WINDOWS
    // using System.Diagnostics;
    // var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
    // cpuCounter.NextValue(); // 第一次调用返回 0
    // await Task.Delay(100);
    // return cpuCounter.NextValue();
    // #endif
    
    // 跨平台方案：可以使用 System.Diagnostics.Process 获取进程 CPU 时间
    // 但这不是实时 CPU 使用率，而是累计值
    var process = System.Diagnostics.Process.GetCurrentProcess();
    var totalProcessorTime = process.TotalProcessorTime.TotalMilliseconds;
    var uptime = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds;
    return uptime > 0 ? (totalProcessorTime / uptime) * 100 : 0.0;
}

private double GetMemoryUsage()
{
    // 获取当前进程的内存使用（MB）
    var process = System.Diagnostics.Process.GetCurrentProcess();
    var workingSet = process.WorkingSet64;
    return (double)workingSet / (1024 * 1024); // 转换为 MB
    
    // 或者获取内存使用百分比（需要知道总内存）
    // var totalMemory = // 需要从系统获取总内存
    // return (double)workingSet / totalMemory * 100;
}
```

### 完整示例：定期更新节点信息

```csharp
var app = builder.Build();

// 配置 WebSocket 中间件
app.UseWebSockets();
app.UseWebSocketServer();

// 启动集群传输
var clusterTransport = app.Services.GetRequiredService<IClusterTransport>();
await clusterTransport.StartAsync();

// 定期更新节点信息（每 5 秒）
var updateTimer = new System.Timers.Timer(5000);
updateTimer.Elapsed += async (sender, e) =>
{
    try
    {
        // 获取当前连接数
        var connectionCount = MvcChannelHandler.Clients?.Count ?? 0;
        
        // 获取系统资源使用率
        var cpuUsage = GetCpuUsage();
        var memoryUsage = GetMemoryUsage();
        
        // 更新节点信息
        await clusterTransport.UpdateNodeInfoAsync(new NodeInfo
        {
            NodeId = "node1",
            Address = "localhost",
            Port = 5001,
            Endpoint = "/ws",
            ConnectionCount = connectionCount,  // ← 这里会更新到 Redis
            MaxConnections = 10000,
            CpuUsage = cpuUsage,                // ← 这里会更新到 Redis
            MemoryUsage = memoryUsage,           // ← 这里会更新到 Redis
            Status = NodeStatus.Active
        });
        
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogDebug($"Updated node info: Connections={connectionCount}, CPU={cpuUsage:F2}%, Memory={memoryUsage:F2}MB");
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Failed to update node info");
    }
};
updateTimer.Start();

app.Run();
```

## 自定义实现

您可以实现 `IRedisService` 和 `IMessageQueueService` 接口以使用不同的库。

### 自定义 Redis 实现

```csharp
public class CustomRedisService : IRedisService
{
    // 实现 IRedisService 接口
    public Task ConnectAsync() { /* ... */ }
    public Task DisconnectAsync() { /* ... */ }
    public Task<string> GetAsync(string key) { /* ... */ }
    public Task SetAsync(string key, string value, TimeSpan? expiry = null) { /* ... */ }
    public Task<bool> ExistsAsync(string key) { /* ... */ }
    public Task DeleteAsync(string key) { /* ... */ }
    public Task<List<string>> GetKeysAsync(string pattern) { /* ... */ }
    public Task SubscribeAsync(string channel, Action<string, string> handler) { /* ... */ }
    public Task UnsubscribeAsync(string channel) { /* ... */ }
    public Task PublishAsync(string channel, string message) { /* ... */ }
}
```

### 自定义消息队列实现

```csharp
public class CustomMessageQueueService : IMessageQueueService
{
    // 实现 IMessageQueueService 接口
    public Task ConnectAsync() { /* ... */ }
    public Task DisconnectAsync() { /* ... */ }
    public Task<string> DeclareExchangeAsync(string exchange, string type, bool durable = false) { /* ... */ }
    public Task<string> DeclareQueueAsync(string queue, bool durable = false, bool exclusive = false, bool autoDelete = false) { /* ... */ }
    public Task BindQueueAsync(string queue, string exchange, string routingKey) { /* ... */ }
    public Task PublishAsync(string exchange, string routingKey, byte[] body, MessageProperties properties = null) { /* ... */ }
    public Task ConsumeAsync(string queue, Func<byte[], MessageProperties, Task> handler, bool autoAck = true) { /* ... */ }
}
```

## 最佳实践

1. **定期更新节点信息** - 更新连接数和资源使用率以实现准确的负载均衡
2. **监控节点健康** - 检查 `LastHeartbeat` 以检测离线节点
3. **设置合适的最大连接数** - 帮助负载均衡器做出更好的决策
4. **高流量使用最少连接策略** - 对 WebSocket 连接最有效
5. **优雅处理节点故障** - 为消息发送实现重试逻辑

## 完整示例

```csharp
using Cyaim.WebSocketServer.Cluster.Hybrid;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Cyaim.WebSocketServer.Cluster.Hybrid.Redis.FreeRedis;
using Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// 配置 FreeRedis（或使用 Cyaim.WebSocketServer.Cluster.Hybrid.Redis.StackExchange 中的 StackExchangeRedisService）
builder.Services.AddSingleton<IRedisService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<FreeRedisService>>();
    return new FreeRedisService(logger, "localhost:6379");
});

// 配置 RabbitMQ
builder.Services.AddSingleton<IMessageQueueService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<RabbitMQMessageQueueService>>();
    return new RabbitMQMessageQueueService(logger, "amqp://guest:guest@localhost:5672/");
});

// 配置 Hybrid 集群传输
builder.Services.AddSingleton<IClusterTransport>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HybridClusterTransport>>();
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    var redisService = provider.GetRequiredService<IRedisService>();
    var messageQueueService = provider.GetRequiredService<IMessageQueueService>();
    
    var nodeInfo = new NodeInfo
    {
        NodeId = Environment.GetEnvironmentVariable("NODE_ID") ?? "node1",
        Address = "localhost",
        Port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "5001"),
        Endpoint = "/ws",
        MaxConnections = 10000,
        Status = NodeStatus.Active
    };
    
    return new HybridClusterTransport(
        logger,
        loggerFactory,
        redisService,
        messageQueueService,
        nodeId: nodeInfo.NodeId,
        nodeInfo: nodeInfo,
        loadBalancingStrategy: LoadBalancingStrategy.LeastConnections
    );
});

var app = builder.Build();

// 启动集群传输
var clusterTransport = app.Services.GetRequiredService<IClusterTransport>();
await clusterTransport.StartAsync();

// 定期更新节点信息
var timer = new System.Timers.Timer(5000); // 每 5 秒
timer.Elapsed += async (sender, e) =>
{
    var nodeInfo = new NodeInfo
    {
        NodeId = "node1",
        Address = "localhost",
        Port = 5001,
        ConnectionCount = GetCurrentConnectionCount(),
        MaxConnections = 10000,
        CpuUsage = GetCpuUsage(),
        MemoryUsage = GetMemoryUsage(),
        Status = NodeStatus.Active
    };
    
    // 通过发现服务更新
    // 注意：您需要访问发现服务实例
};

timer.Start();

app.Run();
```

## 故障排除

### 节点无法相互发现

- 检查 Redis 连接
- 验证集群前缀是否匹配
- 检查 Redis 键：`websocket:cluster:nodes:*`

### 消息无法路由

- 检查 RabbitMQ 连接
- 验证交换机和队列声明
- 检查 RabbitMQ 管理界面

### 负载均衡不工作

- 确保节点信息定期更新
- 检查连接数值
- 验证节点状态为 `Active`

## 相关文档

- [集群模块文档](./CLUSTER.md)
- [集群传输扩展文档](./CLUSTER_TRANSPORTS.md)
- [配置指南](./CONFIGURATION.md)

