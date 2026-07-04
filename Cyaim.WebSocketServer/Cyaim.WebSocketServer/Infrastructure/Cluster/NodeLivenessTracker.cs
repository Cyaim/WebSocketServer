using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Cyaim.WebSocketServer.Infrastructure.Cluster
{
    /// <summary>
    /// Tracks node liveness based on last-seen timestamps for broker-based transports
    /// (Redis / FreeRedis / RabbitMQ) that have no direct peer connection to observe.
    /// Every received cluster message (including Raft heartbeats, sent roughly every second)
    /// should call <see cref="Touch"/>; a lightweight timer raises <see cref="NodeTimedOut"/>
    /// when a node has not been seen within the configured timeout.
    /// 基于最后一次收到消息的时间戳跟踪节点存活状态，供基于消息代理的传输
    /// （Redis / FreeRedis / RabbitMQ）使用，这些传输没有可观察的点对点连接。
    /// 每收到一条集群消息（包括约每秒一次的 Raft 心跳）都应调用 <see cref="Touch"/>；
    /// 一个轻量级定时器会在节点超过配置的超时时间未被观察到时触发 <see cref="NodeTimedOut"/>。
    /// </summary>
    public sealed class NodeLivenessTracker : IDisposable
    {
        /// <summary>
        /// Default node timeout / 默认节点超时时间
        /// </summary>
        public static readonly TimeSpan DefaultNodeTimeout = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Default liveness check interval / 默认存活检查间隔
        /// </summary>
        public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromSeconds(5);

        private readonly TimeSpan _nodeTimeout;
        private readonly Timer _checkTimer;
        private readonly object _checkLock = new object();
        private bool _disposed;

        /// <summary>
        /// Last-seen timestamp (UTC ticks) per node / 每个节点的最后一次观察时间（UTC ticks）
        /// </summary>
        private readonly ConcurrentDictionary<string, long> _lastSeen = new ConcurrentDictionary<string, long>();

        /// <summary>
        /// Nodes already reported as timed out (to avoid duplicate events)
        /// 已经报告为超时的节点（避免重复触发事件）
        /// </summary>
        private readonly ConcurrentDictionary<string, bool> _reportedDown = new ConcurrentDictionary<string, bool>();

        /// <summary>
        /// Raised once when a previously seen node has not been seen within the timeout.
        /// 当先前观察到的节点在超时时间内未再被观察到时触发（每次故障只触发一次）。
        /// </summary>
        public event EventHandler<ClusterNodeEventArgs> NodeTimedOut;

        /// <summary>
        /// Raised when a node reported as timed out is seen again.
        /// 当已报告超时的节点再次被观察到时触发。
        /// </summary>
        public event EventHandler<ClusterNodeEventArgs> NodeRecovered;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="nodeTimeout">Node timeout; a node unseen for this duration is considered disconnected (default 15s) / 节点超时时间；超过该时长未观察到的节点被视为断开（默认 15 秒）</param>
        /// <param name="checkInterval">Interval of the background liveness check (default 5s) / 后台存活检查的间隔（默认 5 秒）</param>
        public NodeLivenessTracker(TimeSpan? nodeTimeout = null, TimeSpan? checkInterval = null)
        {
            _nodeTimeout = nodeTimeout ?? DefaultNodeTimeout;
            var interval = checkInterval ?? DefaultCheckInterval;
            _checkTimer = new Timer(CheckLiveness, null, interval, interval);
        }

        /// <summary>
        /// Record that a message from the node was observed / 记录观察到来自节点的消息
        /// </summary>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        public void Touch(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                return;
            }

            _lastSeen[nodeId] = DateTime.UtcNow.Ticks;

            // If the node was reported down, report recovery / 如果节点曾被报告断开，报告恢复
            if (_reportedDown.TryRemove(nodeId, out _))
            {
                NodeRecovered?.Invoke(this, new ClusterNodeEventArgs { NodeId = nodeId });
            }
        }

        /// <summary>
        /// Check whether the node has ever been observed / 检查节点是否曾被观察到
        /// </summary>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        /// <returns>True if the node was seen at least once / 如果节点至少被观察到一次则返回 true</returns>
        public bool HasBeenSeen(string nodeId)
        {
            return !string.IsNullOrEmpty(nodeId) && _lastSeen.ContainsKey(nodeId);
        }

        /// <summary>
        /// Check whether the node was seen within the timeout window / 检查节点是否在超时窗口内被观察到
        /// </summary>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        /// <returns>True if seen recently / 如果最近被观察到则返回 true</returns>
        public bool IsAlive(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || !_lastSeen.TryGetValue(nodeId, out var ticks))
            {
                return false;
            }

            return DateTime.UtcNow.Ticks - ticks <= _nodeTimeout.Ticks;
        }

        /// <summary>
        /// Remove a node from tracking (e.g. after explicit deregistration)
        /// 从跟踪中移除节点（例如在显式注销后）
        /// </summary>
        /// <param name="nodeId">Node ID / 节点 ID</param>
        public void Remove(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                return;
            }

            _lastSeen.TryRemove(nodeId, out _);
            _reportedDown.TryRemove(nodeId, out _);
        }

        /// <summary>
        /// Timer callback that reports nodes exceeding the timeout / 报告超过超时时间的节点的定时器回调
        /// </summary>
        /// <param name="state">Timer state / 定时器状态</param>
        private void CheckLiveness(object state)
        {
            if (_disposed)
            {
                return;
            }

            // Avoid overlapping checks / 避免重叠的检查
            if (!Monitor.TryEnter(_checkLock))
            {
                return;
            }

            try
            {
                var nowTicks = DateTime.UtcNow.Ticks;
                List<string> timedOut = null;

                foreach (var kvp in _lastSeen)
                {
                    if (nowTicks - kvp.Value > _nodeTimeout.Ticks && !_reportedDown.ContainsKey(kvp.Key))
                    {
                        (timedOut ??= new List<string>()).Add(kvp.Key);
                    }
                }

                if (timedOut == null)
                {
                    return;
                }

                foreach (var nodeId in timedOut)
                {
                    if (_reportedDown.TryAdd(nodeId, true))
                    {
                        NodeTimedOut?.Invoke(this, new ClusterNodeEventArgs { NodeId = nodeId });
                    }
                }
            }
            catch
            {
                // Never let the timer callback throw / 绝不让定时器回调抛出异常
            }
            finally
            {
                Monitor.Exit(_checkLock);
            }
        }

        /// <summary>
        /// Dispose resources / 释放资源
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _checkTimer.Dispose();
            _lastSeen.Clear();
            _reportedDown.Clear();
        }
    }
}
