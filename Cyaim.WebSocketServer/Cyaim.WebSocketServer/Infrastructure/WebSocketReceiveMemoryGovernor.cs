using System.Threading;

namespace Cyaim.WebSocketServer.Infrastructure
{
    /// <summary>
    /// Process-wide budget for bytes currently held in per-connection multi-frame receive buffers.
    /// The per-request <see cref="Configures.WebSocketRouteOption.MaxRequestReceiveDataLimit"/> bounds a
    /// single message; this bounds the SUM across all connections, so a coordinated burst of many
    /// large in-flight messages cannot exhaust memory. Disabled (no-op, zero overhead) unless
    /// <see cref="MaxBytes"/> is set to a positive value. Single-frame messages never touch this
    /// (their data lives in a pooled rented buffer, already bounded by the receive buffer size).
    ///
    /// 进程级"在途接收缓冲字节"总预算。单条消息由 MaxRequestReceiveDataLimit 限制；这里限制所有连接的
    /// 累计在途多帧字节，防止大量大消息同时到达把内存打爆。未设置 MaxBytes(&lt;=0)时完全无操作、零开销。
    /// 单帧消息不经过此路径（其数据在池化租用缓冲区中，已受接收缓冲区大小约束）。
    /// </summary>
    public static class WebSocketReceiveMemoryGovernor
    {
        private static long _current;

        /// <summary>
        /// Total budget in bytes. &lt;= 0 disables the governor (no reservation, no overhead).
        /// 总预算（字节）。&lt;=0 表示禁用（不预留、无开销）。
        /// </summary>
        public static long MaxBytes;

        /// <summary>Currently reserved bytes across all connections. / 当前所有连接已预留的字节数。</summary>
        public static long CurrentBytes => Interlocked.Read(ref _current);

        /// <summary>
        /// Try to reserve <paramref name="bytes"/> of receive-buffer budget. Returns true (and reserves)
        /// when accepted, false when it would exceed the budget (nothing reserved). When the governor is
        /// disabled it always returns true without touching the counter.
        /// 尝试预留字节预算；接受则返回 true 并预留，超预算则返回 false 且不预留。禁用时恒 true 且不计数。
        /// </summary>
        public static bool TryReserve(long bytes)
        {
            if (MaxBytes <= 0 || bytes <= 0)
            {
                return true;
            }

            var updated = Interlocked.Add(ref _current, bytes);
            if (updated > MaxBytes)
            {
                Interlocked.Add(ref _current, -bytes);
                return false;
            }
            return true;
        }

        /// <summary>Release previously reserved bytes (called after the message is processed / on cleanup).</summary>
        public static void Release(long bytes)
        {
            if (MaxBytes <= 0 || bytes <= 0)
            {
                return;
            }
            Interlocked.Add(ref _current, -bytes);
        }
    }
}
