using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using Cyaim.WebSocketServer.Dashboard.Models;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Dashboard.Services
{
    /// <summary>
    /// Service for collecting dashboard statistics
    /// 用于收集仪表板统计信息的服务
    /// </summary>
    public class DashboardStatisticsService
    {
        private readonly ILogger<DashboardStatisticsService> _logger;
        private readonly ConcurrentDictionary<string, ClientConnectionStats> _connectionStats;
        private readonly Timer _bandwidthTimer;
        private BandwidthStatistics _lastBandwidthStats;
        private DateTime _lastBandwidthUpdate;
        private long _lastTotalBytesSent;
        private long _lastTotalBytesReceived;
        private long _lastTotalMessagesSent;
        private long _lastTotalMessagesReceived;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="logger">Logger instance / 日志实例</param>
        public DashboardStatisticsService(ILogger<DashboardStatisticsService> logger)
        {
            _logger = logger;
            _connectionStats = new ConcurrentDictionary<string, ClientConnectionStats>();
            _lastBandwidthUpdate = DateTime.UtcNow;
            _lastBandwidthStats = new BandwidthStatistics { Timestamp = DateTime.UtcNow };

            // Update bandwidth statistics every second / 每秒更新带宽统计
            _bandwidthTimer = new Timer(UpdateBandwidthStatistics, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Record sent bytes / 记录发送的字节数
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="bytes">Bytes sent / 发送的字节数</param>
        public void RecordBytesSent(string connectionId, int bytes)
        {
            if (string.IsNullOrEmpty(connectionId))
                return;

            var stats = _connectionStats.GetOrAdd(connectionId, _ => new ClientConnectionStats { ConnectionId = connectionId });
            lock (stats)
            {
                stats.BytesSent += bytes;
                stats.MessagesSent++;
            }
        }

        /// <summary>
        /// Record received bytes / 记录接收的字节数
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="bytes">Bytes received / 接收的字节数</param>
        public void RecordBytesReceived(string connectionId, int bytes)
        {
            if (string.IsNullOrEmpty(connectionId))
                return;

            var stats = _connectionStats.GetOrAdd(connectionId, _ => new ClientConnectionStats { ConnectionId = connectionId });
            lock (stats)
            {
                stats.BytesReceived += bytes;
                stats.MessagesReceived++;
            }
        }

        /// <summary>
        /// Remove connection statistics / 移除连接统计信息
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        public void RemoveConnection(string connectionId)
        {
            _connectionStats.TryRemove(connectionId, out _);
        }

        /// <summary>
        /// Get bandwidth statistics / 获取带宽统计信息
        /// </summary>
        /// <returns>Bandwidth statistics / 带宽统计信息</returns>
        public BandwidthStatistics GetBandwidthStatistics()
        {
            return _lastBandwidthStats;
        }

        /// <summary>
        /// Get connection statistics / 获取连接统计信息
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <returns>Connection statistics or null / 连接统计信息或 null</returns>
        public ClientConnectionStats GetConnectionStats(string connectionId)
        {
            _connectionStats.TryGetValue(connectionId, out var stats);
            return stats;
        }

        /// <summary>
        /// Get all connection statistics / 获取所有连接统计信息
        /// </summary>
        /// <returns>All connection statistics / 所有连接统计信息</returns>
        public IEnumerable<ClientConnectionStats> GetAllConnectionStats()
        {
            return _connectionStats.Values;
        }

        /// <summary>
        /// Update bandwidth statistics / 更新带宽统计信息
        /// </summary>
        private void UpdateBandwidthStatistics(object state)
        {
            try
            {
                var now = DateTime.UtcNow;
                var elapsed = (now - _lastBandwidthUpdate).TotalSeconds;
                if (elapsed <= 0)
                    return;

                var totalBytesSent = _connectionStats.Values.Sum(s => s.BytesSent);
                var totalBytesReceived = _connectionStats.Values.Sum(s => s.BytesReceived);
                var totalMessagesSent = _connectionStats.Values.Sum(s => s.MessagesSent);
                var totalMessagesReceived = _connectionStats.Values.Sum(s => s.MessagesReceived);

                var bytesSentPerSecond = (totalBytesSent - _lastTotalBytesSent) / elapsed;
                var bytesReceivedPerSecond = (totalBytesReceived - _lastTotalBytesReceived) / elapsed;
                var messagesSentPerSecond = (totalMessagesSent - _lastTotalMessagesSent) / elapsed;
                var messagesReceivedPerSecond = (totalMessagesReceived - _lastTotalMessagesReceived) / elapsed;

                _lastBandwidthStats = new BandwidthStatistics
                {
                    TotalBytesSent = totalBytesSent,
                    TotalBytesReceived = totalBytesReceived,
                    BytesSentPerSecond = bytesSentPerSecond,
                    BytesReceivedPerSecond = bytesReceivedPerSecond,
                    TotalMessagesSent = totalMessagesSent,
                    TotalMessagesReceived = totalMessagesReceived,
                    MessagesSentPerSecond = messagesSentPerSecond,
                    MessagesReceivedPerSecond = messagesReceivedPerSecond,
                    Timestamp = now
                };

                _lastTotalBytesSent = totalBytesSent;
                _lastTotalBytesReceived = totalBytesReceived;
                _lastTotalMessagesSent = totalMessagesSent;
                _lastTotalMessagesReceived = totalMessagesReceived;
                _lastBandwidthUpdate = now;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating bandwidth statistics");
            }
        }

        /// <summary>
        /// Dispose resources / 释放资源
        /// </summary>
        public void Dispose()
        {
            _bandwidthTimer?.Dispose();
        }
    }

    /// <summary>
    /// Client connection statistics / 客户端连接统计信息
    /// </summary>
    public class ClientConnectionStats
    {
        /// <summary>
        /// Connection ID / 连接 ID
        /// </summary>
        public string ConnectionId { get; set; }

        /// <summary>
        /// Bytes sent / 发送的字节数
        /// </summary>
        public long BytesSent { get; set; }

        /// <summary>
        /// Bytes received / 接收的字节数
        /// </summary>
        public long BytesReceived { get; set; }

        /// <summary>
        /// Messages sent / 发送的消息数
        /// </summary>
        public long MessagesSent { get; set; }

        /// <summary>
        /// Messages received / 接收的消息数
        /// </summary>
        public long MessagesReceived { get; set; }
    }
}

