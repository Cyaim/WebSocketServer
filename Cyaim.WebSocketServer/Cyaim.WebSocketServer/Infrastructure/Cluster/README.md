# WebSocket 多节点集群实现

本目录包含了基于 Raft 协议的 WebSocket 多节点集群实现，支持跨节点的 WebSocket 消息转发。

## 架构概述

### 核心组件

1. **RaftNode**: Raft 一致性协议实现，用于选主和状态同步
2. **ClusterRouter**: 集群路由器，负责路由和转发 WebSocket 消息
3. **ClusterManager**: 集群管理器，统一管理集群生命周期
4. **IClusterTransport**: 传输层接口，支持多种通信方式

### 传输方式

支持三种节点间通信方式：

- **WebSocket (ws/wss)**: 节点间直接通过 WebSocket 连接通信（基础包提供）
- **Redis**: 使用 Redis Pub/Sub 进行节点间通信（需要安装 `Cyaim.WebSocketServer.Cluster.StackExchangeRedis` 包）
- **RabbitMQ**: 使用 RabbitMQ 消息队列进行节点间通信（需要安装 `Cyaim.WebSocketServer.Cluster.RabbitMQ` 包）

## 使用方法

### 1. 配置集群选项

```csharp
var clusterOption = new ClusterOption
{
    // 当前节点 ID（如果不设置会自动生成）
    NodeId = "node1",
    
    // 当前节点地址（WebSocket 传输需要）
    NodeAddress = "localhost",
    NodePort = 5000,
    
    // 传输类型：ws, redis, rabbitmq
    TransportType = "ws", // 或 "redis" 或 "rabbitmq"
    
    // 集群节点列表
    // 格式1: ws://address:port/nodeId
    // 格式2: nodeId@address:port
    // 格式3: nodeId (用于 Redis/RabbitMQ)
    Nodes = new[]
    {
        "ws://localhost:5001/node2",
        "ws://localhost:5002/node3"
    },
    
    // Redis 连接字符串（使用 Redis 传输时需要）
    RedisConnectionString = "localhost:6379",
    
    // RabbitMQ 连接字符串（使用 RabbitMQ 传输时需要）
    RabbitMQConnectionString = "amqp://guest:guest@localhost:5672/",
    
    // 集群通道名称
    ChannelName = "/cluster"
};
```

### 2. 创建和启动集群

#### WebSocket 传输（基础包）

```csharp
using Microsoft.Extensions.Logging;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Cluster.Transports;

// 创建日志工厂
var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

// 生成节点 ID（如果未配置）
var nodeId = clusterOption.NodeId ?? Guid.NewGuid().ToString();

// 创建传输层（WebSocket）
var transport = ClusterTransportFactory.CreateTransport(loggerFactory, nodeId, clusterOption);
```

#### Redis 传输（扩展包）

```csharp
using Cyaim.WebSocketServer.Cluster.StackExchangeRedis;

// 创建 Redis 传输
var transport = RedisClusterTransportFactory.CreateTransport(loggerFactory, nodeId, clusterOption);
```

#### RabbitMQ 传输（扩展包）

```csharp
using Cyaim.WebSocketServer.Cluster.RabbitMQ;

// 创建 RabbitMQ 传输
var transport = RabbitMQClusterTransportFactory.CreateTransport(loggerFactory, nodeId, clusterOption);
```

// 创建 Raft 节点
var raftNode = new RaftNode(
    loggerFactory.CreateLogger<RaftNode>(),
    transport,
    nodeId);

// 创建集群路由器
var router = new ClusterRouter(
    loggerFactory.CreateLogger<ClusterRouter>(),
    transport,
    raftNode,
    nodeId);

// 创建集群管理器
var clusterManager = new ClusterManager(
    loggerFactory.CreateLogger<ClusterManager>(),
    transport,
    raftNode,
    router,
    nodeId,
    clusterOption);

// 设置 WebSocket 连接提供者
var connectionProvider = new DefaultWebSocketConnectionProvider();
clusterManager.SetConnectionProvider(connectionProvider);
router.SetConnectionProvider(connectionProvider);

// 启动集群
await clusterManager.StartAsync();

// 保存到全局中心
GlobalClusterCenter.ClusterContext = clusterOption;
GlobalClusterCenter.ClusterManager = clusterManager;
GlobalClusterCenter.ConnectionProvider = connectionProvider;
```

### 3. 注册 WebSocket 连接

```csharp
// 当新的 WebSocket 连接建立时
var connectionId = httpContext.Connection.Id;
await clusterManager.RegisterConnectionAsync(connectionId, "/endpoint");
```

### 4. 路由消息到集群

```csharp
// 路由消息到指定连接（自动处理本地或远程）
var data = Encoding.UTF8.GetBytes("Hello, World!");
var success = await clusterManager.RouteMessageAsync(
    connectionId: connectionId,
    data: data,
    messageType: (int)WebSocketMessageType.Text);
```

### 5. 清理连接

```csharp
// 当 WebSocket 连接断开时
await clusterManager.UnregisterConnectionAsync(connectionId);
```

### 6. 停止集群

```csharp
await clusterManager.StopAsync();
```

## 依赖项和扩展包

### WebSocket 传输

无需额外依赖，使用 .NET 内置的 `ClientWebSocket`。基础包 `Cyaim.WebSocketServer` 已包含。

### Redis 传输

需要安装扩展包：

```bash
dotnet add package Cyaim.WebSocketServer.Cluster.StackExchangeRedis
```

或使用 NuGet 管理器安装。

该扩展包内部依赖 `StackExchange.Redis` (版本 2.7.33)。

### RabbitMQ 传输

需要安装扩展包：

```bash
dotnet add package Cyaim.WebSocketServer.Cluster.RabbitMQ
```

或使用 NuGet 管理器安装。

该扩展包内部依赖 `RabbitMQ.Client` (版本 6.8.1)。

## 项目结构

```
Cyaim.WebSocketServer/
├── Infrastructure/
│   └── Cluster/
│       ├── Transports/
│       │   └── WebSocketClusterTransport.cs  (基础包，原生 WebSocket)
│       ├── RaftNode.cs
│       ├── ClusterRouter.cs
│       ├── ClusterManager.cs
│       └── ...
│
Cyaim.WebSocketServer.Cluster.StackExchangeRedis/
└── RedisClusterTransport.cs  (Redis 扩展包)

Cyaim.WebSocketServer.Cluster.RabbitMQ/
└── RabbitMQClusterTransport.cs  (RabbitMQ 扩展包)
```

## Raft 协议说明

集群使用 Raft 协议进行选主和状态同步：

- **Leader**: 集群主节点，负责处理所有写操作
- **Follower**: 从节点，接收 Leader 的心跳和日志同步
- **Candidate**: 候选节点，在选举过程中使用

### 选举流程

1. Follower 在超时时间内未收到 Leader 心跳，转为 Candidate
2. Candidate 增加任期并请求投票
3. 获得多数票后成为 Leader
4. Leader 定期发送心跳维持领导地位

## 消息转发流程

1. **本地连接**: 消息直接发送到本地 WebSocket
2. **远程连接**: 消息通过传输层转发到目标节点
3. **未知连接**: Leader 节点会查询集群找到连接位置

## 注意事项

1. **节点配置**: 确保所有节点都能访问配置中指定的地址
2. **连接管理**: 及时注册和注销 WebSocket 连接
3. **错误处理**: 注意处理节点断开、网络错误等情况
4. **性能考虑**: 大量连接时考虑使用 Redis 或 RabbitMQ 传输

## 扩展

### 自定义传输实现

实现 `IClusterTransport` 接口即可添加新的传输方式：

```csharp
public class CustomTransport : IClusterTransport
{
    // 实现接口方法
}
```

### 自定义连接提供者

实现 `IWebSocketConnectionProvider` 接口以支持不同的连接存储方式：

```csharp
public class CustomConnectionProvider : IWebSocketConnectionProvider
{
    // 实现接口方法
}
```

## 示例场景

### 场景1: 简单 WebSocket 集群（3 节点）

```csharp
// 节点1配置
var node1Config = new ClusterOption
{
    NodeId = "node1",
    NodeAddress = "localhost",
    NodePort = 5000,
    TransportType = "ws",
    Nodes = new[] { "ws://localhost:5001/node2", "ws://localhost:5002/node3" }
};
```

### 场景2: 使用 Redis 的集群

```csharp
var redisConfig = new ClusterOption
{
    NodeId = "node1",
    TransportType = "redis",
    RedisConnectionString = "localhost:6379",
    Nodes = new[] { "node2", "node3" } // 其他节点 ID
};
```

### 场景3: 使用 RabbitMQ 的集群

```csharp
var rabbitmqConfig = new ClusterOption
{
    NodeId = "node1",
    TransportType = "rabbitmq",
    RabbitMQConnectionString = "amqp://guest:guest@localhost:5672/",
    Nodes = new[] { "node2", "node3" }
};
```

