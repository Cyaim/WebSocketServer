# Hybrid Cluster Transport / 混合集群传输

This package provides a hybrid cluster transport implementation that uses **Redis for service discovery** and **RabbitMQ for message routing**. It supports automatic node discovery, registration, and load balancing.

本包提供了一个混合集群传输实现，使用 **Redis 进行服务发现**，使用 **RabbitMQ 进行消息路由**。支持自动节点发现、注册和负载均衡。

## Features / 功能特性

- ✅ **Redis-based Service Discovery** - Automatic node registration and discovery / 基于 Redis 的服务发现 - 自动节点注册和发现
- ✅ **RabbitMQ Message Routing** - Efficient message routing between nodes / RabbitMQ 消息路由 - 节点间高效消息路由
- ✅ **Automatic Load Balancing** - Multiple load balancing strategies / 自动负载均衡 - 多种负载均衡策略
- ✅ **Abstraction Layer** - Support different Redis and RabbitMQ libraries / 抽象层 - 支持不同的 Redis 和 RabbitMQ 库
- ✅ **Node Health Monitoring** - Automatic detection of offline nodes / 节点健康监控 - 自动检测离线节点
- ✅ **Connection Count Tracking** - Real-time connection statistics / 连接数跟踪 - 实时连接统计

## Architecture / 架构

```text
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
└──────────┘      └─────────────────┘
```

### Component Responsibilities / 组件职责

- **Redis**: Stores node information, enables automatic discovery / 存储节点信息，实现自动发现
- **RabbitMQ**: Routes WebSocket messages between nodes / 在节点间路由 WebSocket 消息
- **LoadBalancer**: Selects optimal node based on strategy / 根据策略选择最优节点

## Installation / 安装

### Core Package / 核心包

```bash
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid
```

### Implementation Packages / 实现包（模块化设计）

The Hybrid cluster transport uses a modular design. You can choose the implementations you need:

混合集群传输采用模块化设计。您可以选择需要的实现：

#### Redis Implementations / Redis 实现（服务发现）

Choose one Redis implementation for service discovery:

选择一个 Redis 实现用于服务发现：

**Option 1: StackExchange.Redis**
```bash
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.Redis.StackExchange
```

**Option 2: FreeRedis**
```bash
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.Redis.FreeRedis
```

#### Message Queue Implementations / 消息队列实现（消息路由）

Choose one message queue implementation for message routing:

选择一个消息队列实现用于消息路由：

**RabbitMQ**
```bash
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ
```

**Future implementations / 未来实现**:
- `Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.MQTT` - MQTT support / MQTT 支持

### ⚠️ Deprecated Package / 已弃用的包

The old `Cyaim.WebSocketServer.Cluster.Hybrid.Implementations` package is deprecated. Please use the new modular packages instead.

旧的 `Cyaim.WebSocketServer.Cluster.Hybrid.Implementations` 包已弃用。请使用新的模块化包。

## Quick Start / 快速开始

### 1. Install Packages / 安装包

**Example: StackExchange.Redis + RabbitMQ / 示例：StackExchange.Redis + RabbitMQ**

```bash
# Core package / 核心包
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid

# Redis implementation (choose one) / Redis 实现（选择一个）
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.Redis.StackExchange

# Message queue implementation / 消息队列实现
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ
```

**Example: FreeRedis + RabbitMQ / 示例：FreeRedis + RabbitMQ**

```bash
# Core package / 核心包
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid

# Redis implementation / Redis 实现
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.Redis.FreeRedis

# Message queue implementation / 消息队列实现
dotnet add package Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ
```

### 2. Configure Services / 配置服务

**Example: StackExchange.Redis + RabbitMQ / 示例：StackExchange.Redis + RabbitMQ**

```csharp
using Cyaim.WebSocketServer.Cluster.Hybrid;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Cyaim.WebSocketServer.Cluster.Hybrid.Redis.StackExchange;
using Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Register StackExchange.Redis service / 注册 StackExchange.Redis 服务
builder.Services.AddSingleton<IRedisService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<StackExchangeRedisService>>();
    return new StackExchangeRedisService(logger, "localhost:6379");
});

// Register RabbitMQ service / 注册 RabbitMQ 服务
builder.Services.AddSingleton<IMessageQueueService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<RabbitMQMessageQueueService>>();
    return new RabbitMQMessageQueueService(logger, "amqp://guest:guest@localhost:5672/");
});
```

**Example: FreeRedis + RabbitMQ / 示例：FreeRedis + RabbitMQ**

```csharp
using Cyaim.WebSocketServer.Cluster.Hybrid;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Cyaim.WebSocketServer.Cluster.Hybrid.Redis.FreeRedis;
using Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Register FreeRedis service / 注册 FreeRedis 服务
builder.Services.AddSingleton<IRedisService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<FreeRedisService>>();
    return new FreeRedisService(logger, "localhost:6379");
});

// Register RabbitMQ service / 注册 RabbitMQ 服务
builder.Services.AddSingleton<IMessageQueueService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<RabbitMQMessageQueueService>>();
    return new RabbitMQMessageQueueService(logger, "amqp://guest:guest@localhost:5672/");
});
```

// Register hybrid cluster transport / 注册混合集群传输
builder.Services.AddSingleton<IClusterTransport>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HybridClusterTransport>>();
    var redisService = provider.GetRequiredService<IRedisService>();
    var messageQueueService = provider.GetRequiredService<IMessageQueueService>();
    
    return HybridClusterTransportFactory.Create(
        logger,
        redisService,
        messageQueueService,
        nodeId: "node1",
        nodeAddress: "localhost",
        nodePort: 5001,
        endpoint: "/ws",
        maxConnections: 10000,
        loadBalancingStrategy: LoadBalancingStrategy.LeastConnections
    );
});
```

### 3. Configure WebSocket Server (Required) / 配置 WebSocket 服务器（必需）

⚠️ **Important / 重要提示**: When using cluster transport, you **MUST** still configure the WebSocket server with `ConfigureWebSocketRoute`. The cluster transport (`IClusterTransport`) only handles inter-node communication, not client connections. Client WebSocket connections still need to go through the regular WebSocket server channels.

⚠️ **重要提示**: 使用集群传输时，您**必须**仍然使用 `ConfigureWebSocketRoute` 配置 WebSocket 服务器。集群传输（`IClusterTransport`）只处理节点间通信，不处理客户端连接。客户端 WebSocket 连接仍需要通过常规的 WebSocket 服务器通道。

```csharp
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;

// Configure WebSocket server channels / 配置 WebSocket 服务器通道
builder.Services.ConfigureWebSocketRoute(x =>
{
    var mvcHandler = new MvcChannelHandler();
    
    // Define business channels for client connections / 定义客户端连接的业务通道
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", mvcHandler.ConnectionEntry }  // ← Required: Client connection endpoint / 必需：客户端连接端点
    };
    x.ApplicationServiceCollection = builder.Services;
});
```

### 4. Start Cluster Transport / 启动集群传输

```csharp
var app = builder.Build();

// Configure WebSocket middleware / 配置 WebSocket 中间件
app.UseWebSockets();
app.UseWebSocketServer();

// Get cluster transport and start it / 获取集群传输并启动
var clusterTransport = app.Services.GetRequiredService<IClusterTransport>();
await clusterTransport.StartAsync();

app.Run();
```

## ⚠️ Important Notes / 重要注意事项

### Single Node Configuration Must Be Retained / 单节点配置必须保留

**Key Point / 关键点**: `IClusterTransport` (including `HybridClusterTransport`) is **only** responsible for inter-node communication (service discovery and message routing). It does **NOT** handle client WebSocket connections.

**关键点**: `IClusterTransport`（包括 `HybridClusterTransport`）**仅**负责节点间通信（服务发现和消息路由）。它**不**处理客户端 WebSocket 连接。

#### Architecture Overview / 架构概览

```text
┌─────────────────────────────────────────────────────────┐
│         WebSocket Server (Single Node Config)          │
│  ┌──────────────────────────────────────────────────┐  │
│  │  ConfigureWebSocketRoute                          │  │
│  │  └── /ws (Business Channel) ← Clients connect    │  │
│  └──────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
                        │
                        │ Uses IClusterTransport
                        ▼
┌─────────────────────────────────────────────────────────┐
│      HybridClusterTransport (Cluster Transport)         │
│  ┌──────────────┐         ┌──────────────┐            │
│  │ Redis        │         │ RabbitMQ    │            │
│  │ (Discovery)  │         │ (Routing)   │            │
│  └──────────────┘         └──────────────┘            │
└─────────────────────────────────────────────────────────┘
```

#### Responsibilities / 职责划分

| Component / 组件 | Responsibility / 职责 | Required / 必需 |
|------------------|---------------------|----------------|
| `ConfigureWebSocketRoute` | Handle client WebSocket connections / 处理客户端 WebSocket 连接 | ✅ **Yes / 是** |
| `IClusterTransport` | Inter-node communication / 节点间通信 | ✅ **Yes / 是** |

#### Complete Configuration Example / 完整配置示例

Here's a complete example showing both configurations:

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
// Step 1: Configure Redis Service / 步骤 1：配置 Redis 服务
// ============================================
builder.Services.AddSingleton<IRedisService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<FreeRedisService>>();
    return new FreeRedisService(logger, "localhost:6379");
});

// ============================================
// Step 2: Configure RabbitMQ Service / 步骤 2：配置 RabbitMQ 服务
// ============================================
builder.Services.AddSingleton<IMessageQueueService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<RabbitMQMessageQueueService>>();
    return new RabbitMQMessageQueueService(logger, "amqp://guest:guest@localhost:5672/");
});

// ============================================
// Step 3: Configure WebSocket Server / 步骤 3：配置 WebSocket 服务器
// ⚠️ THIS IS REQUIRED - DO NOT SKIP / ⚠️ 这是必需的 - 不要跳过
// ============================================
builder.Services.ConfigureWebSocketRoute(x =>
{
    var mvcHandler = new MvcChannelHandler();
    
    // Define business channels for client connections
    // 定义客户端连接的业务通道
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", mvcHandler.ConnectionEntry }  // Client connection endpoint / 客户端连接端点
    };
    x.ApplicationServiceCollection = builder.Services;
});

// ============================================
// Step 4: Configure Cluster Transport / 步骤 4：配置集群传输
// ============================================
builder.Services.AddSingleton<IClusterTransport>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HybridClusterTransport>>();
    var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
    var redisService = provider.GetRequiredService<IRedisService>();
    var messageQueueService = provider.GetRequiredService<IMessageQueueService>();
    
    return HybridClusterTransportFactory.Create(
        logger,
        loggerFactory,
        redisService,
        messageQueueService,
        nodeId: "node1",
        nodeAddress: "localhost",
        nodePort: 5001,
        endpoint: "/ws",  // This is just for node info, NOT a replacement for ConfigureWebSocketRoute
        maxConnections: 10000,
        loadBalancingStrategy: LoadBalancingStrategy.LeastConnections
    );
});

var app = builder.Build();

// ============================================
// Step 5: Configure Middleware / 步骤 5：配置中间件
// ============================================
app.UseWebSockets();
app.UseWebSocketServer();  // Required for WebSocket server / WebSocket 服务器必需

// ============================================
// Step 6: Start Cluster Transport / 步骤 6：启动集群传输
// ============================================
var clusterTransport = app.Services.GetRequiredService<IClusterTransport>();
await clusterTransport.StartAsync();

app.Run();
```

#### Common Mistakes / 常见错误

❌ **Wrong / 错误**: Only configuring `IClusterTransport` without `ConfigureWebSocketRoute`

```csharp
// ❌ This will NOT work - clients cannot connect / 这不会工作 - 客户端无法连接
builder.Services.AddSingleton<IClusterTransport>(...);
// Missing ConfigureWebSocketRoute / 缺少 ConfigureWebSocketRoute
```

✅ **Correct / 正确**: Configuring both `ConfigureWebSocketRoute` and `IClusterTransport`

```csharp
// ✅ Correct - both are required / 正确 - 两者都是必需的
builder.Services.ConfigureWebSocketRoute(...);  // For client connections / 用于客户端连接
builder.Services.AddSingleton<IClusterTransport>(...);  // For inter-node communication / 用于节点间通信
```

#### Why Both Are Needed / 为什么两者都需要

1. **Client Connections / 客户端连接**: 
   - Clients connect to `/ws` endpoint configured via `ConfigureWebSocketRoute`
   - 客户端连接到通过 `ConfigureWebSocketRoute` 配置的 `/ws` 端点
   - This is handled by the WebSocket server, not the cluster transport
   - 这由 WebSocket 服务器处理，而不是集群传输

2. **Inter-Node Communication / 节点间通信**:
   - When a message needs to be sent to a client on another node, `IClusterTransport` routes it
   - 当需要将消息发送到另一个节点上的客户端时，`IClusterTransport` 进行路由
   - This uses Redis for discovery and RabbitMQ for routing
   - 这使用 Redis 进行发现，使用 RabbitMQ 进行路由

#### Summary / 总结

- ✅ **Always configure** `ConfigureWebSocketRoute` for client connections
- ✅ **始终配置** `ConfigureWebSocketRoute` 用于客户端连接
- ✅ **Always configure** `IClusterTransport` for inter-node communication
- ✅ **始终配置** `IClusterTransport` 用于节点间通信
- ❌ **Never skip** `ConfigureWebSocketRoute` when using cluster transport
- ❌ **永远不要跳过** 使用集群传输时的 `ConfigureWebSocketRoute`

## Load Balancing Strategies / 负载均衡策略

### LeastConnections (Default) / 最少连接（默认）

Selects the node with the fewest active connections.

选择连接数最少的节点。

```csharp
LoadBalancingStrategy.LeastConnections
```

### RoundRobin / 轮询

Selects nodes in rotation order.

按轮换顺序选择节点。

```csharp
LoadBalancingStrategy.RoundRobin
```

### LeastResourceUsage / 最少资源使用

Selects the node with the lowest CPU and memory usage.

选择 CPU 和内存使用率最低的节点。

```csharp
LoadBalancingStrategy.LeastResourceUsage
```

### Random / 随机

Randomly selects a node.

随机选择一个节点。

```csharp
LoadBalancingStrategy.Random
```

## Node Information / 节点信息

The `NodeInfo` class contains information about each cluster node:

`NodeInfo` 类包含每个集群节点的信息：

```csharp
public class NodeInfo
{
    public string NodeId { get; set; }           // Node ID / 节点 ID
    public string Address { get; set; }          // Node address / 节点地址
    public int Port { get; set; }                // Node port / 节点端口
    public string Endpoint { get; set; }         // WebSocket endpoint / WebSocket 端点
    public int ConnectionCount { get; set; }     // Current connections / 当前连接数
    public int MaxConnections { get; set; }      // Max connections / 最大连接数
    public double CpuUsage { get; set; }         // CPU usage % / CPU 使用率 %
    public double MemoryUsage { get; set; }      // Memory usage % / 内存使用率 %
    public DateTime LastHeartbeat { get; set; }  // Last heartbeat / 最后心跳
    public NodeStatus Status { get; set; }       // Node status / 节点状态
}
```

## Node Information Management / 节点信息管理

### NodeInfo Class / NodeInfo 类

The `NodeInfo` class contains information about each cluster node:

`NodeInfo` 类包含每个集群节点的信息：

```csharp
public class NodeInfo
{
    public string NodeId { get; set; }           // Node ID / 节点 ID
    public string Address { get; set; }          // Node address / 节点地址
    public int Port { get; set; }                // Node port / 节点端口
    public string Endpoint { get; set; }         // WebSocket endpoint / WebSocket 端点
    public int ConnectionCount { get; set; }     // Current connections / 当前连接数
    public int MaxConnections { get; set; }      // Max connections / 最大连接数
    public double CpuUsage { get; set; }         // CPU usage % / CPU 使用率 %
    public double MemoryUsage { get; set; }      // Memory usage % / 内存使用率 %
    public DateTime LastHeartbeat { get; set; }  // Last heartbeat / 最后心跳时间
    public DateTime RegisteredAt { get; set; }   // Registration time / 注册时间
    public NodeStatus Status { get; set; }       // Node status / 节点状态
    public Dictionary<string, string> Metadata { get; set; }  // Metadata / 元数据
}
```

### ✅ Auto-Update Node Information (Enabled by Default) / ✅ 自动更新节点信息（默认启用）

**Good News / 好消息**: `HybridClusterTransport` **automatically updates** node information by default! During each heartbeat (every 5 seconds), the system automatically:

**好消息**: `HybridClusterTransport` **默认会自动更新**节点信息！每次心跳时（每 5 秒），系统会自动：

1. **Auto-detects connection count** - Gets from `MvcChannelHandler.Clients` or `ClusterManager` / **自动检测连接数** - 从 `MvcChannelHandler.Clients` 或 `ClusterManager` 获取
2. **Auto-gets CPU usage** - Calculated from process information / **自动获取 CPU 使用率** - 从进程信息计算
3. **Auto-gets memory usage** - Retrieved from process information / **自动获取内存使用率** - 从进程信息获取

**No additional configuration needed** - node information stays up-to-date automatically!

**无需任何额外配置**，节点信息会自动保持最新！

If you need custom update logic, you can pass a `nodeInfoProvider` parameter.

如果您需要自定义更新逻辑，可以传入 `nodeInfoProvider` 参数。

### Auto-Update Mechanism / 自动更新机制

#### Default Auto-Update (No Configuration Needed) / 默认自动更新（无需配置）

`HybridClusterTransport` **automatically updates** node information by default, no additional configuration needed:

`HybridClusterTransport` **默认会自动更新**节点信息，无需任何额外配置：

```csharp
// Standard configuration - auto-update is enabled / 标准配置 - 自动更新已启用
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
    
    // No additional configuration needed - auto-update is enabled! / 无需额外配置，自动更新已启用！
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

**Auto-update will / 自动更新会**：
- ✅ Automatically update every 5 seconds (heartbeat interval) / 每 5 秒（心跳间隔）自动更新一次
- ✅ Automatically get connection count from `MvcChannelHandler.Clients` / 自动从 `MvcChannelHandler.Clients` 获取连接数
- ✅ Automatically get connection count from `ClusterManager` (if available) / 自动从 `ClusterManager` 获取连接数（如果可用）
- ✅ Automatically calculate CPU usage / 自动计算 CPU 使用率
- ✅ Automatically get memory usage / 自动获取内存使用率

#### Custom Update Logic / 自定义更新逻辑

If you need custom update logic, you can pass a `nodeInfoProvider` parameter:

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
    
    // Custom node info provider / 自定义节点信息提供者
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
            // Custom logic: get information from your business system / 自定义逻辑：从您的业务系统获取信息
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

#### Manual Update (Optional) / 手动更新（可选）

If you need to update node information immediately (without waiting for heartbeat), you can call `UpdateNodeInfoAsync`:

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

## Custom Implementations / 自定义实现

You can implement `IRedisService` and `IMessageQueueService` to use different libraries:

您可以实现 `IRedisService` 和 `IMessageQueueService` 以使用不同的库：

### Custom Redis Implementation / 自定义 Redis 实现

```csharp
public class CustomRedisService : IRedisService
{
    // Implement IRedisService interface / 实现 IRedisService 接口
    // ...
}
```

### Custom Message Queue Implementation / 自定义消息队列实现

```csharp
public class CustomMessageQueueService : IMessageQueueService
{
    // Implement IMessageQueueService interface / 实现 IMessageQueueService 接口
    // ...
}
```

## Configuration Options / 配置选项

### Redis Connection String / Redis 连接字符串

```
localhost:6379
redis://localhost:6379
redis://password@localhost:6379
```

### RabbitMQ Connection String / RabbitMQ 连接字符串

```
amqp://guest:guest@localhost:5672/
amqp://username:password@host:port/vhost
```

### Cluster Prefix / 集群前缀

The default Redis key prefix is `websocket:cluster`. You can customize it:

默认的 Redis 键前缀是 `websocket:cluster`。您可以自定义：

```csharp
var discoveryService = new RedisNodeDiscoveryService(
    logger,
    redisService,
    nodeId,
    nodeInfo,
    clusterPrefix: "myapp:cluster"  // Custom prefix / 自定义前缀
);
```

## Best Practices / 最佳实践

1. **Auto-Update is Enabled by Default** - Node information is automatically updated every 5 seconds / **默认启用自动更新** - 节点信息每 5 秒自动更新一次

2. **Monitor Node Health** - Check `LastHeartbeat` to detect offline nodes / 监控节点健康 - 检查 `LastHeartbeat` 以检测离线节点

3. **Set Appropriate MaxConnections** - Helps load balancer make better decisions / 设置合适的最大连接数 - 帮助负载均衡器做出更好的决策

4. **Use LeastConnections for High Traffic** - Most effective for WebSocket connections / 高流量使用最少连接 - 对 WebSocket 连接最有效

5. **Handle Node Failures Gracefully** - Implement retry logic for message sending / 优雅处理节点故障 - 为消息发送实现重试逻辑

## Example: Complete Setup / 示例：完整设置

```csharp
using Cyaim.WebSocketServer.Cluster.Hybrid;
using Cyaim.WebSocketServer.Cluster.Hybrid.Abstractions;
using Cyaim.WebSocketServer.Cluster.Hybrid.Redis.FreeRedis;
using Cyaim.WebSocketServer.Cluster.Hybrid.MessageQueue.RabbitMQ;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Configure FreeRedis (or use StackExchangeRedisService from Cyaim.WebSocketServer.Cluster.Hybrid.Redis.StackExchange) / 配置 FreeRedis（或使用 Cyaim.WebSocketServer.Cluster.Hybrid.Redis.StackExchange 中的 StackExchangeRedisService）
builder.Services.AddSingleton<IRedisService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<FreeRedisService>>();
    return new FreeRedisService(logger, "localhost:6379");
});

// Configure RabbitMQ / 配置 RabbitMQ
builder.Services.AddSingleton<IMessageQueueService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<RabbitMQMessageQueueService>>();
    return new RabbitMQMessageQueueService(logger, "amqp://guest:guest@localhost:5672/");
});

// Configure Hybrid Cluster Transport / 配置混合集群传输
builder.Services.AddSingleton<IClusterTransport>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<HybridClusterTransport>>();
    var redisService = provider.GetRequiredService<IRedisService>();
    var messageQueueService = provider.GetRequiredService<IMessageQueueService>();
    
    return HybridClusterTransportFactory.Create(
        logger,
        redisService,
        messageQueueService,
        nodeId: Environment.GetEnvironmentVariable("NODE_ID") ?? "node1",
        nodeAddress: "localhost",
        nodePort: int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "5001"),
        endpoint: "/ws",
        maxConnections: 10000,
        loadBalancingStrategy: LoadBalancingStrategy.LeastConnections
    );
});

var app = builder.Build();

// Start cluster transport / 启动集群传输
var clusterTransport = app.Services.GetRequiredService<IClusterTransport>();
await clusterTransport.StartAsync();

// Note: Node information is automatically updated every 5 seconds (heartbeat interval)
// No manual update timer needed - auto-update is enabled by default!
// 注意：节点信息每 5 秒（心跳间隔）自动更新
// 无需手动更新定时器 - 默认已启用自动更新！

app.Run();
```

## Troubleshooting / 故障排除

### Nodes Not Discovering Each Other / 节点无法相互发现

- Check Redis connection / 检查 Redis 连接
- Verify cluster prefix matches / 验证集群前缀匹配
- Check Redis keys: `websocket:cluster:nodes:*` / 检查 Redis 键：`websocket:cluster:nodes:*`

### Messages Not Routing / 消息无法路由

- Check RabbitMQ connection / 检查 RabbitMQ 连接
- Verify exchange and queue declarations / 验证交换机和队列声明
- Check RabbitMQ management UI / 检查 RabbitMQ 管理界面

### Load Balancing Not Working / 负载均衡不工作

- Node info is automatically updated every 5 seconds (default) / 节点信息每 5 秒自动更新（默认）
- Check connection count values / 检查连接数值
- Verify node status is `Active` / 验证节点状态为 `Active`
- If using custom `nodeInfoProvider`, ensure it returns valid data / 如果使用自定义 `nodeInfoProvider`，确保它返回有效数据

## License / 许可证

See LICENSE file for details.

详见 LICENSE 文件。

