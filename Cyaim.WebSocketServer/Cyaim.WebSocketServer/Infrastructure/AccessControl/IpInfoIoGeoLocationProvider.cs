using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Infrastructure.AccessControl
{
    /// <summary>
    /// Geographic location provider using ipinfo.io (free tier)
    /// 使用 ipinfo.io（免费版）的地理位置提供者
    /// Rate limit: 50,000 requests/month for free tier / 免费版限制：每月 50,000 次请求
    /// Note: Requires API key for free tier / 注意：免费版需要 API 密钥
    /// </summary>
    public class IpInfoIoGeoLocationProvider : IGeoLocationProvider
    {
        private readonly ILogger<IpInfoIoGeoLocationProvider> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _apiUrl;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="apiKey">API key (required for free tier) / API 密钥（免费版必需）</param>
        /// <param name="httpClient">HTTP client (optional, will create if null) / HTTP 客户端（可选，如果为 null 则创建）</param>
        /// <param name="apiUrl">API URL (default: https://ipinfo.io/{ip}/json?token={key}) / API URL（默认：https://ipinfo.io/{ip}/json?token={key}）</param>
        public IpInfoIoGeoLocationProvider(
            ILogger<IpInfoIoGeoLocationProvider> logger,
            string apiKey,
            HttpClient httpClient = null,
            string apiUrl = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey), "API key is required for ipinfo.io");
            _httpClient = httpClient ?? new HttpClient();
            _apiUrl = apiUrl ?? "https://ipinfo.io/{ip}/json?token={key}";
        }

        /// <summary>
        /// Get geographic location information for an IP address / 获取 IP 地址的地理位置信息
        /// </summary>
        /// <param name="ipAddress">IP address / IP 地址</param>
        /// <returns>Geographic location information or null if not found / 地理位置信息，如果未找到则返回 null</returns>
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

                var url = _apiUrl.Replace("{ip}", ipAddress).Replace("{key}", _apiKey);
                var response = await _httpClient.GetStringAsync(url);

                if (string.IsNullOrEmpty(response))
                {
                    return null;
                }

                var jsonDoc = JsonDocument.Parse(response);
                var root = jsonDoc.RootElement;

                // Check for error / 检查错误
                if (root.TryGetProperty("error", out var errorElement))
                {
                    _logger.LogWarning($"Geo lookup failed for IP {ipAddress}: {errorElement.GetRawText()}");
                    return null;
                }

                // Parse location from "loc" field (format: "lat,lon") / 从 "loc" 字段解析位置（格式："lat,lon"）
                double? latitude = null;
                double? longitude = null;
                if (root.TryGetProperty("loc", out var locElement))
                {
                    var loc = locElement.GetString();
                    if (!string.IsNullOrEmpty(loc))
                    {
                        var parts = loc.Split(',');
                        if (parts.Length == 2)
                        {
                            if (double.TryParse(parts[0], out var lat))
                                latitude = lat;
                            if (double.TryParse(parts[1], out var lon))
                                longitude = lon;
                        }
                    }
                }

                var geoInfo = new GeoLocationInfo
                {
                    CountryCode = root.TryGetProperty("country", out var countryCode)
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
                    Latitude = latitude,
                    Longitude = longitude
                };

                _logger.LogDebug($"Geo lookup for IP {ipAddress}: {geoInfo.CountryCode}/{geoInfo.CityName}");
                return geoInfo;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error getting geographic location for IP {ipAddress}");
                return null;
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
                // Check for private IP ranges / 检查私有 IP 范围
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

