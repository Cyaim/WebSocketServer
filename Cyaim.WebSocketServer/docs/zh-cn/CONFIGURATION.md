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

// 添加 WebSocket 指标收集
builder.Services.AddWebSocketMetrics();

// 配置 OpenTelemetry Metrics
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

