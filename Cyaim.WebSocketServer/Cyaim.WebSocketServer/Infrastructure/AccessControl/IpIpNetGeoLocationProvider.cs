using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Infrastructure.AccessControl
{
    /// <summary>
    /// Geographic location provider using ipip.net (free tier)
    /// 使用 ipip.net（免费版）的地理位置提供者
    /// Rate limit: Varies by plan / 速率限制：根据套餐不同
    /// Note: May require API key for some features / 注意：某些功能可能需要 API 密钥
    /// </summary>
    public class IpIpNetGeoLocationProvider : IGeoLocationProvider
    {
        private readonly ILogger<IpIpNetGeoLocationProvider> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _apiUrl;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="apiKey">API key (optional for free tier) / API 密钥（免费版可选）</param>
        /// <param name="httpClient">HTTP client (optional, will create if null) / HTTP 客户端（可选，如果为 null 则创建）</param>
        /// <param name="apiUrl">API URL (default: https://freeapi.ipip.net/{ip}) / API URL（默认：https://freeapi.ipip.net/{ip}）</param>
        public IpIpNetGeoLocationProvider(
            ILogger<IpIpNetGeoLocationProvider> logger,
            string apiKey = null,
            HttpClient httpClient = null,
            string apiUrl = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _apiKey = apiKey;
            _httpClient = httpClient ?? new HttpClient();
            _apiUrl = apiUrl ?? "https://freeapi.ipip.net/{ip}";
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

                var url = _apiUrl.Replace("{ip}", ipAddress);
                if (!string.IsNullOrEmpty(_apiKey))
                {
                    url += $"?token={_apiKey}";
                }

                var response = await _httpClient.GetStringAsync(url);

                if (string.IsNullOrEmpty(response))
                {
                    return null;
                }

                // ipip.net returns array format: ["country", "region", "city", "isp", ...]
                // ipip.net 返回数组格式：["country", "region", "city", "isp", ...]
                var jsonDoc = JsonDocument.Parse(response);
                var root = jsonDoc.RootElement;

                // Handle array response / 处理数组响应
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 3)
                {
                    var geoInfo = new GeoLocationInfo
                    {
                        CountryName = root[0].GetString(),
                        RegionName = root[1].GetString(),
                        CityName = root[2].GetString()
                    };

                    // Extract country code if available / 如果可用，提取国家代码
                    // Usually the first element contains country name, we need to map it to code
                    // 通常第一个元素包含国家名称，我们需要将其映射到代码
                    if (!string.IsNullOrEmpty(geoInfo.CountryName))
                    {
                        geoInfo.CountryCode = GetCountryCode(geoInfo.CountryName);
                    }

                    _logger.LogDebug($"Geo lookup for IP {ipAddress}: {geoInfo.CountryCode}/{geoInfo.CityName}");
                    return geoInfo;
                }

                // Handle object response (if API returns object format) / 处理对象响应（如果 API 返回对象格式）
                if (root.ValueKind == JsonValueKind.Object)
                {
                    var geoInfo = new GeoLocationInfo
                    {
                        CountryCode = root.TryGetProperty("country_code", out var countryCode)
                            ? countryCode.GetString()
                            : null,
                        CountryName = root.TryGetProperty("country", out var country)
                            ? country.GetString()
                            : root.TryGetProperty("country_name", out var countryName)
                                ? countryName.GetString()
                                : null,
                        RegionName = root.TryGetProperty("region", out var region)
                            ? region.GetString()
                            : root.TryGetProperty("region_name", out var regionName)
                                ? regionName.GetString()
                                : null,
                        CityName = root.TryGetProperty("city", out var city)
                            ? city.GetString()
                            : root.TryGetProperty("city_name", out var cityName)
                                ? cityName.GetString()
                                : null,
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

                _logger.LogWarning($"Unexpected response format from ipip.net for IP {ipAddress}: {response}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error getting geographic location for IP {ipAddress}");
                return null;
            }
        }

        /// <summary>
        /// Get country code from country name (simple mapping) / 从国家名称获取国家代码（简单映射）
        /// </summary>
        private string GetCountryCode(string countryName)
        {
            if (string.IsNullOrEmpty(countryName))
                return null;

            // Simple mapping for common countries / 常见国家的简单映射
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "中国", "CN" },
                { "China", "CN" },
                { "美国", "US" },
                { "United States", "US" },
                { "日本", "JP" },
                { "Japan", "JP" },
                { "韩国", "KR" },
                { "South Korea", "KR" },
                { "英国", "GB" },
                { "United Kingdom", "GB" },
                { "德国", "DE" },
                { "Germany", "DE" },
                { "法国", "FR" },
                { "France", "FR" }
            };

            return mapping.TryGetValue(countryName, out var code) ? code : null;
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

