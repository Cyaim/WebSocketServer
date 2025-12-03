# 集群模块文档

本文档详细介绍 Cyaim.WebSocketServer 的集群功能，包括 Raft 协议、消息路由、故障转移等。

## 目录

- [概述](#概述)
- [架构设计](#架构设计)
- [快速开始](#快速开始)
- [Raft 协议](#raft-协议)
- [消息路由](#消息路由)
- [故障处理](#故障处理)
- [优雅关闭](#优雅关闭)
- [配置选项](#配置选项)
- [最佳实践](#最佳实践)

## 概述

集群模块提供了多节点 WebSocket 服务器集群功能，支持：

- ✅ **多节点集群** - 支持水平扩展
- ✅ **Raft 协议** - 基于 Raft 的一致性协议
- ✅ **自动路由** - 跨节点消息自动路由
- ✅ **故障转移** - 节点故障自动处理
- ✅ **优雅关闭** - 支持连接迁移和优雅关闭
- ✅ **网络质量评估** - 自动评估节点间网络质量
- ✅ **负载均衡** - 智能选择最优节点

## 架构设计

### 核心组件

```
┌─────────────────────────────────────────────────────────┐
│                    ClusterManager                        │
│  (统一管理集群生命周期)                                    │
└──────────────┬──────────────────────────────────────────┘
               │
    ┌──────────┴──────────┐
    │                     │
┌───▼──────┐      ┌────────▼────────┐
│ RaftNode │      │ ClusterRouter  │
│          │      │                 │
│ 选主和    │      │ 消息路由和转发   │
│ 状态同步  │      │ 连接管理         │
└───┬──────┘      └────────┬────────┘
    │                     │
    └──────────┬──────────┘
               │
        ┌──────▼──────┐
        │IClusterTransport│
        │                │
        │ 传输层抽象      │
        └──────┬──────┘
               │
    ┌──────────┼──────────┐
    │          │          │
┌───▼──┐  ┌────▼────┐  ┌──▼─────┐
│  WS  │  │  Redis  │  │RabbitMQ│
└──────┘  └─────────┘  └────────┘
```

### 数据流

1. **连接注册**: 新连接建立时，向所有节点广播注册信息
2. **消息路由**: 根据路由表将消息路由到目标节点
3. **状态同步**: 通过 Raft 协议同步集群状态
4. **故障检测**: 定期检查节点健康状态

## 快速开始

### 1. 配置集群选项

```csharp
var clusterOption = new ClusterOption
{
    NodeId = "node1",
    NodeAddress = "localhost",
    NodePort = 5001,
    TransportType = "ws", // ws, redis, rabbitmq
    ChannelName = "/cluster",
    Nodes = new[]
    {
        "ws://localhost:5002/node2",
        "ws://localhost:5003/node3"
    }
};
```

### 2. 创建集群组件

```csharp
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Microsoft.Extensions.Logging;

var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

// 创建传输层
var transport = ClusterTransportFactory.CreateTransport(
    loggerFactory, 
    clusterOption.NodeId, 
    clusterOption
);

// 创建 Raft 节点
var raftNode = new RaftNode(
    loggerFactory.CreateLogger<RaftNode>(),
    transport,
    clusterOption.NodeId
);

// 创建集群路由器
var router = new ClusterRouter(
    loggerFactory.CreateLogger<ClusterRouter>(),
    transport,
    raftNode,
    clusterOption.NodeId
);

// 创建集群管理器
var clusterManager = new ClusterManager(
    loggerFactory.CreateLogger<ClusterManager>(),
    transport,
    raftNode,
    router,
    clusterOption.NodeId,
    clusterOption
);
```

### 3. 设置连接提供者

```csharp
var connectionProvider = new DefaultWebSocketConnectionProvider();
clusterManager.SetConnectionProvider(connectionProvider);
router.SetConnectionProvider(connectionProvider);
```

### 4. 启动集群

```csharp
await clusterManager.StartAsync();
```

### 5. 注册连接

```csharp
// 当新连接建立时
await clusterManager.RegisterConnectionAsync(
    connectionId: context.Connection.Id,
    endpoint: context.Request.Path
);
```

### 6. 路由消息

```csharp
// 路由消息到指定连接（自动处理本地或远程）
var success = await clusterManager.RouteMessageAsync(
    connectionId: connectionId,
    data: Encoding.UTF8.GetBytes("Hello"),
    messageType: (int)WebSocketMessageType.Text
);
```

## Raft 协议

### 节点状态

- **Leader**: 集群主节点，负责处理所有写操作
- **Follower**: 从节点，接收 Leader 的心跳和日志同步
- **Candidate**: 候选节点，在选举过程中使用

### 选举流程

1. **超时触发**: Follower 在超时时间内未收到 Leader 心跳
2. **转为 Candidate**: 增加任期并请求投票
3. **收集选票**: 向其他节点发送投票请求
4. **成为 Leader**: 获得多数票后成为 Leader
5. **发送心跳**: Leader 定期发送心跳维持领导地位

### 2 节点优化

当集群只有 2 个节点时，系统会：

- 评估网络质量，优先选择网络质量好的节点
- 添加随机延迟，避免同时成为 Candidate
- 使用网络质量作为选举依据

### 网络质量评估

系统会自动评估节点间网络质量：

```csharp
// 测量延迟
var latency = await transport.MeasureLatencyAsync(targetNodeId);

// 获取网络质量分数 (0-100)
var quality = await transport.GetNetworkQualityAsync(targetNodeId);
```

网络质量考虑因素：
- 延迟（Latency）
- 本地连接优先
- 连接稳定性

## 消息路由

### 路由表

集群维护一个全局路由表，记录每个连接所在的节点：

```
connectionId -> nodeId
```

### 路由流程

1. **本地连接**: 消息直接发送到本地 WebSocket
2. **远程连接**: 消息通过传输层转发到目标节点
3. **未知连接**: 返回错误或查询集群

### 单连接路由 vs 批量路由

- **`RouteMessageAsync`**: 向单个连接发送消息，返回 `bool` 表示是否成功
- **`RouteMessagesAsync`**: 向多个连接批量发送消息，返回 `Dictionary<string, bool>` 包含每个连接的路由结果
  - 支持跨节点：可以同时向不同节点上的客户端发送消息
  - 并行处理：所有消息并行发送，提高效率
  - 结果追踪：可以查看每个连接是否成功接收消息

### 流转发（支持大文件、网络流等）

集群支持流转发功能，可以高效地传输大文件、网络流等数据：

- **`RouteStreamAsync`**: 向单个连接发送流，自动分块传输
- **`RouteStreamsAsync`**: 向多个连接批量发送流，支持跨节点
- **分块传输**: 自动将大流分块（默认 64KB），避免内存溢出
- **自动重组**: 接收端自动重组分块数据，确保数据完整性
- **支持所有流类型**: 文件流、网络流、内存流等

#### 流转发示例

```csharp
// 发送文件流
using var fileStream = File.OpenRead("largefile.bin");
var success = await clusterManager.RouteStreamAsync(
    connectionId: "connection-123",
    stream: fileStream,
    messageType: WebSocketMessageType.Binary,
    chunkSize: 64 * 1024  // 可选：自定义块大小
);

// 批量发送流到多个连接（支持跨节点）
var connectionIds = new[] { "connection-1", "connection-2", "connection-3" };
var results = await clusterManager.RouteStreamsAsync(
    connectionIds: connectionIds,
    stream: fileStream,
    messageType: WebSocketMessageType.Binary
);

// 发送网络流
using var networkStream = new NetworkStream(socket);
await clusterManager.RouteStreamAsync("connection-123", networkStream);

// 发送内存流
using var memoryStream = new MemoryStream(data);
await clusterManager.RouteStreamAsync("connection-123", memoryStream);
```

#### 流转发工作原理

1. **分块读取**: 从流中按块大小（默认 64KB）读取数据
2. **生成流ID**: 为每个流生成唯一的 StreamId
3. **分块发送**: 将每个块封装为 `ForwardWebSocketStream` 消息发送
4. **接收重组**: 目标节点接收所有块后自动重组
5. **发送到客户端**: 重组完成后发送到目标 WebSocket 连接

#### 流转发特性

- ✅ **内存高效**: 分块传输，不会一次性加载整个流到内存
- ✅ **自动重试**: 支持网络错误重试
- ✅ **顺序保证**: 确保数据块按顺序重组
- ✅ **进度跟踪**: 可选的总大小参数用于进度跟踪
- ✅ **跨节点支持**: 自动路由到正确的节点

### 路由示例

```csharp
// 自动路由单个连接（推荐）
var success = await clusterManager.RouteMessageAsync(
    connectionId: "connection-123",
    data: Encoding.UTF8.GetBytes("Hello"),
    messageType: (int)WebSocketMessageType.Text
);

// 批量路由多个连接（支持跨节点）
var connectionIds = new[] { "connection-1", "connection-2", "connection-3" };
var results = await clusterManager.RouteMessagesAsync(
    connectionIds: connectionIds,
    data: Encoding.UTF8.GetBytes("Hello from cluster!"),
    messageType: (int)WebSocketMessageType.Text
);

// 检查每个连接的路由结果
foreach (var result in results)
{
    if (result.Value)
    {
        Console.WriteLine($"成功发送到连接 {result.Key}");
    }
    else
    {
        Console.WriteLine($"发送到连接 {result.Key} 失败");
    }
}

// 手动查询路由
var nodeId = router.GetConnectionNodeId("connection-123");
if (nodeId == currentNodeId)
{
    // 本地连接
}
else
{
    // 远程连接，需要转发
}
```

## 故障处理

### 节点断开检测

系统通过以下方式检测节点断开：

1. **事件通知**: 传输层触发 `NodeDisconnected` 事件
2. **健康检查**: 定期检查节点连接状态（默认 5 秒）

### 断开处理

当检测到节点断开时：

1. **清理路由表**: 移除该节点的所有连接
2. **更新统计**: 更新连接数统计
3. **触发事件**: 通知应用层节点断开

### 连接转移

当节点优雅关闭时，连接会转移到其他节点：

```csharp
// 优雅关闭
await clusterManager.ShutdownAsync(force: false);

// 强制关闭
await clusterManager.ShutdownAsync(force: true);
```

转移策略：
- **负载均衡**: 选择连接数最少的节点
- **网络质量**: 优先选择网络质量好的节点
- **就近原则**: 优先选择延迟低的节点

### 自适应批处理

连接转移时使用自适应批处理：

- 初始批次大小: 1000 个连接
- 动态调整: 根据网络和处理能力调整批次大小
- 性能优化: 最大化转移速度

## 优雅关闭

### 关闭流程

1. **停止接收新连接**: 停止接受新的 WebSocket 连接
2. **选择目标节点**: 根据负载和网络质量选择最优节点
3. **批量转移连接**: 分批通知客户端重定向
4. **等待转移完成**: 等待连接转移完成
5. **关闭节点**: 关闭集群节点

### 客户端重定向

使用 WebSocket Close Frame 通知客户端重定向：

```json
{
    "redirect": "ws://new-node:port/endpoint"
}
```

客户端应：
1. 接收 Close Frame
2. 解析重定向 URL
3. 自动重连到新节点

### 配置

```csharp
// 在应用关闭时
lifetime.ApplicationStopping.Register(() =>
{
    Task.Run(async () =>
    {
        await clusterManager.ShutdownAsync(force: false);
    }).Wait(TimeSpan.FromSeconds(30));
});
```

## 配置选项

### ClusterOption

```csharp
public class ClusterOption
{
    // 节点 ID
    public string NodeId { get; set; }
    
    // 节点地址（WebSocket 传输需要）
    public string NodeAddress { get; set; }
    public int NodePort { get; set; }
    
    // 传输类型
    public string TransportType { get; set; } // ws, redis, rabbitmq
    
    // 集群节点列表
    public string[] Nodes { get; set; }
    
    // Redis 连接字符串（Redis 传输需要）
    public string RedisConnectionString { get; set; }
    
    // RabbitMQ 连接字符串（RabbitMQ 传输需要）
    public string RabbitMQConnectionString { get; set; }
    
    // 集群通道名称
    public string ChannelName { get; set; } = "/cluster";
}
```

### 节点列表格式

支持多种节点列表格式：

```csharp
// 格式1: WebSocket URL
Nodes = new[] { "ws://localhost:5002/node2" }

// 格式2: 节点ID@地址:端口
Nodes = new[] { "node2@localhost:5002" }

// 格式3: 仅节点ID（Redis/RabbitMQ）
Nodes = new[] { "node2", "node3" }
```

## 最佳实践

### 1. 节点配置

- 确保所有节点都能访问配置中指定的地址
- 使用稳定的节点 ID
- 配置合适的超时时间

### 2. 连接管理

- 及时注册和注销连接
- 处理连接断开事件
- 监控连接数变化

### 3. 错误处理

- 处理节点断开情况
- 处理网络错误
- 实现重试机制

### 4. 性能优化

- 使用 Redis 或 RabbitMQ 传输（大量节点时）
- 监控网络质量
- 合理配置健康检查间隔

### 5. 监控和日志

- 记录集群状态变化
- 监控节点健康状态
- 记录消息路由统计

## 相关文档

- [集群传输扩展](./CLUSTER_TRANSPORTS.md) - Redis、RabbitMQ 传输
- [配置指南](./CONFIGURATION.md) - 详细配置选项
- [API 参考](./API_REFERENCE.md) - 完整 API 文档

