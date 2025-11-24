# WebSocket 集群测试接口

## 概述

本目录包含了 WebSocket 集群的完整测试接口，提供了所有可测试功能的接口定义和实现。

## 文件说明

- `IWebSocketClusterTestApi.cs` - 接口定义，包含所有可测试的方法
- `WebSocketClusterTestApi.cs` - 接口实现，调用 Dashboard API 控制器

## 接口分类

### 1. 集群管理 (Cluster Management)

- `GetClusterOverviewAsync()` - 获取集群概览
- `GetNodesAsync()` - 获取所有节点列表
- `GetNodeAsync(string nodeId)` - 获取指定节点信息
- `IsLeaderAsync()` - 检查当前节点是否为领导者
- `GetCurrentNodeIdAsync()` - 获取当前节点ID
- `GetOptimalNodeAsync()` - 获取用于负载均衡的最优节点

### 2. 客户端连接管理 (Client Connection Management)

- `GetAllClientsAsync(string? nodeId = null)` - 获取集群中全部客户端连接
- `GetClientsByNodeAsync(string nodeId)` - 获取指定节点的客户端连接
- `GetLocalClientsAsync()` - 获取本地节点的客户端连接
- `GetClientAsync(string connectionId)` - 获取指定连接的信息
- `GetTotalConnectionCountAsync()` - 获取连接总数
- `GetLocalConnectionCountAsync()` - 获取本地连接数
- `GetNodeConnectionCountAsync(string nodeId)` - 获取指定节点的连接数

### 3. 消息发送 (Message Sending)

- `SendTextMessageAsync(string connectionId, string content)` - 发送文本消息
- `SendBinaryMessageAsync(string connectionId, string content)` - 发送二进制消息
- `SendMessageAsync(SendMessageRequest request)` - 发送消息（支持文本和二进制）
- `SendMessagesAsync(List<SendMessageRequest> requests)` - 批量发送消息
- `BroadcastMessageAsync(string content, string messageType = "Text")` - 广播消息到所有连接
- `BroadcastMessageToNodeAsync(string nodeId, string content, string messageType = "Text")` - 广播消息到指定节点

### 4. 统计信息 (Statistics)

- `GetBandwidthStatisticsAsync()` - 获取带宽统计信息
- `GetConnectionStatisticsAsync(string connectionId)` - 获取指定连接的统计信息
- `GetAllConnectionStatisticsAsync()` - 获取所有连接的统计信息

### 5. 连接路由 (Connection Routing)

- `GetConnectionRoutesAsync()` - 获取连接路由表
- `QueryConnectionNodeAsync(string connectionId)` - 查询连接所在的节点
- `GetNodeConnectionIdsAsync(string nodeId)` - 获取指定节点的所有连接ID

### 6. 健康检查 (Health Check)

- `GetClusterHealthAsync()` - 检查集群健康状态
- `GetNodeHealthAsync(string nodeId)` - 检查节点健康状态

## 使用示例

### 基本使用

```csharp
using Cyaim.WebSocketServer.Sample.Dashboard.Interfaces;
using Microsoft.Extensions.Logging;
using System.Net.Http;

// 创建 HTTP 客户端
var httpClient = new HttpClient();
var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<WebSocketClusterTestApi>();

// 创建测试 API 实例
var testApi = new WebSocketClusterTestApi(logger, httpClient);

// 获取集群概览
var overview = await testApi.GetClusterOverviewAsync();
if (overview.Success)
{
    Console.WriteLine($"总节点数: {overview.Data.TotalNodes}");
    Console.WriteLine($"总连接数: {overview.Data.TotalConnections}");
}

// 获取所有客户端
var clients = await testApi.GetAllClientsAsync();
if (clients.Success && clients.Data != null)
{
    foreach (var client in clients.Data)
    {
        Console.WriteLine($"连接ID: {client.ConnectionId}, 节点: {client.NodeId}");
    }
}

// 发送消息
var sendResult = await testApi.SendTextMessageAsync("connection-id", "Hello, World!");
if (sendResult.Success)
{
    Console.WriteLine("消息发送成功");
}
```

### 依赖注入使用

在 `Program.cs` 或 `Startup.cs` 中注册服务：

```csharp
// 注册 HTTP 客户端
builder.Services.AddHttpClient<WebSocketClusterTestApi>(client =>
{
    client.BaseAddress = new Uri("http://localhost:5000");
});

// 或者直接注册服务
builder.Services.AddSingleton<IWebSocketClusterTestApi>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<WebSocketClusterTestApi>>();
    var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient();
    return new WebSocketClusterTestApi(logger, httpClient);
});
```

在控制器或服务中使用：

```csharp
public class TestController : ControllerBase
{
    private readonly IWebSocketClusterTestApi _testApi;

    public TestController(IWebSocketClusterTestApi testApi)
    {
        _testApi = testApi;
    }

    [HttpGet("test/cluster")]
    public async Task<IActionResult> TestCluster()
    {
        var overview = await _testApi.GetClusterOverviewAsync();
        return Ok(overview);
    }
}
```

## 测试场景

### 1. 集群信息测试

```csharp
// 测试获取集群概览
var overview = await testApi.GetClusterOverviewAsync();
Assert.True(overview.Success);
Assert.NotNull(overview.Data);

// 测试获取节点列表
var nodes = await testApi.GetNodesAsync();
Assert.True(nodes.Success);
Assert.NotNull(nodes.Data);
```

### 2. 连接管理测试

```csharp
// 测试获取所有客户端
var clients = await testApi.GetAllClientsAsync();
Assert.True(clients.Success);

// 测试按节点过滤
var nodeClients = await testApi.GetClientsByNodeAsync("node1");
Assert.True(nodeClients.Success);

// 测试获取连接数
var count = await testApi.GetTotalConnectionCountAsync();
Assert.True(count.Success);
```

### 3. 消息发送测试

```csharp
// 测试发送文本消息
var result = await testApi.SendTextMessageAsync("connection-id", "Test message");
Assert.True(result.Success);

// 测试广播消息
var broadcastResult = await testApi.BroadcastMessageAsync("Broadcast message");
Assert.True(broadcastResult.Success);
```

### 4. 路由测试

```csharp
// 测试获取路由表
var routes = await testApi.GetConnectionRoutesAsync();
Assert.True(routes.Success);
Assert.NotNull(routes.Data);

// 测试查询连接所在节点
var nodeId = await testApi.QueryConnectionNodeAsync("connection-id");
Assert.True(nodeId.Success);
```

### 5. 健康检查测试

```csharp
// 测试集群健康状态
var clusterHealth = await testApi.GetClusterHealthAsync();
Assert.True(clusterHealth.Success);
Assert.NotNull(clusterHealth.Data);

// 测试节点健康状态
var nodeHealth = await testApi.GetNodeHealthAsync("node1");
Assert.True(nodeHealth.Success);
```

## 注意事项

1. **API 基础 URL**: 默认使用 `/api/dashboard`，可以通过构造函数参数修改
2. **错误处理**: 所有方法都包含异常处理，返回 `ApiResponse` 对象，包含 `Success` 和 `Error` 属性
3. **异步操作**: 所有方法都是异步的，需要使用 `await` 关键字
4. **空值处理**: 某些方法可能返回 `null`，需要检查 `Success` 属性和 `Data` 是否为 `null`

## 扩展

如果需要添加新的测试接口：

1. 在 `IWebSocketClusterTestApi` 接口中添加方法定义
2. 在 `WebSocketClusterTestApi` 类中实现该方法
3. 确保方法包含适当的错误处理和日志记录

## 许可证

Copyright © Cyaim Studio

