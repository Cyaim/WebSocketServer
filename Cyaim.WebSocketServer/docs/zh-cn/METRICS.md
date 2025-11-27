# 指标统计文档

本文档详细介绍 Cyaim.WebSocketServer 的指标统计功能，包括 OpenTelemetry 集成和性能监控。

## 目录

- [概述](#概述)
- [支持的指标](#支持的指标)
- [快速开始](#快速开始)
- [OpenTelemetry 集成](#opentelemetry-集成)
- [指标说明](#指标说明)
- [查询示例](#查询示例)
- [最佳实践](#最佳实践)

## 概述

Cyaim.WebSocketServer 提供了完整的指标统计功能，支持：

- ✅ **实时统计** - 连接数、消息数、带宽等
- ✅ **OpenTelemetry** - 标准指标导出
- ✅ **性能分析** - 详细的性能指标
- ✅ **集群指标** - 集群级别的统计信息

## 支持的指标

### WebSocket 连接指标

- `websocket_connections_total`: WebSocket 连接总数（Counter）
- `websocket_connections_closed_total`: WebSocket 连接关闭总数（Counter）
- `websocket_connections_active`: 当前活跃的 WebSocket 连接数（UpDownCounter）

### 消息指标

- `websocket_messages_received_total`: 接收的消息总数（Counter）
- `websocket_messages_sent_total`: 发送的消息总数（Counter）
- `websocket_message_size_received_bytes`: 接收消息的大小分布（Histogram）
- `websocket_message_size_sent_bytes`: 发送消息的大小分布（Histogram）

### 字节指标

- `websocket_bytes_received_total`: 接收的总字节数（Counter）
- `websocket_bytes_sent_total`: 发送的总字节数（Counter）

### 集群指标

- `websocket_cluster_messages_forwarded_total`: 集群中转发的消息总数（Counter）
- `websocket_cluster_messages_received_total`: 从集群接收的消息总数（Counter）
- `websocket_cluster_nodes_connected`: 当前连接的集群节点数（UpDownCounter）
- `websocket_cluster_connections_total`: 集群中的连接总数（UpDownCounter）

### 错误指标

- `websocket_errors_total`: 错误总数（Counter）

## 快速开始

### 1. 安装依赖

项目已包含 OpenTelemetry 包，无需额外安装。

### 2. 配置指标收集

```csharp
using OpenTelemetry.Metrics;
using Cyaim.WebSocketServer.Infrastructure.Metrics;

var builder = WebApplication.CreateBuilder(args);

// 添加 WebSocket 指标收集
builder.Services.AddWebSocketMetrics();

// 配置 OpenTelemetry Metrics
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddWebSocketMetricsExporter();
    });
```

### 3. 配置集群指标（如果使用集群）

```csharp
// 创建集群管理器后
var clusterManager = new ClusterManager(...);

// 从依赖注入容器获取指标收集器
var metricsCollector = serviceProvider.GetService<WebSocketMetricsCollector>();
if (metricsCollector != null)
{
    clusterManager.SetMetricsCollector(metricsCollector);
}
```

## OpenTelemetry 集成

### 配置 OTLP 导出器

```csharp
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
                
                // HTTP 头（仅 HTTP/Protobuf 协议）
                // options.Headers = "api-key=your-api-key";
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

### OpenTelemetry Collector 配置

创建 `otel-collector-config.yaml`:

```yaml
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
      http:
        endpoint: 0.0.0.0:4318

exporters:
  prometheus:
    endpoint: "0.0.0.0:8889"
  # 或者导出到其他后端
  # otlp:
  #   endpoint: "your-backend:4317"

service:
  pipelines:
    metrics:
      receivers: [otlp]
      exporters: [prometheus]
```

## 指标说明

### 指标标签

所有指标都支持以下可选标签：

- `node_id`: 节点 ID（集群模式下）
- `endpoint`: WebSocket 端点路径
- `close_status`: 连接关闭状态
- `error_type`: 错误类型
- `source_node_id`: 源节点 ID（集群消息）
- `target_node_id`: 目标节点 ID（集群消息）

### 指标类型

- **Counter**: 只增不减的计数器
- **UpDownCounter**: 可增可减的计数器
- **Histogram**: 直方图，用于统计分布

## 查询示例

### Prometheus 查询

```promql
# 当前活跃连接数
websocket_connections_active

# 每秒接收的消息数
rate(websocket_messages_received_total[5m])

# 每秒发送的字节数
rate(websocket_bytes_sent_total[5m])

# 平均消息大小
rate(websocket_bytes_received_total[5m]) / rate(websocket_messages_received_total[5m])

# 集群总连接数
websocket_cluster_connections_total

# 按节点分组的连接数
sum by (node_id) (websocket_connections_active)

# 错误率
rate(websocket_errors_total[5m]) / rate(websocket_messages_received_total[5m])
```

### Grafana 仪表板

可以创建 Grafana 仪表板来可视化指标：

1. **连接监控面板**
   - 当前活跃连接数
   - 连接总数趋势
   - 按节点分组的连接数

2. **消息监控面板**
   - 消息发送/接收速率
   - 消息大小分布
   - 消息类型分布

3. **带宽监控面板**
   - 发送/接收带宽
   - 带宽使用趋势
   - 按节点分组的带宽

4. **集群监控面板**
   - 集群节点状态
   - 集群消息转发量
   - 集群连接分布

## 最佳实践

### 1. 指标收集

- 在生产环境中启用指标收集
- 配置合适的采样率
- 监控指标收集的性能影响

### 2. 指标导出

- 使用 OpenTelemetry Collector 统一收集
- 配置合适的导出间隔
- 确保网络连接稳定

### 3. 监控告警

- 设置连接数告警阈值
- 监控错误率
- 监控带宽使用情况

### 4. 性能优化

- 指标收集会带来轻微性能开销
- 在大量连接时考虑采样
- 合理配置 Histogram 的桶大小

## 故障排除

### 指标无法导出

1. 检查 OpenTelemetry Collector 是否运行
2. 检查 OTLP 端点配置
3. 检查网络连接和防火墙
4. 查看应用程序日志

### 指标数据不更新

1. 确认 `AddWebSocketMetrics()` 已调用
2. 检查指标收集器是否正确注入
3. 查看应用日志是否有错误
4. 确认 OpenTelemetry Collector 正在接收数据

### 集群指标缺失

1. 确认已调用 `clusterManager.SetMetricsCollector()`
2. 检查集群是否正常启动
3. 确认节点 ID 是否正确设置
4. 检查每个节点的 OTLP 导出配置

## 相关文档

- [配置指南](./CONFIGURATION.md)
- [集群模块文档](./CLUSTER.md)
- [Dashboard 文档](./DASHBOARD.md)

