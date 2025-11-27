using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Infrastructure.AccessControl
{
    /// <summary>
    /// Geographic location provider using ipwhois.app (free tier)
    /// 使用 ipwhois.app（免费版）的地理位置提供者
    /// Rate limit: 10,000 requests/month for free tier / 免费版限制：每月 10,000 次请求
    /// </summary>
    public class IpWhoisAppGeoLocationProvider : IGeoLocationProvider
    {
        private readonly ILogger<IpWhoisAppGeoLocationProvider> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _apiUrl;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="httpClient">HTTP client (optional, will create if null) / HTTP 客户端（可选，如果为 null 则创建）</param>
        /// <param name="apiUrl">API URL (default: http://ipwhois.app/json/{ip}) / API URL（默认：http://ipwhois.app/json/{ip}）</param>
        public IpWhoisAppGeoLocationProvider(
            ILogger<IpWhoisAppGeoLocationProvider> logger,
            HttpClient httpClient = null,
            string apiUrl = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? new HttpClient();
            _apiUrl = apiUrl ?? "http://ipwhois.app/json/{ip}";
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
                var response = await _httpClient.GetStringAsync(url);

                if (string.IsNullOrEmpty(response))
                {
                    return null;
                }

                var jsonDoc = JsonDocument.Parse(response);
                var root = jsonDoc.RootElement;

                // Check if query was successful / 检查查询是否成功
                if (root.TryGetProperty("success", out var successElement) && 
                    successElement.GetBoolean() == false)
                {
                    _logger.LogWarning($"Geo lookup failed for IP {ipAddress}: {response}");
                    return null;
                }

                var geoInfo = new GeoLocationInfo
                {
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

