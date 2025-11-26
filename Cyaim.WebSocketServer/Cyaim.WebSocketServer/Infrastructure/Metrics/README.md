# WebSocket 服务器 OpenTelemetry 指标导出

本文档说明如何配置 WebSocket 服务器和集群的指标导出到 OpenTelemetry，使用 OTLP (OpenTelemetry Protocol) 协议导出，由 OpenTelemetry 采集程序统一处理。

## 功能特性

本实现提供了以下指标：

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

## 配置步骤

### 1. 安装 NuGet 包

项目已包含以下 OpenTelemetry 包：
- `OpenTelemetry` (1.9.0)
- `OpenTelemetry.Extensions.Hosting` (1.9.0)
- `OpenTelemetry.Exporter.OpenTelemetryProtocol` (1.9.0)

### 2. 在 Startup.cs 或 Program.cs 中配置

#### .NET 6+ (使用 Program.cs)

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
        metrics
            .AddWebSocketMetricsExporter(options =>
            {
                // 可选：自定义 OTLP 导出器配置
                // options.Endpoint = new Uri("http://localhost:4317"); // gRPC 端点
                // options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc; // 或 HttpProtobuf
            });
    });

var app = builder.Build();

// 其他中间件配置...
app.UseWebSocketServer();

app.Run();
```

#### .NET Core 3.1 / .NET 5 (使用 Startup.cs)

```csharp
using OpenTelemetry.Metrics;
using Cyaim.WebSocketServer.Infrastructure.Metrics;

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        // 添加 WebSocket 指标收集
        services.AddWebSocketMetrics();

        // 配置 OpenTelemetry Metrics
        services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics
                    .AddWebSocketMetricsExporter(options =>
                    {
                        // 可选：自定义 OTLP 导出器配置
                        // options.Endpoint = new Uri("http://localhost:4317");
                        // options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                    });
            });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        // 其他中间件配置...
        app.UseWebSocketServer();
    }
}
```

### 3. 配置集群指标收集（如果使用集群）

如果您的应用使用了集群功能，需要在创建集群管理器后设置指标收集器：

```csharp
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Metrics;

// 创建集群管理器后
var clusterManager = new ClusterManager(...);

// 从依赖注入容器获取指标收集器
var metricsCollector = serviceProvider.GetService<WebSocketMetricsCollector>();
if (metricsCollector != null)
{
    clusterManager.SetMetricsCollector(metricsCollector);
}

// 启动集群
await clusterManager.StartAsync();
```

### 4. 配置 OpenTelemetry 采集器

指标通过 OTLP 协议导出，需要配置 OpenTelemetry Collector 或兼容的采集程序来接收和处理指标。

#### 使用环境变量配置（推荐）

```bash
# OTLP gRPC 端点（默认：http://localhost:4317）
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317

# OTLP 协议类型：grpc 或 http/protobuf（默认：grpc）
export OTEL_EXPORTER_OTLP_PROTOCOL=grpc

# 可选的 HTTP 头
export OTEL_EXPORTER_OTLP_HEADERS="api-key=your-api-key"
```

#### OpenTelemetry Collector 配置示例

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

#### 在代码中配置

如果不想使用环境变量，可以在代码中直接配置：

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddWebSocketMetricsExporter(options =>
            {
                // 配置 OTLP 端点
                options.Endpoint = new Uri("http://localhost:4317");
                
                // 配置协议类型
                options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                // 或使用 HTTP/Protobuf
                // options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                
                // 配置 HTTP 头（仅 HTTP/Protobuf 协议）
                // options.Headers = "api-key=your-api-key";
            });
    });
```

## 指标标签

所有指标都支持以下可选标签：
- `node_id`: 节点 ID（集群模式下）
- `endpoint`: WebSocket 端点路径
- `close_status`: 连接关闭状态
- `error_type`: 错误类型
- `source_node_id`: 源节点 ID（集群消息）
- `target_node_id`: 目标节点 ID（集群消息）

## 示例查询

指标通过 OTLP 导出后，可以在配置的后端（如 Prometheus、Grafana Cloud 等）进行查询。

### Prometheus 查询示例（如果导出到 Prometheus）

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
```

## 注意事项

1. **性能影响**: 指标收集会带来轻微的性能开销，但在大多数场景下可以忽略不计。

2. **内存使用**: 指标数据会占用一定内存，特别是 Histogram 类型指标。

3. **OTLP 协议**: 
   - 支持 gRPC 和 HTTP/Protobuf 两种协议
   - 默认使用 gRPC 协议（端口 4317）
   - HTTP/Protobuf 协议使用端口 4318

4. **环境变量**: OTLP 导出器支持通过环境变量配置，这是推荐的方式，便于在不同环境中灵活配置。

5. **集群模式**: 在集群模式下，每个节点都会独立收集和导出指标。需要确保 OpenTelemetry Collector 能够接收来自所有节点的指标。

6. **网络要求**: 确保应用程序能够访问 OpenTelemetry Collector 的端点。

## 故障排除

### 指标无法导出到 Collector

1. 检查 OpenTelemetry Collector 是否正在运行
2. 检查 OTLP 端点配置是否正确（默认：http://localhost:4317）
3. 检查网络连接和防火墙设置
4. 查看应用程序日志中的 OpenTelemetry 错误信息

### 指标数据不更新

1. 确认 `AddWebSocketMetrics()` 已调用
2. 检查指标收集器是否正确注入到服务中
3. 查看应用日志是否有错误信息
4. 确认 OpenTelemetry Collector 正在接收数据

### 集群指标缺失

1. 确认已调用 `clusterManager.SetMetricsCollector(metricsCollector)`
2. 检查集群是否正常启动
3. 确认节点 ID 是否正确设置
4. 检查每个节点的 OTLP 导出配置

### OTLP 连接问题

1. **gRPC 协议问题**:
   - 确认端口 4317 是否开放
   - 检查 gRPC 客户端是否能够连接

2. **HTTP/Protobuf 协议问题**:
   - 确认端口 4318 是否开放
   - 检查 HTTP 端点是否正确

3. **查看详细日志**:
   - 设置 `OTEL_LOG_LEVEL=debug` 环境变量以获取详细日志
   - 检查 OpenTelemetry Collector 的日志输出

## 更多信息

- [OpenTelemetry .NET 文档](https://opentelemetry.io/docs/instrumentation/net/)
- [OpenTelemetry Collector 文档](https://opentelemetry.io/docs/collector/)
- [OTLP 协议规范](https://opentelemetry.io/docs/specs/otlp/)
- [Prometheus 文档](https://prometheus.io/docs/)
- [Grafana 仪表板示例](https://grafana.com/docs/grafana/latest/dashboards/)

