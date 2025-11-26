using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure
{
    /// <summary>
    /// 带宽限速管理器
    /// </summary>
    public class BandwidthLimitManager
    {
        private readonly ILogger<BandwidthLimitManager> _logger;
        private readonly BandwidthLimitPolicy _policy;
        private readonly ConcurrentDictionary<string, ChannelBandwidthTracker> _channelTrackers = new ConcurrentDictionary<string, ChannelBandwidthTracker>();
        private readonly ConcurrentDictionary<string, EndPointBandwidthTracker> _endPointTrackers = new ConcurrentDictionary<string, EndPointBandwidthTracker>();
        private readonly ConcurrentDictionary<string, ConnectionBandwidthTracker> _connectionTrackers = new ConcurrentDictionary<string, ConnectionBandwidthTracker>();

        /// <summary>
        /// 限速策略更新事件
        /// </summary>
        public event Action<BandwidthLimitPolicy> PolicyUpdated;

        public BandwidthLimitManager(ILogger<BandwidthLimitManager> logger, BandwidthLimitPolicy policy)
        {
            _logger = logger;
            _policy = policy ?? new BandwidthLimitPolicy();
        }

        /// <summary>
        /// 更新限速策略
        /// </summary>
        public void UpdatePolicy(BandwidthLimitPolicy newPolicy)
        {
            if (newPolicy == null)
                return;

            // 更新策略配置（这里需要实现深拷贝或使用新的策略对象）
            _policy.Enabled = newPolicy.Enabled;
            _policy.GlobalChannelBandwidthLimit = newPolicy.GlobalChannelBandwidthLimit ?? new Dictionary<string, long>();
            _policy.ChannelMinBandwidthGuarantee = newPolicy.ChannelMinBandwidthGuarantee ?? new Dictionary<string, long>();
            _policy.ChannelMaxBandwidthLimit = newPolicy.ChannelMaxBandwidthLimit ?? new Dictionary<string, long>();
            _policy.ChannelEnableAverageBandwidth = newPolicy.ChannelEnableAverageBandwidth ?? new Dictionary<string, bool>();
            _policy.ChannelConnectionMinBandwidthGuarantee = newPolicy.ChannelConnectionMinBandwidthGuarantee ?? new Dictionary<string, long>();
            _policy.ChannelConnectionMaxBandwidthLimit = newPolicy.ChannelConnectionMaxBandwidthLimit ?? new Dictionary<string, long>();
            _policy.EndPointMaxBandwidthLimit = newPolicy.EndPointMaxBandwidthLimit ?? new Dictionary<string, long>();
            _policy.EndPointMinBandwidthGuarantee = newPolicy.EndPointMinBandwidthGuarantee ?? new Dictionary<string, long>();

            PolicyUpdated?.Invoke(_policy);
        }

        /// <summary>
        /// 等待接收数据（应用限速策略）
        /// </summary>
        /// <param name="channel">通道路径</param>
        /// <param name="connectionId">连接ID</param>
        /// <param name="endPoint">端点路径（可选）</param>
        /// <param name="dataSize">要接收的数据大小（字节）</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task WaitForBandwidthAsync(string channel, string connectionId, string endPoint, int dataSize, CancellationToken cancellationToken = default)
        {
            if (!_policy.Enabled || dataSize <= 0)
                return;

            try
            {
                // 获取或创建通道跟踪器
                var channelTracker = _channelTrackers.GetOrAdd(channel, _ => new ChannelBandwidthTracker(channel, _policy));

                // 获取或创建连接跟踪器
                var connectionTracker = _connectionTrackers.GetOrAdd(connectionId, _ => new ConnectionBandwidthTracker(connectionId, channel, _policy));

                // 获取或创建端点跟踪器（如果提供了端点）
                EndPointBandwidthTracker endPointTracker = null;
                if (!string.IsNullOrEmpty(endPoint))
                {
                    endPointTracker = _endPointTrackers.GetOrAdd(endPoint, _ => new EndPointBandwidthTracker(endPoint, _policy));
                }

                // 计算需要等待的时间
                var waitTime = CalculateWaitTime(channelTracker, connectionTracker, endPointTracker, dataSize);

                if (waitTime > TimeSpan.Zero)
                {
                    await Task.Delay(waitTime, cancellationToken);
                }

                // 更新统计信息
                channelTracker.RecordData(dataSize);
                connectionTracker.RecordData(dataSize);
                endPointTracker?.RecordData(dataSize);
            }
            catch (OperationCanceledException)
            {
                // 取消操作，正常情况
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, $"限速等待过程中发生异常: Channel={channel}, ConnectionId={connectionId}, EndPoint={endPoint}");
            }
        }

        /// <summary>
        /// 计算需要等待的时间
        /// </summary>
        private TimeSpan CalculateWaitTime(ChannelBandwidthTracker channelTracker, ConnectionBandwidthTracker connectionTracker, EndPointBandwidthTracker endPointTracker, int dataSize)
        {
            var maxWaitTime = TimeSpan.Zero;

            // 1. 全局服务级别：通道限速
            if (_policy.GlobalChannelBandwidthLimit.TryGetValue(channelTracker.Channel, out var globalLimit) && globalLimit > 0)
            {
                var channelWaitTime = channelTracker.CalculateWaitTime(dataSize, globalLimit);
                if (channelWaitTime > maxWaitTime)
                    maxWaitTime = channelWaitTime;
            }

            // 2. 通道级别（单个连接）：最低带宽保障和最高带宽限制
            if (_policy.ChannelMinBandwidthGuarantee.TryGetValue(channelTracker.Channel, out var minGuarantee) && minGuarantee > 0)
            {
                // 确保最低带宽保障
                var guaranteeWaitTime = connectionTracker.CalculateWaitTimeForGuarantee(dataSize, minGuarantee);
                if (guaranteeWaitTime > maxWaitTime)
                    maxWaitTime = guaranteeWaitTime;
            }

            if (_policy.ChannelMaxBandwidthLimit.TryGetValue(channelTracker.Channel, out var maxLimit) && maxLimit > 0)
            {
                var maxWaitTimeForLimit = connectionTracker.CalculateWaitTime(dataSize, maxLimit);
                if (maxWaitTimeForLimit > maxWaitTime)
                    maxWaitTime = maxWaitTimeForLimit;
            }

            // 3. 通道级别（多个连接）：平均分配带宽、最低保障、最高限制
            var activeConnections = GetActiveConnectionsCount(channelTracker.Channel);
            if (activeConnections > 1)
            {
                // 平均分配带宽策略
                if (_policy.ChannelEnableAverageBandwidth.TryGetValue(channelTracker.Channel, out var enableAverage) && enableAverage)
                {
                    if (_policy.GlobalChannelBandwidthLimit.TryGetValue(channelTracker.Channel, out var channelLimit) && channelLimit > 0)
                    {
                        var averageLimit = channelLimit / activeConnections;
                        var averageWaitTime = connectionTracker.CalculateWaitTime(dataSize, averageLimit);
                        if (averageWaitTime > maxWaitTime)
                            maxWaitTime = averageWaitTime;
                    }
                }

                // 连接最低带宽保障
                if (_policy.ChannelConnectionMinBandwidthGuarantee.TryGetValue(channelTracker.Channel, out var connMinGuarantee) && connMinGuarantee > 0)
                {
                    var connGuaranteeWaitTime = connectionTracker.CalculateWaitTimeForGuarantee(dataSize, connMinGuarantee);
                    if (connGuaranteeWaitTime > maxWaitTime)
                        maxWaitTime = connGuaranteeWaitTime;
                }

                // 连接最高带宽限制
                if (_policy.ChannelConnectionMaxBandwidthLimit.TryGetValue(channelTracker.Channel, out var connMaxLimit) && connMaxLimit > 0)
                {
                    var connMaxWaitTime = connectionTracker.CalculateWaitTime(dataSize, connMaxLimit);
                    if (connMaxWaitTime > maxWaitTime)
                        maxWaitTime = connMaxWaitTime;
                }
            }

            // 4. WebSocket端点级别限速
            if (endPointTracker != null)
            {
                if (_policy.EndPointMaxBandwidthLimit.TryGetValue(endPointTracker.EndPoint, out var endPointMaxLimit) && endPointMaxLimit > 0)
                {
                    var endPointWaitTime = endPointTracker.CalculateWaitTime(dataSize, endPointMaxLimit);
                    if (endPointWaitTime > maxWaitTime)
                        maxWaitTime = endPointWaitTime;
                }

                if (_policy.EndPointMinBandwidthGuarantee.TryGetValue(endPointTracker.EndPoint, out var endPointMinGuarantee) && endPointMinGuarantee > 0)
                {
                    var endPointGuaranteeWaitTime = endPointTracker.CalculateWaitTimeForGuarantee(dataSize, endPointMinGuarantee);
                    if (endPointGuaranteeWaitTime > maxWaitTime)
                        maxWaitTime = endPointGuaranteeWaitTime;
                }
            }

            return maxWaitTime;
        }

        /// <summary>
        /// 获取通道的活跃连接数
        /// </summary>
        private int GetActiveConnectionsCount(string channel)
        {
            return _connectionTrackers.Values.Count(t => t.Channel == channel);
        }

        /// <summary>
        /// 移除连接跟踪器
        /// </summary>
        public void RemoveConnection(string connectionId)
        {
            if (_connectionTrackers.TryRemove(connectionId, out var tracker))
            {
                tracker.Dispose();
            }
        }

        /// <summary>
        /// 清理所有跟踪器
        /// </summary>
        public void Clear()
        {
            foreach (var tracker in _channelTrackers.Values)
            {
                tracker.Dispose();
            }
            _channelTrackers.Clear();

            foreach (var tracker in _connectionTrackers.Values)
            {
                tracker.Dispose();
            }
            _connectionTrackers.Clear();

            foreach (var tracker in _endPointTrackers.Values)
            {
                tracker.Dispose();
            }
            _endPointTrackers.Clear();
        }
    }

    /// <summary>
    /// 通道带宽跟踪器（无锁实现）
    /// </summary>
    internal class ChannelBandwidthTracker : IDisposable
    {
        private readonly BandwidthLimitPolicy _policy;
        private long _totalBytes;
        private long _windowStartTicks; // 使用 Ticks 存储时间，支持原子操作
        private readonly long _windowSizeTicks = TimeSpan.FromSeconds(1).Ticks;

        public string Channel { get; }

        public ChannelBandwidthTracker(string channel, BandwidthLimitPolicy policy)
        {
            Channel = channel;
            _policy = policy;
            _windowStartTicks = DateTime.UtcNow.Ticks;
        }

        public void RecordData(int bytes)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            long currentWindowStart;
            long currentTotalBytes;
            long newWindowStart;
            long newTotalBytes;

            // 无锁循环，直到成功更新
            do
            {
                currentWindowStart = Interlocked.Read(ref _windowStartTicks);
                currentTotalBytes = Interlocked.Read(ref _totalBytes);

                // 检查是否需要重置窗口
                if (nowTicks - currentWindowStart >= _windowSizeTicks)
                {
                    newWindowStart = nowTicks;
                    newTotalBytes = bytes;
                }
                else
                {
                    newWindowStart = currentWindowStart;
                    newTotalBytes = currentTotalBytes + bytes;
                }

                // 尝试原子更新窗口开始时间
                var originalWindowStart = Interlocked.CompareExchange(ref _windowStartTicks, newWindowStart, currentWindowStart);
                if (originalWindowStart == currentWindowStart)
                {
                    // 窗口时间更新成功，更新总字节数
                    if (newWindowStart != currentWindowStart)
                    {
                        // 窗口已重置，直接设置新值
                        Interlocked.Exchange(ref _totalBytes, newTotalBytes);
                    }
                    else
                    {
                        // 窗口未重置，累加字节数
                        Interlocked.Add(ref _totalBytes, bytes);
                    }
                    break;
                }
                // 如果窗口时间被其他线程修改，重试
            } while (true);
        }

        public TimeSpan CalculateWaitTime(int dataSize, long limitBytesPerSecond)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            long currentWindowStart;
            long currentTotalBytes;

            // 无锁读取，可能需要重置窗口
            do
            {
                currentWindowStart = Interlocked.Read(ref _windowStartTicks);
                currentTotalBytes = Interlocked.Read(ref _totalBytes);

                var elapsedTicks = nowTicks - currentWindowStart;
                if (elapsedTicks >= _windowSizeTicks)
                {
                    // 需要重置窗口
                    var newWindowStart = nowTicks;
                    var originalWindowStart = Interlocked.CompareExchange(ref _windowStartTicks, newWindowStart, currentWindowStart);
                    if (originalWindowStart == currentWindowStart)
                    {
                        // 成功重置窗口
                        Interlocked.Exchange(ref _totalBytes, 0);
                        return TimeSpan.Zero;
                    }
                    // 窗口被其他线程重置，重试
                    continue;
                }

                // 窗口未过期，计算等待时间
                var elapsed = TimeSpan.FromTicks(elapsedTicks).TotalSeconds;
                var currentRate = currentTotalBytes / Math.Max(elapsed, 0.001);
                var targetRate = limitBytesPerSecond;

                if (currentRate + dataSize / TimeSpan.FromTicks(_windowSizeTicks).TotalSeconds <= targetRate)
                {
                    return TimeSpan.Zero;
                }

                // 计算需要等待的时间
                var excessBytes = currentTotalBytes + dataSize - (targetRate * elapsed);
                var waitSeconds = excessBytes / targetRate;
                return TimeSpan.FromSeconds(Math.Max(0, waitSeconds));
            } while (true);
        }

        public void Dispose()
        {
            // 清理资源
        }
    }

    /// <summary>
    /// 连接带宽跟踪器（无锁实现）
    /// </summary>
    internal class ConnectionBandwidthTracker : IDisposable
    {
        private readonly BandwidthLimitPolicy _policy;
        private long _totalBytes;
        private long _windowStartTicks; // 使用 Ticks 存储时间，支持原子操作
        private readonly long _windowSizeTicks = TimeSpan.FromSeconds(1).Ticks;

        public string ConnectionId { get; }
        public string Channel { get; }

        public ConnectionBandwidthTracker(string connectionId, string channel, BandwidthLimitPolicy policy)
        {
            ConnectionId = connectionId;
            Channel = channel;
            _policy = policy;
            _windowStartTicks = DateTime.UtcNow.Ticks;
        }

        public void RecordData(int bytes)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            long currentWindowStart;
            long currentTotalBytes;
            long newWindowStart;
            long newTotalBytes;

            // 无锁循环，直到成功更新
            do
            {
                currentWindowStart = Interlocked.Read(ref _windowStartTicks);
                currentTotalBytes = Interlocked.Read(ref _totalBytes);

                // 检查是否需要重置窗口
                if (nowTicks - currentWindowStart >= _windowSizeTicks)
                {
                    newWindowStart = nowTicks;
                    newTotalBytes = bytes;
                }
                else
                {
                    newWindowStart = currentWindowStart;
                    newTotalBytes = currentTotalBytes + bytes;
                }

                // 尝试原子更新窗口开始时间
                var originalWindowStart = Interlocked.CompareExchange(ref _windowStartTicks, newWindowStart, currentWindowStart);
                if (originalWindowStart == currentWindowStart)
                {
                    // 窗口时间更新成功，更新总字节数
                    if (newWindowStart != currentWindowStart)
                    {
                        // 窗口已重置，直接设置新值
                        Interlocked.Exchange(ref _totalBytes, newTotalBytes);
                    }
                    else
                    {
                        // 窗口未重置，累加字节数
                        Interlocked.Add(ref _totalBytes, bytes);
                    }
                    break;
                }
                // 如果窗口时间被其他线程修改，重试
            } while (true);
        }

        public TimeSpan CalculateWaitTime(int dataSize, long limitBytesPerSecond)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            long currentWindowStart;
            long currentTotalBytes;

            // 无锁读取，可能需要重置窗口
            do
            {
                currentWindowStart = Interlocked.Read(ref _windowStartTicks);
                currentTotalBytes = Interlocked.Read(ref _totalBytes);

                var elapsedTicks = nowTicks - currentWindowStart;
                if (elapsedTicks >= _windowSizeTicks)
                {
                    // 需要重置窗口
                    var newWindowStart = nowTicks;
                    var originalWindowStart = Interlocked.CompareExchange(ref _windowStartTicks, newWindowStart, currentWindowStart);
                    if (originalWindowStart == currentWindowStart)
                    {
                        // 成功重置窗口
                        Interlocked.Exchange(ref _totalBytes, 0);
                        return TimeSpan.Zero;
                    }
                    // 窗口被其他线程重置，重试
                    continue;
                }

                // 窗口未过期，计算等待时间
                var elapsed = TimeSpan.FromTicks(elapsedTicks).TotalSeconds;
                var currentRate = currentTotalBytes / Math.Max(elapsed, 0.001);
                var targetRate = limitBytesPerSecond;

                if (currentRate + dataSize / TimeSpan.FromTicks(_windowSizeTicks).TotalSeconds <= targetRate)
                {
                    return TimeSpan.Zero;
                }

                // 计算需要等待的时间
                var excessBytes = currentTotalBytes + dataSize - (targetRate * elapsed);
                var waitSeconds = excessBytes / targetRate;
                return TimeSpan.FromSeconds(Math.Max(0, waitSeconds));
            } while (true);
        }

        public TimeSpan CalculateWaitTimeForGuarantee(int dataSize, long guaranteeBytesPerSecond)
        {
            // 最低带宽保障：确保每个连接都能获得最低带宽
            var nowTicks = DateTime.UtcNow.Ticks;
            long currentWindowStart;
            long currentTotalBytes;

            // 无锁读取，可能需要重置窗口
            do
            {
                currentWindowStart = Interlocked.Read(ref _windowStartTicks);
                currentTotalBytes = Interlocked.Read(ref _totalBytes);

                var elapsedTicks = nowTicks - currentWindowStart;
                if (elapsedTicks >= _windowSizeTicks)
                {
                    // 需要重置窗口
                    var newWindowStart = nowTicks;
                    var originalWindowStart = Interlocked.CompareExchange(ref _windowStartTicks, newWindowStart, currentWindowStart);
                    if (originalWindowStart == currentWindowStart)
                    {
                        // 成功重置窗口
                        Interlocked.Exchange(ref _totalBytes, 0);
                        return TimeSpan.Zero;
                    }
                    // 窗口被其他线程重置，重试
                    continue;
                }

                // 窗口未过期，计算保障等待时间
                var elapsed = TimeSpan.FromTicks(elapsedTicks).TotalSeconds;
                
                // 如果当前速率低于保障速率，不需要等待
                var currentRate = currentTotalBytes / Math.Max(elapsed, 0.001);
                if (currentRate < guaranteeBytesPerSecond)
                {
                    return TimeSpan.Zero;
                }

                // 如果超过保障速率，需要等待
                var excessBytes = currentTotalBytes - (guaranteeBytesPerSecond * elapsed);
                if (excessBytes > 0)
                {
                    var waitSeconds = excessBytes / guaranteeBytesPerSecond;
                    return TimeSpan.FromSeconds(Math.Max(0, waitSeconds));
                }

                return TimeSpan.Zero;
            } while (true);
        }

        public void Dispose()
        {
            // 清理资源
        }
    }

    /// <summary>
    /// 端点带宽跟踪器（无锁实现）
    /// </summary>
    internal class EndPointBandwidthTracker : IDisposable
    {
        private readonly BandwidthLimitPolicy _policy;
        private long _totalBytes;
        private long _windowStartTicks; // 使用 Ticks 存储时间，支持原子操作
        private readonly long _windowSizeTicks = TimeSpan.FromSeconds(1).Ticks;

        public string EndPoint { get; }

        public EndPointBandwidthTracker(string endPoint, BandwidthLimitPolicy policy)
        {
            EndPoint = endPoint;
            _policy = policy;
            _windowStartTicks = DateTime.UtcNow.Ticks;
        }

        public void RecordData(int bytes)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            long currentWindowStart;
            long currentTotalBytes;
            long newWindowStart;
            long newTotalBytes;

            // 无锁循环，直到成功更新
            do
            {
                currentWindowStart = Interlocked.Read(ref _windowStartTicks);
                currentTotalBytes = Interlocked.Read(ref _totalBytes);

                // 检查是否需要重置窗口
                if (nowTicks - currentWindowStart >= _windowSizeTicks)
                {
                    newWindowStart = nowTicks;
                    newTotalBytes = bytes;
                }
                else
                {
                    newWindowStart = currentWindowStart;
                    newTotalBytes = currentTotalBytes + bytes;
                }

                // 尝试原子更新窗口开始时间
                var originalWindowStart = Interlocked.CompareExchange(ref _windowStartTicks, newWindowStart, currentWindowStart);
                if (originalWindowStart == currentWindowStart)
                {
                    // 窗口时间更新成功，更新总字节数
                    if (newWindowStart != currentWindowStart)
                    {
                        // 窗口已重置，直接设置新值
                        Interlocked.Exchange(ref _totalBytes, newTotalBytes);
                    }
                    else
                    {
                        // 窗口未重置，累加字节数
                        Interlocked.Add(ref _totalBytes, bytes);
                    }
                    break;
                }
                // 如果窗口时间被其他线程修改，重试
            } while (true);
        }

        public TimeSpan CalculateWaitTime(int dataSize, long limitBytesPerSecond)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            long currentWindowStart;
            long currentTotalBytes;

            // 无锁读取，可能需要重置窗口
            do
            {
                currentWindowStart = Interlocked.Read(ref _windowStartTicks);
                currentTotalBytes = Interlocked.Read(ref _totalBytes);

                var elapsedTicks = nowTicks - currentWindowStart;
                if (elapsedTicks >= _windowSizeTicks)
                {
                    // 需要重置窗口
                    var newWindowStart = nowTicks;
                    var originalWindowStart = Interlocked.CompareExchange(ref _windowStartTicks, newWindowStart, currentWindowStart);
                    if (originalWindowStart == currentWindowStart)
                    {
                        // 成功重置窗口
                        Interlocked.Exchange(ref _totalBytes, 0);
                        return TimeSpan.Zero;
                    }
                    // 窗口被其他线程重置，重试
                    continue;
                }

                // 窗口未过期，计算等待时间
                var elapsed = TimeSpan.FromTicks(elapsedTicks).TotalSeconds;
                var currentRate = currentTotalBytes / Math.Max(elapsed, 0.001);
                var targetRate = limitBytesPerSecond;

                if (currentRate + dataSize / TimeSpan.FromTicks(_windowSizeTicks).TotalSeconds <= targetRate)
                {
                    return TimeSpan.Zero;
                }

                // 计算需要等待的时间
                var excessBytes = currentTotalBytes + dataSize - (targetRate * elapsed);
                var waitSeconds = excessBytes / targetRate;
                return TimeSpan.FromSeconds(Math.Max(0, waitSeconds));
            } while (true);
        }

        public TimeSpan CalculateWaitTimeForGuarantee(int dataSize, long guaranteeBytesPerSecond)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            long currentWindowStart;
            long currentTotalBytes;

            // 无锁读取，可能需要重置窗口
            do
            {
                currentWindowStart = Interlocked.Read(ref _windowStartTicks);
                currentTotalBytes = Interlocked.Read(ref _totalBytes);

                var elapsedTicks = nowTicks - currentWindowStart;
                if (elapsedTicks >= _windowSizeTicks)
                {
                    // 需要重置窗口
                    var newWindowStart = nowTicks;
                    var originalWindowStart = Interlocked.CompareExchange(ref _windowStartTicks, newWindowStart, currentWindowStart);
                    if (originalWindowStart == currentWindowStart)
                    {
                        // 成功重置窗口
                        Interlocked.Exchange(ref _totalBytes, 0);
                        return TimeSpan.Zero;
                    }
                    // 窗口被其他线程重置，重试
                    continue;
                }

                // 窗口未过期，计算保障等待时间
                var elapsed = TimeSpan.FromTicks(elapsedTicks).TotalSeconds;
                
                // 如果当前速率低于保障速率，不需要等待
                var currentRate = currentTotalBytes / Math.Max(elapsed, 0.001);
                if (currentRate < guaranteeBytesPerSecond)
                {
                    return TimeSpan.Zero;
                }

                // 如果超过保障速率，需要等待
                var excessBytes = currentTotalBytes - (guaranteeBytesPerSecond * elapsed);
                if (excessBytes > 0)
                {
                    var waitSeconds = excessBytes / guaranteeBytesPerSecond;
                    return TimeSpan.FromSeconds(Math.Max(0, waitSeconds));
                }

                return TimeSpan.Zero;
            } while (true);
        }

        public void Dispose()
        {
            // 清理资源
        }
    }
}

