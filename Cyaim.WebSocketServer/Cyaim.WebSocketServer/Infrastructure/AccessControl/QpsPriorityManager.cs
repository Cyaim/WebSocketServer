using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Infrastructure.AccessControl
{
    /// <summary>
    /// QPS优先级管理器
    /// 根据优先级分配带宽资源，在固定带宽下缩小名单外用户的带宽占用，提高名单内用户的带宽
    /// </summary>
    public class QpsPriorityManager
    {
        private readonly ILogger<QpsPriorityManager> _logger;
        private readonly QpsPriorityPolicy _policy;
        private readonly PriorityListService _priorityListService;
        private readonly ConcurrentDictionary<string, PriorityBandwidthTracker> _connectionTrackers = new ConcurrentDictionary<string, PriorityBandwidthTracker>();

        public QpsPriorityManager(
            ILogger<QpsPriorityManager> logger,
            QpsPriorityPolicy policy,
            PriorityListService priorityListService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _priorityListService = priorityListService ?? throw new ArgumentNullException(nameof(priorityListService));
        }

        /// <summary>
        /// 获取连接的有效带宽限制（根据优先级计算）
        /// </summary>
        /// <param name="channel">通道路径</param>
        /// <param name="connectionId">连接ID</param>
        /// <param name="ipAddress">IP地址</param>
        /// <returns>该连接的有效带宽限制（字节/秒）</returns>
        public long GetEffectiveBandwidthLimit(string channel, string connectionId, string ipAddress)
        {
            if (!_policy.Enabled)
            {
                return long.MaxValue; // 未启用时，不限制
            }

            // 获取连接的优先级
            var connectionPriority = _priorityListService.GetConnectionPriority(connectionId, _policy.DefaultPriority);
            var ipPriority = _priorityListService.GetIpPriority(ipAddress, _policy.DefaultPriority);
            var priority = Math.Max(connectionPriority, ipPriority); // 取较高优先级

            // 获取通道策略（如果有）
            var channelPolicy = _policy.ChannelPolicies.TryGetValue(channel, out var cp) ? cp : null;

            // 确定总带宽限制
            long totalBandwidth = long.MaxValue;
            if (channelPolicy?.ChannelBandwidthLimit.HasValue == true)
            {
                totalBandwidth = channelPolicy.ChannelBandwidthLimit.Value;
            }
            else if (_policy.GlobalBandwidthLimit.HasValue)
            {
                totalBandwidth = _policy.GlobalBandwidthLimit.Value;
            }

            if (totalBandwidth == long.MaxValue)
            {
                return long.MaxValue; // 未设置总带宽限制
            }

            // 获取该优先级的带宽比例
            var priorityRatios = channelPolicy?.PriorityBandwidthRatios ?? _policy.PriorityBandwidthRatios;
            var defaultRatio = channelPolicy?.DefaultPriorityBandwidthRatio ?? _policy.DefaultPriorityBandwidthRatio;

            if (!priorityRatios.TryGetValue(priority, out var ratio))
            {
                ratio = defaultRatio;
            }

            // 计算该优先级的带宽分配
            var priorityBandwidth = (long)(totalBandwidth * ratio);

            // 如果启用了动态调整，根据实际连接数调整
            if (_policy.EnableDynamicBandwidthAdjustment)
            {
                priorityBandwidth = AdjustBandwidthByConnectionCount(channel, priority, priorityBandwidth, totalBandwidth);
            }

            // 应用最小带宽保障
            if (_policy.MinBandwidthGuarantee.HasValue && priorityBandwidth < _policy.MinBandwidthGuarantee.Value)
            {
                priorityBandwidth = _policy.MinBandwidthGuarantee.Value;
            }

            return priorityBandwidth;
        }

        /// <summary>
        /// 根据连接数动态调整带宽分配
        /// </summary>
        private long AdjustBandwidthByConnectionCount(string channel, int priority, long allocatedBandwidth, long totalBandwidth)
        {
            // 获取该通道下该优先级的连接数
            var priorityConnections = _connectionTrackers.Values
                .Count(t => t.Channel == channel && t.Priority == priority);

            if (priorityConnections == 0)
            {
                return allocatedBandwidth; // 没有连接，返回分配的带宽
            }

            // 计算每个连接的平均带宽
            var perConnectionBandwidth = allocatedBandwidth / priorityConnections;

            // 如果平均带宽太小，尝试从低优先级借用带宽
            var minBandwidth = _policy.MinBandwidthGuarantee ?? 0;
            if (perConnectionBandwidth < minBandwidth && minBandwidth > 0)
            {
                // 尝试从低优先级借用带宽
                var neededBandwidth = minBandwidth * priorityConnections;
                var borrowedBandwidth = Math.Min(neededBandwidth - allocatedBandwidth, totalBandwidth * 0.1); // 最多借用10%
                allocatedBandwidth += (long)borrowedBandwidth;
            }

            return allocatedBandwidth;
        }

        /// <summary>
        /// 记录连接使用带宽
        /// </summary>
        public void RecordConnection(string channel, string connectionId, string ipAddress)
        {
            if (!_policy.Enabled)
            {
                return;
            }

            var connectionPriority = _priorityListService.GetConnectionPriority(connectionId, _policy.DefaultPriority);
            var ipPriority = _priorityListService.GetIpPriority(ipAddress, _policy.DefaultPriority);
            var priority = Math.Max(connectionPriority, ipPriority);

            _connectionTrackers.AddOrUpdate(
                connectionId,
                new PriorityBandwidthTracker(connectionId, channel, priority),
                (key, existing) =>
                {
                    existing.Priority = priority; // 更新优先级
                    existing.Channel = channel;
                    return existing;
                });
        }

        /// <summary>
        /// 移除连接跟踪
        /// </summary>
        public void RemoveConnection(string connectionId)
        {
            _connectionTrackers.TryRemove(connectionId, out _);
        }

        /// <summary>
        /// 获取通道的优先级统计信息
        /// </summary>
        public Dictionary<int, int> GetChannelPriorityStatistics(string channel)
        {
            return _connectionTrackers.Values
                .Where(t => t.Channel == channel)
                .GroupBy(t => t.Priority)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        /// <summary>
        /// 清空所有连接跟踪
        /// </summary>
        public void Clear()
        {
            _connectionTrackers.Clear();
        }
    }

    /// <summary>
    /// 优先级带宽跟踪器
    /// </summary>
    internal class PriorityBandwidthTracker
    {
        public string ConnectionId { get; }
        public string Channel { get; set; }
        public int Priority { get; set; }

        public PriorityBandwidthTracker(string connectionId, string channel, int priority)
        {
            ConnectionId = connectionId;
            Channel = channel;
            Priority = priority;
        }
    }
}

