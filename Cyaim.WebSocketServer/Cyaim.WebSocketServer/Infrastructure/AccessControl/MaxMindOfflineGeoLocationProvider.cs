using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Infrastructure.AccessControl
{
    /// <summary>
    /// Geographic location provider using MaxMind GeoIP2/GeoLite2 offline database
    /// 使用 MaxMind GeoIP2/GeoLite2 离线数据库的地理位置提供者
    /// Database format: .mmdb (MaxMind Binary Database) / 数据库格式：.mmdb (MaxMind 二进制数据库)
    /// Note: Requires MaxMind.GeoIP2 NuGet package / 注意：需要 MaxMind.GeoIP2 NuGet 包
    /// Download: https://dev.maxmind.com/geoip/geoip2/geolite2/ / 下载：https://dev.maxmind.com/geoip/geoip2/geolite2/
    /// </summary>
    public class MaxMindOfflineGeoLocationProvider : IGeoLocationProvider
    {
        private readonly ILogger<MaxMindOfflineGeoLocationProvider> _logger;
        private readonly string _databasePath;
        private readonly object _lockObject = new object();
        private object _databaseReader;
        private bool _databaseLoaded;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="databasePath">Path to MaxMind .mmdb database file / MaxMind .mmdb 数据库文件路径</param>
        public MaxMindOfflineGeoLocationProvider(
            ILogger<MaxMindOfflineGeoLocationProvider> logger,
            string databasePath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));

            if (!File.Exists(_databasePath))
            {
                throw new FileNotFoundException($"MaxMind database file not found: {_databasePath}");
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

                if (_databaseReader == null)
                {
                    _logger.LogWarning("MaxMind database reader is not available. Please install MaxMind.GeoIP2 NuGet package.");
                    return null;
                }

                // Parse IP address / 解析 IP 地址
                if (!IPAddress.TryParse(ipAddress, out var ip))
                {
                    _logger.LogWarning($"Invalid IP address: {ipAddress}");
                    return null;
                }

                // Query database using reflection (to avoid hard dependency) / 使用反射查询数据库（避免硬依赖）
                var geoInfo = QueryDatabase(ip);
                if (geoInfo != null)
                {
                    _logger.LogDebug($"Geo lookup for IP {ipAddress}: {geoInfo.CountryCode}/{geoInfo.CityName}");
                }

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
                    // Try to load MaxMind database using reflection / 尝试使用反射加载 MaxMind 数据库
                    var geoip2Assembly = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "MaxMind.GeoIP2");

                    if (geoip2Assembly == null)
                    {
                        _logger.LogWarning("MaxMind.GeoIP2 assembly not found. Please install MaxMind.GeoIP2 NuGet package.");
                        _databaseLoaded = true; // Mark as loaded to avoid repeated warnings / 标记为已加载以避免重复警告
                        return Task.CompletedTask;
                    }

                    var databaseReaderType = geoip2Assembly.GetType("MaxMind.GeoIP2.DatabaseReader");
                    if (databaseReaderType == null)
                    {
                        _logger.LogWarning("MaxMind.GeoIP2.DatabaseReader type not found.");
                        _databaseLoaded = true;
                        return Task.CompletedTask;
                    }

                    var constructor = databaseReaderType.GetConstructor(new[] { typeof(string) });
                    if (constructor == null)
                    {
                        _logger.LogWarning("MaxMind.GeoIP2.DatabaseReader constructor not found.");
                        _databaseLoaded = true;
                        return Task.CompletedTask;
                    }

                    _databaseReader = constructor.Invoke(new object[] { _databasePath });
                    _databaseLoaded = true;
                    _logger.LogInformation($"MaxMind database loaded: {_databasePath}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to load MaxMind database: {_databasePath}");
                    _databaseLoaded = true; // Mark as loaded to avoid repeated errors / 标记为已加载以避免重复错误
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Query database using reflection / 使用反射查询数据库
        /// </summary>
        private GeoLocationInfo QueryDatabase(IPAddress ip)
        {
            if (_databaseReader == null)
                return null;

            try
            {
                // Use reflection to call City() method / 使用反射调用 City() 方法
                var cityMethod = _databaseReader.GetType().GetMethod("City", new[] { typeof(IPAddress) });
                if (cityMethod == null)
                {
                    _logger.LogWarning("MaxMind DatabaseReader.City method not found.");
                    return null;
                }

                var cityResponse = cityMethod.Invoke(_databaseReader, new object[] { ip });
                if (cityResponse == null)
                    return null;

                var geoInfo = new GeoLocationInfo();

                // Get Country / 获取国家
                var countryProperty = cityResponse.GetType().GetProperty("Country");
                if (countryProperty != null)
                {
                    var country = countryProperty.GetValue(cityResponse);
                    if (country != null)
                    {
                        var countryNameProperty = country.GetType().GetProperty("Name");
                        var countryIsoCodeProperty = country.GetType().GetProperty("IsoCode");

                        geoInfo.CountryName = countryNameProperty?.GetValue(country)?.ToString();
                        geoInfo.CountryCode = countryIsoCodeProperty?.GetValue(country)?.ToString();
                    }
                }

                // Get Subdivision (Region) / 获取地区
                var subdivisionsProperty = cityResponse.GetType().GetProperty("Subdivisions");
                if (subdivisionsProperty != null)
                {
                    var subdivisions = subdivisionsProperty.GetValue(cityResponse);
                    if (subdivisions != null)
                    {
                        // Get first subdivision / 获取第一个地区
                        var enumerable = subdivisions as System.Collections.IEnumerable;
                        if (enumerable != null)
                        {
                            var enumerator = enumerable.GetEnumerator();
                            if (enumerator.MoveNext())
                            {
                                var subdivision = enumerator.Current;
                                var subdivisionNameProperty = subdivision?.GetType().GetProperty("Name");
                                geoInfo.RegionName = subdivisionNameProperty?.GetValue(subdivision)?.ToString();
                            }
                        }
                    }
                }

                // Get City / 获取城市
                var cityProperty = cityResponse.GetType().GetProperty("City");
                if (cityProperty != null)
                {
                    var city = cityProperty.GetValue(cityResponse);
                    if (city != null)
                    {
                        var cityNameProperty = city.GetType().GetProperty("Name");
                        geoInfo.CityName = cityNameProperty?.GetValue(city)?.ToString();
                    }
                }

                // Get Location (Latitude/Longitude) / 获取位置（纬度/经度）
                var locationProperty = cityResponse.GetType().GetProperty("Location");
                if (locationProperty != null)
                {
                    var location = locationProperty.GetValue(cityResponse);
                    if (location != null)
                    {
                        var latitudeProperty = location.GetType().GetProperty("Latitude");
                        var longitudeProperty = location.GetType().GetProperty("Longitude");

                        if (latitudeProperty != null)
                        {
                            var lat = latitudeProperty.GetValue(location);
                            if (lat != null && double.TryParse(lat.ToString(), out var latValue))
                                geoInfo.Latitude = latValue;
                        }

                        if (longitudeProperty != null)
                        {
                            var lon = longitudeProperty.GetValue(location);
                            if (lon != null && double.TryParse(lon.ToString(), out var lonValue))
                                geoInfo.Longitude = lonValue;
                        }
                    }
                }

                return geoInfo;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error querying MaxMind database");
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

