# 集群传输扩展文档

本文档详细介绍 Cyaim.WebSocketServer 的集群传输扩展，包括 Redis 和 RabbitMQ 传输实现。

## 目录

- [概述](#概述)
- [WebSocket 传输](#websocket-传输)
- [Redis 传输](#redis-传输)
- [RabbitMQ 传输](#rabbitmq-传输)
- [传输方式对比](#传输方式对比)
- [选择建议](#选择建议)

## 概述

Cyaim.WebSocketServer 支持三种集群传输方式：

1. **WebSocket (ws/wss)** - 基础包提供，节点间直接通过 WebSocket 连接
2. **Redis** - 使用 Redis Pub/Sub 进行节点间通信（扩展包）
3. **RabbitMQ** - 使用 RabbitMQ 消息队列进行节点间通信（扩展包）

## WebSocket 传输

### 特点

- ✅ 无需额外中间件
- ✅ 低延迟
- ✅ 直接连接
- ❌ 需要节点间网络可达
- ❌ 节点数量多时连接数多

### 安装

基础包已包含，无需额外安装。

### 配置

```csharp
var clusterOption = new ClusterOption
{
    NodeId = "node1",
    NodeAddress = "localhost",
    NodePort = 5001,
    TransportType = "ws",
    Nodes = new[]
    {
        "ws://localhost:5002/node2",
        "ws://localhost:5003/node3"
    }
};

var transport = ClusterTransportFactory.CreateTransport(
    loggerFactory,
    clusterOption.NodeId,
    clusterOption
);
```

### 适用场景

- 节点数量较少（< 10 个）
- 节点间网络延迟低
- 不需要额外的中间件

## Redis 传输

### 特点

- ✅ 解耦节点间连接
- ✅ 支持大量节点
- ✅ 消息持久化（可选）
- ✅ 高可用性
- ❌ 需要 Redis 服务器
- ❌ 额外网络跳转

### 安装

```bash
dotnet add package Cyaim.WebSocketServer.Cluster.StackExchangeRedis
```

### 配置

```csharp
using Cyaim.WebSocketServer.Cluster.StackExchangeRedis;

var clusterOption = new ClusterOption
{
    NodeId = "node1",
    TransportType = "redis",
    RedisConnectionString = "localhost:6379",
    Nodes = new[]
    {
        "node2",
        "node3"
    }
};

var transport = RedisClusterTransportFactory.CreateTransport(
    loggerFactory,
    clusterOption.NodeId,
    clusterOption
);
```

### Redis 连接字符串格式

```
localhost:6379
password@localhost:6379
localhost:6379,password=password
```

### 适用场景

- 节点数量较多（> 10 个）
- 需要高可用性
- 已有 Redis 基础设施
- 节点间网络复杂

## RabbitMQ 传输

### 特点

- ✅ 消息队列保证
- ✅ 支持消息持久化
- ✅ 高可用性
- ✅ 灵活的路由规则
- ❌ 需要 RabbitMQ 服务器
- ❌ 额外网络跳转

### 安装

```bash
dotnet add package Cyaim.WebSocketServer.Cluster.RabbitMQ
```

### 配置

```csharp
using Cyaim.WebSocketServer.Cluster.RabbitMQ;

var clusterOption = new ClusterOption
{
    NodeId = "node1",
    TransportType = "rabbitmq",
    RabbitMQConnectionString = "amqp://guest:guest@localhost:5672/",
    Nodes = new[]
    {
        "node2",
        "node3"
    }
};

var transport = RabbitMQClusterTransportFactory.CreateTransport(
    loggerFactory,
    clusterOption.NodeId,
    clusterOption
);
```

### RabbitMQ 连接字符串格式

```
amqp://username:password@host:port/vhost
amqp://guest:guest@localhost:5672/
```

### 适用场景

- 需要消息保证
- 需要消息持久化
- 已有 RabbitMQ 基础设施
- 复杂的消息路由需求

## 传输方式对比

| 特性 | WebSocket | Redis | RabbitMQ |
|------|----------|-------|----------|
| 安装复杂度 | ⭐ 低 | ⭐⭐ 中 | ⭐⭐ 中 |
| 延迟 | ⭐⭐⭐ 低 | ⭐⭐ 中 | ⭐⭐ 中 |
| 可扩展性 | ⭐ 低 | ⭐⭐⭐ 高 | ⭐⭐⭐ 高 |
| 消息保证 | ⭐⭐ 中 | ⭐⭐⭐ 高 | ⭐⭐⭐ 高 |
| 持久化 | ❌ | ✅ 可选 | ✅ 可选 |
| 高可用 | ⭐⭐ 中 | ⭐⭐⭐ 高 | ⭐⭐⭐ 高 |
| 额外依赖 | ❌ | Redis | RabbitMQ |

## 选择建议

### 选择 WebSocket 传输

- 节点数量 < 10 个
- 节点间网络延迟低
- 不需要额外的中间件
- 快速原型开发

### 选择 Redis 传输

- 节点数量 > 10 个
- 需要高可用性
- 已有 Redis 基础设施
- 节点间网络复杂

### 选择 RabbitMQ 传输

- 需要消息保证
- 需要消息持久化
- 已有 RabbitMQ 基础设施
- 复杂的消息路由需求

## 配置示例

### 多环境配置

```json
{
  "Cluster": {
    "TransportType": "redis",
    "RedisConnectionString": "localhost:6379",
    "Nodes": ["node2", "node3"]
  }
}
```

### 动态切换传输

```csharp
var transportType = configuration["Cluster:TransportType"] ?? "ws";

IClusterTransport transport;
switch (transportType.ToLower())
{
    case "redis":
        transport = RedisClusterTransportFactory.CreateTransport(
            loggerFactory, nodeId, clusterOption);
        break;
    case "rabbitmq":
        transport = RabbitMQClusterTransportFactory.CreateTransport(
            loggerFactory, nodeId, clusterOption);
        break;
    default:
        transport = ClusterTransportFactory.CreateTransport(
            loggerFactory, nodeId, clusterOption);
        break;
}
```

## 性能优化

### Redis 优化

- 使用连接池
- 配置合适的超时时间
- 使用 Redis Cluster 提高性能

### RabbitMQ 优化

- 使用连接池
- 配置合适的队列大小
- 使用 RabbitMQ Cluster 提高可用性

## 故障排除

### Redis 连接问题

1. 检查 Redis 服务器是否运行
2. 检查连接字符串是否正确
3. 检查网络连接
4. 查看 Redis 日志

### RabbitMQ 连接问题

1. 检查 RabbitMQ 服务器是否运行
2. 检查连接字符串是否正确
3. 检查用户权限
4. 查看 RabbitMQ 日志

## 相关文档

- [集群模块文档](./CLUSTER.md)
- [配置指南](./CONFIGURATION.md)
- [最佳实践](./BEST_PRACTICES.md)

