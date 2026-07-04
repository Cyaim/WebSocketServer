# 集群模式真实中间件验证报告

本文档记录 Cyaim.WebSocketServer 全部集群传输在**真实 Redis 集群与真实 RabbitMQ** 上的端到端验证结果、连接字符串格式、以及在生产环境（尤其是 Redis 集群）下必须了解的兼容性要点。所有结论均有实测数据支持，而非形式代码。

> 验证环境：Redis 6 节点集群 `10.0.0.1:10001-10006`（密码鉴权）+ RabbitMQ `amqp://…:15671/`。验证方式：两个独立的库节点（进程）连接同一套中间件，从节点 A 向节点 B（及反向）发送带唯一标记的真实消息，校验对端 `MessageReceived` 收到的 `Payload` 与发送内容逐字节一致。

## 一、验证结论总览

| 传输方式 | 跨节点广播 | 定向发送 | 反向发送 | 节点发现 | Redis 路由存储 | 结论 |
|---|---|---|---|---|---|---|
| **WebSocket（内置）** | ✅ | ✅ | ✅ | 静态配置 | 不适用 | **可用**（双节点 E2E，含 100KB 多帧跨节点） |
| **StackExchangeRedis** | ✅ | ✅ | ✅ | Pub/Sub | 不支持 | **可用**（真实 Redis 集群） |
| **FreeRedis** | ✅ | ✅ | ✅ | Pub/Sub | 不支持 | **可用**（真实 Redis 集群，启动最快） |
| **RabbitMQ** | ✅ | ✅ | ✅ | 队列绑定 | 不支持 | **可用**（真实 RabbitMQ） |
| **Hybrid（Redis 发现 + RabbitMQ 路由）** | ✅ | ✅ | ✅ | Redis Hash | ✅（Redis） | **可用**（真实 Redis 集群 + RabbitMQ，需节点独立进程） |

> 每种传输的验证均观测到 `MessageReceived` 事件携带与发送方完全一致的 `Payload`，例如 `{"marker":"SND-…","data":"REALDATA:SND-…"}`。

## 二、连接字符串格式（对接真实中间件）

### Redis —— StackExchange 传输
逗号分隔全部节点，附加参数：
```
10.0.0.1:10001,10.0.0.1:10002,10.0.0.1:10003,10.0.0.1:10004,10.0.0.1:10005,10.0.0.1:10006,password=你的密码,abortConnect=false,connectTimeout=5000,syncTimeout=5000
```
> 集群模式下 `abortConnect=false` 很重要；StackExchange 需要发现集群拓扑，首次启动约 10 秒。

### Redis —— FreeRedis 传输 / Hybrid 发现
竖线 `|` 分隔每个节点，每段可带独立密码：
```
10.0.0.1:10001,password=你的密码|10.0.0.1:10002,password=你的密码|…|10.0.0.1:10006,password=你的密码
```
> FreeRedis 自动处理 MOVED/ASK 重定向，启动通常在 1 秒内。

### RabbitMQ / Hybrid 路由
标准 AMQP URI：
```
amqp://用户名:密码@主机:端口/%2f
```
> `%2f` 是默认 vhost `/` 的 URL 编码。

## 三、生产环境必读的兼容性要点

### 1. Redis 集群下的节点发现（Hybrid）
Hybrid 的节点发现**不能**依赖 `KEYS`/`SCAN` 通配扫描 —— 在 Redis 集群中，形如 `websocket:cluster:nodes:{nodeId}` 的各节点键会按哈希槽分散到不同分片，单连接的 `SCAN` 只扫描其所连分片，会漏掉其他分片上的节点键，导致**节点互相发现不全**（典型现象：先启动的节点能发现后启动的，反之不行）。

本库的实现已改为把所有节点注册写入**同一个 Redis Hash 键** `websocket:cluster:nodes`（field = 节点 ID，value = 节点信息 JSON）。单键落单分片，任何节点 `HGETALL` 都能可靠拿到全部成员，因此在 Redis 集群下也能对称、完整地发现。存活性通过心跳时间戳判定（60 秒无心跳视为下线并从 Hash 删除），无需依赖单键 TTL。

> 若你自定义 `IRedisService` 实现，请确保 `HashSetAsync/HashGetAllAsync/HashGetAsync/HashDeleteAsync` 正确工作。

### 2. RabbitMQ.Client 7.x 消费者
Hybrid 的 `RabbitMQMessageQueueService` 消费回调中，`ReceivedAsync` 的 sender 在 RabbitMQ.Client 7.x 是**消费者对象本身**（旧版 5.x/6.x 才是 `IModel/IChannel`）。实现取消费者的 `Channel` 属性并回退到共享 channel，切勿把 sender 强转为 `IChannel`（会得到 null 并静默丢弃所有消息）。

### 3. 队列命名与混用
Redis/RabbitMQ/Hybrid 传输为每个节点声明队列 `cluster:node:{nodeId}`。**不要在同一个 RabbitMQ vhost 上用相同的节点 ID 混用不同传输类型** —— 不同传输对同名队列的声明参数不同（如 `x-expires`），会触发 `406 PRECONDITION_FAILED`。生产中一个集群应统一使用一种传输，且节点 ID 唯一。

### 4. Hybrid 拓扑要求
Hybrid 的每个节点必须运行在**独立进程**（真实部署本就如此）。两个 Hybrid 节点跑在同一进程内会因共享进程级中间件客户端资源而在启动阶段相互阻塞。Redis/RabbitMQ/StackExchange 传输无此限制。

### 5. Hybrid 发现收敛时间
Hybrid 启动需先建立 Redis 连接、RabbitMQ 连接、声明交换机/队列/绑定并启动消费者，随后经发现定时器（约每 5 秒）互相发现。在跨公网访问 RabbitMQ 时，端到端从启动到首条消息可达约需数秒到数十秒；同机房内会快得多。`BroadcastAsync` 在未发现任何其他节点前不会发送（避免无谓广播），应用层可先轮询 `IsNodeConnected(peer)` 再发关键广播。

## 四、自行复现验证

仓库 `scratchpad/ClusterRealMW`（示例 harness）演示了对每种传输创建两个节点、互发真实消息并校验送达的完整流程。核心步骤：

```csharp
// 以 FreeRedis 为例
var a = FreeRedisClusterTransportFactory.Create(logger, "nodeA", redisConn);
var b = FreeRedisClusterTransportFactory.Create(logger, "nodeB", redisConn);
await a.StartAsync(); await b.StartAsync();

b.MessageReceived += (s, e) => Console.WriteLine("B 收到: " + e.Message.Payload);
await a.BroadcastAsync(new ClusterMessage {
    Type = ClusterMessageType.ForwardWebSocketMessage,
    FromNodeId = "nodeA",
    Payload = "{\"hello\":\"world\"}"
});
// 断言 B 在超时内收到 Payload 完全一致的消息
```

Hybrid 需两个独立进程，各自 `HybridClusterTransportFactory.Create(...)` 后进入收发循环，用 Redis 集群做发现、RabbitMQ 做路由。

## 五、传输选择建议（基于实测）

- **同机房、节点数少、追求最低延迟**：WebSocket 内置传输。
- **已有 Redis、希望零运维新增中间件、消息量中等**：FreeRedis（启动快）或 StackExchangeRedis。
- **需要可靠投递/持久化/削峰**：RabbitMQ。
- **大规模、需要基于连接路由做定向投递（知道某连接在哪个节点）**：Hybrid（Redis 发现 + 连接路由存储 + RabbitMQ 路由）。

---

相关文档：[集群传输扩展](CLUSTER_TRANSPORTS.md) · [Hybrid 集群](HYBRID_CLUSTER.md) · [集群总览](CLUSTER.md)
