using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure.AccessControl
{
    /// <summary>
    /// Interface for geographic location lookup / 地理位置查询接口
    /// </summary>
    public interface IGeoLocationProvider
    {
        /// <summary>
        /// Get geographic location information for an IP address / 获取 IP 地址的地理位置信息
        /// </summary>
        /// <param name="ipAddress">IP address / IP 地址</param>
        /// <returns>Geographic location information or null if not found / 地理位置信息，如果未找到则返回 null</returns>
        Task<GeoLocationInfo> GetLocationAsync(string ipAddress);
    }

    /// <summary>
    /// Geographic location information / 地理位置信息
    /// </summary>
    public class GeoLocationInfo
    {
        /// <summary>
        /// Country code (ISO 3166-1 alpha-2, e.g., "CN", "US") / 国家代码（ISO 3166-1 alpha-2，例如："CN", "US"）
        /// </summary>
        public string CountryCode { get; set; }

        /// <summary>
        /// Country name / 国家名称
        /// </summary>
        public string CountryName { get; set; }

        /// <summary>
        /// Region/State name / 地区/州名称
        /// </summary>
        public string RegionName { get; set; }

        /// <summary>
        /// City name / 城市名称
        /// </summary>
        public string CityName { get; set; }

        /// <summary>
        /// Latitude / 纬度
        /// </summary>
        public double? Latitude { get; set; }

        /// <summary>
        /// Longitude / 经度
        /// </summary>
        public double? Longitude { get; set; }
    }
}

