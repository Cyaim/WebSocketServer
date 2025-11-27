# Access Control Module / 访问控制模块

This module provides IP and geographic location-based access control for WebSocket connections.

本模块为 WebSocket 连接提供基于 IP 和地理位置的访问控制。

## Features / 功能特性

- ✅ **IP Whitelist/Blacklist** - Support CIDR notation / 支持 CIDR 表示法
- ✅ **Country-based Filtering** - Filter by ISO 3166-1 alpha-2 country codes / 按 ISO 3166-1 alpha-2 国家代码过滤
- ✅ **City-based Filtering** - Filter by city name / 按城市名称过滤
- ✅ **Region-based Filtering** - Filter by region/state name / 按地区/州名称过滤
- ✅ **Geographic Location Lookup** - Automatic IP geolocation / 自动 IP 地理位置查询
- ✅ **Caching** - Cache geographic location results / 缓存地理位置查询结果

## Quick Start / 快速开始

### 1. Configure Services / 配置服务

```csharp
using Cyaim.WebSocketServer.Infrastructure.AccessControl;

var builder = WebApplication.CreateBuilder(args);

// Add access control service / 添加访问控制服务
builder.Services.AddAccessControl(policy =>
{
    policy.Enabled = true;
    
    // IP whitelist / IP 白名单
    policy.IpWhitelist = new List<string>
    {
        "192.168.1.0/24",  // CIDR notation / CIDR 表示法
        "10.0.0.1"         // Single IP / 单个 IP
    };
    
    // IP blacklist / IP 黑名单
    policy.IpBlacklist = new List<string>
    {
        "203.0.113.0/24"
    };
    
    // Country whitelist / 国家白名单
    policy.CountryWhitelist = new List<string> { "CN", "US" };
    
    // Country blacklist / 国家黑名单
    policy.CountryBlacklist = new List<string> { "XX" };
    
    // City whitelist / 城市白名单
    policy.CityWhitelist = new List<string>
    {
        "CN:Beijing",
        "US:New York"
    };
    
    // Region whitelist / 地区白名单
    policy.RegionWhitelist = new List<string>
    {
        "CN:Beijing",
        "US:California"
    };
    
    // Enable geographic location lookup / 启用地理位置查询
    policy.EnableGeoLocationLookup = true;
    policy.GeoLocationCacheSeconds = 3600; // 1 hour / 1 小时
});

// Register geographic location provider (optional) / 注册地理位置提供者（可选）
builder.Services.AddGeoLocationProvider<DefaultGeoLocationProvider>();
```

### 2. Configuration from appsettings.json / 从 appsettings.json 配置

```json
{
  "AccessControlPolicy": {
    "Enabled": true,
    "IpWhitelist": [
      "192.168.1.0/24",
      "10.0.0.1"
    ],
    "IpBlacklist": [
      "203.0.113.0/24"
    ],
    "CountryWhitelist": ["CN", "US"],
    "CountryBlacklist": ["XX"],
    "CityWhitelist": [
      "CN:Beijing",
      "US:New York"
    ],
    "RegionWhitelist": [
      "CN:Beijing",
      "US:California"
    ],
    "EnableGeoLocationLookup": true,
    "GeoLocationCacheSeconds": 3600,
    "DeniedAction": "CloseConnection",
    "DenialMessage": "Access denied"
  }
}
```

```csharp
// Load from configuration / 从配置加载
builder.Services.AddAccessControl(builder.Configuration);
```

## Access Control Logic / 访问控制逻辑

1. **If IP whitelist is configured** / 如果配置了 IP 白名单：
   - Only IPs in the whitelist are allowed / 只允许白名单中的 IP
   - All other IPs are denied / 其他所有 IP 都被拒绝

2. **If IP whitelist is not configured** / 如果未配置 IP 白名单：
   - Check IP blacklist / 检查 IP 黑名单
   - If IP is in blacklist, deny / 如果 IP 在黑名单中，拒绝
   - Otherwise, check geographic location (if enabled) / 否则，检查地理位置（如果启用）

3. **Geographic location check** / 地理位置检查：
   - Check country whitelist/blacklist / 检查国家白名单/黑名单
   - Check city whitelist/blacklist / 检查城市白名单/黑名单
   - Check region whitelist/blacklist / 检查地区白名单/黑名单

## Geographic Location Providers / 地理位置提供者

### DefaultGeoLocationProvider

Uses ip-api.com (free tier) for IP geolocation.

使用 ip-api.com（免费版）进行 IP 地理位置查询。

**Note**: The free tier has rate limits. For production use, consider using a commercial service or MaxMind GeoIP2.

**注意**：免费版有速率限制。生产环境请考虑使用商业服务或 MaxMind GeoIP2。

### Custom Provider

Implement `IGeoLocationProvider` interface:

```csharp
public class CustomGeoLocationProvider : IGeoLocationProvider
{
    public async Task<GeoLocationInfo> GetLocationAsync(string ipAddress)
    {
        // Your implementation / 您的实现
        return new GeoLocationInfo
        {
            CountryCode = "CN",
            CountryName = "China",
            CityName = "Beijing",
            RegionName = "Beijing"
        };
    }
}

// Register / 注册
builder.Services.AddGeoLocationProvider<CustomGeoLocationProvider>();
```

## Access Denied Actions / 拒绝访问操作

- `CloseConnection` (default) - Close connection immediately / 立即关闭连接
- `ReturnForbidden` - Return HTTP 403 Forbidden / 返回 HTTP 403 Forbidden
- `ReturnUnauthorized` - Return HTTP 401 Unauthorized / 返回 HTTP 401 Unauthorized

## Best Practices / 最佳实践

1. **Use CIDR notation** for IP ranges / 使用 CIDR 表示法表示 IP 范围
2. **Enable caching** for geographic location to reduce API calls / 启用地理位置缓存以减少 API 调用
3. **Use whitelist mode** for strict access control / 使用白名单模式进行严格访问控制
4. **Monitor logs** for denied access attempts / 监控日志以查看被拒绝的访问尝试
5. **Consider rate limits** when using free geolocation APIs / 使用免费地理位置 API 时考虑速率限制

## Complete Example / 完整示例

### Example 1: Complete Program.cs with IP Whitelist / 完整的 Program.cs 示例（IP 白名单）

```csharp
using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Middlewares;

var builder = WebApplication.CreateBuilder(args);

// Configure access control / 配置访问控制
builder.Services.AddAccessControl(policy =>
{
    policy.Enabled = true;
    
    // IP whitelist - only allow these IPs / IP 白名单 - 只允许这些 IP
    policy.IpWhitelist = new List<string>
    {
        "127.0.0.1",        // Localhost / 本地
        "192.168.1.0/24",  // Local network / 本地网络
        "10.0.0.0/8"        // Private network / 私有网络
    };
    
    // Disable geo lookup for IP-only mode / 仅 IP 模式时禁用地理位置查询
    policy.EnableGeoLocationLookup = false;
    
    // Action when denied / 拒绝时的操作
    policy.DeniedAction = AccessDeniedAction.CloseConnection;
    policy.DenialMessage = "Your IP is not in the whitelist";
});

// Configure WebSocket / 配置 WebSocket
builder.Services.ConfigureWebSocketRoute(x =>
{
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", new MvcChannelHandler(4 * 1024).ConnectionEntry }
    };
    x.ApplicationServiceCollection = builder.Services;
});

var app = builder.Build();

// Configure WebSocket options / 配置 WebSocket 选项
var webSocketOptions = new Microsoft.AspNetCore.Builder.WebSocketOptions()
{
    KeepAliveInterval = TimeSpan.FromSeconds(120)
};

app.UseWebSockets(webSocketOptions);
app.UseWebSocketServer();

app.Run();
```

### Example 2: Country-based Filtering / 基于国家的过滤

```csharp
using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Middlewares;

var builder = WebApplication.CreateBuilder(args);

// Configure access control with country filtering / 配置基于国家的访问控制
builder.Services.AddAccessControl(policy =>
{
    policy.Enabled = true;
    
    // Country whitelist - only allow these countries / 国家白名单 - 只允许这些国家
    policy.CountryWhitelist = new List<string> { "CN", "US", "JP" };
    
    // Or use blacklist to block specific countries / 或使用黑名单阻止特定国家
    // policy.CountryBlacklist = new List<string> { "XX", "YY" };
    
    // Enable geographic location lookup / 启用地理位置查询
    policy.EnableGeoLocationLookup = true;
    policy.GeoLocationCacheSeconds = 3600; // Cache for 1 hour / 缓存 1 小时
});

// Register geographic location provider / 注册地理位置提供者
builder.Services.AddGeoLocationProvider<DefaultGeoLocationProvider>();

// Configure WebSocket / 配置 WebSocket
builder.Services.ConfigureWebSocketRoute(x =>
{
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", new MvcChannelHandler(4 * 1024).ConnectionEntry }
    };
    x.ApplicationServiceCollection = builder.Services;
});

var app = builder.Build();

var webSocketOptions = new Microsoft.AspNetCore.Builder.WebSocketOptions()
{
    KeepAliveInterval = TimeSpan.FromSeconds(120)
};

app.UseWebSockets(webSocketOptions);
app.UseWebSocketServer();

app.Run();
```

### Example 3: Configuration from appsettings.json / 从 appsettings.json 配置

**appsettings.json**:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  },
  "AccessControlPolicy": {
    "Enabled": true,
    "IpWhitelist": [
      "127.0.0.1",
      "192.168.1.0/24"
    ],
    "IpBlacklist": [
      "203.0.113.0/24"
    ],
    "CountryWhitelist": ["CN", "US"],
    "CountryBlacklist": ["XX"],
    "CityWhitelist": [
      "CN:Beijing",
      "CN:Shanghai",
      "US:New York"
    ],
    "RegionWhitelist": [
      "CN:Beijing",
      "US:California"
    ],
    "EnableGeoLocationLookup": true,
    "GeoLocationCacheSeconds": 3600,
    "DeniedAction": "CloseConnection",
    "DenialMessage": "Access denied"
  }
}
```

**Program.cs**:
```csharp
using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Middlewares;

var builder = WebApplication.CreateBuilder(args);

// Load access control from configuration / 从配置加载访问控制
builder.Services.AddAccessControl(builder.Configuration, "AccessControlPolicy");

// Register geographic location provider / 注册地理位置提供者
builder.Services.AddGeoLocationProvider<DefaultGeoLocationProvider>();

// Configure WebSocket / 配置 WebSocket
builder.Services.ConfigureWebSocketRoute(x =>
{
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", new MvcChannelHandler(4 * 1024).ConnectionEntry }
    };
    x.ApplicationServiceCollection = builder.Services;
});

var app = builder.Build();

var webSocketOptions = new Microsoft.AspNetCore.Builder.WebSocketOptions()
{
    KeepAliveInterval = TimeSpan.FromSeconds(120)
};

app.UseWebSockets(webSocketOptions);
app.UseWebSocketServer();

app.Run();
```

### Example 4: Custom Geographic Location Provider / 自定义地理位置提供者

```csharp
using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using System.Threading.Tasks;

// Implement custom provider / 实现自定义提供者
public class MaxMindGeoLocationProvider : IGeoLocationProvider
{
    private readonly ILogger<MaxMindGeoLocationProvider> _logger;
    
    public MaxMindGeoLocationProvider(ILogger<MaxMindGeoLocationProvider> logger)
    {
        _logger = logger;
    }
    
    public async Task<GeoLocationInfo> GetLocationAsync(string ipAddress)
    {
        // Use MaxMind GeoIP2 database / 使用 MaxMind GeoIP2 数据库
        // Implementation here / 在此实现
        
        return new GeoLocationInfo
        {
            CountryCode = "CN",
            CountryName = "China",
            CityName = "Beijing",
            RegionName = "Beijing"
        };
    }
}

// In Program.cs / 在 Program.cs 中
builder.Services.AddAccessControl(policy =>
{
    policy.Enabled = true;
    policy.EnableGeoLocationLookup = true;
});

// Register custom provider / 注册自定义提供者
builder.Services.AddGeoLocationProvider<MaxMindGeoLocationProvider>();
```

### Example 5: Testing Access Control / 测试访问控制

Create a test client to verify access control:

创建一个测试客户端来验证访问控制：

```csharp
// Test client code / 测试客户端代码
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public class AccessControlTestClient
{
    public static async Task TestConnection(string url)
    {
        try
        {
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri(url), CancellationToken.None);
            Console.WriteLine($"✓ Connected to {url}");
            
            // Send a test message / 发送测试消息
            var message = Encoding.UTF8.GetBytes("Hello, Server!");
            await client.SendAsync(
                new ArraySegment<byte>(message),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
            
            Console.WriteLine("✓ Message sent successfully");
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"✗ Connection failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Error: {ex.Message}");
        }
    }
}

// Usage / 使用
await AccessControlTestClient.TestConnection("ws://localhost:5000/ws");
```

## Testing Scenarios / 测试场景

### Scenario 1: IP Whitelist Test / IP 白名单测试

1. Configure whitelist with `127.0.0.1` / 配置白名单包含 `127.0.0.1`
2. Connect from `127.0.0.1` - Should succeed / 从 `127.0.0.1` 连接 - 应该成功
3. Connect from `192.168.1.100` - Should fail (if not in whitelist) / 从 `192.168.1.100` 连接 - 应该失败（如果不在白名单中）

### Scenario 2: Country Blacklist Test / 国家黑名单测试

1. Configure blacklist with country code `XX` / 配置黑名单包含国家代码 `XX`
2. Connect from IP in country `XX` - Should fail / 从国家 `XX` 的 IP 连接 - 应该失败
3. Connect from IP in other countries - Should succeed / 从其他国家的 IP 连接 - 应该成功

### Scenario 3: City Whitelist Test / 城市白名单测试

1. Configure city whitelist with `CN:Beijing` / 配置城市白名单包含 `CN:Beijing`
2. Connect from Beijing - Should succeed / 从北京连接 - 应该成功
3. Connect from other cities - Should fail / 从其他城市连接 - 应该失败

