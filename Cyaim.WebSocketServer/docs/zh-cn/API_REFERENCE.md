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

### WebSocketManager

WebSocketManager 提供了统一的发送方法，自动适配单机和集群模式。

#### 统一发送方法（单机和集群）

```csharp
public static class WebSocketManager
{
    // 发送字节数组到单个连接
    public static async Task<bool> SendAsync(
        string connectionId,
        byte[] data,
        WebSocketMessageType messageType = WebSocketMessageType.Text);
    
    // 发送字节数组到多个连接
    public static async Task<Dictionary<string, bool>> SendAsync(
        IEnumerable<string> connectionIds,
        byte[] data,
        WebSocketMessageType messageType = WebSocketMessageType.Text);
    
    // 发送文本到单个连接
    public static async Task<bool> SendAsync(
        string connectionId,
        string text,
        Encoding encoding = null);
    
    // 发送文本到多个连接
    public static async Task<Dictionary<string, bool>> SendAsync(
        IEnumerable<string> connectionIds,
        string text,
        Encoding encoding = null);
    
    // 发送 JSON 对象到单个连接
    public static async Task<bool> SendAsync<T>(
        string connectionId,
        T data,
        JsonSerializerOptions options = null,
        Encoding encoding = null);
    
    // 发送 JSON 对象到多个连接
    public static async Task<Dictionary<string, bool>> SendAsync<T>(
        IEnumerable<string> connectionIds,
        T data,
        JsonSerializerOptions options = null,
        Encoding encoding = null);
}
```

#### 扩展方法（便于使用）

```csharp
// 字节数组扩展方法
public static async Task<bool> SendAsync(
    this byte[] data,
    string connectionId,
    WebSocketMessageType messageType = WebSocketMessageType.Text);

public static async Task<Dictionary<string, bool>> SendAsync(
    this byte[] data,
    IEnumerable<string> connectionIds,
    WebSocketMessageType messageType = WebSocketMessageType.Text);

// 文本扩展方法
public static async Task<bool> SendTextAsync(
    this string text,
    string connectionId,
    Encoding encoding = null);

public static async Task<Dictionary<string, bool>> SendTextAsync(
    this string text,
    IEnumerable<string> connectionIds,
    Encoding encoding = null);

// JSON 对象扩展方法
public static async Task<bool> SendJsonAsync<T>(
    this T data,
    string connectionId,
    JsonSerializerOptions options = null,
    Encoding encoding = null)
    where T : class;

public static async Task<Dictionary<string, bool>> SendJsonAsync<T>(
    this T data,
    IEnumerable<string> connectionIds,
    JsonSerializerOptions options = null,
    Encoding encoding = null)
    where T : class;

// 流扩展方法（支持大文件、网络流等）
public static async Task<bool> SendAsync(
    this Stream stream,
    string connectionId,
    WebSocketMessageType messageType = WebSocketMessageType.Binary,
    int chunkSize = 64 * 1024,
    CancellationToken cancellationToken = default);

public static async Task<Dictionary<string, bool>> SendAsync(
    this Stream stream,
    IEnumerable<string> connectionIds,
    WebSocketMessageType messageType = WebSocketMessageType.Binary,
    int chunkSize = 64 * 1024,
    CancellationToken cancellationToken = default);
```

#### 使用示例

```csharp
// 使用扩展方法发送文本
await "Hello World".SendTextAsync("connectionId");

// 使用扩展方法发送 JSON 对象
var user = new { Id = 1, Name = "Alice" };
await user.SendJsonAsync("connectionId");

// 使用扩展方法发送流
using var fileStream = File.OpenRead("largefile.bin");
await fileStream.SendAsync("connectionId");

// 批量发送
var connectionIds = new[] { "conn1", "conn2", "conn3" };
await "Hello".SendTextAsync(connectionIds);
await fileStream.SendAsync(connectionIds);
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
    
    // 流转发方法 - 支持大文件、网络流等
    // Stream routing methods - supports large files, network streams, etc.
    
    // 向单个连接路由流（支持分块传输）
    // Route stream to single connection (supports chunked transmission)
    public Task<bool> RouteStreamAsync(
        string connectionId,
        Stream stream,
        WebSocketMessageType messageType = WebSocketMessageType.Binary,
        int chunkSize = 64 * 1024,
        CancellationToken cancellationToken = default);
    
    // 向多个连接路由流（支持跨节点、分块传输）
    // Route stream to multiple connections (supports cross-node, chunked transmission)
    public Task<Dictionary<string, bool>> RouteStreamsAsync(
        IEnumerable<string> connectionIds,
        Stream stream,
        WebSocketMessageType messageType = WebSocketMessageType.Binary,
        int chunkSize = 64 * 1024,
        CancellationToken cancellationToken = default);
    
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

### ForwardWebSocketStream

```csharp
public class ForwardWebSocketStream
{
    public string ConnectionId { get; set; }
    public string TargetNodeId { get; set; }
    public string StreamId { get; set; }  // 流ID，用于块关联
    public int ChunkIndex { get; set; }    // 块索引（从0开始）
    public bool IsLastChunk { get; set; }  // 是否是最后一块
    public byte[] Data { get; set; }       // 块数据
    public int MessageType { get; set; }   // WebSocket消息类型
    public long? TotalSize { get; set; }    // 流总大小（可选，用于进度跟踪）
    public string Endpoint { get; set; }    // 端点
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

