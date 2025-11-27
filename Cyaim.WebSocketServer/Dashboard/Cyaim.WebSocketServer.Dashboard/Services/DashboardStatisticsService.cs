using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using Cyaim.WebSocketServer.Dashboard.Models;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Infrastructure.Metrics;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Dashboard.Services
{
    /// <summary>
    /// Service for collecting dashboard statistics
    /// 用于收集仪表板统计信息的服务
    /// </summary>
    public class DashboardStatisticsService : IWebSocketStatisticsRecorder
    {
        private readonly ILogger<DashboardStatisticsService> _logger;
        private readonly ConcurrentDictionary<string, ClientConnectionStats> _connectionStats;
        private readonly Timer _bandwidthTimer;
        private BandwidthStatistics _lastBandwidthStats;
        private DateTime _lastBandwidthUpdate;
        private ulong _lastTotalBytesSent;
        private ulong _lastTotalBytesReceived;
        private ulong _lastTotalMessagesSent;
        private ulong _lastTotalMessagesReceived;

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
            if (string.IsNullOrEmpty(connectionId) || bytes <= 0)
                return;

            var stats = _connectionStats.GetOrAdd(connectionId, _ => new ClientConnectionStats { ConnectionId = connectionId });
            
            // Use lock-free operations with Interlocked for better performance / 使用无锁的 Interlocked 操作以获得更好的性能
            // Interlocked.Add supports long, so we use long internally and convert to ulong when reading / Interlocked.Add 支持 long，所以内部使用 long，读取时转换为 ulong
            Interlocked.Add(ref stats._bytesSentLong, bytes);
            Interlocked.Increment(ref stats._messagesSentLong);
        }

        /// <summary>
        /// Record received bytes / 记录接收的字节数
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="bytes">Bytes received / 接收的字节数</param>
        public void RecordBytesReceived(string connectionId, int bytes)
        {
            if (string.IsNullOrEmpty(connectionId) || bytes <= 0)
                return;

            var stats = _connectionStats.GetOrAdd(connectionId, _ => new ClientConnectionStats { ConnectionId = connectionId });
            
            // Use lock-free operations with Interlocked for better performance / 使用无锁的 Interlocked 操作以获得更好的性能
            // Interlocked.Add supports long, so we use long internally and convert to ulong when reading / Interlocked.Add 支持 long，所以内部使用 long，读取时转换为 ulong
            Interlocked.Add(ref stats._bytesReceivedLong, bytes);
            Interlocked.Increment(ref stats._messagesReceivedLong);
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

                // Use Aggregate for ulong sum (LINQ Sum doesn't support ulong directly) / 使用 Aggregate 进行 ulong 求和（LINQ Sum 不直接支持 ulong）
                var totalBytesSent = _connectionStats.Values.Aggregate(0UL, (sum, s) => sum + s.BytesSent);
                var totalBytesReceived = _connectionStats.Values.Aggregate(0UL, (sum, s) => sum + s.BytesReceived);
                var totalMessagesSent = _connectionStats.Values.Aggregate(0UL, (sum, s) => sum + s.MessagesSent);
                var totalMessagesReceived = _connectionStats.Values.Aggregate(0UL, (sum, s) => sum + s.MessagesReceived);

                // Calculate per-second rates (handle potential overflow by checking if current > last)
                // 计算每秒速率（通过检查当前值是否大于上次值来处理潜在的溢出）
                var bytesSentPerSecond = totalBytesSent > _lastTotalBytesSent 
                    ? (totalBytesSent - _lastTotalBytesSent) / elapsed 
                    : 0.0;
                var bytesReceivedPerSecond = totalBytesReceived > _lastTotalBytesReceived 
                    ? (totalBytesReceived - _lastTotalBytesReceived) / elapsed 
                    : 0.0;
                var messagesSentPerSecond = totalMessagesSent > _lastTotalMessagesSent 
                    ? (totalMessagesSent - _lastTotalMessagesSent) / elapsed 
                    : 0.0;
                var messagesReceivedPerSecond = totalMessagesReceived > _lastTotalMessagesReceived 
                    ? (totalMessagesReceived - _lastTotalMessagesReceived) / elapsed 
                    : 0.0;

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

        // Internal fields for lock-free operations using long (Interlocked supports long) / 用于无锁操作的内部字段，使用 long（Interlocked 支持 long）
        // We use long internally and convert to ulong when reading / 内部使用 long，读取时转换为 ulong
        // This allows us to use Interlocked.Add which is much faster than lock / 这允许我们使用 Interlocked.Add，比 lock 快得多
        internal long _bytesSentLong;
        internal long _bytesReceivedLong;
        internal long _messagesSentLong;
        internal long _messagesReceivedLong;

        /// <summary>
        /// Bytes sent / 发送的字节数
        /// </summary>
        public ulong BytesSent 
        { 
            get => unchecked((ulong)Interlocked.Read(ref _bytesSentLong)); 
            set => Interlocked.Exchange(ref _bytesSentLong, unchecked((long)value)); 
        }

        /// <summary>
        /// Bytes received / 接收的字节数
        /// </summary>
        public ulong BytesReceived 
        { 
            get => unchecked((ulong)Interlocked.Read(ref _bytesReceivedLong)); 
            set => Interlocked.Exchange(ref _bytesReceivedLong, unchecked((long)value)); 
        }

        /// <summary>
        /// Messages sent / 发送的消息数
        /// </summary>
        public ulong MessagesSent 
        { 
            get => unchecked((ulong)Interlocked.Read(ref _messagesSentLong)); 
            set => Interlocked.Exchange(ref _messagesSentLong, unchecked((long)value)); 
        }

        /// <summary>
        /// Messages received / 接收的消息数
        /// </summary>
        public ulong MessagesReceived 
        { 
            get => unchecked((ulong)Interlocked.Read(ref _messagesReceivedLong)); 
            set => Interlocked.Exchange(ref _messagesReceivedLong, unchecked((long)value)); 
        }
    }
}

