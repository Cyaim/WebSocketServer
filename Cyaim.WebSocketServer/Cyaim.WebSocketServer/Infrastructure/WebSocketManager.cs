using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;

namespace Cyaim.WebSocketServer.Infrastructure
{
    /// <summary>
    /// WebSocket operation method
    /// </summary>
    public static class WebSocketManager
    {
        /// <summary>
        /// Upper limit of WebSockets processed per batch
        /// </summary>
        public static int BatchProcessingWebsocketLimit { get; set; } = 1000;

        /// <summary>
        /// Default send encoding
        /// </summary>
        private static Encoding DefaultEncoding { get; } = Encoding.UTF8;

        /// <summary>
        /// Per-socket send gate. WebSocket allows only one outstanding SendAsync per instance,
        /// so concurrent callers targeting the same socket serialize here instead of through
        /// a process-wide queue. Entries are released automatically when the socket is collected.
        /// 每个 socket 的发送门闩。WebSocket 同一实例只允许一个未完成的 SendAsync，
        /// 并发发送同一 socket 时在此串行化（替代旧的全局单消费者队列）。socket 被回收后条目自动释放。
        /// </summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebSocket, SemaphoreSlim> SendLocks = new System.Runtime.CompilerServices.ConditionalWeakTable<WebSocket, SemaphoreSlim>();

        /// <summary>
        /// Send buffer size used by the connection-id send paths, matching <see cref="SendLocalAsync"/>'s default.
        /// 按连接 ID 发送时使用的缓冲区大小，与 SendLocalAsync 的默认值一致。
        /// </summary>
        private const uint DefaultSendBufferSize = 4 * 1024;

        /// <summary>
        /// 补发收尾帧的时间上限。超出即认定该连接不可用并 Abort。
        /// 它守的是 per-socket 发送门闩：这次写在门闩内进行，写不动就等于整条连接的发送路径死锁。
        /// How long the terminating frame may take before the connection is declared unusable and aborted.
        /// This bounds the per-socket send gate: the write happens while the gate is held, so a write that
        /// never completes deadlocks every later send on that connection.
        /// </summary>
        private static readonly TimeSpan TerminatorTimeout = TimeSpan.FromSeconds(5);

        private static SemaphoreSlim GetSendLock(WebSocket socket)
        {
            return SendLocks.GetValue(socket, static _ => new SemaphoreSlim(1, 1));
        }

        #region Send core

        /// <summary>
        /// Send a buffer to a single socket, holding the socket's send gate for the whole message
        /// so multi-frame sends never interleave with other senders.
        /// 向单个 socket 发送缓冲区数据，整条消息期间持有该 socket 的发送门闩，避免多帧交叠。
        /// </summary>
        /// <returns>
        /// True when the payload was written; false when the socket was no longer open by the time
        /// the gate was acquired. Callers that report per-connection outcomes need to tell those
        /// apart — a socket that closed while queued behind another send was never written to, and
        /// saying otherwise would report a delivery that did not happen.
        /// 返回是否真的写出：排在别的发送后面时连接可能已经关闭，那种情况下什么都没发出去，
        /// 需要逐连接汇报结果的调用方必须能区分这两者。
        /// </returns>
        private static async Task<bool> SendBufferCoreAsync(WebSocket socket, ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool sendAtOnce, uint sendBufferSize, CancellationToken cancellationToken)
        {
            var gate = GetSendLock(socket);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (socket.State != WebSocketState.Open)
                {
                    return false;
                }
                if (sendAtOnce || buffer.Length <= sendBufferSize)
                {
                    await socket.SendAsync(buffer, messageType, endOfMessage: true, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendBufferedDataInBatchesAsync(socket, messageType, buffer, sendBufferSize, cancellationToken).ConfigureAwait(false);
                }

                return true;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Send stream content to a single socket under its send gate.
        /// 在发送门闩保护下向单个 socket 发送流数据。
        /// </summary>
        private static async Task SendStreamCoreAsync(WebSocket socket, Stream stream, WebSocketMessageType messageType, bool sendAtOnce, uint sendBufferSize, CancellationToken cancellationToken)
        {
            var gate = GetSendLock(socket);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (socket.State != WebSocketState.Open)
                {
                    return;
                }
                if (sendAtOnce)
                {
                    await SendStreamDataAsync(socket, messageType, stream, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendStreamDataInBatchesAsync(socket, messageType, stream, sendBufferSize, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Send a buffer to many sockets, bounded by <see cref="BatchProcessingWebsocketLimit"/> per wave.
        /// Individual socket failures are swallowed so one bad connection doesn't fail the batch.
        /// 向多个 socket 发送缓冲区数据，每波并发受 <see cref="BatchProcessingWebsocketLimit"/> 限制。
        /// 单个 socket 的失败被吞掉，避免一个坏连接影响整批。
        /// </summary>
        private static async Task SendBufferToManyAsync(WebSocket[] sockets, ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool sendAtOnce, uint sendBufferSize, CancellationToken cancellationToken)
        {
            List<Task> batch = new List<Task>(Math.Min(sockets.Length, BatchProcessingWebsocketLimit));
            for (int i = 0; i < sockets.Length; i++)
            {
                WebSocket socket = sockets[i];
                if (socket == null || socket.State != WebSocketState.Open)
                {
                    continue;
                }
                batch.Add(SendBufferCoreAsync(socket, buffer, messageType, sendAtOnce, sendBufferSize, cancellationToken));
                if (batch.Count >= BatchProcessingWebsocketLimit)
                {
                    try { await Task.WhenAll(batch).ConfigureAwait(false); } catch { }
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                try { await Task.WhenAll(batch).ConfigureAwait(false); } catch { }
            }
        }

        /// <summary>
        /// Await a send with an optional completion-wait timeout. On timeout the send keeps
        /// running detached (previous channel-based behavior) and its exception, if any, is observed.
        /// 等待发送完成，支持可选超时。超时后发送继续在后台执行（与旧的通道行为一致），异常会被观察以防进程崩溃。
        /// </summary>
        /// <remarks>
        /// <b>超时分支会让发送脱离等待并在后台继续运行。</b>调用方若把**借来的**内存（ArrayPool 租用缓冲、
        /// 复用缓冲区）交给了这个发送，就不能在本方法返回后立即归还或复用它：脱离的发送仍在读那段内存，
        /// 而下一个租用者会把它覆盖掉，结果是已发出的帧被写坏——而且没有任何异常。
        /// 只有在 <paramref name="timeout"/> 为 null 或 <see cref="Timeout.InfiniteTimeSpan"/> 时，
        /// 发送才保证在本方法返回前结束，此时借用内存是安全的。
        /// <b>The timeout branch detaches the send and lets it keep running.</b> A caller that lent
        /// <i>borrowed</i> memory (a pooled rental, a reused buffer) to that send must not return or
        /// reuse it once this method comes back: the detached send is still reading it, the next
        /// renter overwrites it, and the frame already on the wire is corrupted — silently. Only when
        /// <paramref name="timeout"/> is null or <see cref="Timeout.InfiniteTimeSpan"/> is the send
        /// guaranteed to be finished on return, which is the only case where lending is safe.
        /// </remarks>
        private static async Task AwaitWithTimeoutAsync(Task sendTask, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            if (timeout == null || timeout.Value == Timeout.InfiniteTimeSpan)
            {
                // 无超时：直接等待并让异常传播，调用方（如集群本地流路由）据此判断发送是否成功。
                // 多目标扇出在 SendBufferToManyAsync 内部已吞掉单 socket 失败，不会传播到这里。
                // No timeout: await directly and let exceptions propagate so callers (e.g. cluster
                // local stream routing) can detect send failure. Multi-target fan-out already
                // swallows per-socket faults inside SendBufferToManyAsync, so nothing propagates there.
                await sendTask.ConfigureAwait(false);
                return;
            }

            var completed = await Task.WhenAny(sendTask, Task.Delay(timeout.Value, cancellationToken)).ConfigureAwait(false);
            if (completed == sendTask)
            {
                try { await sendTask.ConfigureAwait(false); } catch { }
            }
            else
            {
                // Detached: observe faults to avoid unobserved task exceptions
                _ = sendTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
        }

        /// <summary>
        /// Send data from the stream (all at once)
        /// 从流中发送数据（一次性发送）
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="messageType"></param>
        /// <param name="stream"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task SendStreamDataAsync(WebSocket webSocket, WebSocketMessageType messageType, Stream stream, CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent((int)stream.Length);
            try
            {
                int totalBytesRead = 0;
                int bytesRead;
                while (totalBytesRead < buffer.Length && (bytesRead = await stream.ReadAsync(buffer, totalBytesRead, buffer.Length - totalBytesRead, cancellationToken)) > 0)
                {
                    totalBytesRead += bytesRead;
                }

                if (totalBytesRead > 0)
                {
                    await webSocket.SendAsync(new ArraySegment<byte>(buffer, 0, totalBytesRead), messageType, endOfMessage: true, cancellationToken);
                }
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// 发一帧；写失败就中止连接，绝不留下状态不明的分帧。
        /// Sends one frame; aborts the connection on failure rather than leaving framing in an unknown state.
        /// </summary>
        /// <remarks>
        /// 写失败之后，线上到底有没有字节、有几个字节，是无法知道的：帧可能一个字节没发，也可能发了一半。
        /// 被撕断的帧用一个空的收尾帧救不回来——对端还在等这一帧剩余的载荷，收尾帧的字节会被它当载荷吞掉，
        /// 分帧照样是坏的。所以这里不猜，直接 Abort：让客户端重连，好过留一个分帧已坏的连接继续服务。
        /// After a failed write there is no way to know whether any bytes reached the wire, or how many: the
        /// frame may have gone out whole, in part, or not at all. A torn frame cannot be repaired by an empty
        /// terminator — the peer is still waiting for that frame's remaining payload and would swallow the
        /// terminator's bytes as payload, leaving framing broken anyway. So this does not guess: it aborts,
        /// because a client that reconnects beats a connection whose framing is silently wrong.
        /// </remarks>
        private static async Task SendFrameOrAbortAsync(WebSocket socket, ReadOnlyMemory<byte> frame, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            try
            {
                await socket.SendAsync(frame, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try { socket.Abort(); } catch { }
                throw;
            }
        }

        /// <summary>
        /// 一条多帧消息已经开了头却发不完时，把它收尾，避免该 socket 的分帧永久错乱。
        /// Closes a multi-frame message that was started but cannot be finished, so the socket's framing
        /// does not stay broken forever.
        /// </summary>
        /// <remarks>
        /// <para>
        /// 分批发送是「若干 endOfMessage:false 帧 + 一个 endOfMessage:true 收尾帧」。中途抛异常时，
        /// 收尾帧永远不会发出，而门闩照常释放——于是这条消息在协议上一直没有结束，
        /// <b>下一个发送者的帧会被对端当作它的延续帧</b>。之后每一条消息都被粘在这条残消息后面，
        /// 该连接的分帧就此永久错乱，且服务端一行日志都没有。
        /// Batched sending is "N frames with endOfMessage:false, then a terminating frame". If it throws
        /// partway, the terminator never goes out while the send gate is released anyway — the message
        /// stays open at the protocol level and <b>the next sender's frames become continuation frames of
        /// it</b>. Every later message is glued onto the truncated one: framing is broken for the life of
        /// the connection, with nothing logged.
        /// </para>
        /// <para>
        /// 补一个收尾帧的结果是对端收到一条被截断的消息——它解不开、会丢弃并（通常）记一条日志。
        /// 那是可见且可恢复的；静默的永久错乱不是。收尾帧发不出去说明 socket 本身已经不可用，
        /// 此时 Abort 让客户端重连，好过留一个分帧已坏的连接继续服务。
        /// Terminating the message leaves the peer with a truncated one: it fails to parse, drops it and
        /// usually logs. That is visible and recoverable; silent permanent corruption is not. If even the
        /// terminator cannot be sent the socket is already unusable, and aborting so the client reconnects
        /// beats leaving a connection whose framing is broken.
        /// </para>
        /// <para>
        /// 刻意不接受取消令牌：走到这里往往正是因为调用方的令牌被取消了，而收尾恰恰是这种时候最该做的事。
        /// Deliberately takes no cancellation token: reaching here often means the caller's token was
        /// cancelled, which is exactly when the message most needs closing.
        /// </para>
        /// </remarks>
        private static async Task CloseOpenMessageAsync(WebSocket socket, WebSocketMessageType messageType)
        {
            // 收尾帧必须有界。它是在 per-socket 发送门闩**之内**被等待的（gate.Release() 在更外层的
            // finally），所以一次写不动就意味着门闩永远不释放——该连接此后任何发送都排不进去，
            // 而 socket.State 仍是 Open、没有 Abort、没有日志。那比原本的分帧错乱更难恢复。
            // 对端不排空时这次写确实会无限阻塞（实测），所以这里给它自己的有界令牌，
            // 而不是调用方的令牌（走到这里往往正是因为调用方的令牌被取消了）。
            // The terminator must be bounded. It is awaited INSIDE the per-socket send gate (gate.Release()
            // lives in an outer finally), so a write that never completes means the gate is never released:
            // nothing can be sent on this connection again, while State stays Open with no abort and no log
            // — harder to recover from than the framing corruption this exists to prevent. Against a peer
            // that is not draining, this write does block indefinitely (measured), so it gets its own
            // bounded token rather than the caller's (which has often already been cancelled).
            try
            {
                // CloseReceived 也是合法的发送状态（.NET 的 s_validSendStates 就是 {Open, CloseReceived}），
                // 此时消息同样还开着，同样需要收尾。
                // CloseReceived is a valid send state too (.NET's s_validSendStates is {Open, CloseReceived});
                // the message is just as open there and needs closing just the same.
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    using var terminatorTimeout = new CancellationTokenSource(TerminatorTimeout);
                    await socket.SendAsync(Memory<byte>.Empty, messageType, endOfMessage: true, terminatorTimeout.Token)
                        .ConfigureAwait(false);
                    return;
                }
            }
            catch
            {
                // 落到下面的 Abort。
            }

            // 收尾发不出去（写不动、被拒、socket 已不在可发送状态）：这个连接已经没救了。
            // 中止它，让客户端重连，好过留一个分帧已坏、或门闩已被占死的 socket 继续服务。
            // The terminator could not go out (blocked, refused, socket no longer sendable): this connection
            // is beyond saving. Abort so the client reconnects, rather than leaving a socket whose framing is
            // broken or whose send gate is wedged.
            try { socket.Abort(); } catch { }
        }

        /// <summary>
        /// Send data in batches from the stream
        /// 从流中分批发送数据
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="messageType"></param>
        /// <param name="stream"></param>
        /// <param name="bufferSize"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task SendStreamDataInBatchesAsync(WebSocket webSocket, WebSocketMessageType messageType, Stream stream, uint bufferSize, CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent((int)bufferSize);
            // 已经有非结束帧成功上线、这条消息尚未收尾。只在「读流失败」这条路径上用得到：
            // 写失败一律 Abort，不需要它。
            // A non-final frame has gone out and the message is not terminated yet. Only the read-failure
            // path consults it; a send failure aborts unconditionally and does not need it.
            bool messageOpen = false;
            try
            {
                while (true)
                {
                    int bytesRead;
                    try
                    {
                        // 按请求的 bufferSize 读取，租借的缓冲区可能大于请求大小
                        // Read at the requested bufferSize: the rented buffer may be larger than requested
                        bytesRead = await stream.ReadAsync(buffer, 0, (int)bufferSize, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 读流失败（调用方 Dispose 了它、IO 错误、被取消）。socket 本身没问题，
                        // 已上线的帧也都是完整帧，所以补一个收尾帧就能让分帧闭合。
                        // The read failed (the caller disposed the stream, an IO error, cancellation). The socket
                        // itself is fine and every frame already sent was whole, so a terminator closes the framing.
                        if (messageOpen)
                        {
                            await CloseOpenMessageAsync(webSocket, messageType).ConfigureAwait(false);
                        }

                        throw;
                    }

                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    await SendFrameOrAbortAsync(webSocket, buffer.AsMemory(0, bytesRead), messageType, endOfMessage: false, cancellationToken).ConfigureAwait(false);
                    messageOpen = true;
                }

                await SendFrameOrAbortAsync(webSocket, Memory<byte>.Empty, messageType, endOfMessage: true, cancellationToken).ConfigureAwait(false);
                messageOpen = false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Send data in batches from the buffer
        /// 从缓冲区中分批发送数据
        /// </summary>
        /// <param name="webSocket"></param>
        /// <param name="messageType"></param>
        /// <param name="buffer"></param>
        /// <param name="batchSize"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task SendBufferedDataInBatchesAsync(WebSocket webSocket, WebSocketMessageType messageType, ReadOnlyMemory<byte> buffer, uint batchSize, CancellationToken cancellationToken)
        {
            int offset = 0;

            while (offset < buffer.Length)
            {
                int count = Math.Min((int)batchSize, buffer.Length - offset);
                await SendFrameOrAbortAsync(webSocket, buffer.Slice(offset, count), messageType, endOfMessage: false, CancellationToken.None).ConfigureAwait(false);
                offset += count;
            }

            await SendFrameOrAbortAsync(webSocket, Memory<byte>.Empty, messageType, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        #endregion

        /// <summary>
        /// Send data to local WebSocket connections (single machine mode only)
        /// 向本地 WebSocket 连接发送数据（仅单机模式）
        /// </summary>
        /// <param name="sendStream">Stream to send / 要发送的流</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <param name="timeout">Timeout / 超时时间</param>
        /// <param name="sendAtOnce">Send at once / 是否一次性发送</param>
        /// <param name="sendBufferSize">Send buffer size / 发送缓冲区大小</param>
        /// <param name="sockets">Local WebSocket connections / 本地 WebSocket 连接</param>
        /// <returns></returns>
        public static async Task SendLocalAsync(Stream sendStream, WebSocketMessageType messageType, CancellationToken cancellationToken, TimeSpan? timeout = null, bool sendAtOnce = false, uint sendBufferSize = 4 * 1024, params WebSocket[] sockets)
        {
            if (sockets == null || sockets.LongLength < 1 || sendBufferSize < 1)
            {
                return;
            }

            Task sendTask;
            WebSocket single = sockets.Length == 1 ? sockets[0] : null;
            if (single != null)
            {
                if (single.State != WebSocketState.Open)
                {
                    return;
                }
                sendTask = SendStreamCoreAsync(single, sendStream, messageType, sendAtOnce, sendBufferSize, cancellationToken);
            }
            else
            {
                // Multiple targets cannot share one stream concurrently: buffer it once, then fan out
                // 多个目标不能并发共享同一个流：先一次性缓冲，再分发
                sendTask = SendStreamToManyAsync(sockets, sendStream, messageType, sendAtOnce, sendBufferSize, cancellationToken);
            }

            // 超时仍是「不再等待」而不是「取消发送」，与内存重载保持一致。
            //
            // 曾经考虑过在这里改成取消：调用方几乎总是 `using` 这个流，脱离后后台还在读它。
            // 但那会让 timeout 失去「调用方等待时长上界」这个硬保证——只重写同步 Read 的流会完全无视
            // 取消令牌，取消打在飞行中的帧上还会让真实的 ManagedWebSocket 直接 Abort 整条连接，
            // 排队等门闩期间超时更会让整条消息一帧未发就被静默丢弃。代价远大于收益。
            //
            // 真正的危险从来不是「后台还在读」，而是「读失败后消息没收尾」。那一点已经在
            // SendStreamDataInBatchesAsync 里解决了：调用方 Dispose 后台读就抛，抛就补收尾帧，
            // 分帧闭合。所以这里不需要改语义。
            //
            // The timeout still means "stop waiting", not "cancel the send", matching the memory overload.
            // Cancelling here was considered — the caller almost always has the stream in a `using`, and a
            // detached send keeps reading it. But that costs the hard guarantee that `timeout` bounds how
            // long the caller waits: a stream overriding only synchronous Read ignores the token entirely,
            // cancelling an in-flight frame makes the real ManagedWebSocket abort the whole connection, and
            // a timeout while queueing for the send gate drops the message without a single frame going out.
            // The danger was never "still reading" but "the message was left unterminated after the read
            // failed" — and that is handled in SendStreamDataInBatchesAsync: the caller's Dispose makes the
            // read throw, the throw terminates the message, framing stays closed. No semantic change needed.
            await AwaitWithTimeoutAsync(sendTask, timeout, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Buffer a stream once and fan it out to many sockets.
        /// 将流缓冲一次后分发给多个 socket。
        /// </summary>
        private static async Task SendStreamToManyAsync(WebSocket[] sockets, Stream stream, WebSocketMessageType messageType, bool sendAtOnce, uint sendBufferSize, CancellationToken cancellationToken)
        {
            using var buffered = new MemoryStream();
            await stream.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
            await SendBufferToManyAsync(sockets, buffered.GetBuffer().AsMemory(0, (int)buffered.Length), messageType, sendAtOnce, sendBufferSize, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Send data to local WebSocket connections (single machine mode only)
        /// 向本地 WebSocket 连接发送数据（仅单机模式）
        /// </summary>
        /// <param name="buffer">Data buffer / 数据缓冲区</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="sendAtOnce">Send at once / 是否一次性发送</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <param name="timeout">Timeout / 超时时间</param>
        /// <param name="sendBufferSize">Send buffer size / 发送缓冲区大小</param>
        /// <param name="sockets">Local WebSocket connections / 本地 WebSocket 连接</param>
        /// <returns></returns>
        public static async Task SendLocalAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool sendAtOnce, CancellationToken cancellationToken, TimeSpan? timeout = null, uint sendBufferSize = 4 * 1024, params WebSocket[] sockets)
        {
            if (sockets == null || sockets.LongLength < 1)
            {
                return;
            }

            // Fast path: single open socket, no intermediate allocations
            // 快速路径：单个打开的 socket，无中间分配
            WebSocket single = sockets.Length == 1 ? sockets[0] : null;
            if (single != null)
            {
                if (single.State != WebSocketState.Open)
                {
                    throw new ArgumentNullException(nameof(sockets));
                }
                await AwaitWithTimeoutAsync(SendBufferCoreAsync(single, buffer, messageType, sendAtOnce, sendBufferSize, cancellationToken), timeout, cancellationToken).ConfigureAwait(false);
                return;
            }

            bool anyOpen = false;
            for (int i = 0; i < sockets.Length; i++)
            {
                if (sockets[i] != null && sockets[i].State == WebSocketState.Open)
                {
                    anyOpen = true;
                    break;
                }
            }
            if (!anyOpen)
            {
                throw new ArgumentNullException(nameof(sockets));
            }

            await AwaitWithTimeoutAsync(SendBufferToManyAsync(sockets, buffer, messageType, sendAtOnce, sendBufferSize, cancellationToken), timeout, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Send text data to local WebSocket connections (single machine mode only)
        /// 向本地 WebSocket 连接发送文本数据（仅单机模式）
        /// </summary>
        /// <param name="data">Text data / 文本数据</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <param name="timeout">Timeout / 超时时间</param>
        /// <param name="encoding">Text encoding / 文本编码</param>
        /// <param name="sendBufferSize">Send buffer size / 发送缓冲区大小</param>
        /// <param name="socket">Local WebSocket connections / 本地 WebSocket 连接</param>
        /// <returns></returns>
        public static async Task SendLocalAsync(
            string data,
            WebSocketMessageType messageType,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null,
            Encoding encoding = null,
            int sendBufferSize = 4 * 1024,
            params WebSocket[] socket)
        {
            if (string.IsNullOrEmpty(data) || socket == null || socket.LongLength < 1)
            {
                return;
            }
            encoding ??= DefaultEncoding;

            // 有限超时会让发送脱离等待并在后台继续读这段内存（见 AwaitWithTimeoutAsync 的 remarks）。
            // 池化缓冲一旦在此处归还，就可能被下一个租用者覆盖，而那条帧还在发送途中——收到的是乱码，
            // 且没有任何异常。因此只有"发送必定在返回前结束"的无超时路径才借用池化内存；
            // 传了超时就编码到一个独占数组，把所有权直接交给可能脱离的发送。
            // A finite timeout detaches the send, which keeps reading this memory (see the remarks on
            // AwaitWithTimeoutAsync). Returning a pooled buffer here lets the next renter overwrite a
            // frame that is still on its way out — corruption, silently. So the pooled fast path is
            // used only when no timeout can detach the send; with a timeout we encode into a private
            // array and hand its ownership to the send that may outlive this call.
            bool canDetach = timeout != null && timeout.Value != Timeout.InfiniteTimeSpan;

            if (canDetach)
            {
                byte[] owned = encoding.GetBytes(data);
                await SendLocalAsync(owned.AsMemory(), messageType, owned.Length <= 4 * 1024, cancellationToken: cancellationToken, timeout, sendBufferSize: (uint)sendBufferSize, sockets: socket);
                return;
            }

            // 编码到租用缓冲区，避免为每次发送分配一个 byte[]。无超时路径下发送必定在 await 返回前结束，
            // 此时归还是安全的。
            // Encode into a pooled buffer to avoid a per-send byte[] allocation. On the no-timeout path
            // the send is guaranteed finished when the await returns, so returning it here is safe.
            int rentSize = encoding.GetMaxByteCount(data.Length);
            var rented = ArrayPool<byte>.Shared.Rent(rentSize);
            try
            {
                int written = encoding.GetBytes(data.AsSpan(), rented.AsSpan());
                await SendLocalAsync(rented.AsMemory(0, written), messageType, written <= 4 * 1024, cancellationToken: cancellationToken, timeout, sendBufferSize: (uint)sendBufferSize, sockets: socket);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        /// <summary>
        /// Send serialized JSON object to local WebSocket connections (single machine mode only)
        /// 向本地 WebSocket 连接发送序列化的 JSON 对象（仅单机模式）
        /// </summary>
        /// <typeparam name="T">Object type / 对象类型</typeparam>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="options">JSON serializer options / JSON 序列化选项</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <param name="timeout">Timeout / 超时时间</param>
        /// <param name="encoding">Text encoding / 文本编码</param>
        /// <param name="sendBufferSize">Send buffer size / 发送缓冲区大小</param>
        /// <param name="socket">Local WebSocket connections / 本地 WebSocket 连接</param>
        /// <returns></returns>
        public static async Task SendLocalAsync<T>(
            this T data,
            JsonSerializerOptions options = null,
            WebSocketMessageType messageType = WebSocketMessageType.Text,
            CancellationToken? cancellationToken = null,
            TimeSpan? timeout = null,
            Encoding encoding = null,
            int sendBufferSize = 4 * 1024,
            params WebSocket[] socket)
        {
            if (data == null || socket == null || socket.LongLength < 1)
            {
                return;
            }
            // 默认/UTF-8：直接序列化为 UTF-8 字节，省去中间 string 分配（WebSocket 文本本就是 UTF-8）。
            // Default/UTF-8: serialize straight to UTF-8 bytes, skipping the intermediate string allocation.
            if (encoding == null || ReferenceEquals(encoding, Encoding.UTF8))
            {
                var utf8 = JsonSerializer.SerializeToUtf8Bytes(data, options);
                await SendLocalAsync(new ReadOnlyMemory<byte>(utf8), messageType, utf8.Length <= sendBufferSize, cancellationToken ?? CancellationToken.None, timeout, (uint)sendBufferSize, socket);
                return;
            }
            await SendLocalAsync(JsonSerializer.Serialize(data, options), messageType, cancellationToken ?? CancellationToken.None, timeout, encoding, sendBufferSize, socket);
        }

        /// <summary>
        /// Send serialized JSON object to local WebSocket connection (single machine mode only)
        /// 向本地 WebSocket 连接发送序列化的 JSON 对象（仅单机模式）
        /// </summary>
        /// <typeparam name="T">Object type / 对象类型</typeparam>
        /// <param name="socket">Local WebSocket connection / 本地 WebSocket 连接</param>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="options">JSON serializer options / JSON 序列化选项</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <param name="timeout">Timeout / 超时时间</param>
        /// <param name="encoding">Text encoding / 文本编码</param>
        /// <param name="sendBufferSize">Send buffer size / 发送缓冲区大小</param>
        /// <returns></returns>
        public static async Task SendLocalAsync<T>(
            this WebSocket socket,
            T data,
            JsonSerializerOptions options = null,
            WebSocketMessageType messageType = WebSocketMessageType.Text,
            CancellationToken? cancellationToken = null,
            TimeSpan? timeout = null,
            Encoding encoding = null,
            int sendBufferSize = 4 * 1024)
        {
            if (data == null || socket == null)
            {
                return;
            }
            // 默认/UTF-8：直接序列化为 UTF-8 字节，省去中间 string 分配。
            // Default/UTF-8: serialize straight to UTF-8 bytes, skipping the intermediate string.
            if (encoding == null || ReferenceEquals(encoding, Encoding.UTF8))
            {
                var utf8 = JsonSerializer.SerializeToUtf8Bytes(data, options);
                await SendLocalAsync(new ReadOnlyMemory<byte>(utf8), messageType, utf8.Length <= sendBufferSize, cancellationToken ?? CancellationToken.None, timeout, (uint)sendBufferSize, socket);
                return;
            }
            await SendLocalAsync(JsonSerializer.Serialize(data, options), messageType, cancellationToken ?? CancellationToken.None, timeout, encoding, sendBufferSize, socket);
        }

        #region Unified Send Methods (Single Machine & Cluster) / 统一发送方法（单机和集群）

        /// <summary>
        /// Send message to connection(s) - automatically handles single machine or cluster mode
        /// 向连接发送消息 - 自动处理单机或集群模式
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="data">Message data as byte array / 消息数据（字节数组）</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <returns>True if sent successfully / 发送成功返回 true</returns>
        /// <remarks>
        /// This method automatically detects if cluster is enabled:
        /// - If cluster is enabled: uses ClusterManager to route message (supports cross-node)
        /// - If cluster is disabled: sends directly to local WebSocket connection
        /// 此方法自动检测是否启用集群：
        /// - 如果启用集群：使用 ClusterManager 路由消息（支持跨节点）
        /// - 如果未启用集群：直接发送到本地 WebSocket 连接
        /// </remarks>
        public static async Task<bool> SendAsync(
            string connectionId,
            byte[] data,
            WebSocketMessageType messageType = WebSocketMessageType.Text)
        {
            if (string.IsNullOrEmpty(connectionId) || data == null || data.Length == 0)
            {
                return false;
            }

            // Call batch method for single connection / 调用批量方法处理单个连接
            var results = await SendAsync(new[] { connectionId }, data, messageType);
            return results.TryGetValue(connectionId, out var success) && success;
        }

        /// <summary>
        /// Send text message to connection(s) - automatically handles single machine or cluster mode
        /// 向连接发送文本消息 - 自动处理单机或集群模式
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="text">Text message / 文本消息</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>True if sent successfully / 发送成功返回 true</returns>
        public static async Task<bool> SendAsync(
            string connectionId,
            string text,
            Encoding encoding = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            encoding ??= DefaultEncoding;
            var data = encoding.GetBytes(text);
            // Call batch method for single connection / 调用批量方法处理单个连接
            var results = await SendAsync(new[] { connectionId }, data, WebSocketMessageType.Text);
            return results.TryGetValue(connectionId, out var success) && success;
        }

        /// <summary>
        /// Send JSON object to connection(s) - automatically handles single machine or cluster mode
        /// 向连接发送 JSON 对象 - 自动处理单机或集群模式
        /// </summary>
        /// <typeparam name="T">Object type / 对象类型</typeparam>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="options">JSON serializer options / JSON 序列化选项</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>True if sent successfully / 发送成功返回 true</returns>
        public static async Task<bool> SendAsync<T>(
            string connectionId,
            T data,
            JsonSerializerOptions options = null,
            Encoding encoding = null)
        {
            if (data == null)
            {
                return false;
            }

            encoding ??= DefaultEncoding;
            var json = JsonSerializer.Serialize(data, options);
            var bytes = encoding.GetBytes(json);
            // Call batch method for single connection / 调用批量方法处理单个连接
            var results = await SendAsync(new[] { connectionId }, bytes, WebSocketMessageType.Text);
            return results.TryGetValue(connectionId, out var success) && success;
        }

        /// <summary>
        /// Send message to connection(s) - automatically handles single machine or cluster mode (supports batch)
        /// 向连接发送消息 - 自动处理单机或集群模式（支持批量）
        /// </summary>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表（支持单个或多个）</param>
        /// <param name="data">Message data as byte array / 消息数据（字节数组）</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <returns>Dictionary of connection ID to send result / 连接ID到发送结果的字典</returns>
        /// <remarks>
        /// This method automatically detects if cluster is enabled:
        /// - If cluster is enabled: uses ClusterManager to route message (supports cross-node)
        /// - If cluster is disabled: sends directly to local WebSocket connection
        /// 此方法自动检测是否启用集群：
        /// - 如果启用集群：使用 ClusterManager 路由消息（支持跨节点）
        /// - 如果未启用集群：直接发送到本地 WebSocket 连接
        /// </remarks>
        public static async Task<Dictionary<string, bool>> SendAsync(
            IEnumerable<string> connectionIds,
            byte[] data,
            WebSocketMessageType messageType = WebSocketMessageType.Text)
        {
            if (connectionIds == null)
            {
                return new Dictionary<string, bool>();
            }

            // Check if cluster is enabled / 检查是否启用集群
            var clusterManager = GlobalClusterCenter.ClusterManager;
            if (clusterManager != null)
            {
                // Use cluster routing / 使用集群路由
                var connectionIdsList = connectionIds.ToList();
                var connectionIdsArray = connectionIdsList.ToArray();

                var results = await clusterManager.RouteMessagesAsync(connectionIdsArray, data, (int)messageType);
                return results;
            }
            else
            {
                // Use local WebSocket / 使用本地 WebSocket
                //
                // Two things this must get right, and both were wrong before:
                //
                // 1. Sends go through SendBufferCoreAsync, which holds the per-socket send gate.
                //    A WebSocket permits exactly one outstanding SendAsync per instance, and this
                //    is the API an application uses to push to a client it did not hear from — a
                //    message arriving, a presence change, a notification. Those are produced by
                //    unrelated things happening at once, so two calls landing on one connection is
                //    the normal case, not the edge case. Calling socket.SendAsync directly here
                //    made the framework throw InvalidOperationException on exactly that case.
                //    走 SendBufferCoreAsync 是为了持有 per-socket 发送门闩：WebSocket 同一实例只允许
                //    一个未完成的发送，而按连接 ID 推送本来就会被互不相关的来源同时调用到同一个连接。
                //
                // 2. A result is recorded after the send resolves, not when it is queued. The
                //    return type promises a per-connection outcome; recording true up front and
                //    then letting Task.WhenAll throw on the first failure gave the caller an
                //    exception and no idea which of the others were delivered. A fan-out to a room
                //    always races a disconnect, so that is the common path, not the rare one.
                //    结果在发送完成后才记录：返回值承诺的是逐连接结果，先写 true 再让 WhenAll 抛异常，
                //    等于把其余人的投递结果一并丢掉——而群发撞上掉线是常态，不是罕见情况。
                var results = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
                var tasks = new List<Task>();
                foreach (var connectionId in connectionIds)
                {
                    if (string.IsNullOrEmpty(connectionId))
                    {
                        continue;
                    }

                    var webSocket = GetLocalWebSocket(connectionId);
                    if (webSocket == null || webSocket.State != WebSocketState.Open)
                    {
                        results[connectionId] = false;
                        continue;
                    }

                    tasks.Add(SendToConnectionAsync(connectionId, webSocket, data, messageType, results));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
                return new Dictionary<string, bool>(results);
            }
        }

        /// <summary>
        /// Sends to one connection under its send gate and records the outcome, never throwing.
        /// 在该连接的发送门闩保护下发送并记录结果，不会抛出异常。
        /// </summary>
        /// <remarks>
        /// Failure is recorded rather than propagated because the caller is fanning out: the socket
        /// closing between the state check above and the write is an ordinary race that says nothing
        /// about the other recipients. The result is what the gated send reports, so a socket that
        /// closed while queued behind another send is reported as false rather than as a delivery.
        /// 失败被记录而不是抛出：状态检查与真正写入之间连接关闭是常态竞争，与其他收件人无关。
        /// 结果取自门闩内的实际发送，因此"排队期间已关闭"会如实报 false，而不是谎称已投递。
        /// </remarks>
        private static async Task SendToConnectionAsync(
            string connectionId,
            WebSocket webSocket,
            byte[] data,
            WebSocketMessageType messageType,
            System.Collections.Concurrent.ConcurrentDictionary<string, bool> results)
        {
            try
            {
                results[connectionId] = await SendBufferCoreAsync(
                    webSocket,
                    data.AsMemory(),
                    messageType,
                    sendAtOnce: true,
                    sendBufferSize: DefaultSendBufferSize,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The connection went away mid-send. That is a per-connection outcome, not a batch
                // failure — see the remarks.
                results[connectionId] = false;
            }
        }

        /// <summary>
        /// Send text message to connection(s) - automatically handles single machine or cluster mode (supports batch)
        /// 向连接发送文本消息 - 自动处理单机或集群模式（支持批量）
        /// </summary>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表（支持单个或多个）</param>
        /// <param name="text">Text message / 文本消息</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>Dictionary of connection ID to send result / 连接ID到发送结果的字典</returns>
        public static async Task<Dictionary<string, bool>> SendAsync(
            IEnumerable<string> connectionIds,
            string text,
            Encoding encoding = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new Dictionary<string, bool>();
            }

            encoding ??= DefaultEncoding;
            var data = encoding.GetBytes(text);
            return await SendAsync(connectionIds, data, WebSocketMessageType.Text);
        }

        /// <summary>
        /// Send JSON object to connection(s) - automatically handles single machine or cluster mode (supports batch)
        /// 向连接发送 JSON 对象 - 自动处理单机或集群模式（支持批量）
        /// </summary>
        /// <typeparam name="T">Object type / 对象类型</typeparam>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表（支持单个或多个）</param>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="options">JSON serializer options / JSON 序列化选项</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>Dictionary of connection ID to send result / 连接ID到发送结果的字典</returns>
        public static async Task<Dictionary<string, bool>> SendAsync<T>(
            IEnumerable<string> connectionIds,
            T data,
            JsonSerializerOptions options = null,
            Encoding encoding = null)
        {
            if (data == null)
            {
                return new Dictionary<string, bool>();
            }

            encoding ??= DefaultEncoding;
            var json = JsonSerializer.Serialize(data, options);
            var bytes = encoding.GetBytes(json);
            return await SendAsync(connectionIds, bytes, WebSocketMessageType.Text);
        }

        /// <summary>
        /// Get local WebSocket connection by connection ID / 根据连接 ID 获取本地 WebSocket 连接
        /// </summary>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <returns>WebSocket instance or null / WebSocket 实例或 null</returns>
        private static WebSocket GetLocalWebSocket(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId))
            {
                return null;
            }

            // Try to get from MvcChannelHandler / 尝试从 MvcChannelHandler 获取
            if (MvcChannelHandler.Clients != null && MvcChannelHandler.Clients.TryGetValue(connectionId, out var webSocket))
            {
                return webSocket;
            }

            // Try to get from GlobalClusterCenter connection provider / 尝试从 GlobalClusterCenter 连接提供者获取
            var connectionProvider = GlobalClusterCenter.ConnectionProvider;
            if (connectionProvider != null)
            {
                return connectionProvider.GetConnection(connectionId);
            }

            return null;
        }

        #endregion

        #region Extension Methods for Convenient Usage / 扩展方法（便于使用）

        /// <summary>
        /// Send byte array to connection(s) - extension method for convenient usage
        /// 向连接发送字节数组 - 扩展方法（便于使用）
        /// </summary>
        /// <param name="data">Message data as byte array / 消息数据（字节数组）</param>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <returns>True if sent successfully / 发送成功返回 true</returns>
        public static async Task<bool> SendAsync(
            this byte[] data,
            string connectionId,
            WebSocketMessageType messageType = WebSocketMessageType.Text)
        {
            return await SendAsync(connectionId, data, messageType);
        }

        /// <summary>
        /// Send byte array to multiple connections - extension method for convenient usage
        /// 向多个连接发送字节数组 - 扩展方法（便于使用）
        /// </summary>
        /// <param name="data">Message data as byte array / 消息数据（字节数组）</param>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <returns>Dictionary of connection ID to send result / 连接ID到发送结果的字典</returns>
        public static async Task<Dictionary<string, bool>> SendAsync(
            this byte[] data,
            IEnumerable<string> connectionIds,
            WebSocketMessageType messageType = WebSocketMessageType.Text)
        {
            return await SendAsync(connectionIds, data, messageType);
        }

        /// <summary>
        /// Send text message to connection(s) - extension method for convenient usage
        /// 向连接发送文本消息 - 扩展方法（便于使用）
        /// </summary>
        /// <param name="text">Text message / 文本消息</param>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>True if sent successfully / 发送成功返回 true</returns>
        public static async Task<bool> SendTextAsync(
            this string text,
            string connectionId,
            Encoding encoding = null)
        {
            return await SendAsync(connectionId, text, encoding);
        }

        /// <summary>
        /// Send text message to multiple connections - extension method for convenient usage
        /// 向多个连接发送文本消息 - 扩展方法（便于使用）
        /// </summary>
        /// <param name="text">Text message / 文本消息</param>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>Dictionary of connection ID to send result / 连接ID到发送结果的字典</returns>
        public static async Task<Dictionary<string, bool>> SendTextAsync(
            this string text,
            IEnumerable<string> connectionIds,
            Encoding encoding = null)
        {
            return await SendAsync(connectionIds, text, encoding);
        }

        /// <summary>
        /// Send JSON object to connection(s) - extension method for convenient usage (excludes string type)
        /// 向连接发送 JSON 对象 - 扩展方法（便于使用，排除 string 类型）
        /// </summary>
        /// <typeparam name="T">Object type (must not be string) / 对象类型（不能是 string）</typeparam>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="options">JSON serializer options / JSON 序列化选项</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>True if sent successfully / 发送成功返回 true</returns>
        public static async Task<bool> SendJsonAsync<T>(
            this T data,
            string connectionId,
            JsonSerializerOptions options = null,
            Encoding encoding = null)
            where T : class
        {
            return await SendAsync(connectionId, data, options, encoding);
        }

        /// <summary>
        /// Send JSON object to multiple connections - extension method for convenient usage (excludes string type)
        /// 向多个连接发送 JSON 对象 - 扩展方法（便于使用，排除 string 类型）
        /// </summary>
        /// <typeparam name="T">Object type (must not be string) / 对象类型（不能是 string）</typeparam>
        /// <param name="data">Object to serialize / 要序列化的对象</param>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表</param>
        /// <param name="options">JSON serializer options / JSON 序列化选项</param>
        /// <param name="encoding">Text encoding, defaults to UTF-8 / 文本编码，默认为 UTF-8</param>
        /// <returns>Dictionary of connection ID to send result / 连接ID到发送结果的字典</returns>
        public static async Task<Dictionary<string, bool>> SendJsonAsync<T>(
            this T data,
            IEnumerable<string> connectionIds,
            JsonSerializerOptions options = null,
            Encoding encoding = null)
            where T : class
        {
            return await SendAsync(connectionIds, data, options, encoding);
        }

        /// <summary>
        /// Send stream to connection(s) - extension method for convenient usage
        /// 向连接发送流 - 扩展方法（便于使用）
        /// </summary>
        /// <param name="stream">Stream to send / 要发送的流</param>
        /// <param name="connectionId">Connection ID / 连接 ID</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="chunkSize">Chunk size in bytes (default: 64KB) / 块大小（字节，默认：64KB）</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <returns>True if sent successfully / 发送成功返回 true</returns>
        public static async Task<bool> SendAsync(
            this Stream stream,
            string connectionId,
            WebSocketMessageType messageType = WebSocketMessageType.Binary,
            int chunkSize = 64 * 1024,
            CancellationToken cancellationToken = default)
        {
            if (stream == null || !stream.CanRead)
            {
                return false;
            }

            // Check if cluster is enabled / 检查是否启用集群
            var clusterManager = GlobalClusterCenter.ClusterManager;
            if (clusterManager != null)
            {
                // Use cluster routing / 使用集群路由
                return await clusterManager.RouteStreamAsync(connectionId, stream, messageType, chunkSize, cancellationToken);
            }
            else
            {
                // Use local WebSocket / 使用本地 WebSocket
                var webSocket = GetLocalWebSocket(connectionId);
                if (webSocket != null && webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        await SendLocalAsync(stream, messageType, cancellationToken, timeout: null, sendAtOnce: false, sendBufferSize: (uint)chunkSize, sockets: webSocket);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Send stream to multiple connections - extension method for convenient usage
        /// 向多个连接发送流 - 扩展方法（便于使用）
        /// </summary>
        /// <param name="stream">Stream to send / 要发送的流</param>
        /// <param name="connectionIds">Connection IDs / 连接 ID 列表</param>
        /// <param name="messageType">WebSocket message type / WebSocket 消息类型</param>
        /// <param name="chunkSize">Chunk size in bytes (default: 64KB) / 块大小（字节，默认：64KB）</param>
        /// <param name="cancellationToken">Cancellation token / 取消令牌</param>
        /// <returns>Dictionary of connection ID to send result / 连接ID到发送结果的字典</returns>
        public static async Task<Dictionary<string, bool>> SendAsync(
            this Stream stream,
            IEnumerable<string> connectionIds,
            WebSocketMessageType messageType = WebSocketMessageType.Binary,
            int chunkSize = 64 * 1024,
            CancellationToken cancellationToken = default)
        {
            if (stream == null || !stream.CanRead)
            {
                return new Dictionary<string, bool>();
            }

            // Check if cluster is enabled / 检查是否启用集群
            var clusterManager = GlobalClusterCenter.ClusterManager;
            if (clusterManager != null)
            {
                // Use cluster routing / 使用集群路由
                return await clusterManager.RouteStreamsAsync(connectionIds, stream, messageType, chunkSize, cancellationToken);
            }
            else
            {
                // Use local WebSocket / 使用本地 WebSocket
                var results = new Dictionary<string, bool>();
                var connectionIdList = connectionIds.Where(id => !string.IsNullOrEmpty(id)).ToList();

                if (connectionIdList.Count == 0)
                {
                    return results;
                }

                // For multiple connections, buffer the stream once into an immutable byte[].
                // 对于多个连接，将流一次性缓冲为不可变的 byte[]。
                // 关键：不能让多个并发任务共享同一个可变 MemoryStream 并各自重置 Position——
                // 并发读取会相互踩踏导致发给不同客户端的数据损坏；结果也不能并发写普通 Dictionary。
                // Critical: concurrent tasks must NOT share one mutable MemoryStream and reset its
                // Position — concurrent reads corrupt each other, delivering garbled data to clients;
                // and results must not be written to a plain Dictionary concurrently.
                if (connectionIdList.Count > 1)
                {
                    byte[] payload;
                    using (var memoryStream = new MemoryStream())
                    {
                        await stream.CopyToAsync(memoryStream, cancellationToken);
                        payload = memoryStream.ToArray();
                    }

                    var sendResults = await Task.WhenAll(connectionIdList.Select(async connectionId =>
                    {
                        var webSocket = GetLocalWebSocket(connectionId);
                        if (webSocket != null && webSocket.State == WebSocketState.Open)
                        {
                            try
                            {
                                await SendLocalAsync(new ReadOnlyMemory<byte>(payload), messageType, sendAtOnce: false, cancellationToken, timeout: null, sendBufferSize: (uint)chunkSize, sockets: webSocket);
                                return (connectionId, ok: true);
                            }
                            catch
                            {
                                return (connectionId, ok: false);
                            }
                        }
                        return (connectionId, ok: false);
                    }));

                    foreach (var (connectionId, ok) in sendResults)
                    {
                        results[connectionId] = ok;
                    }
                }
                else
                {
                    // Single connection - stream directly / 单个连接 - 直接流式传输
                    var connectionId = connectionIdList[0];
                    var webSocket = GetLocalWebSocket(connectionId);
                    if (webSocket != null && webSocket.State == WebSocketState.Open)
                    {
                        try
                        {
                            await SendLocalAsync(stream, messageType, cancellationToken, timeout: null, sendAtOnce: false, sendBufferSize: (uint)chunkSize, sockets: webSocket);
                            results[connectionId] = true;
                        }
                        catch
                        {
                            results[connectionId] = false;
                        }
                    }
                    else
                    {
                        results[connectionId] = false;
                    }
                }

                return results;
            }
        }

        #endregion
    }



}