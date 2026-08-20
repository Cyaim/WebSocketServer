using System.Threading;

namespace Cyaim.WebSocketServer.Infrastructure
{
    /// <summary>
    /// 进程级「同时在物化的发送载荷字节」总预算。发送侧的
    /// <see cref="WebSocketReceiveMemoryGovernor"/> 对应物。未设置 <see cref="MaxBytes"/>(&lt;=0) 时完全无操作。
    /// Process-wide budget for payload bytes being materialised for sending at once — the send-side
    /// counterpart of <see cref="WebSocketReceiveMemoryGovernor"/>. Fully no-op unless
    /// <see cref="MaxBytes"/> is positive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>超预算时降级，绝不等待。</b>这是它与接收侧最重要的区别。接收侧拒绝一帧后，对端要么重发要么
    /// 断开，总会有进展；而发送侧一旦 await 一个预算，就是在等一个「停滞的对端把它的写放完」——
    /// 那个写可能永远不完成，于是预算永不释放，所有健康连接一起饿死。所以这里只有
    /// <see cref="TryReserve"/>：拿不到就走流式，不排队。
    /// <b>Degrade over budget, never wait.</b> This is the important difference from the receive side.
    /// A rejected receive frame leaves the peer to resend or disconnect, so progress is always possible;
    /// awaiting a send budget means waiting for a stalled peer's write to drain, which may never happen —
    /// the budget is never released and every healthy connection starves behind it. Hence
    /// <see cref="TryReserve"/> only: miss the budget and the send streams instead of queueing.
    /// </para>
    /// <para>
    /// 计量的是**物化缓冲**，不是在途帧。在途帧由
    /// <see cref="Configures.WebSocketRouteOption.MaxSendFrameBytes"/> 结构性地封顶，不需要记账——
    /// 而一个基于记账的帧预算在停滞对端下必然退化成只增不减的泄漏计数器。
    /// It counts materialisation buffers, not in-flight frames. In-flight frames are bounded structurally
    /// by MaxSendFrameBytes, which needs no accounting — and an accounting-based frame budget degenerates
    /// into a leaking counter the moment a peer stalls.
    /// </para>
    /// </remarks>
    public static class WebSocketSendMemoryGovernor
    {
        private static long _current;

        /// <summary>总预算（字节）。&lt;=0 表示禁用。Total budget in bytes; &lt;= 0 disables it.</summary>
        public static long MaxBytes;

        /// <summary>当前已预留的字节数。Currently reserved bytes.</summary>
        public static long CurrentBytes => Interlocked.Read(ref _current);

        /// <summary>
        /// 尝试预留物化预算。拿不到返回 false，调用方应降级为流式而不是等待。
        /// Tries to reserve materialisation budget. On false the caller streams instead of waiting.
        /// </summary>
        public static bool TryReserve(long bytes)
        {
            if (MaxBytes <= 0 || bytes <= 0)
            {
                return true;
            }

            var updated = Interlocked.Add(ref _current, bytes);
            if (updated <= MaxBytes)
            {
                return true;
            }

            Interlocked.Add(ref _current, -bytes);
            return false;
        }

        /// <summary>归还预算。Releases previously reserved budget.</summary>
        public static void Release(long bytes)
        {
            if (MaxBytes <= 0 || bytes <= 0)
            {
                return;
            }

            Interlocked.Add(ref _current, -bytes);
        }

        /// <summary>测试用：把计数归零。Test hook: resets the counter.</summary>
        internal static void Reset() => Interlocked.Exchange(ref _current, 0);
    }
}
