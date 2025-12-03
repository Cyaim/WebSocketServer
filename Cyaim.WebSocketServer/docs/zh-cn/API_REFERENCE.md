# API 参考文档

本文档提供 Cyaim.WebSocketServer 的完整 API 参考。

## 目录

- [核心 API](#核心-api)
- [集群 API](#集群-api)
- [Dashboard API](#dashboard-api)
- [配置 API](#配置-api)

## 核心 API

### WebSocketRouteOption

```csharp
public class WebSocketRouteOption
{
    public Dictionary<string, WebSocketChannelHandler> WebSocketChannels { get; set; }
    public IServiceCollection ApplicationServiceCollection { get; set; }
    public BandwidthLimitPolicy BandwidthLimitPolicy { get; set; }
    public bool EnableCluster { get; set; }
}
```

### MvcChannelHandler

```csharp
public class MvcChannelHandler : IWebSocketHandler
{
    public MvcChannelHandler(int receiveBufferSize = 4 * 1024, int sendBufferSize = 4 * 1024);
    
    public int ReceiveTextBufferSize { get; set; }
    public int ReceiveBinaryBufferSize { get; set; }
    public int SendTextBufferSize { get; set; }
    public int SendBinaryBufferSize { get; set; }
    public TimeSpan ResponseSendTimeout { get; set; }
    
    public static ConcurrentDictionary<string, WebSocket> Clients { get; set; }
    
    public Task ConnectionEntry(HttpContext context, WebSocket webSocket);
}
```

### IWebSocketHandler

```csharp
public interface IWebSocketHandler
{
    WebSocketHandlerMetadata Metadata { get; }
    Task ConnectionEntry(HttpContext context, WebSocket webSocket);
}
```

## 集群 API

### ClusterManager

```csharp
public class ClusterManager
{
    public ClusterManager(
        ILogger<ClusterManager> logger,
        IClusterTransport transport,
        RaftNode raftNode,
        ClusterRouter router,
        string nodeId,
        ClusterOption clusterOption);
    
    public Task StartAsync();
    public Task StopAsync();
    public Task ShutdownAsync(bool force = false);
    
    public Task RegisterConnectionAsync(string connectionId, string endpoint = null, 
        string remoteIpAddress = null, int remotePort = 0);
    public Task UnregisterConnectionAsync(string connectionId);
    
    public Task<bool> RouteMessageAsync(string connectionId, byte[] data, int messageType);
    
    // 向多个连接路由消息（支持跨节点）
    // Route WebSocket message to multiple connections (supports cross-node)
    public Task<Dictionary<string, bool>> RouteMessagesAsync(
        IEnumerable<string> connectionIds, 
        byte[] data, 
        int messageType);
    
    public void SetConnectionProvider(IWebSocketConnectionProvider provider);
    public void SetMetricsCollector(WebSocketMetricsCollector metricsCollector);
    
    public ConnectionMetadata GetConnectionMetadata(string connectionId);
    public string GetConnectionEndpoint(string connectionId);
}
```

### ClusterRouter

```csharp
public class ClusterRouter
{
    public ClusterRouter(
        ILogger<ClusterRouter> logger,
        IClusterTransport transport,
        RaftNode raftNode,
        string nodeId);
    
    public IReadOnlyDictionary<string, string> ConnectionRoutes { get; }
    
    public Task RegisterConnectionAsync(string connectionId, string endpoint = null,
        string remoteIpAddress = null, int remotePort = 0);
    public Task UnregisterConnectionAsync(string connectionId);
    
    public Task<bool> RouteMessageAsync(string connectionId, byte[] data, int messageType);
    
    public string GetConnectionNodeId(string connectionId);
    
    public void SetConnectionProvider(IWebSocketConnectionProvider provider);
    public void SetMetricsCollector(WebSocketMetricsCollector metricsCollector);
}
```

### RaftNode

```csharp
public class RaftNode
{
    public RaftNode(
        ILogger<RaftNode> logger,
        IClusterTransport transport,
        string nodeId);
    
    public string NodeId { get; }
    public RaftState State { get; }
    public long CurrentTerm { get; }
    public string LeaderId { get; }
    
    public Task StartAsync();
    public Task StopAsync();
}
```

### IClusterTransport

```csharp
public interface IClusterTransport
{
    event Action<string, ClusterMessage> MessageReceived;
    event Action<string> NodeDisconnected;
    
    Task StartAsync();
    Task StopAsync();
    
    Task SendAsync(string nodeId, ClusterMessage message);
    
    bool IsNodeConnected(string nodeId);
    
    Task<long> MeasureLatencyAsync(string nodeId);
    Task<int> GetNetworkQualityAsync(string nodeId);
}
```

### ClusterOption

```csharp
public class ClusterOption
{
    public string NodeId { get; set; }
    public string NodeAddress { get; set; }
    public int NodePort { get; set; }
    public string TransportType { get; set; }
    public string[] Nodes { get; set; }
    public string RedisConnectionString { get; set; }
    public string RabbitMQConnectionString { get; set; }
    public string ChannelName { get; set; }
}
```

## Dashboard API

### 集群接口

#### GET /ws_server/api/dashboard/cluster/overview

获取集群概览。

**响应**:
```json
{
  "success": true,
  "data": {
    "totalNodes": 3,
    "connectedNodes": 3,
    "totalConnections": 100,
    "localConnections": 30,
    "currentNodeId": "node1",
    "isCurrentNodeLeader": true,
    "nodes": [...]
  }
}
```

#### GET /ws_server/api/dashboard/cluster/nodes

获取所有节点。

#### GET /ws_server/api/dashboard/cluster/nodes/{nodeId}

获取指定节点信息。

### 客户端接口

#### GET /ws_server/api/client

获取所有客户端。

**查询参数**:
- `nodeId` (可选): 节点 ID 过滤器

**响应**:
```json
{
  "success": true,
  "data": [
    {
      "connectionId": "connection-id",
      "nodeId": "node1",
      "remoteIpAddress": "127.0.0.1",
      "remotePort": 12345,
      "state": "Open",
      "connectedAt": "2024-01-01T00:00:00Z",
      "endpoint": "/ws",
      "bytesSent": 1024,
      "bytesReceived": 2048,
      "messagesSent": 10,
      "messagesReceived": 20
    }
  ]
}
```

#### GET /ws_server/api/client/count

获取连接数统计。

**响应**:
```json
{
  "success": true,
  "data": {
    "total": 100,
    "local": 30
  }
}
```

### 消息接口

#### POST /ws_server/api/messages/send

发送消息。

**请求体**:
```json
{
  "connectionId": "connection-id",
  "content": "Hello, World!",
  "messageType": "Text"
}
```

**响应**:
```json
{
  "success": true,
  "data": true
}
```

#### POST /ws_server/api/messages/broadcast

广播消息。

**请求体**:
```json
{
  "content": "Broadcast message",
  "messageType": "Text"
}
```

**响应**:
```json
{
  "success": true,
  "data": 100
}
```

### 路由接口

#### GET /ws_server/api/routes

获取路由表。

**响应**:
```json
{
  "success": true,
  "data": {
    "connection-id-1": "node1",
    "connection-id-2": "node2"
  }
}
```

### 统计接口

#### GET /ws_server/api/statistics/bandwidth

获取带宽统计。

**响应**:
```json
{
  "success": true,
  "data": {
    "totalBytesSent": 1024000,
    "totalBytesReceived": 2048000,
    "totalMessagesSent": 1000,
    "totalMessagesReceived": 2000,
    "bytesSentPerSecond": 1024,
    "bytesReceivedPerSecond": 2048,
    "messagesSentPerSecond": 10,
    "messagesReceivedPerSecond": 20
  }
}
```

#### GET /ws_server/api/statistics/connections

获取连接统计。

## 配置 API

### WebSocketRouteServiceCollectionExtensions

```csharp
public static class WebSocketRouteServiceCollectionExtensions
{
    public static IServiceCollection ConfigureWebSocketRoute(
        this IServiceCollection services,
        Action<WebSocketRouteOption> configure);
}
```

### WebSocketRouteMiddlewareExtensions

```csharp
public static class WebSocketRouteMiddlewareExtensions
{
    public static IApplicationBuilder UseWebSocketServer(
        this IApplicationBuilder app);
}
```

### DashboardMiddlewareExtensions

```csharp
public static class DashboardMiddlewareExtensions
{
    public static IServiceCollection AddWebSocketDashboard(
        this IServiceCollection services);
    
    public static IApplicationBuilder UseWebSocketDashboard(
        this IApplicationBuilder app,
        string pathPrefix = "/dashboard");
}
```

### WebSocketMetricsExtensions

```csharp
public static class WebSocketMetricsExtensions
{
    public static IServiceCollection AddWebSocketMetrics(
        this IServiceCollection services);
    
    public static MeterProviderBuilder AddWebSocketMetricsExporter(
        this MeterProviderBuilder builder,
        Action<OtlpExporterOptions> configure = null);
}
```

## 数据模型

### MvcRequestScheme

```csharp
public class MvcRequestScheme
{
    public string Target { get; set; }
    public object Body { get; set; }
}
```

### MvcResponseScheme

```csharp
public class MvcResponseScheme
{
    public string Target { get; set; }
    public int Status { get; set; }
    public string Msg { get; set; }
    public long RequestTime { get; set; }
    public long CompleteTime { get; set; }
    public object Body { get; set; }
}
```

### ClusterMessage

```csharp
public class ClusterMessage
{
    public string Type { get; set; }
    public string FromNodeId { get; set; }
    public string ToNodeId { get; set; }
    public object Data { get; set; }
    public string MessageId { get; set; }
}
```

### ConnectionMetadata

```csharp
public class ConnectionMetadata
{
    public string RemoteIpAddress { get; set; }
    public int RemotePort { get; set; }
    public DateTime ConnectedAt { get; set; }
}
```

## 相关文档

- [核心库文档](./CORE.md)
- [集群模块文档](./CLUSTER.md)
- [Dashboard 文档](./DASHBOARD.md)
- [配置指南](./CONFIGURATION.md)

