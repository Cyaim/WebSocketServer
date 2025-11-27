using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Infrastructure.AccessControl
{
    /// <summary>
    /// Geographic location provider using ChunZhen (纯真) IP database offline file (qqwry.dat)
    /// 使用纯真 IP 数据库离线文件（qqwry.dat）的地理位置提供者
    /// Database format: qqwry.dat / 数据库格式：qqwry.dat
    /// </summary>
    public class ChunZhenOfflineGeoLocationProvider : IGeoLocationProvider
    {
        private readonly ILogger<ChunZhenOfflineGeoLocationProvider> _logger;
        private readonly string _databasePath;
        private readonly object _lockObject = new object();
        private byte[] _databaseData;
        private bool _databaseLoaded;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="databasePath">Path to qqwry.dat file / qqwry.dat 文件路径</param>
        public ChunZhenOfflineGeoLocationProvider(
            ILogger<ChunZhenOfflineGeoLocationProvider> logger,
            string databasePath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));

            if (!File.Exists(_databasePath))
            {
                throw new FileNotFoundException($"ChunZhen database file not found: {_databasePath}");
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
                var location = SearchLocation(ipValue);
                if (location == null)
                {
                    return null;
                }

                // Parse location string (format: "国家 省份 城市" or "Country Region City") / 解析位置字符串（格式："国家 省份 城市" 或 "Country Region City"）
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
                    _logger.LogInformation($"ChunZhen database loaded: {_databasePath} ({_databaseData.Length} bytes)");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to load ChunZhen database: {_databasePath}");
                    throw;
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Search location in qqwry.dat database / 在 qqwry.dat 数据库中搜索位置
        /// </summary>
        private string SearchLocation(uint ipValue)
        {
            if (_databaseData == null || _databaseData.Length < 8)
                return null;

            try
            {
                // Read index offset from header / 从头部读取索引偏移
                var indexOffset = ReadUInt32(_databaseData, 0);
                var recordCount = (ReadUInt32(_databaseData, 4) - indexOffset) / 7;

                // Binary search / 二分搜索
                uint left = 0;
                uint right = recordCount;
                uint middle;
                uint recordOffset;

                while (left < right)
                {
                    middle = (left + right) / 2;
                    recordOffset = indexOffset + middle * 7;
                    var recordIp = ReadUInt32(_databaseData, (int)recordOffset);

                    if (ipValue < recordIp)
                    {
                        right = middle;
                    }
                    else
                    {
                        left = middle + 1;
                    }
                }

                if (left > 0)
                {
                    recordOffset = indexOffset + (left - 1) * 7;
                    var recordIp = ReadUInt32(_databaseData, (int)recordOffset);
                    var locationOffset = ReadUInt24(_databaseData, (int)(recordOffset + 4));

                    if (locationOffset < _databaseData.Length)
                    {
                        return ReadLocationString((int)locationOffset);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error searching in ChunZhen database");
            }

            return null;
        }

        /// <summary>
        /// Read location string from database / 从数据库读取位置字符串
        /// </summary>
        private string ReadLocationString(int offset)
        {
            if (offset < 0 || offset >= _databaseData.Length)
                return null;

            // Check for redirect / 检查重定向
            if (_databaseData[offset] == 0x01 || _databaseData[offset] == 0x02)
            {
                var redirectOffset = ReadUInt24(_databaseData, offset + 1);
                if (redirectOffset < _databaseData.Length)
                {
                    offset = (int)redirectOffset;
                }
            }

            // Read string (GB2312 encoding) / 读取字符串（GB2312 编码）
            var endOffset = offset;
            while (endOffset < _databaseData.Length && _databaseData[endOffset] != 0)
            {
                endOffset++;
            }

            if (endOffset > offset)
            {
                var bytes = new byte[endOffset - offset];
                Array.Copy(_databaseData, offset, bytes, 0, bytes.Length);
                
                // Try to decode as GB2312, fallback to UTF-8 / 尝试解码为 GB2312，回退到 UTF-8
                try
                {
                    return System.Text.Encoding.GetEncoding("GB2312").GetString(bytes);
                }
                catch
                {
                    return System.Text.Encoding.UTF8.GetString(bytes);
                }
            }

            return null;
        }

        /// <summary>
        /// Read UInt32 from byte array / 从字节数组读取 UInt32
        /// </summary>
        private uint ReadUInt32(byte[] data, int offset)
        {
            if (offset + 4 > data.Length)
                return 0;
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        /// <summary>
        /// Read UInt24 from byte array / 从字节数组读取 UInt24
        /// </summary>
        private uint ReadUInt24(byte[] data, int offset)
        {
            if (offset + 3 > data.Length)
                return 0;
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16));
        }

        /// <summary>
        /// Parse location string to GeoLocationInfo / 将位置字符串解析为 GeoLocationInfo
        /// </summary>
        private GeoLocationInfo ParseLocationString(string location)
        {
            if (string.IsNullOrEmpty(location))
                return new GeoLocationInfo();

            // Common format: "国家 省份 城市" or "Country Region City" / 常见格式："国家 省份 城市" 或 "Country Region City"
            var parts = location.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            
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
            else if (parts.Length > 1)
            {
                // Sometimes city is in region / 有时城市在地区中
                geoInfo.CityName = parts[1];
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
                { "United States", "US" },
                { "日本", "JP" },
                { "Japan", "JP" }
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

