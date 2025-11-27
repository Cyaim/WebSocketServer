using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Infrastructure.AccessControl
{
    /// <summary>
    /// Service for IP and geographic location access control / IP 和地理位置访问控制服务
    /// </summary>
    public class AccessControlService
    {
        private readonly ILogger<AccessControlService> _logger;
        private readonly AccessControlPolicy _policy;
        private readonly IGeoLocationProvider _geoLocationProvider;
        private readonly Dictionary<string, CachedGeoLocation> _geoLocationCache;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="policy">Access control policy / 访问控制策略</param>
        /// <param name="geoLocationProvider">Geographic location provider (optional) / 地理位置提供者（可选）</param>
        public AccessControlService(
            ILogger<AccessControlService> logger,
            AccessControlPolicy policy,
            IGeoLocationProvider geoLocationProvider = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _geoLocationProvider = geoLocationProvider;
            _geoLocationCache = new Dictionary<string, CachedGeoLocation>();
        }

        /// <summary>
        /// Check if an IP address is allowed to connect / 检查 IP 地址是否允许连接
        /// </summary>
        /// <param name="ipAddress">IP address / IP 地址</param>
        /// <returns>True if allowed, false if denied / 允许返回 true，拒绝返回 false</returns>
        public async Task<bool> IsAllowedAsync(string ipAddress)
        {
            if (!_policy.Enabled)
            {
                return true;
            }

            if (string.IsNullOrEmpty(ipAddress))
            {
                _logger.LogWarning("IP address is null or empty, denying access");
                return false;
            }

            // Parse IP address / 解析 IP 地址
            if (!IPAddress.TryParse(ipAddress, out var ip))
            {
                _logger.LogWarning($"Invalid IP address format: {ipAddress}");
                return false;
            }

            // Check IP whitelist first / 首先检查 IP 白名单
            if (_policy.IpWhitelist != null && _policy.IpWhitelist.Count > 0)
            {
                if (IsIpInList(ip, _policy.IpWhitelist))
                {
                    _logger.LogDebug($"IP {ipAddress} is in whitelist, allowing access");
                    return true;
                }
                else
                {
                    _logger.LogInformation($"IP {ipAddress} is not in whitelist, denying access");
                    return false;
                }
            }

            // Check IP blacklist / 检查 IP 黑名单
            if (_policy.IpBlacklist != null && _policy.IpBlacklist.Count > 0)
            {
                if (IsIpInList(ip, _policy.IpBlacklist))
                {
                    _logger.LogInformation($"IP {ipAddress} is in blacklist, denying access");
                    return false;
                }
            }

            // Check geographic location if enabled / 如果启用，检查地理位置
            if (_policy.EnableGeoLocationLookup && _geoLocationProvider != null)
            {
                var geoInfo = await GetCachedGeoLocationAsync(ipAddress);
                if (geoInfo != null)
                {
                    if (!IsGeoLocationAllowed(geoInfo))
                    {
                        _logger.LogInformation($"IP {ipAddress} from {geoInfo.CountryCode}/{geoInfo.CityName} is denied by geographic policy");
                        return false;
                    }
                }
                else
                {
                    _logger.LogWarning($"Could not determine geographic location for IP {ipAddress}, allowing by default");
                }
            }

            return true;
        }

        /// <summary>
        /// Check if IP is in the list (supports CIDR notation) / 检查 IP 是否在列表中（支持 CIDR 表示法）
        /// </summary>
        private bool IsIpInList(IPAddress ip, List<string> ipList)
        {
            foreach (var entry in ipList)
            {
                if (string.IsNullOrWhiteSpace(entry))
                    continue;

                // Check for CIDR notation / 检查 CIDR 表示法
                if (entry.Contains('/'))
                {
                    if (IsIpInCidrRange(ip, entry))
                        return true;
                }
                else
                {
                    // Direct IP match / 直接 IP 匹配
                    if (IPAddress.TryParse(entry, out var listIp) && ip.Equals(listIp))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Check if IP is in CIDR range / 检查 IP 是否在 CIDR 范围内
        /// </summary>
        private bool IsIpInCidrRange(IPAddress ip, string cidr)
        {
            try
            {
                var parts = cidr.Split('/');
                if (parts.Length != 2)
                    return false;

                if (!IPAddress.TryParse(parts[0], out var networkIp))
                    return false;

                if (!int.TryParse(parts[1], out var prefixLength))
                    return false;

                // Only support IPv4 for now / 目前仅支持 IPv4
                if (ip.AddressFamily != AddressFamily.InterNetwork || networkIp.AddressFamily != AddressFamily.InterNetwork)
                    return false;

                var ipBytes = ip.GetAddressBytes();
                var networkBytes = networkIp.GetAddressBytes();

                if (ipBytes.Length != 4 || networkBytes.Length != 4)
                    return false;

                var maskBytes = new byte[4];
                var fullBytes = prefixLength / 8;
                var remainderBits = prefixLength % 8;

                for (int i = 0; i < fullBytes; i++)
                {
                    maskBytes[i] = 0xFF;
                }

                if (remainderBits > 0 && fullBytes < 4)
                {
                    maskBytes[fullBytes] = (byte)(0xFF << (8 - remainderBits));
                }

                for (int i = 0; i < 4; i++)
                {
                    if ((ipBytes[i] & maskBytes[i]) != (networkBytes[i] & maskBytes[i]))
                        return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error checking CIDR range for {cidr}");
                return false;
            }
        }

        /// <summary>
        /// Get cached geographic location / 获取缓存的地理位置
        /// </summary>
        private async Task<GeoLocationInfo> GetCachedGeoLocationAsync(string ipAddress)
        {
            // Check cache / 检查缓存
            if (_geoLocationCache.TryGetValue(ipAddress, out var cached) &&
                cached.ExpiresAt > DateTime.UtcNow)
            {
                return cached.Location;
            }

            // Query from provider / 从提供者查询
            var location = await _geoLocationProvider.GetLocationAsync(ipAddress);
            if (location != null)
            {
                _geoLocationCache[ipAddress] = new CachedGeoLocation
                {
                    Location = location,
                    ExpiresAt = DateTime.UtcNow.AddSeconds(_policy.GeoLocationCacheSeconds)
                };
            }

            return location;
        }

        /// <summary>
        /// Check if geographic location is allowed / 检查地理位置是否允许
        /// </summary>
        private bool IsGeoLocationAllowed(GeoLocationInfo geoInfo)
        {
            // Check country whitelist / 检查国家白名单
            if (_policy.CountryWhitelist != null && _policy.CountryWhitelist.Count > 0)
            {
                if (!_policy.CountryWhitelist.Contains(geoInfo.CountryCode, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // Check country blacklist / 检查国家黑名单
            if (_policy.CountryBlacklist != null && _policy.CountryBlacklist.Count > 0)
            {
                if (_policy.CountryBlacklist.Contains(geoInfo.CountryCode, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // Check city whitelist / 检查城市白名单
            if (_policy.CityWhitelist != null && _policy.CityWhitelist.Count > 0)
            {
                var cityKey = $"{geoInfo.CountryCode}:{geoInfo.CityName}";
                if (!_policy.CityWhitelist.Any(c => 
                    string.Equals(c, cityKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c, geoInfo.CityName, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            // Check city blacklist / 检查城市黑名单
            if (_policy.CityBlacklist != null && _policy.CityBlacklist.Count > 0)
            {
                var cityKey = $"{geoInfo.CountryCode}:{geoInfo.CityName}";
                if (_policy.CityBlacklist.Any(c => 
                    string.Equals(c, cityKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c, geoInfo.CityName, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            // Check region whitelist / 检查地区白名单
            if (_policy.RegionWhitelist != null && _policy.RegionWhitelist.Count > 0)
            {
                var regionKey = $"{geoInfo.CountryCode}:{geoInfo.RegionName}";
                if (!_policy.RegionWhitelist.Any(r => 
                    string.Equals(r, regionKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r, geoInfo.RegionName, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            // Check region blacklist / 检查地区黑名单
            if (_policy.RegionBlacklist != null && _policy.RegionBlacklist.Count > 0)
            {
                var regionKey = $"{geoInfo.CountryCode}:{geoInfo.RegionName}";
                if (_policy.RegionBlacklist.Any(r => 
                    string.Equals(r, regionKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r, geoInfo.RegionName, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Clear geographic location cache / 清除地理位置缓存
        /// </summary>
        public void ClearCache()
        {
            _geoLocationCache.Clear();
        }

        /// <summary>
        /// Cached geographic location / 缓存的地理位置
        /// </summary>
        private class CachedGeoLocation
        {
            public GeoLocationInfo Location { get; set; }
            public DateTime ExpiresAt { get; set; }
        }
    }
}

