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

### 3. Start Cluster Transport / 启动集群传输

```csharp
var app = builder.Build();

// Get cluster transport and start it / 获取集群传输并启动
var clusterTransport = app.Services.GetRequiredService<IClusterTransport>();
await clusterTransport.StartAsync();

app.Run();
```

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

## Updating Node Information / 更新节点信息

You can update node information (e.g., connection count) to enable better load balancing:

您可以更新节点信息（例如连接数）以实现更好的负载均衡：

```csharp
var discoveryService = new RedisNodeDiscoveryService(
    logger,
    redisService,
    nodeId,
    nodeInfo);

await discoveryService.UpdateNodeInfoAsync(new NodeInfo
{
    NodeId = nodeId,
    Address = "localhost",
    Port = 5001,
    ConnectionCount = currentConnectionCount,
    MaxConnections = 10000,
    CpuUsage = GetCpuUsage(),
    MemoryUsage = GetMemoryUsage(),
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

1. **Update Node Info Regularly** - Update connection count and resource usage for accurate load balancing / 定期更新节点信息 - 更新连接数和资源使用率以实现准确的负载均衡

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

// Update node info periodically / 定期更新节点信息
var timer = new System.Timers.Timer(5000); // Every 5 seconds / 每 5 秒
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
    
    // Update via discovery service / 通过发现服务更新
    // Note: You'll need to access the discovery service / 注意：您需要访问发现服务
};

timer.Start();

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

- Ensure node info is updated regularly / 确保节点信息定期更新
- Check connection count values / 检查连接数值
- Verify node status is `Active` / 验证节点状态为 `Active`

## License / 许可证

See LICENSE file for details.

详见 LICENSE 文件。

