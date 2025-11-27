using System.Collections.Generic;

namespace Cyaim.WebSocketServer.Infrastructure.AccessControl
{
    /// <summary>
    /// Access control policy for IP and geographic location filtering
    /// IP 和地理位置访问控制策略
    /// </summary>
    public class AccessControlPolicy
    {
        /// <summary>
        /// Enable access control / 启用访问控制
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// IP whitelist / IP 白名单
        /// Supports CIDR notation (e.g., "192.168.1.0/24") / 支持 CIDR 表示法（例如："192.168.1.0/24"）
        /// </summary>
        public List<string> IpWhitelist { get; set; } = new List<string>();

        /// <summary>
        /// IP blacklist / IP 黑名单
        /// Supports CIDR notation (e.g., "10.0.0.0/8") / 支持 CIDR 表示法（例如："10.0.0.0/8"）
        /// </summary>
        public List<string> IpBlacklist { get; set; } = new List<string>();

        /// <summary>
        /// Country whitelist (ISO 3166-1 alpha-2 country codes, e.g., "CN", "US") / 国家白名单（ISO 3166-1 alpha-2 国家代码，例如："CN", "US"）
        /// </summary>
        public List<string> CountryWhitelist { get; set; } = new List<string>();

        /// <summary>
        /// Country blacklist (ISO 3166-1 alpha-2 country codes) / 国家黑名单（ISO 3166-1 alpha-2 国家代码）
        /// </summary>
        public List<string> CountryBlacklist { get; set; } = new List<string>();

        /// <summary>
        /// City whitelist (format: "CountryCode:CityName", e.g., "CN:Beijing", "US:New York") / 城市白名单（格式："CountryCode:CityName"，例如："CN:Beijing", "US:New York"）
        /// </summary>
        public List<string> CityWhitelist { get; set; } = new List<string>();

        /// <summary>
        /// City blacklist (format: "CountryCode:CityName") / 城市黑名单（格式："CountryCode:CityName"）
        /// </summary>
        public List<string> CityBlacklist { get; set; } = new List<string>();

        /// <summary>
        /// Region whitelist (format: "CountryCode:RegionName", e.g., "CN:Beijing", "US:California") / 地区白名单（格式："CountryCode:RegionName"，例如："CN:Beijing", "US:California"）
        /// </summary>
        public List<string> RegionWhitelist { get; set; } = new List<string>();

        /// <summary>
        /// Region blacklist (format: "CountryCode:RegionName") / 地区黑名单（格式："CountryCode:RegionName"）
        /// </summary>
        public List<string> RegionBlacklist { get; set; } = new List<string>();

        /// <summary>
        /// Action when access is denied / 拒绝访问时的操作
        /// </summary>
        public AccessDeniedAction DeniedAction { get; set; } = AccessDeniedAction.CloseConnection;

        /// <summary>
        /// Custom denial message / 自定义拒绝消息
        /// </summary>
        public string DenialMessage { get; set; } = "Access denied";

        /// <summary>
        /// Enable geographic location lookup / 启用地理位置查询
        /// </summary>
        public bool EnableGeoLocationLookup { get; set; } = true;

        /// <summary>
        /// Cache geographic location results (in seconds) / 缓存地理位置查询结果（秒）
        /// </summary>
        public int GeoLocationCacheSeconds { get; set; } = 3600; // 1 hour
    }

    /// <summary>
    /// Action to take when access is denied / 拒绝访问时的操作
    /// </summary>
    public enum AccessDeniedAction
    {
        /// <summary>
        /// Close connection immediately / 立即关闭连接
        /// </summary>
        CloseConnection,

        /// <summary>
        /// Return HTTP 403 Forbidden / 返回 HTTP 403 Forbidden
        /// </summary>
        ReturnForbidden,

        /// <summary>
        /// Return HTTP 401 Unauthorized / 返回 HTTP 401 Unauthorized
        /// </summary>
        ReturnUnauthorized
    }
}

