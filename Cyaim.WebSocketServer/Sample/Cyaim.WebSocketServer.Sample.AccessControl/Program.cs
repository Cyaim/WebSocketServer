using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Middlewares;
using Cyaim.WebSocketServer.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Configure access control / 配置访问控制
// Example 1: IP Whitelist / 示例1：IP 白名单
builder.Services.AddAccessControl(policy =>
{
    policy.Enabled = true;

    // IP whitelist - only allow these IPs / IP 白名单 - 只允许这些 IP
    policy.IpWhitelist = new List<string>
    {
        "127.0.0.1",        // Localhost / 本地
        "::1",              // IPv6 localhost / IPv6 本地
        "192.168.1.0/24",  // Local network / 本地网络 (CIDR notation)
        "10.0.0.0/8"        // Private network / 私有网络
    };

    // IP blacklist - block these IPs / IP 黑名单 - 阻止这些 IP
    // policy.IpBlacklist = new List<string>
    // {
    //     "203.0.113.0/24"
    // };

    // Country whitelist - only allow these countries / 国家白名单 - 只允许这些国家
    // policy.CountryWhitelist = new List<string> { "CN", "US", "JP" };

    // Country blacklist - block these countries / 国家黑名单 - 阻止这些国家
    // policy.CountryBlacklist = new List<string> { "XX", "YY" };

    // City whitelist - only allow these cities / 城市白名单 - 只允许这些城市
    // policy.CityWhitelist = new List<string>
    // {
    //     "CN:Beijing",
    //     "CN:Shanghai",
    //     "US:New York"
    // };

    // Region whitelist - only allow these regions / 地区白名单 - 只允许这些地区
    // policy.RegionWhitelist = new List<string>
    // {
    //     "CN:Beijing",
    //     "US:California"
    // };

    // Enable geographic location lookup / 启用地理位置查询
    // Set to false if you only want IP-based filtering / 如果只需要基于 IP 的过滤，设置为 false
    policy.EnableGeoLocationLookup = false;
    policy.GeoLocationCacheSeconds = 3600; // Cache for 1 hour / 缓存 1 小时

    // Action when access is denied / 拒绝访问时的操作
    policy.DeniedAction = AccessDeniedAction.CloseConnection;
    policy.DenialMessage = "Your IP address is not allowed to connect";
});

// Register geographic location provider (optional, only needed if EnableGeoLocationLookup is true)
// 注册地理位置提供者（可选，仅在 EnableGeoLocationLookup 为 true 时需要）
// builder.Services.AddGeoLocationProvider<DefaultGeoLocationProvider>();

// Add controllers / 添加控制器
builder.Services.AddControllers();

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

// Add a simple controller for testing / 添加一个简单的控制器用于测试
app.MapGet("/", () => "WebSocket Server with Access Control. Connect to ws://localhost:5000/ws");

// Add controllers / 添加控制器
app.MapControllers();

app.Run();

