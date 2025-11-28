using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Infrastructure.AccessControl
{
    /// <summary>
    /// 优先级名单管理服务
    /// 支持动态添加、删除和查询优先级名单
    /// </summary>
    public class PriorityListService
    {
        private readonly ILogger<PriorityListService> _logger;
        private readonly ConcurrentDictionary<string, int> _ipPriorityMap = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, int> _connectionIdPriorityMap = new ConcurrentDictionary<string, int>();
        private readonly object _lockObject = new object();

        public PriorityListService(ILogger<PriorityListService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 添加IP到优先级名单（单个）
        /// </summary>
        /// <param name="ipAddress">IP地址（支持CIDR表示法）</param>
        /// <param name="priority">优先级等级</param>
        /// <returns>是否添加成功</returns>
        public bool AddIpToPriorityList(string ipAddress, int priority)
        {
            if (string.IsNullOrEmpty(ipAddress))
            {
                _logger.LogWarning("IP地址为空，无法添加到优先级名单");
                return false;
            }

            try
            {
                _ipPriorityMap.AddOrUpdate(ipAddress, priority, (key, oldValue) => priority);
                _logger.LogInformation($"已将IP {ipAddress} 添加到优先级名单，优先级: {priority}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"添加IP到优先级名单失败: {ipAddress}");
                return false;
            }
        }

        /// <summary>
        /// 批量添加IP到优先级名单
        /// </summary>
        /// <param name="ipAddresses">IP地址列表（支持CIDR表示法）</param>
        /// <param name="priority">优先级等级</param>
        /// <returns>成功添加的数量</returns>
        public int AddIpsToPriorityList(IEnumerable<string> ipAddresses, int priority)
        {
            if (ipAddresses == null)
            {
                return 0;
            }

            int successCount = 0;
            foreach (var ip in ipAddresses)
            {
                if (AddIpToPriorityList(ip, priority))
                {
                    successCount++;
                }
            }

            _logger.LogInformation($"批量添加IP到优先级名单完成，成功: {successCount}, 优先级: {priority}");
            return successCount;
        }

        /// <summary>
        /// 添加连接ID到优先级名单（单个）
        /// </summary>
        /// <param name="connectionId">连接ID</param>
        /// <param name="priority">优先级等级</param>
        /// <returns>是否添加成功</returns>
        public bool AddConnectionToPriorityList(string connectionId, int priority)
        {
            if (string.IsNullOrEmpty(connectionId))
            {
                _logger.LogWarning("连接ID为空，无法添加到优先级名单");
                return false;
            }

            try
            {
                _connectionIdPriorityMap.AddOrUpdate(connectionId, priority, (key, oldValue) => priority);
                _logger.LogInformation($"已将连接 {connectionId} 添加到优先级名单，优先级: {priority}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"添加连接到优先级名单失败: {connectionId}");
                return false;
            }
        }

        /// <summary>
        /// 批量添加连接到优先级名单
        /// </summary>
        /// <param name="connectionIds">连接ID列表</param>
        /// <param name="priority">优先级等级</param>
        /// <returns>成功添加的数量</returns>
        public int AddConnectionsToPriorityList(IEnumerable<string> connectionIds, int priority)
        {
            if (connectionIds == null)
            {
                return 0;
            }

            int successCount = 0;
            foreach (var connId in connectionIds)
            {
                if (AddConnectionToPriorityList(connId, priority))
                {
                    successCount++;
                }
            }

            _logger.LogInformation($"批量添加连接到优先级名单完成，成功: {successCount}, 优先级: {priority}");
            return successCount;
        }

        /// <summary>
        /// 从优先级名单中移除IP（单个）
        /// </summary>
        /// <param name="ipAddress">IP地址</param>
        /// <returns>是否移除成功</returns>
        public bool RemoveIpFromPriorityList(string ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress))
            {
                return false;
            }

            if (_ipPriorityMap.TryRemove(ipAddress, out var priority))
            {
                _logger.LogInformation($"已从优先级名单中移除IP: {ipAddress} (原优先级: {priority})");
                return true;
            }

            return false;
        }

        /// <summary>
        /// 批量从优先级名单中移除IP
        /// </summary>
        /// <param name="ipAddresses">IP地址列表</param>
        /// <returns>成功移除的数量</returns>
        public int RemoveIpsFromPriorityList(IEnumerable<string> ipAddresses)
        {
            if (ipAddresses == null)
            {
                return 0;
            }

            int successCount = 0;
            foreach (var ip in ipAddresses)
            {
                if (RemoveIpFromPriorityList(ip))
                {
                    successCount++;
                }
            }

            _logger.LogInformation($"批量移除IP完成，成功: {successCount}");
            return successCount;
        }

        /// <summary>
        /// 从优先级名单中移除连接（单个）
        /// </summary>
        /// <param name="connectionId">连接ID</param>
        /// <returns>是否移除成功</returns>
        public bool RemoveConnectionFromPriorityList(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId))
            {
                return false;
            }

            if (_connectionIdPriorityMap.TryRemove(connectionId, out var priority))
            {
                _logger.LogInformation($"已从优先级名单中移除连接: {connectionId} (原优先级: {priority})");
                return true;
            }

            return false;
        }

        /// <summary>
        /// 批量从优先级名单中移除连接
        /// </summary>
        /// <param name="connectionIds">连接ID列表</param>
        /// <returns>成功移除的数量</returns>
        public int RemoveConnectionsFromPriorityList(IEnumerable<string> connectionIds)
        {
            if (connectionIds == null)
            {
                return 0;
            }

            int successCount = 0;
            foreach (var connId in connectionIds)
            {
                if (RemoveConnectionFromPriorityList(connId))
                {
                    successCount++;
                }
            }

            _logger.LogInformation($"批量移除连接完成，成功: {successCount}");
            return successCount;
        }

        /// <summary>
        /// 获取IP的优先级
        /// </summary>
        /// <param name="ipAddress">IP地址</param>
        /// <param name="defaultPriority">默认优先级（如果不在名单中）</param>
        /// <returns>优先级等级</returns>
        public int GetIpPriority(string ipAddress, int defaultPriority = 1)
        {
            if (string.IsNullOrEmpty(ipAddress))
            {
                return defaultPriority;
            }

            // 首先检查精确匹配
            if (_ipPriorityMap.TryGetValue(ipAddress, out var priority))
            {
                return priority;
            }

            // 检查CIDR匹配
            if (IPAddress.TryParse(ipAddress, out var ip))
            {
                foreach (var kvp in _ipPriorityMap)
                {
                    if (IsIpInCidrRange(ip, kvp.Key))
                    {
                        return kvp.Value;
                    }
                }
            }

            return defaultPriority;
        }

        /// <summary>
        /// 获取连接的优先级
        /// </summary>
        /// <param name="connectionId">连接ID</param>
        /// <param name="defaultPriority">默认优先级（如果不在名单中）</param>
        /// <returns>优先级等级</returns>
        public int GetConnectionPriority(string connectionId, int defaultPriority = 1)
        {
            if (string.IsNullOrEmpty(connectionId))
            {
                return defaultPriority;
            }

            return _connectionIdPriorityMap.TryGetValue(connectionId, out var priority) ? priority : defaultPriority;
        }

        /// <summary>
        /// 获取所有优先级名单中的IP
        /// </summary>
        /// <param name="priority">优先级等级（可选，如果指定则只返回该优先级的IP）</param>
        /// <returns>IP地址和优先级的字典</returns>
        public Dictionary<string, int> GetAllIpPriorities(int? priority = null)
        {
            if (priority.HasValue)
            {
                return _ipPriorityMap.Where(kvp => kvp.Value == priority.Value)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }

            return _ipPriorityMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        /// <summary>
        /// 获取所有优先级名单中的连接
        /// </summary>
        /// <param name="priority">优先级等级（可选，如果指定则只返回该优先级的连接）</param>
        /// <returns>连接ID和优先级的字典</returns>
        public Dictionary<string, int> GetAllConnectionPriorities(int? priority = null)
        {
            if (priority.HasValue)
            {
                return _connectionIdPriorityMap.Where(kvp => kvp.Value == priority.Value)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }

            return _connectionIdPriorityMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        /// <summary>
        /// 清空所有优先级名单
        /// </summary>
        public void ClearAll()
        {
            lock (_lockObject)
            {
                _ipPriorityMap.Clear();
                _connectionIdPriorityMap.Clear();
                _logger.LogInformation("已清空所有优先级名单");
            }
        }

        /// <summary>
        /// 检查IP是否在CIDR范围内
        /// </summary>
        private bool IsIpInCidrRange(IPAddress ip, string cidr)
        {
            if (string.IsNullOrEmpty(cidr))
            {
                return false;
            }

            try
            {
                var parts = cidr.Split('/');
                if (parts.Length != 2)
                {
                    return false;
                }

                if (!IPAddress.TryParse(parts[0], out var networkIp))
                {
                    return false;
                }

                if (!int.TryParse(parts[1], out var prefixLength))
                {
                    return false;
                }

                if (ip.AddressFamily != networkIp.AddressFamily)
                {
                    return false;
                }

                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    // IPv4
                    var ipBytes = ip.GetAddressBytes();
                    var networkBytes = networkIp.GetAddressBytes();
                    var maskBytes = GetMaskBytes(prefixLength, 4);

                    for (int i = 0; i < 4; i++)
                    {
                        if ((ipBytes[i] & maskBytes[i]) != (networkBytes[i] & maskBytes[i]))
                        {
                            return false;
                        }
                    }

                    return true;
                }
                else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    // IPv6
                    var ipBytes = ip.GetAddressBytes();
                    var networkBytes = networkIp.GetAddressBytes();
                    var maskBytes = GetMaskBytes(prefixLength, 16);

                    for (int i = 0; i < 16; i++)
                    {
                        if ((ipBytes[i] & maskBytes[i]) != (networkBytes[i] & maskBytes[i]))
                        {
                            return false;
                        }
                    }

                    return true;
                }
            }
            catch
            {
                // 忽略解析错误
            }

            return false;
        }

        /// <summary>
        /// 获取子网掩码字节数组
        /// </summary>
        private byte[] GetMaskBytes(int prefixLength, int addressLength)
        {
            var maskBytes = new byte[addressLength];
            var fullBytes = prefixLength / 8;
            var remainderBits = prefixLength % 8;

            for (int i = 0; i < fullBytes; i++)
            {
                maskBytes[i] = 0xFF;
            }

            if (remainderBits > 0 && fullBytes < addressLength)
            {
                maskBytes[fullBytes] = (byte)(0xFF << (8 - remainderBits));
            }

            return maskBytes;
        }
    }
}

