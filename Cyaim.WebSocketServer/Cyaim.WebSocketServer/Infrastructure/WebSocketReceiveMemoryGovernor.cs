using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

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

        /// <summary>
        /// Bytes drained before giving up on an oversized message and closing the connection.
        /// 在放弃一条超限消息并关闭连接前，最多排空的字节数。
        /// </summary>
        public const long DefaultMaxDrainBytes = 64 * 1024;

        /// <summary>
        /// A message tripped the size limit (or the global budget) and must be discarded. Read (and
        /// discard, no memory growth) the rest of THIS message so its remaining frames aren't misread
        /// as a new message — but only up to <paramref name="maxDrainBytes"/>. Reading stops exactly at
        /// this message's EndOfMessage boundary, so the NEXT message is never consumed.
        ///
        /// If the message is grossly oversized or never ends (an unbounded/streaming message), draining
        /// the whole thing would waste bandwidth and could hang the connection forever, so instead a
        /// 1009 (Message Too Big) close frame is sent and the socket is aborted; the connection loop then
        /// exits promptly at its next state check.
        ///
        /// 超限消息需丢弃：读并丢弃(内存不增长)本条消息的剩余帧,避免残余帧被当作新消息误读——但最多排空
        /// maxDrainBytes。到本条消息 EndOfMessage 即停,绝不会吃掉下一条消息。若消息过大或永不结束(无界/流式),
        /// 排空整条会浪费带宽且可能永久挂住连接,故改为发送 1009(Message Too Big)并 Abort,连接循环随后即退出。
        /// </summary>
        /// <returns>true if the connection was aborted (too large to drain); false if fully drained and the connection can continue.</returns>
        public static async Task<bool> DrainOversizedAsync(
            WebSocket webSocket, byte[] buffer, WebSocketReceiveResult result, long maxDrainBytes = DefaultMaxDrainBytes)
        {
            long drained = 0;
            while (result != null && !(result.EndOfMessage || result.CloseStatus.HasValue))
            {
                result = await webSocket.ReceiveAsync(new System.ArraySegment<byte>(buffer), CancellationToken.None).ConfigureAwait(false);
                drained += result.Count;
                if (drained > maxDrainBytes)
                {
                    // Grossly oversized / unbounded: notify the client (1009) without waiting for its ack,
                    // then abort so the receive loop exits at its next state check instead of draining forever.
                    try
                    {
                        if (webSocket.State == WebSocketState.Open)
                        {
                            await webSocket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Request exceeds size limit", CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch { /* best effort */ }
                    webSocket.Abort();
                    return true;
                }
            }
            return false;
        }
    }
}
