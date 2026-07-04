# Hybrid 集群生产部署 Checklist

面向使用 **Hybrid 传输**（Redis 服务发现 + 连接路由存储 + RabbitMQ 消息路由）上生产的团队。清单中的每一条都对应真实中间件验证中踩过的坑或观测到的行为。逐项确认后再上线。

> 适用场景：大规模连接、需要按连接精确定向投递（知道某连接在哪个节点）、已有 Redis + RabbitMQ 基础设施。若只需房间/频道广播且能接受抖动丢消息，用 FreeRedis 更简单。

---

## 1. 中间件前置条件

- [ ] **Redis**：可用（单实例或集群均可）。若是 **Redis 集群**，本库的节点发现已使用单个 Hash 键 `websocket:cluster:nodes`，跨分片可靠，无需特殊处理；但请确认客户端连接串包含**所有分片节点**。
- [ ] **RabbitMQ**：可用，且账号对目标 vhost 有 `configure/write/read` 权限（需声明 exchange/queue、绑定、消费）。
- [ ] **中间件与应用同机房内网互通**：Hybrid 启动要串行完成 Redis 连接 + RabbitMQ 连接 + 声明 exchange/queue/binding + 启动消费者 + 节点发现。跨公网访问 RabbitMQ 时，从启动到首条消息可达会明显变慢（数秒到数十秒）。**强烈建议中间件与服务节点在同一内网**。
- [ ] Redis / RabbitMQ 自身**已做高可用**（Redis 集群/哨兵、RabbitMQ 镜像/quorum 队列或集群），否则中间件是单点。

## 2. 连接字符串

- [ ] **Redis（FreeRedis，Hybrid 发现默认）**：竖线 `|` 分隔每个节点，每段带密码：
      `host1:port,password=xxx|host2:port,password=xxx|...`（列全部分片节点）
- [ ] **RabbitMQ**：`amqp://user:pass@host:port/vhost`（默认 vhost `/` 写成 `%2f`）。
- [ ] 连接串中的**密码等敏感信息用配置中心/环境变量注入**，不要硬编码进代码或提交进仓库。

## 3. 节点身份与进程模型

- [ ] **每个 Hybrid 节点运行在独立进程/容器**。两个 Hybrid 节点跑在同一进程会在启动阶段因共享进程级中间件客户端而相互阻塞（这是本库实测确认的限制；Redis/RabbitMQ/StackExchange 传输无此限制）。
- [ ] **NodeId 全局唯一且稳定**（如用 `主机名-实例序号` 或注入的 Pod 名）。NodeId 决定该节点的 RabbitMQ 队列名 `cluster:node:{NodeId}` 与 Redis 发现字段。
- [ ] NodeId **不要随重启变化**（否则重启会遗留旧队列/旧发现字段，靠 60 秒无心跳与队列 TTL 才回收）。

## 4. 队列 / vhost 隔离（避免 406 PRECONDITION_FAILED）

- [ ] **同一个集群统一只用一种传输**。不要在同一个 RabbitMQ vhost 上用相同 NodeId 混用 Hybrid 与独立 RabbitMQ 传输——两者对同名队列 `cluster:node:{NodeId}` 的声明参数不同，会触发 `406 inequivalent arg`。
- [ ] 多套环境（dev/staging/prod）或多个逻辑集群**用不同的 RabbitMQ vhost 或不同的 Redis 集群前缀隔离**，避免互相串消息。
- [ ] 若曾用旧参数声明过同名队列，升级前**先删除遗留队列**（或确认其参数与新版本一致）。

## 5. 发现与存活

- [ ] 了解节点发现节奏：注册写入 Redis Hash，心跳每 5 秒刷新，发现每 5 秒轮询；**60 秒无心跳视为下线**并从 Hash 删除。
- [ ] 应用层发关键广播前，**先轮询 `IsNodeConnected(peer)` 确认对端已被发现**（`BroadcastAsync` 在未发现任何其他节点前不会真正发送，避免无谓广播）。
- [ ] 若自定义 `IRedisService` 实现，确保 `HashSetAsync/HashGetAsync/HashGetAllAsync/HashDeleteAsync` 正确工作（发现依赖它们）。

## 6. 连接路由（Hybrid 的核心价值）

- [ ] 客户端连接建立/断开时，确认库已把"连接→节点"映射写入/删除 Redis（`StoreConnectionRouteAsync`/`RemoveConnectionRouteAsync`），这样定向投递才是点对点查表，而非全网广播查询。
- [ ] 关注路由 TTL 与刷新：确认长连接期间路由被周期性刷新，避免长时间在线的连接路由过期丢失。
- [ ] 压测**定向投递**（给指定 connectionId 发消息，验证跨节点精确送达），而不仅是广播。

## 7. 容量与扩缩容

- [ ] 先在 **3~5 个节点**的小规模验证：跨节点定向投递、广播、节点上下线时的路由清理与故障切换。
- [ ] 逐步放大节点数，观察 **Redis Hash 大小、RabbitMQ 连接/队列数、发现收敛时间** 随节点数的变化。
- [ ] 扩容新节点上线后，确认**老节点能在一个发现周期内（约 5 秒）发现它**；缩容/下线节点后，确认其发现字段与队列被回收、路由被清理。
- [ ] 评估单节点客户端连接上限（`WebSocketRouteOption.MaxConnectionLimit`）与内存/句柄，结合目标总量倒推所需节点数。

## 8. 网络与超时

- [ ] RabbitMQ 客户端开启自动恢复（`AutomaticRecoveryEnabled`/`TopologyRecoveryEnabled`，库默认已开），确认 broker 抖动后能自动重连并恢复队列/绑定/消费者。
- [ ] Redis 连接串设置合理的 `connectTimeout`；StackExchange 集群模式务必 `abortConnect=false`。
- [ ] 关闭/重启节点走优雅关闭：确认停止时会注销发现字段、发送重定向关闭帧、且不会因对端不响应而永久卡住（库已给关闭握手加超时）。

## 9. 监控与告警

- [ ] 接入库自带的 OpenTelemetry 指标（连接数、消息收发、集群转发/接收、节点连接数、错误数）。
- [ ] 对以下建告警：**节点发现数异常下降**、**RabbitMQ 队列积压**、**Redis 连接失败**、**跨节点转发错误率上升**、**发现收敛时间过长**。
- [ ] 保留 Hybrid 传输的启动/发现日志，便于排查"启动慢/发现不到对端"类问题。

## 10. 升级与回滚

- [ ] 升级前在预发环境用真实中间件跑一遍 `scratchpad/ClusterRealMW`（或等效的双进程 Hybrid 收发验证）。
- [ ] 采用滚动升级：逐个替换节点，每替换一个确认它被其余节点发现、且能收发跨节点消息后再继续。
- [ ] 准备回滚：确认新旧版本的**队列声明参数、Redis 发现键结构兼容**（本版本发现键从 `websocket:cluster:nodes:{id}` 逐节点键改为单 Hash 键 `websocket:cluster:nodes`——新旧版本混跑期间互相发现不到，**不要新旧版本同时在线**，应整批切换或用独立集群前缀隔离）。

---

## 关键提醒（一句话）

1. 每节点独立进程；NodeId 唯一稳定。
2. 中间件与服务同机房内网。
3. 一个集群只用一种传输、一个 vhost/前缀。
4. 发消息前确认对端已被发现；压测走定向投递。
5. 新旧版本不要同时在线（发现键结构已升级）。

相关文档：[集群真实中间件验证](./CLUSTER_VALIDATION.md) · [Hybrid 集群](./HYBRID_CLUSTER.md) · [集群传输扩展](./CLUSTER_TRANSPORTS.md)
