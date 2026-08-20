# 配置指南

本文档详细介绍 Cyaim.WebSocketServer 的所有配置选项。

## 目录

- [基础配置](#基础配置)
- [WebSocket 配置](#websocket-配置)
- [集群配置](#集群配置)
- [带宽限制配置](#带宽限制配置)
- [指标统计配置](#指标统计配置)
- [配置文件示例](#配置文件示例)

## 基础配置

### WebSocketRouteOption

```csharp
builder.Services.ConfigureWebSocketRoute(x =>
{
    // 通道配置
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", mvcHandler.ConnectionEntry }
    };
    
    // 服务集合（用于依赖注入）
    x.ApplicationServiceCollection = builder.Services;
    
    // 带宽限制策略（可选）
    x.BandwidthLimitPolicy = new BandwidthLimitPolicy { /* ... */ };
    
    // 集群配置（可选）
    x.EnableCluster = false;
});
```

### 接收内存控制（防 OOM/DoS）

| 选项 | 默认 | 说明 |
|---|---|---|
| `MaxRequestReceiveDataLimit` | **4 MiB** | 单条消息最多缓冲的字节；`null` = 不限（自担 OOM 风险）。 |
| `MaxTotalReceiveBufferBytes` | `null`（禁用） | 所有连接"在途多帧接收缓冲"字节总预算（纵深防御）。 |
| `MaxConnectionLimit` | `null` | 最大并发连接数。 |

```csharp
builder.Services.AddWebSocketServer(x =>
{
    x.MaxRequestReceiveDataLimit = 4L * 1024 * 1024;   // 默认 4 MiB；null=不限
    x.MaxTotalReceiveBufferBytes = 512L * 1024 * 1024; // 可选：全局在途接收缓冲总预算
});
```

端点级覆盖：`[WebSocket("bulk.import", MaxBytes = 32 * 1024 * 1024)]`（缓冲式端点单条上限）。
大文件请改用流式端点 `[WebSocket(Stream = true)]`（内存恒定）。

> ⚠️ **2.0 行为变更**：`MaxRequestReceiveDataLimit` 默认从"不限"改为 **4 MiB**。若现有业务经普通端点收发大于 4 MiB 的单条消息，请显式调大或设 `null`，或改用流式端点。详见 [流式上传与内存控制](./STREAMING_UPLOAD.md)。

### 发送完整性与内存控制

**不变式：只要一条消息带着 `endOfMessage` 标志发出，它的内容就一定完整。**

客户端判断「消息到齐了」的唯一依据就是这个 WebSocket 层的结束标志。如果服务端可能发出一条带着该标志、
内容却少了一截的消息，那么**每一个**客户端都得自己去实现截断检测——这是把服务端的正确性问题变成所有
接入方的负担。所以本库宁可终结连接，也绝不发出这样的消息。

失败只有两种收场，没有第三种：

| 情形 | 收场 | 对端看到 |
|---|---|---|
| 载荷在物化上限内，读源失败 | **一帧未发**，异常抛给调用方 | 什么都没收到，连接照常可用 |
| 载荷超过上限走流式，中途失败 | **终结连接**（Close 1011，Abort 兜底） | 协议错误 / 连接关闭，绝不是一条「完整」消息 |

| 选项 | 默认 | 说明 |
|---|---|---|
| `MaxSendMaterializeBytes` | **4 MiB** | 发送前先读进内存的最大载荷；`null` = 不限。**只作用于 Stream 重载**，调用方直接交进来的 buffer 不受约束。 |
| `MaxSendFrameBytes` | **256 KiB − 16** | 单个 WebSocket 帧的最大字节；`0` = 不切分。 |
| `MaxTotalSendMaterializeBytes` | `null`（禁用） | 所有连接同时物化的进程级总预算。超预算**降级流式而不排队**。 |
| `AllowChunkedSendAboveMaterializeLimit` | `true` | 超过物化上限时降级流式（默认），还是直接抛 `WebSocketMessageTooLargeException`。 |

```csharp
builder.Services.AddWebSocketServer(x =>
{
    x.MaxSendMaterializeBytes = 4L * 1024 * 1024;   // 默认 4 MiB，与接收侧对称
    x.MaxSendFrameBytes = 256 * 1024 - 16;          // 减 16 是为了对齐 ArrayPool 分桶
    x.MaxTotalSendMaterializeBytes = 512L * 1024 * 1024;  // 可选
});
```

**为什么 `MaxSendFrameBytes` 要减 16**：底层按「载荷 + 帧头（最多 14 字节）」向 `ArrayPool` 租借，
取整 2^n 会让租借落进 2^(n+1) 的桶里白占一倍。

**切帧与消息完整性无关**。已在内存里的载荷，多帧发送唯一可能的失败就是写失败，而写失败一律终结连接，
产生不了带结束标志的短消息。帧上限守的是另外三件事：扇出时的峰值内存、带外控制帧（如 Close）的排队
延迟、以及取消所暴露的窗口。

**池滞留（反直觉的代价）**：物化用的是 `ArrayPool`，所以「历史峰值并发物化字节」会长期驻留在进程里。
这是刻意的取舍——改用裸分配会带来数量级更高的 GC 停顿。注意接收侧的 4 MiB 走的是普通 `MemoryStream`，
没有这条成本：**两侧数值对称，但内存画像并不对称**。

> ⚠️ **行为变更**：多帧消息不再发送末尾那个多余的空收尾帧（最后一帧自带结束标志），因此**帧数变了**——
> 按帧计费的带宽统计需要重新核对。`sendAtOnce` 与 `sendBufferSize` 不再影响 buffer 的分帧
> （分帧只由 `MaxSendFrameBytes` 决定）；`sendBufferSize` 在 Stream 路径上现在表示「读块大小」。
> 可 Seek 的流若发生静默短读（`Read` 返回 0 但源未读完），现在会抛 `EndOfStreamException` 且一帧未发，
> 而不是像以前那样静默发出一条截断消息。

## WebSocket 配置

### WebSocketOptions

```csharp
var webSocketOptions = new WebSocketOptions()
{
    // Keep-Alive 间隔（默认 120 秒）
    KeepAliveInterval = TimeSpan.FromSeconds(120),
    
    // 接收缓冲区大小（已弃用，在处理器中配置）
    // ReceiveBufferSize = 4 * 1024
};
```

### MvcChannelHandler 配置

```csharp
var handler = new MvcChannelHandler(
    receiveBufferSize: 4 * 1024,  // 接收缓冲区大小
    sendBufferSize: 4 * 1024      // 发送缓冲区大小
);

// 响应发送超时
handler.ResponseSendTimeout = TimeSpan.FromSeconds(10);
```

## 集群配置

### ClusterOption

```csharp
var clusterOption = new ClusterOption
{
    // 节点 ID（如果不设置会自动生成）
    NodeId = "node1",
    
    // 节点地址（WebSocket 传输需要）
    NodeAddress = "localhost",
    NodePort = 5001,
    
    // 传输类型：ws, redis, rabbitmq
    TransportType = "ws",
    
    // 集群节点列表
    Nodes = new[]
    {
        "ws://localhost:5002/node2",
        "ws://localhost:5003/node3"
    },
    
    // Redis 连接字符串（Redis 传输需要）
    RedisConnectionString = "localhost:6379",
    
    // RabbitMQ 连接字符串（RabbitMQ 传输需要）
    RabbitMQConnectionString = "amqp://guest:guest@localhost:5672/",
    
    // 集群通道名称
    ChannelName = "/cluster"
};
```

### 从配置文件加载

```json
{
  "Cluster": {
    "NodeId": "node1",
    "NodeAddress": "localhost",
    "NodePort": 5001,
    "TransportType": "ws",
    "ChannelName": "/cluster",
    "Nodes": [
      "ws://localhost:5002/node2",
      "ws://localhost:5003/node3"
    ]
  }
}
```

```csharp
var clusterConfig = app.Configuration.GetSection("Cluster");
var clusterOption = new ClusterOption
{
    NodeId = clusterConfig["NodeId"],
    NodeAddress = clusterConfig["NodeAddress"],
    NodePort = clusterConfig.GetValue<int>("NodePort"),
    TransportType = clusterConfig["TransportType"],
    ChannelName = clusterConfig["ChannelName"],
    Nodes = clusterConfig.GetSection("Nodes").Get<string[]>()
};
```

## 带宽限制配置

### BandwidthLimitPolicy

```csharp
var policy = new BandwidthLimitPolicy
{
    // 启用/禁用限速
    Enabled = true,
    
    // 全局通道限速（字节/秒）
    GlobalChannelBandwidthLimit = new Dictionary<string, long>
    {
        { "/ws", 10 * 1024 * 1024 } // 10MB/s
    },
    
    // 通道最低带宽保障（字节/秒）
    ChannelMinBandwidthGuarantee = new Dictionary<string, long>
    {
        { "/ws", 1024 * 1024 } // 1MB/s
    },
    
    // 通道最高带宽限制（字节/秒）
    ChannelMaxBandwidthLimit = new Dictionary<string, long>
    {
        { "/ws", 5 * 1024 * 1024 } // 5MB/s
    },
    
    // 启用平均分配带宽
    ChannelEnableAverageBandwidth = new Dictionary<string, bool>
    {
        { "/ws", true }
    },
    
    // 连接最低带宽保障（字节/秒）
    ChannelConnectionMinBandwidthGuarantee = new Dictionary<string, long>
    {
        { "/ws", 512 * 1024 } // 512KB/s
    },
    
    // 连接最高带宽限制（字节/秒）
    ChannelConnectionMaxBandwidthLimit = new Dictionary<string, long>
    {
        { "/ws", 2 * 1024 * 1024 } // 2MB/s
    },
    
    // 端点最高限速（字节/秒）
    EndPointMaxBandwidthLimit = new Dictionary<string, long>
    {
        { "controller.action", 1024 * 1024 } // 1MB/s
    },
    
    // 端点最低带宽保障（字节/秒）
    EndPointMinBandwidthGuarantee = new Dictionary<string, long>
    {
        { "controller.action", 256 * 1024 } // 256KB/s
    }
};
```

### 从配置文件加载

```json
{
  "BandwidthLimitPolicy": {
    "Enabled": true,
    "GlobalChannelBandwidthLimit": {
      "/ws": 10485760
    },
    "ChannelMinBandwidthGuarantee": {
      "/ws": 1048576
    },
    "ChannelMaxBandwidthLimit": {
      "/ws": 5242880
    },
    "ChannelEnableAverageBandwidth": {
      "/ws": true
    },
    "ChannelConnectionMinBandwidthGuarantee": {
      "/ws": 524288
    },
    "ChannelConnectionMaxBandwidthLimit": {
      "/ws": 2097152
    },
    "EndPointMaxBandwidthLimit": {
      "controller.action": 1048576
    },
    "EndPointMinBandwidthGuarantee": {
      "controller.action": 262144
    }
  }
}
```

```csharp
var policy = new BandwidthLimitPolicy();
policy.LoadFromConfiguration(configuration, "BandwidthLimitPolicy");
```

## 指标统计配置

### OpenTelemetry 配置

```csharp
using OpenTelemetry.Metrics;
using Cyaim.WebSocketServer.Infrastructure.Metrics;
// OTLP 导出扩展在可选包 Cyaim.WebSocketServer.OpenTelemetry 中
using Cyaim.WebSocketServer.OpenTelemetry;

// 添加 WebSocket 指标收集（核心库，无 OpenTelemetry 依赖）
builder.Services.AddWebSocketMetrics();

// 配置 OpenTelemetry Metrics（需引用可选包 Cyaim.WebSocketServer.OpenTelemetry）
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddWebSocketMetricsExporter(options =>
            {
                // OTLP 端点（默认：http://localhost:4317）
                options.Endpoint = new Uri("http://localhost:4317");
                
                // 协议类型：Grpc 或 HttpProtobuf
                options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
            });
    });
```

### 环境变量配置

```bash
# OTLP gRPC 端点
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317

# OTLP 协议类型
export OTEL_EXPORTER_OTLP_PROTOCOL=grpc

# HTTP 头（可选）
export OTEL_EXPORTER_OTLP_HEADERS="api-key=your-api-key"
```

## 配置文件示例

### appsettings.json

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  },
  "Cluster": {
    "NodeId": "node1",
    "NodeAddress": "localhost",
    "NodePort": 5001,
    "TransportType": "ws",
    "ChannelName": "/cluster",
    "Nodes": [
      "ws://localhost:5002/node2",
      "ws://localhost:5003/node3"
    ]
  },
  "BandwidthLimitPolicy": {
    "Enabled": true,
    "GlobalChannelBandwidthLimit": {
      "/ws": 10485760
    },
    "ChannelMaxBandwidthLimit": {
      "/ws": 5242880
    }
  }
}
```

### appsettings.{NodeId}.json

为每个节点创建独立的配置文件：

**appsettings.node1.json**:
```json
{
  "Cluster": {
    "NodeId": "node1",
    "NodeAddress": "localhost",
    "NodePort": 5001,
    "Nodes": [
      "ws://localhost:5002/node2",
      "ws://localhost:5003/node3"
    ]
  }
}
```

**appsettings.node2.json**:
```json
{
  "Cluster": {
    "NodeId": "node2",
    "NodeAddress": "localhost",
    "NodePort": 5002,
    "Nodes": [
      "ws://localhost:5001/node1",
      "ws://localhost:5003/node3"
    ]
  }
}
```

## 配置最佳实践

1. **使用配置文件**: 将配置放在配置文件中，便于管理
2. **环境分离**: 为不同环境（开发、测试、生产）使用不同配置
3. **节点特定配置**: 为每个节点创建独立的配置文件
4. **配置验证**: 在启动时验证配置的有效性
5. **敏感信息**: 使用环境变量或密钥管理服务存储敏感信息

## 相关文档

- [核心库文档](./CORE.md)
- [集群模块文档](./CLUSTER.md)
- [指标统计文档](./METRICS.md)

