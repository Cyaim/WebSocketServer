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
// Choose one of the following providers / 选择以下提供者之一

// Online providers / 在线提供者
builder.Services.AddGeoLocationProvider<IpApiComGeoLocationProvider>();      // ip-api.com (45 req/min)
// builder.Services.AddGeoLocationProvider<IpApiCoGeoLocationProvider>();   // ipapi.co (1,000 req/day)
// builder.Services.AddGeoLocationProvider<IpWhoisAppGeoLocationProvider>(); // ipwhois.app (10,000 req/month)
// builder.Services.AddGeoLocationProvider<IpApiIoGeoLocationProvider>();     // ip-api.io (45 req/min)
// builder.Services.AddGeoLocationProvider<IpIpNetGeoLocationProvider>();     // ipip.net (varies)
// builder.Services.AddSingleton<IGeoLocationProvider>(provider =>          // ipinfo.io (50,000 req/month, requires API key)
//     new IpInfoIoGeoLocationProvider(
//         provider.GetRequiredService<ILogger<IpInfoIoGeoLocationProvider>>(),
//         apiKey: "your-api-key"));

// Offline database providers / 离线数据库提供者
// builder.Services.AddSingleton<IGeoLocationProvider>(provider =>          // ChunZhen offline (qqwry.dat)
//     new ChunZhenOfflineGeoLocationProvider(
//         provider.GetRequiredService<ILogger<ChunZhenOfflineGeoLocationProvider>>(),
//         databasePath: "path/to/qqwry.dat"));
// builder.Services.AddSingleton<IGeoLocationProvider>(provider =>          // ipip.net offline
//     new IpIpNetOfflineGeoLocationProvider(
//         provider.GetRequiredService<ILogger<IpIpNetOfflineGeoLocationProvider>>(),
//         databasePath: "path/to/ipipnet-database"));
// builder.Services.AddSingleton<IGeoLocationProvider>(provider =>          // MaxMind offline (.mmdb)
//     new MaxMindOfflineGeoLocationProvider(
//         provider.GetRequiredService<ILogger<MaxMindOfflineGeoLocationProvider>>(),
//         databasePath: "path/to/GeoLite2-City.mmdb"));
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

The library provides multiple free IP geolocation providers. Choose the one that best fits your needs:

库提供了多个免费的 IP 地理位置提供者。选择最适合您需求的：

### IpApiComGeoLocationProvider (ip-api.com)

- **Rate Limit**: 45 requests/minute / 每分钟 45 次请求
- **API Key**: Not required / 不需要 API 密钥
- **URL**: http://ip-api.com/json/{ip}

```csharp
builder.Services.AddGeoLocationProvider<IpApiComGeoLocationProvider>();
```

### IpApiCoGeoLocationProvider (ipapi.co)

- **Rate Limit**: 1,000 requests/day / 每天 1,000 次请求
- **API Key**: Optional / 可选
- **URL**: https://ipapi.co/{ip}/json/

```csharp
builder.Services.AddGeoLocationProvider<IpApiCoGeoLocationProvider>();
// With API key / 使用 API 密钥
builder.Services.AddSingleton<IGeoLocationProvider>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<IpApiCoGeoLocationProvider>>();
    return new IpApiCoGeoLocationProvider(logger, apiKey: "your-api-key");
});
```

### IpWhoisAppGeoLocationProvider (ipwhois.app)

- **Rate Limit**: 10,000 requests/month / 每月 10,000 次请求
- **API Key**: Not required / 不需要 API 密钥
- **URL**: http://ipwhois.app/json/{ip}

```csharp
builder.Services.AddGeoLocationProvider<IpWhoisAppGeoLocationProvider>();
```

### IpApiIoGeoLocationProvider (ip-api.io)

- **Rate Limit**: 45 requests/minute / 每分钟 45 次请求
- **API Key**: Not required / 不需要 API 密钥
- **URL**: https://ip-api.io/json/{ip}

```csharp
builder.Services.AddGeoLocationProvider<IpApiIoGeoLocationProvider>();
```

### IpInfoIoGeoLocationProvider (ipinfo.io)

- **Rate Limit**: 50,000 requests/month / 每月 50,000 次请求
- **API Key**: Required (free tier) / 必需（免费版）
- **URL**: https://ipinfo.io/{ip}/json?token={key}

```csharp
// Requires API key / 需要 API 密钥
builder.Services.AddSingleton<IGeoLocationProvider>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<IpInfoIoGeoLocationProvider>>();
    return new IpInfoIoGeoLocationProvider(logger, apiKey: "your-api-key");
});
```

### IpIpNetGeoLocationProvider (ipip.net)

- **Rate Limit**: Varies by plan / 根据套餐不同
- **API Key**: Optional / 可选
- **URL**: https://freeapi.ipip.net/{ip}

```csharp
builder.Services.AddGeoLocationProvider<IpIpNetGeoLocationProvider>();
// With API key / 使用 API 密钥
builder.Services.AddSingleton<IGeoLocationProvider>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<IpIpNetGeoLocationProvider>>();
    return new IpIpNetGeoLocationProvider(logger, apiKey: "your-api-key");
});
```

### ChunZhenGeoLocationProvider (纯真在线)

- **Note**: ChunZhen typically uses offline database. Online API may not be available.
- **注意**：纯真通常使用离线数据库。在线 API 可能不可用。

```csharp
// Note: This is a placeholder. Use ChunZhenOfflineGeoLocationProvider instead.
// 注意：这是占位符。请使用 ChunZhenOfflineGeoLocationProvider。
builder.Services.AddGeoLocationProvider<ChunZhenGeoLocationProvider>();
```

## Offline Database Providers / 离线数据库提供者

### ChunZhenOfflineGeoLocationProvider (纯真离线数据库)

- **Database Format**: qqwry.dat / 数据库格式：qqwry.dat
- **Download**: Search for "纯真IP数据库" or "qqwry.dat" / 搜索"纯真IP数据库"或"qqwry.dat"
- **Encoding**: GB2312 / 编码：GB2312

```csharp
builder.Services.AddSingleton<IGeoLocationProvider>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<ChunZhenOfflineGeoLocationProvider>>();
    return new ChunZhenOfflineGeoLocationProvider(logger, databasePath: "path/to/qqwry.dat");
});
```

### IpIpNetOfflineGeoLocationProvider (ipip.net 离线数据库)

- **Database Format**: ipip.net database file / 数据库格式：ipip.net 数据库文件
- **Download**: From ipip.net official website / 从 ipip.net 官网下载
- **Note**: Database format may vary. Implementation may need adjustment based on actual format.
- **注意**：数据库格式可能不同。实现可能需要根据实际格式进行调整。

```csharp
builder.Services.AddSingleton<IGeoLocationProvider>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<IpIpNetOfflineGeoLocationProvider>>();
    return new IpIpNetOfflineGeoLocationProvider(logger, databasePath: "path/to/ipipnet-database");
});
```

### MaxMindOfflineGeoLocationProvider (MaxMind GeoIP2/GeoLite2)

- **Database Format**: .mmdb (MaxMind Binary Database) / 数据库格式：.mmdb (MaxMind 二进制数据库)
- **Download**: https://dev.maxmind.com/geoip/geoip2/geolite2/ / 下载：https://dev.maxmind.com/geoip/geoip2/geolite2/
- **NuGet Package**: MaxMind.GeoIP2 (optional, uses reflection if not installed) / MaxMind.GeoIP2（可选，如果未安装则使用反射）

```csharp
// Install MaxMind.GeoIP2 NuGet package first / 首先安装 MaxMind.GeoIP2 NuGet 包
// dotnet add package MaxMind.GeoIP2

builder.Services.AddSingleton<IGeoLocationProvider>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<MaxMindOfflineGeoLocationProvider>>();
    return new MaxMindOfflineGeoLocationProvider(logger, databasePath: "path/to/GeoLite2-City.mmdb");
});
```

**Note**: All free tiers have rate limits. For production use, consider using a commercial service or MaxMind GeoIP2.

**注意**：所有免费版都有速率限制。生产环境请考虑使用商业服务或 MaxMind GeoIP2。

## Creating Custom GeoLocation Providers / 创建自定义地理位置提供者

If you need to integrate with a custom IP geolocation service or offline database, you can create your own provider by implementing the `IGeoLocationProvider` interface.

如果您需要集成自定义的 IP 地理位置服务或离线数据库，可以通过实现 `IGeoLocationProvider` 接口来创建自己的提供者。

### Interface Definition / 接口定义

```csharp
public interface IGeoLocationProvider
{
    Task<GeoLocationInfo> GetLocationAsync(string ipAddress);
}

public class GeoLocationInfo
{
    public string CountryCode { get; set; }      // ISO 3166-1 alpha-2 (e.g., "CN", "US")
    public string CountryName { get; set; }      // Country name / 国家名称
    public string RegionName { get; set; }       // Region/State name / 地区/州名称
    public string CityName { get; set; }         // City name / 城市名称
    public double? Latitude { get; set; }        // Latitude / 纬度
    public double? Longitude { get; set; }       // Longitude / 经度
}
```

### Example 1: Online API Provider / 示例 1：在线 API 提供者

This example shows how to create a provider for a custom online API service.

此示例展示如何为自定义在线 API 服务创建提供者。

```csharp
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace YourNamespace
{
    /// <summary>
    /// Custom online API provider example / 自定义在线 API 提供者示例
    /// </summary>
    public class CustomApiGeoLocationProvider : IGeoLocationProvider
    {
        private readonly ILogger<CustomApiGeoLocationProvider> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _apiUrl;
        private readonly string _apiKey;

        public CustomApiGeoLocationProvider(
            ILogger<CustomApiGeoLocationProvider> logger,
            HttpClient httpClient = null,
            string apiUrl = "https://api.example.com/geo/{ip}",
            string apiKey = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? new HttpClient();
            _apiUrl = apiUrl;
            _apiKey = apiKey;
        }

        public async Task<GeoLocationInfo> GetLocationAsync(string ipAddress)
        {
            // 1. Validate input / 验证输入
            if (string.IsNullOrEmpty(ipAddress))
            {
                return null;
            }

            try
            {
                // 2. Skip local/private IPs (optional but recommended) / 跳过本地/私有 IP（可选但推荐）
                if (IsLocalOrPrivateIp(ipAddress))
                {
                    _logger.LogDebug($"IP {ipAddress} is local/private, skipping geo lookup");
                    return null;
                }

                // 3. Build API URL / 构建 API URL
                var url = _apiUrl.Replace("{ip}", ipAddress);
                if (!string.IsNullOrEmpty(_apiKey))
                {
                    url += $"?key={_apiKey}";
                }

                // 4. Make HTTP request / 发起 HTTP 请求
                var response = await _httpClient.GetStringAsync(url);

                if (string.IsNullOrEmpty(response))
                {
                    return null;
                }

                // 5. Parse JSON response / 解析 JSON 响应
                var jsonDoc = JsonDocument.Parse(response);
                var root = jsonDoc.RootElement;

                // 6. Check for errors / 检查错误
                if (root.TryGetProperty("error", out var errorElement))
                {
                    _logger.LogWarning($"Geo lookup failed for IP {ipAddress}: {errorElement.GetRawText()}");
                    return null;
                }

                // 7. Extract location information / 提取位置信息
                var geoInfo = new GeoLocationInfo
                {
                    // Map API response fields to GeoLocationInfo / 将 API 响应字段映射到 GeoLocationInfo
                    CountryCode = root.TryGetProperty("country_code", out var countryCode)
                        ? countryCode.GetString()
                        : null,
                    CountryName = root.TryGetProperty("country", out var country)
                        ? country.GetString()
                        : null,
                    RegionName = root.TryGetProperty("region", out var region)
                        ? region.GetString()
                        : null,
                    CityName = root.TryGetProperty("city", out var city)
                        ? city.GetString()
                        : null,
                    // Handle numeric fields with null check / 处理数值字段并检查 null
                    Latitude = root.TryGetProperty("latitude", out var lat) && lat.ValueKind == JsonValueKind.Number
                        ? (double?)lat.GetDouble()
                        : (double?)null,
                    Longitude = root.TryGetProperty("longitude", out var lon) && lon.ValueKind == JsonValueKind.Number
                        ? (double?)lon.GetDouble()
                        : (double?)null
                };

                _logger.LogDebug($"Geo lookup for IP {ipAddress}: {geoInfo.CountryCode}/{geoInfo.CityName}");
                return geoInfo;
            }
            catch (Exception ex)
            {
                // 8. Handle errors gracefully / 优雅地处理错误
                _logger.LogWarning(ex, $"Error getting geographic location for IP {ipAddress}");
                return null; // Return null on error, don't throw / 出错时返回 null，不要抛出异常
            }
        }

        /// <summary>
        /// Check if IP is local or private / 检查 IP 是否为本地或私有
        /// </summary>
        private bool IsLocalOrPrivateIp(string ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress))
                return true;

            if (ipAddress == "::1" || ipAddress == "127.0.0.1" || ipAddress == "localhost")
                return true;

            if (System.Net.IPAddress.TryParse(ipAddress, out var ip))
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    var bytes = ip.GetAddressBytes();
                    // 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16
                    if (bytes[0] == 10 ||
                        (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                        (bytes[0] == 192 && bytes[1] == 168))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
```

### Example 2: Offline Database Provider / 示例 2：离线数据库提供者

This example shows how to create a provider for a custom offline database.

此示例展示如何为自定义离线数据库创建提供者。

```csharp
using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace YourNamespace
{
    /// <summary>
    /// Custom offline database provider example / 自定义离线数据库提供者示例
    /// </summary>
    public class CustomOfflineGeoLocationProvider : IGeoLocationProvider
    {
        private readonly ILogger<CustomOfflineGeoLocationProvider> _logger;
        private readonly string _databasePath;
        private readonly object _lockObject = new object();
        private byte[] _databaseData;
        private bool _databaseLoaded;

        public CustomOfflineGeoLocationProvider(
            ILogger<CustomOfflineGeoLocationProvider> logger,
            string databasePath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));

            if (!File.Exists(_databasePath))
            {
                throw new FileNotFoundException($"Database file not found: {_databasePath}");
            }
        }

        public async Task<GeoLocationInfo> GetLocationAsync(string ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress))
            {
                return null;
            }

            try
            {
                // Skip local/private IPs / 跳过本地/私有 IP
                if (IsLocalOrPrivateIp(ipAddress))
                {
                    _logger.LogDebug($"IP {ipAddress} is local/private, skipping geo lookup");
                    return null;
                }

                // Load database if not loaded / 如果未加载则加载数据库
                await EnsureDatabaseLoadedAsync();

                // Parse IP address / 解析 IP 地址
                if (!IPAddress.TryParse(ipAddress, out var ip) || 
                    ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    _logger.LogWarning($"Invalid IPv4 address: {ipAddress}");
                    return null;
                }

                // Convert IP to numeric value / 将 IP 转换为数值
                var ipBytes = ip.GetAddressBytes();
                var ipValue = (uint)(ipBytes[0] << 24) | 
                             (uint)(ipBytes[1] << 16) | 
                             (uint)(ipBytes[2] << 8) | 
                             ipBytes[3];

                // Search in database (implement your search algorithm) / 在数据库中搜索（实现您的搜索算法）
                var location = SearchLocation(ipValue);
                if (location == null)
                {
                    return null;
                }

                // Parse location string / 解析位置字符串
                var geoInfo = ParseLocationString(location);

                _logger.LogDebug($"Geo lookup for IP {ipAddress}: {geoInfo.CountryName}/{geoInfo.CityName}");
                return geoInfo;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error getting geographic location for IP {ipAddress}");
                return null;
            }
        }

        /// <summary>
        /// Ensure database is loaded / 确保数据库已加载
        /// </summary>
        private Task EnsureDatabaseLoadedAsync()
        {
            if (_databaseLoaded)
            {
                return Task.CompletedTask;
            }

            lock (_lockObject)
            {
                if (_databaseLoaded)
                {
                    return Task.CompletedTask;
                }

                _databaseData = File.ReadAllBytes(_databasePath);
                _databaseLoaded = true;
                _logger.LogInformation($"Loaded database from {_databasePath}, size: {_databaseData.Length} bytes");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Search location in database / 在数据库中搜索位置
        /// Implement your database-specific search algorithm here / 在此处实现特定于数据库的搜索算法
        /// </summary>
        private string SearchLocation(uint ipValue)
        {
            // TODO: Implement your database search logic / 实现您的数据库搜索逻辑
            // Example: Binary search, B-tree lookup, etc. / 示例：二分搜索、B 树查找等
            return null;
        }

        /// <summary>
        /// Parse location string to GeoLocationInfo / 将位置字符串解析为 GeoLocationInfo
        /// </summary>
        private GeoLocationInfo ParseLocationString(string location)
        {
            // TODO: Parse your database format / 解析您的数据库格式
            // Example: "Country Region City" or JSON string / 示例："国家 地区 城市" 或 JSON 字符串
            return new GeoLocationInfo
            {
                CountryName = "Unknown",
                CityName = location
            };
        }

        private bool IsLocalOrPrivateIp(string ipAddress)
        {
            // Same as Example 1 / 与示例 1 相同
            // ... (implementation omitted for brevity) / ...（为简洁起见省略实现）
            return false;
        }
    }
}
```

### Example 3: Simple Static Provider / 示例 3：简单静态提供者

For testing or simple use cases, you can create a provider that returns static data.

对于测试或简单用例，您可以创建一个返回静态数据的提供者。

```csharp
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace YourNamespace
{
    /// <summary>
    /// Simple static provider for testing / 用于测试的简单静态提供者
    /// </summary>
    public class StaticGeoLocationProvider : IGeoLocationProvider
    {
        private readonly ILogger<StaticGeoLocationProvider> _logger;

        public StaticGeoLocationProvider(ILogger<StaticGeoLocationProvider> logger)
        {
            _logger = logger;
        }

        public Task<GeoLocationInfo> GetLocationAsync(string ipAddress)
        {
            // Return static data for testing / 返回静态数据用于测试
            return Task.FromResult<GeoLocationInfo>(new GeoLocationInfo
            {
                CountryCode = "CN",
                CountryName = "China",
                RegionName = "Beijing",
                CityName = "Beijing",
                Latitude = 39.9042,
                Longitude = 116.4074
            });
        }
    }
}
```

### Registering Custom Providers / 注册自定义提供者

After creating your custom provider, register it in your `Program.cs`:

创建自定义提供者后，在 `Program.cs` 中注册它：

```csharp
using Cyaim.WebSocketServer.Infrastructure.AccessControl;

var builder = WebApplication.CreateBuilder(args);

// Method 1: Using extension method (if provider has parameterless constructor) / 方法 1：使用扩展方法（如果提供者有无参构造函数）
builder.Services.AddGeoLocationProvider<CustomApiGeoLocationProvider>();

// Method 2: Manual registration with parameters / 方法 2：使用参数手动注册
builder.Services.AddSingleton<IGeoLocationProvider>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<CustomApiGeoLocationProvider>>();
    var httpClient = provider.GetService<HttpClient>() ?? new HttpClient();
    return new CustomApiGeoLocationProvider(
        logger,
        httpClient,
        apiUrl: "https://api.example.com/geo/{ip}",
        apiKey: "your-api-key"
    );
});

// Method 3: For offline database / 方法 3：离线数据库
builder.Services.AddSingleton<IGeoLocationProvider>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<CustomOfflineGeoLocationProvider>>();
    return new CustomOfflineGeoLocationProvider(
        logger,
        databasePath: "path/to/your/database.dat"
    );
});

// Configure access control / 配置访问控制
builder.Services.AddAccessControl(policy =>
{
    policy.Enabled = true;
    policy.EnableGeoLocationLookup = true;
    // ... other settings / 其他设置
});
```

### Best Practices for Custom Providers / 自定义提供者最佳实践

1. **Error Handling / 错误处理**
   - Always return `null` on error, don't throw exceptions / 出错时始终返回 `null`，不要抛出异常
   - Log errors for debugging / 记录错误以便调试

2. **Performance / 性能**
   - Cache database data in memory for offline providers / 为离线提供者缓存数据库数据
   - Use `HttpClient` efficiently (consider reusing instances) / 高效使用 `HttpClient`（考虑重用实例）
   - Skip local/private IPs to avoid unnecessary lookups / 跳过本地/私有 IP 以避免不必要的查询

3. **Null Safety / 空值安全**
   - Always check for null values when parsing JSON / 解析 JSON 时始终检查空值
   - Use `TryGetProperty` for optional fields / 对可选字段使用 `TryGetProperty`
   - Handle missing fields gracefully / 优雅地处理缺失字段

4. **Type Safety / 类型安全**
   - Use explicit type casting for nullable types in C# 8.0 / 在 C# 8.0 中为可空类型使用显式类型转换
   - Example: `? (double?)lat.GetDouble() : (double?)null` / 示例：`? (double?)lat.GetDouble() : (double?)null`

5. **Logging / 日志记录**
   - Log successful lookups at Debug level / 在 Debug 级别记录成功查询
   - Log errors at Warning level / 在 Warning 级别记录错误
   - Include IP address in log messages / 在日志消息中包含 IP 地址

### Testing Your Provider / 测试您的提供者

```csharp
// Simple test / 简单测试
var provider = new CustomApiGeoLocationProvider(logger, httpClient);
var result = await provider.GetLocationAsync("8.8.8.8");
Console.WriteLine($"Country: {result?.CountryName}, City: {result?.CityName}");
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
builder.Services.AddGeoLocationProvider<IpApiComGeoLocationProvider>();

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
builder.Services.AddGeoLocationProvider<IpApiComGeoLocationProvider>();

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

### Example 4: Using Offline Database (ChunZhen) / 使用离线数据库（纯真）

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
    policy.CountryWhitelist = new List<string> { "CN" };
    policy.EnableGeoLocationLookup = true;
});

// Register ChunZhen offline database provider / 注册纯真离线数据库提供者
builder.Services.AddSingleton<IGeoLocationProvider>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<ChunZhenOfflineGeoLocationProvider>>();
    return new ChunZhenOfflineGeoLocationProvider(logger, databasePath: "path/to/qqwry.dat");
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

var webSocketOptions = new Microsoft.AspNetCore.Builder.WebSocketOptions()
{
    KeepAliveInterval = TimeSpan.FromSeconds(120)
};

app.UseWebSockets(webSocketOptions);
app.UseWebSocketServer();

app.Run();
```

### Example 5: Using MaxMind Offline Database / 使用 MaxMind 离线数据库

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
    policy.CountryWhitelist = new List<string> { "CN", "US" };
    policy.EnableGeoLocationLookup = true;
});

// Register MaxMind offline database provider / 注册 MaxMind 离线数据库提供者
// First install MaxMind.GeoIP2 NuGet package: dotnet add package MaxMind.GeoIP2
// 首先安装 MaxMind.GeoIP2 NuGet 包：dotnet add package MaxMind.GeoIP2
builder.Services.AddSingleton<IGeoLocationProvider>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<MaxMindOfflineGeoLocationProvider>>();
    return new MaxMindOfflineGeoLocationProvider(logger, databasePath: "path/to/GeoLite2-City.mmdb");
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

var webSocketOptions = new Microsoft.AspNetCore.Builder.WebSocketOptions()
{
    KeepAliveInterval = TimeSpan.FromSeconds(120)
};

app.UseWebSockets(webSocketOptions);
app.UseWebSocketServer();

app.Run();
```

### Example 6: Custom Geographic Location Provider / 自定义地理位置提供者

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

