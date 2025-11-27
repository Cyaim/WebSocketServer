using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Infrastructure.AccessControl
{
    /// <summary>
    /// Geographic location provider using ipip.net offline database
    /// 使用 ipip.net 离线数据库的地理位置提供者
    /// Database format: ipip.net database file / 数据库格式：ipip.net 数据库文件
    /// Note: This is a placeholder implementation. Actual database format may vary / 注意：这是占位符实现，实际数据库格式可能不同
    /// </summary>
    public class IpIpNetOfflineGeoLocationProvider : IGeoLocationProvider
    {
        private readonly ILogger<IpIpNetOfflineGeoLocationProvider> _logger;
        private readonly string _databasePath;
        private readonly object _lockObject = new object();
        private byte[] _databaseData;
        private bool _databaseLoaded;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="databasePath">Path to ipip.net database file / ipip.net 数据库文件路径</param>
        public IpIpNetOfflineGeoLocationProvider(
            ILogger<IpIpNetOfflineGeoLocationProvider> logger,
            string databasePath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));

            if (!File.Exists(_databasePath))
            {
                throw new FileNotFoundException($"ipip.net database file not found: {_databasePath}");
            }
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

                // Load database if not loaded / 如果未加载则加载数据库
                await EnsureDatabaseLoadedAsync();

                // Parse IP address / 解析 IP 地址
                if (!IPAddress.TryParse(ipAddress, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    _logger.LogWarning($"Invalid IPv4 address: {ipAddress}");
                    return null;
                }

                var ipBytes = ip.GetAddressBytes();
                var ipValue = (uint)(ipBytes[0] << 24) | (uint)(ipBytes[1] << 16) | (uint)(ipBytes[2] << 8) | ipBytes[3];

                // Search in database / 在数据库中搜索
                // Note: ipip.net database format may vary, this is a generic implementation
                // 注意：ipip.net 数据库格式可能不同，这是通用实现
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
                return Task.CompletedTask;

            lock (_lockObject)
            {
                if (_databaseLoaded)
                    return Task.CompletedTask;

                try
                {
                    _databaseData = File.ReadAllBytes(_databasePath);
                    _databaseLoaded = true;
                    _logger.LogInformation($"ipip.net database loaded: {_databasePath} ({_databaseData.Length} bytes)");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to load ipip.net database: {_databasePath}");
                    throw;
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Search location in database / 在数据库中搜索位置
        /// Note: This is a placeholder. Actual implementation depends on ipip.net database format
        /// 注意：这是占位符。实际实现取决于 ipip.net 数据库格式
        /// </summary>
        private string SearchLocation(uint ipValue)
        {
            // TODO: Implement actual search logic based on ipip.net database format
            // 待办：根据 ipip.net 数据库格式实现实际搜索逻辑
            _logger.LogWarning("ipip.net offline database search is not fully implemented. Please refer to ipip.net documentation for database format.");
            return null;
        }

        /// <summary>
        /// Parse location string to GeoLocationInfo / 将位置字符串解析为 GeoLocationInfo
        /// </summary>
        private GeoLocationInfo ParseLocationString(string location)
        {
            if (string.IsNullOrEmpty(location))
                return new GeoLocationInfo();

            // Common format: "国家|省份|城市" or "Country|Region|City" / 常见格式："国家|省份|城市" 或 "Country|Region|City"
            var parts = location.Split('|', StringSplitOptions.RemoveEmptyEntries);
            
            var geoInfo = new GeoLocationInfo();
            
            if (parts.Length > 0)
            {
                geoInfo.CountryName = parts[0];
                geoInfo.CountryCode = GetCountryCode(parts[0]);
            }
            
            if (parts.Length > 1)
            {
                geoInfo.RegionName = parts[1];
            }
            
            if (parts.Length > 2)
            {
                geoInfo.CityName = parts[2];
            }

            return geoInfo;
        }

        /// <summary>
        /// Get country code from country name / 从国家名称获取国家代码
        /// </summary>
        private string GetCountryCode(string countryName)
        {
            if (string.IsNullOrEmpty(countryName))
                return null;

            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "中国", "CN" },
                { "China", "CN" },
                { "美国", "US" },
                { "United States", "US" }
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

            if (IPAddress.TryParse(ipAddress, out var ip))
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    var bytes = ip.GetAddressBytes();
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

