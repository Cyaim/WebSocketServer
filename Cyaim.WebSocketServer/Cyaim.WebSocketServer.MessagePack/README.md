# Cyaim.WebSocketServer.MessagePack

MessagePack 二进制协议扩展，为 WebSocketServer 提供高性能的二进制序列化支持。

## 特性

- ✅ **高性能二进制协议** - 使用 MessagePack 进行序列化，比 JSON 更小更快
- ✅ **完全兼容** - 与 MvcChannelHandler 功能完全兼容
- ✅ **类型安全** - 强类型参数绑定和响应
- ✅ **管道支持** - 支持所有请求处理管道阶段
- ✅ **集群支持** - 完全支持集群功能

## 安装

```bash
dotnet add package Cyaim.WebSocketServer.MessagePack
```

## 快速开始

### 1. 配置服务

```csharp
using Cyaim.WebSocketServer.MessagePack;
using Cyaim.WebSocketServer.Infrastructure.Configures;

var builder = WebApplication.CreateBuilder(args);

// 配置 WebSocket 路由
builder.Services.ConfigureWebSocketRoute(x =>
{
    // 定义通道 - 使用 MessagePackChannelHandler
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws-mp", new MessagePackChannelHandler(4 * 1024).ConnectionEntry }
    };
    x.ApplicationServiceCollection = builder.Services;
});
```

### 2. 配置中间件

```csharp
var app = builder.Build();

// 配置 WebSocket 选项
var webSocketOptions = new WebSocketOptions()
{
    KeepAliveInterval = TimeSpan.FromSeconds(120)
};

app.UseWebSockets(webSocketOptions);
app.UseWebSocketServer();
```

### 3. 创建控制器

```csharp
using Cyaim.WebSocketServer.Infrastructure.Attributes;

public class TestController
{
    // 使用 [WebSocket] 特性标记端点，method 参数指定方法名称（不是完整路径）
    // 系统会自动构建完整路径：ControllerName（去掉Controller后缀）.MethodName
    // 例如：TestController + [WebSocket("echo")] = "test.echo"
    [WebSocket("echo")]
    public string Echo(string message)
    {
        return $"Echo: {message}";
    }
    
    // 也可以不指定 method 参数，默认使用方法名
    // 例如：TestController + GetTime() = "test.gettime"
    [WebSocket]
    public string GetTime()
    {
        return DateTime.Now.ToString();
    }
}
```

**注意**：

- `[WebSocket]` 特性用于服务端标记端点
- `[WebSocketEndpoint]` 是客户端使用的特性，服务端不需要
- `[WebSocket("method")]` 的 `method` 参数是**方法名称**，不是完整路径
- 完整 endpoint 路径由系统自动构建：`{ControllerName（去掉Controller后缀）}.{MethodName}`
- 例如：`TestController` 类中的 `[WebSocket("echo")]` 方法，完整路径为 `"test.echo"`

## 与 JSON 版本的对比

### 性能优势

- **更小的消息大小** - MessagePack 通常比 JSON 小 30-50%
- **更快的序列化/反序列化** - 二进制格式处理更快
- **更低的 CPU 使用** - 无需解析文本格式

### 使用场景

- **高频消息传输** - 需要处理大量消息的场景
- **带宽敏感应用** - 移动网络或低带宽环境
- **实时游戏/交易** - 需要最小延迟的应用

## 协议格式

### 请求格式 (MessagePackRequestScheme)

```csharp
[MessagePackObject]
public class MessagePackRequestScheme
{
    [Key(0)]
    public string Id { get; set; }      // 请求 ID
    
    [Key(1)]
    public string Target { get; set; }  // 目标端点
    
    [Key(2)]
    public object Body { get; set; }     // 请求体
}
```

### 响应格式 (MessagePackResponseScheme)

```csharp
[MessagePackObject]
public class MessagePackResponseScheme
{
    [Key(0)]
    public int Status { get; set; }        // 状态码 (0=成功, 1=错误, 2=未找到)
    
    [Key(1)]
    public string Msg { get; set; }        // 消息
    
    [Key(2)]
    public long RequestTime { get; set; }   // 请求时间
    
    [Key(3)]
    public long CompleteTime { get; set; }  // 完成时间
    
    [Key(4)]
    public string Id { get; set; }          // 请求 ID
    
    [Key(5)]
    public string Target { get; set; }      // 目标端点
    
    [Key(6)]
    public object Body { get; set; }        // 响应体
}
```

## 集群支持

### 使用 MessagePack 发送消息到集群

MessagePack 扩展提供了便捷的方法，可以直接通过集群管理器发送 MessagePack 序列化的对象：

```csharp
using Cyaim.WebSocketServer.MessagePack;
using Cyaim.WebSocketServer.Infrastructure.Cluster;

// 获取集群管理器
var clusterManager = GlobalClusterCenter.ClusterManager;

// 定义要发送的对象
var user = new { Id = 1, Name = "Alice", Age = 30 };

// 向单个连接发送 MessagePack 序列化的对象（支持跨节点）
await clusterManager.RouteMessagePackAsync("connection-1", user);

// 向多个连接批量发送 MessagePack 序列化的对象（支持跨节点）
var connectionIds = new[] { "connection-1", "connection-2", "connection-3" };
var results = await clusterManager.RouteMessagePacksAsync(connectionIds, user);

// 检查发送结果
foreach (var result in results)
{
    if (result.Value)
    {
        Console.WriteLine($"成功发送到连接 {result.Key}");
    }
    else
    {
        Console.WriteLine($"发送到连接 {result.Key} 失败");
    }
}

// 使用自定义 MessagePack 选项
var options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);
await clusterManager.RouteMessagePackAsync("connection-1", user, options);
```

### 扩展方法说明

- **`RouteMessagePackAsync<T>`**: 向单个连接发送 MessagePack 序列化的对象
- **`RouteMessagePacksAsync<T>`**: 向多个连接批量发送 MessagePack 序列化的对象

这些方法会自动：

- 将对象序列化为 MessagePack 二进制格式
- 通过集群路由系统发送到目标连接（支持跨节点）
- 使用 `WebSocketMessageType.Binary` 消息类型

## 注意事项

1. **消息类型** - MessagePackChannelHandler 只处理二进制消息 (`WebSocketMessageType.Binary`)
2. **兼容性** - 内部仍使用 JSON 进行参数绑定，以保持与现有端点的兼容性
3. **客户端** - 需要支持 MessagePack 的客户端库
4. **集群扩展** - 使用集群扩展方法需要引用 `Cyaim.WebSocketServer.Infrastructure.Cluster` 命名空间

## 许可证

与主项目相同的许可证。
