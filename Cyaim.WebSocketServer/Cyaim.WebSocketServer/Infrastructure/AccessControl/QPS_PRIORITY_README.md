# QPS 优先级策略使用说明

## 概述

QPS 优先级策略用于在固定带宽下，根据用户优先级分配带宽资源。通过设置不同等级的名单，可以缩小名单外用户的带宽占用，提高名单内用户的带宽，从而提升服务质量。

## 功能特性

- ✅ **多级优先级支持** - 支持设置多个优先级等级（如：普通用户、VIP、超级VIP等）
- ✅ **动态名单管理** - 支持通过代码动态添加/删除单个或批量IP/连接
- ✅ **CIDR支持** - IP名单支持CIDR表示法（如：192.168.1.0/24）
- ✅ **通道级别配置** - 支持为不同通道设置独立的优先级策略
- ✅ **动态带宽调整** - 根据实际连接数动态调整各优先级的带宽分配
- ✅ **最小带宽保障** - 确保每个连接至少能获得最小带宽保障

## 快速开始

### 1. 配置服务

```csharp
using Cyaim.WebSocketServer.Infrastructure.AccessControl;

var builder = WebApplication.CreateBuilder(args);

// 添加QPS优先级策略服务
builder.Services.AddQpsPriorityPolicy(policy =>
{
    policy.Enabled = true;
    
    // 设置全局总带宽限制（可选）
    policy.GlobalBandwidthLimit = 100 * 1024 * 1024; // 100MB/s
    
    // 配置优先级带宽比例
    // Key: 优先级等级（数字越大优先级越高）
    // Value: 该优先级分配的带宽比例（0.0-1.0）
    policy.PriorityBandwidthRatios = new Dictionary<int, double>
    {
        { 1, 0.1 },  // 普通用户：10%
        { 2, 0.3 },  // VIP用户：30%
        { 3, 0.5 }   // 超级VIP：50%
    };
    
    // 默认优先级（不在任何名单中的用户）
    policy.DefaultPriority = 1;
    policy.DefaultPriorityBandwidthRatio = 0.1; // 10%
    
    // 启用动态带宽调整
    policy.EnableDynamicBandwidthAdjustment = true;
    
    // 设置最小带宽保障（可选）
    policy.MinBandwidthGuarantee = 64 * 1024; // 64KB/s
    
    // 通道级别配置（可选）
    policy.ChannelPolicies = new Dictionary<string, ChannelQpsPriorityPolicy>
    {
        {
            "/ws",
            new ChannelQpsPriorityPolicy
            {
                ChannelBandwidthLimit = 50 * 1024 * 1024, // 50MB/s
                PriorityBandwidthRatios = new Dictionary<int, double>
                {
                    { 1, 0.2 },
                    { 2, 0.4 },
                    { 3, 0.6 }
                }
            }
        }
    };
});
```

### 2. 从配置文件加载

在 `appsettings.json` 中添加配置：

```json
{
  "QpsPriorityPolicy": {
    "Enabled": true,
    "GlobalBandwidthLimit": 104857600,
    "PriorityBandwidthRatios": {
      "1": 0.1,
      "2": 0.3,
      "3": 0.5
    },
    "DefaultPriority": 1,
    "DefaultPriorityBandwidthRatio": 0.1,
    "EnableDynamicBandwidthAdjustment": true,
    "MinBandwidthGuarantee": 65536,
    "ChannelPolicies": {
      "/ws": {
        "ChannelBandwidthLimit": 52428800,
        "PriorityBandwidthRatios": {
          "1": 0.2,
          "2": 0.4,
          "3": 0.6
        }
      }
    }
  }
}
```

```csharp
// 从配置加载
builder.Services.AddQpsPriorityPolicy(builder.Configuration);
```

## 动态管理优先级名单

### 添加IP到优先级名单

```csharp
// 获取优先级名单服务
var priorityListService = app.Services.GetRequiredService<PriorityListService>();

// 添加单个IP
priorityListService.AddIpToPriorityList("192.168.1.100", priority: 3); // 超级VIP

// 批量添加IP
var vipIps = new List<string>
{
    "192.168.1.101",
    "192.168.1.102",
    "10.0.0.0/24" // 支持CIDR
};
priorityListService.AddIpsToPriorityList(vipIps, priority: 2); // VIP
```

### 添加连接到优先级名单

```csharp
// 添加单个连接
priorityListService.AddConnectionToPriorityList("connection-id-123", priority: 3);

// 批量添加连接
var connectionIds = new List<string> { "conn1", "conn2", "conn3" };
priorityListService.AddConnectionsToPriorityList(connectionIds, priority: 2);
```

### 移除优先级名单

```csharp
// 移除单个IP
priorityListService.RemoveIpFromPriorityList("192.168.1.100");

// 批量移除IP
var ipsToRemove = new List<string> { "192.168.1.101", "192.168.1.102" };
priorityListService.RemoveIpsFromPriorityList(ipsToRemove);

// 移除单个连接
priorityListService.RemoveConnectionFromPriorityList("connection-id-123");

// 批量移除连接
var connsToRemove = new List<string> { "conn1", "conn2" };
priorityListService.RemoveConnectionsFromPriorityList(connsToRemove);
```

### 查询优先级

```csharp
// 获取IP的优先级
var ipPriority = priorityListService.GetIpPriority("192.168.1.100", defaultPriority: 1);

// 获取连接的优先级
var connPriority = priorityListService.GetConnectionPriority("connection-id-123", defaultPriority: 1);

// 获取所有优先级名单
var allIpPriorities = priorityListService.GetAllIpPriorities();
var allConnPriorities = priorityListService.GetAllConnectionPriorities();

// 获取特定优先级的名单
var vipIps = priorityListService.GetAllIpPriorities(priority: 2);
```

### 清空所有名单

```csharp
priorityListService.ClearAll();
```

## 完整示例

### 示例1：基本配置和使用

```csharp
using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Middlewares;

var builder = WebApplication.CreateBuilder(args);

// 配置QPS优先级策略
builder.Services.AddQpsPriorityPolicy(policy =>
{
    policy.Enabled = true;
    policy.GlobalBandwidthLimit = 100 * 1024 * 1024; // 100MB/s
    
    policy.PriorityBandwidthRatios = new Dictionary<int, double>
    {
        { 1, 0.1 },  // 普通用户：10%
        { 2, 0.3 },  // VIP：30%
        { 3, 0.5 }   // 超级VIP：50%
    };
    
    policy.DefaultPriority = 1;
    policy.DefaultPriorityBandwidthRatio = 0.1;
});

// 配置WebSocket
builder.Services.ConfigureWebSocketRoute(x =>
{
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", new MvcChannelHandler(4 * 1024).ConnectionEntry }
    };
    x.ApplicationServiceCollection = builder.Services;
});

var app = builder.Build();

// 配置WebSocket选项
var webSocketOptions = new Microsoft.AspNetCore.Builder.WebSocketOptions()
{
    KeepAliveInterval = TimeSpan.FromSeconds(120)
};

app.UseWebSockets(webSocketOptions);
app.UseWebSocketServer();

// 动态添加VIP用户
var priorityListService = app.Services.GetRequiredService<PriorityListService>();
priorityListService.AddIpToPriorityList("192.168.1.100", priority: 3);
priorityListService.AddIpsToPriorityList(new[] { "10.0.0.0/24" }, priority: 2);

app.Run();
```

### 示例2：通过API动态管理名单

```csharp
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class PriorityListController : ControllerBase
{
    private readonly PriorityListService _priorityListService;

    public PriorityListController(PriorityListService priorityListService)
    {
        _priorityListService = priorityListService;
    }

    // 添加IP到优先级名单
    [HttpPost("ip")]
    public IActionResult AddIp([FromBody] AddIpRequest request)
    {
        var success = _priorityListService.AddIpToPriorityList(request.IpAddress, request.Priority);
        return success ? Ok() : BadRequest();
    }

    // 批量添加IP
    [HttpPost("ips")]
    public IActionResult AddIps([FromBody] AddIpsRequest request)
    {
        var count = _priorityListService.AddIpsToPriorityList(request.IpAddresses, request.Priority);
        return Ok(new { SuccessCount = count });
    }

    // 移除IP
    [HttpDelete("ip/{ipAddress}")]
    public IActionResult RemoveIp(string ipAddress)
    {
        var success = _priorityListService.RemoveIpFromPriorityList(ipAddress);
        return success ? Ok() : NotFound();
    }

    // 获取所有优先级名单
    [HttpGet("ips")]
    public IActionResult GetAllIps([FromQuery] int? priority = null)
    {
        var priorities = _priorityListService.GetAllIpPriorities(priority);
        return Ok(priorities);
    }
}

public class AddIpRequest
{
    public string IpAddress { get; set; }
    public int Priority { get; set; }
}

public class AddIpsRequest
{
    public List<string> IpAddresses { get; set; }
    public int Priority { get; set; }
}
```

## 优先级策略说明

### 优先级等级

- **数字越大，优先级越高**
- 例如：1=普通用户，2=VIP，3=超级VIP

### 带宽分配机制

1. **固定比例分配**：根据配置的带宽比例，为每个优先级分配固定比例的带宽
2. **动态调整**：如果启用了动态调整，会根据实际连接数动态调整各优先级的带宽分配
3. **最小保障**：即使优先级较低，也保证每个连接至少能获得最小带宽保障

### 优先级匹配规则

- 如果IP和连接都在优先级名单中，取**较高优先级**
- 如果只有IP在名单中，使用IP的优先级
- 如果只有连接在名单中，使用连接的优先级
- 如果都不在名单中，使用默认优先级

### CIDR支持

IP名单支持CIDR表示法，例如：
- `192.168.1.0/24` - 匹配 192.168.1.0 到 192.168.1.255
- `10.0.0.0/8` - 匹配 10.0.0.0 到 10.255.255.255

## 最佳实践

1. **合理设置优先级比例**
   - 确保所有优先级比例之和不超过1.0
   - 为高优先级用户分配更多带宽，但也要保证低优先级用户的基本需求

2. **使用最小带宽保障**
   - 设置 `MinBandwidthGuarantee` 确保每个连接至少能正常工作
   - 避免低优先级用户完全无法使用服务

3. **启用动态调整**
   - 启用 `EnableDynamicBandwidthAdjustment` 可以根据实际连接数动态调整
   - 当高优先级用户较少时，可以借用更多带宽

4. **通道级别配置**
   - 为不同通道设置独立的优先级策略
   - 可以根据业务需求为不同通道分配不同的带宽资源

5. **定期清理名单**
   - 定期清理不再需要的优先级名单
   - 避免名单过大影响性能

## 注意事项

1. **带宽比例总和**：所有优先级比例之和应小于等于1.0，否则可能导致带宽超分配
2. **性能考虑**：CIDR匹配需要遍历所有CIDR规则，如果规则很多可能影响性能
3. **线程安全**：所有名单操作都是线程安全的，可以在多线程环境下使用
4. **持久化**：当前实现是内存中的，重启后会丢失。如需持久化，需要自行实现存储逻辑

## 与带宽限制策略的集成

QPS优先级策略与现有的带宽限制策略（`BandwidthLimitPolicy`）可以同时使用：

- QPS优先级策略：根据优先级分配带宽
- 带宽限制策略：设置具体的带宽限制值

两者会取**更严格的限制**，确保带宽资源得到合理分配。

