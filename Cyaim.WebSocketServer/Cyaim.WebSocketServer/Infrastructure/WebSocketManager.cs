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
        /// <summary>
        /// 优雅关闭（Close 帧）的时间上限，超出即 Abort 兜底。
        /// How long a graceful Close may take before falling back to Abort.
        /// </summary>
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// 发送前物化的最大载荷字节数，&lt;=0 表示不限。由通道处理器从 WebSocketRouteOption 镜像过来
        /// （WebSocketManager 是静态类，拿不到 options，与 WebSocketReceiveMemoryGovernor.MaxBytes 同一手法）。
        /// Largest payload materialised before sending; &lt;= 0 = unlimited. Mirrored in by the channel
        /// handlers, the same way WebSocketReceiveMemoryGovernor.MaxBytes is.
        /// </summary>
        public static long MaxSendMaterializeBytes = 4L * 1024 * 1024;

        /// <summary>单个 WebSocket 帧的最大字节数，&lt;=0 表示不切分。Largest frame written; &lt;= 0 = never split.</summary>
        public static int MaxSendFrameBytes = 256 * 1024 - 16;

        /// <summary>超过物化上限时是否降级流式。Whether payloads over the limit fall back to streaming.</summary>
        public static bool AllowChunkedSendAboveMaterializeLimit = true;

        /// <summary>
        /// 扇出时在途帧字节的软预算，用来收敛并发波次大小。纯本地计算，不记账。
        /// Soft budget for in-flight frame bytes during fan-out, used to size concurrency waves.
        /// Computed locally; nothing is tracked.
        /// </summary>
        private const long FanOutFrameBudgetBytes = 64L * 1024 * 1024;

        /// <summary>
        /// 单个数组能装下的绝对上界。即使把物化上限配成「不限」，超过这个大小的载荷也只能走流式——
        /// 「不限」指的是不限制**消息大小**，不是「无论多大都读进内存」。
        /// The hard ceiling one array can hold. Even with the materialization limit set to unlimited, a
        /// payload beyond this must stream — "unlimited" means no limit on <b>message size</b>, not
        /// "read it all into memory whatever it is".
        /// </summary>
        private const long MaxMaterializableBytes = int.MaxValue;

        private static SemaphoreSlim GetSendLock(WebSocket socket)
        {
            return SendLocks.GetValue(socket, static _ => new SemaphoreSlim(1, 1));
        }

        #region Send core

        private static async Task<bool> SendBufferCoreAsync(WebSocket socket, ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool sendAtOnce, uint sendBufferSize, CancellationToken cancellationToken)
        {
            _ = sendAtOnce;        // 分帧只由 MaxSendFrameBytes 决定，见 SendFramedAsync 的注释。
            _ = sendBufferSize;    // Framing is decided solely by MaxSendFrameBytes; see SendFramedAsync.

            var gate = GetSendLock(socket);

            // 调用方的取消令牌只在这里生效：排队等门闩期间取消是安全的，因为一帧都还没发。
            // The caller's token applies here only: cancelling while queued is safe, no frame has gone out.
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            bool wroteFrames = false;
            try
            {
                try
                {
                    if (socket.State != WebSocketState.Open)
                    {
                        return false;
                    }

                    await SendFramedAsync(socket, buffer, messageType).ConfigureAwait(false);
                    return true;
                }
                catch
                {
                    // 写失败后线上有没有字节、有几个字节都无从得知，帧可能被撕断。
                    // After a failed write there is no telling what reached the wire; a frame may be torn.
                    wroteFrames = true;
                    throw;
                }
                finally
                {
                    gate.Release();
                }
            }
            catch
            {
                if (wroteFrames)
                {
                    // 门闩此刻已经释放（上面的 finally 先于这里执行）。不 await：终结自身从不抛异常，
                    // 而让调用方（尤其是扇出的一整波）陪着等一个 Close 超时是没有意义的。
                    // The gate is already free (the finally above ran first). Not awaited: termination never
                    // throws, and making the caller — a whole fan-out wave, in particular — wait out a close
                    // timeout buys nothing.
                    _ = TerminateAfterPartialWriteAsync(socket, I18nText.Send_PayloadSourceFailedMidStream);
                }

                throw;
            }
        }

        /// <summary>
        /// 单 socket 的流发送：能物化就先物化再写第一帧，否则边读边发。
        /// Sends a stream to one socket: materialise before writing the first frame when it fits, otherwise stream.
        /// </summary>
        private static async Task SendStreamCoreAsync(WebSocket socket, Stream stream, WebSocketMessageType messageType, bool sendAtOnce, uint sendBufferSize, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            _ = sendAtOnce;
            _ = timeout;

            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            long cap = MaxSendMaterializeBytes;
            long ceiling = cap > 0 ? Math.Min(cap, MaxMaterializableBytes) : MaxMaterializableBytes;
            long want = stream.CanSeek ? Math.Max(stream.Length - stream.Position, 0) : ceiling;

            // 拿不到预算就降级流式，绝不等待——等一个可能永不释放的发送预算会饿死健康连接。
            // Missing the budget degrades to streaming; waiting on one a stalled peer may never release
            // would starve healthy connections.
            bool reserved = WebSocketSendMemoryGovernor.TryReserve(want);

            Materialized materialized = default;
            try
            {
                if (reserved)
                {
                    try
                    {
                        // 物化在门闩之外做：持锁期间不做 IO，否则同一 socket 上的其他消息要陪着等整段读取。
                        // Materialise outside the gate: holding it across IO makes every other message on
                        // this socket wait out the whole read.
                        materialized = await MaterializeAsync(stream, sendBufferSize, ceiling, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // ★ 一帧都还没发。调用方拿到异常，连接毫发无伤，对端什么都没收到。
                        // ★ Not one frame went out: the caller gets the exception, the connection is untouched.
                        throw;
                    }

                    if (!materialized.OverLimit)
                    {
                        await SendBufferCoreAsync(socket, materialized.AsMemory(), messageType, true, sendBufferSize, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }

                // ── 超出物化上限，或全局预算不足 ──
                // 预算不足只是瞬时内存压力，不是调用方的用法错误，所以降级流式而不是抛异常。
                // 只有调用方显式关掉降级时才拒绝。
                // Budget pressure is transient and not a caller error, so it degrades to streaming rather
                // than throwing. Only an explicit opt-out refuses.
                if (reserved && !AllowChunkedSendAboveMaterializeLimit)
                {
                    long? size = stream.CanSeek ? want : (long?)null;
                    throw new WebSocketMessageTooLargeException(size, ceiling, I18nText.Send_PayloadTooLarge(size, ceiling));
                }

                await SendStreamChunkedAsync(socket, stream, messageType, materialized, sendBufferSize, stream.CanSeek ? want : -1, cancellationToken).ConfigureAwait(false);
                materialized = default;   // 所有权已转移给 SendStreamChunkedAsync / ownership moved
            }
            finally
            {
                materialized.Return();

                // ★ 只归还真正预留过的量。TryReserve 失败时它内部已经回滚，这里再 Release 会让全局计数
                // 单向走负，预算随之被架空——恰恰是在它最该起作用的高压场景下失效。
                // ★ Release only what was actually reserved. A failed TryReserve already rolled itself back,
                // so releasing again drives the counter negative and defeats the budget — precisely under
                // the pressure it exists for.
                if (reserved)
                {
                    WebSocketSendMemoryGovernor.Release(want);
                }
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
            // 波次大小按「在途帧字节」收敛，而不是固定连接数。
            // 一条 4 MiB 载荷发给 1000 个 socket，若一律并发，每个 socket 的帧缓冲同时存在 —— 实测峰值超 5 GB。
            // 按帧字节算波次把它压回一个常数级预算，且全程不需要任何记账。
            // Waves are sized by in-flight frame bytes rather than a fixed connection count. Fanning a
            // 4 MiB payload to 1000 sockets all at once means 1000 concurrent frame buffers — measured at
            // over 5 GB peak. Sizing by frame bytes holds it to a constant budget with no accounting.
            int frameBytes = MaxSendFrameBytes > 0
                ? Math.Min(MaxSendFrameBytes, Math.Max(buffer.Length, 1))
                : Math.Max(buffer.Length, 1);

            // 不能用 Math.Clamp(x, 16, BatchProcessingWebsocketLimit)：该上限是可配置的，被调到小于 16
            // 时 Clamp 会因 min > max 抛 ArgumentException。配置的上限是硬上限，预算只能把波次调小。
            // Not Math.Clamp(x, 16, BatchProcessingWebsocketLimit): that limit is configurable and Clamp
            // throws when min > max. The configured limit is a hard ceiling; the budget only shrinks waves.
            long byBudget = Math.Max(1, FanOutFrameBudgetBytes / frameBytes);
            int waveLimit = (int)Math.Min(byBudget, Math.Max(1, BatchProcessingWebsocketLimit));

            List<Task> batch = new List<Task>(Math.Min(sockets.Length, waveLimit));
            for (int i = 0; i < sockets.Length; i++)
            {
                WebSocket socket = sockets[i];
                if (socket == null || socket.State != WebSocketState.Open)
                {
                    continue;
                }
                batch.Add(SendBufferCoreAsync(socket, buffer, messageType, sendAtOnce, sendBufferSize, cancellationToken));
                if (batch.Count >= waveLimit)
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
                try
                {
                    await sendTask.ConfigureAwait(false);
                }
                catch (WebSocketMessageTooLargeException)
                {
                    // 唯一不吞的异常。传超时的调用方接受「发送失败也不告诉我」，但「载荷太大所以一个字节
                    // 都没发」不是发送失败——它是调用方用法问题，吞掉就等于让消息凭空消失且无从排查。
                    // The one exception not swallowed. A caller passing a timeout accepts not hearing about
                    // send failures, but "the payload was too large so nothing was sent" is not a send
                    // failure — it is a usage error, and swallowing it makes messages vanish undiagnosably.
                    throw;
                }
                catch { }
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
        /// <summary>
        /// 已经全部读进内存的载荷。Rented 为 null 表示什么都没读（超限且可 Seek）。
        /// A payload fully read into memory. A null Rented means nothing was read (over limit, seekable).
        /// </summary>
        private struct Materialized
        {
            public byte[] Rented;
            public int Length;
            public bool OverLimit;

            public ReadOnlyMemory<byte> AsMemory() => Rented == null ? ReadOnlyMemory<byte>.Empty : Rented.AsMemory(0, Length);

            public void Return()
            {
                if (Rented != null)
                {
                    ArrayPool<byte>.Shared.Return(Rented);
                    Rented = null;
                    Length = 0;
                }
            }
        }

        /// <summary>
        /// 把整条载荷读进池化缓冲区。读失败时抛出，此时**一帧都还没发**。
        /// Reads the whole payload into a pooled buffer. Throws on failure, at which point <b>no frame has
        /// been written</b> — which is the entire point of doing this before touching the socket.
        /// </summary>
        /// <remarks>
        /// <para>
        /// 可 Seek 的流会校验读到的字节数等于声明的长度。**静默短读**（Read 返回 0 但源其实没读完，
        /// 例如网络文件系统抖动）在这里被抓住并抛出；不做这个校验的话，它会变成一条语法完整、
        /// 内容却少了一截的消息发给对端，而没有任何一方知道。
        /// A seekable stream is checked against its declared length. A <b>silent short read</b> — Read
        /// returning 0 while the source is not actually finished, as a flaky network filesystem does — is
        /// caught here. Without the check it becomes a syntactically complete message that is quietly
        /// missing its tail, with nobody on either end the wiser.
        /// </para>
        /// <para>
        /// 不可 Seek 的流按倍增扩容读到 EOF；到达上限时返回已读前缀并置 OverLimit，交给流式路径接着发，
        /// 避免白读一遍。
        /// A non-seekable stream is read to EOF with a doubling buffer; on hitting the cap it returns the
        /// prefix already read with OverLimit set, so the streaming path can continue from there instead
        /// of reading it all again.
        /// </para>
        /// </remarks>
        private static async Task<Materialized> MaterializeAsync(Stream stream, uint readChunk, long cap, CancellationToken cancellationToken)
        {
            if (stream.CanSeek)
            {
                // ★ 必须减去 Position：从中途开始的流只该发剩下那段。
                // ★ Subtract Position: a stream positioned mid-way should send only the remainder.
                long remaining = Math.Max(stream.Length - stream.Position, 0);
                if (cap > 0 && remaining > cap)
                {
                    return new Materialized { OverLimit = true };
                }

                byte[] seekBuffer = ArrayPool<byte>.Shared.Rent((int)remaining);
                int read = 0;
                try
                {
                    while (read < remaining)
                    {
                        int n = await stream.ReadAsync(seekBuffer.AsMemory(read, (int)(remaining - read)), cancellationToken).ConfigureAwait(false);
                        if (n <= 0)
                        {
                            break;
                        }
                        read += n;
                    }
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(seekBuffer);
                    throw;
                }

                if (read != remaining)
                {
                    ArrayPool<byte>.Shared.Return(seekBuffer);
                    throw new EndOfStreamException(I18nText.Send_SourceStreamEndedEarly(read, remaining));
                }

                return new Materialized { Rented = seekBuffer, Length = read };
            }

            int size = (int)Math.Max(readChunk, 4096);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
            int length = 0;
            try
            {
                while (true)
                {
                    if (length == buffer.Length)
                    {
                        if (cap > 0 && buffer.Length >= cap)
                        {
                            return new Materialized { Rented = buffer, Length = length, OverLimit = true };
                        }

                        byte[] grown = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                        Buffer.BlockCopy(buffer, 0, grown, 0, length);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = grown;
                    }

                    int n = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        break;
                    }
                    length += n;
                }
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }

            return new Materialized { Rented = buffer, Length = length };
        }

        /// <summary>
        /// 一条多帧消息已经有字节进入传输层却写不下去时，终结这条连接——优雅关闭优先，Abort 兜底。
        /// Terminates a connection whose multi-frame message has bytes on the wire but cannot be finished:
        /// a graceful close first, Abort only as a fallback.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>为什么不补一个收尾帧。</b>补收尾帧会让对端收到一条**语法完整、内容却少了一截**的消息，
        /// 而客户端判断「消息到齐了」的唯一依据就是那个结束标志——于是每一个客户端都得自己去识别并
        /// 丢弃截断消息。那是把服务端的正确性问题变成所有客户端的负担，不可接受。
        /// <b>Why not simply terminate the message.</b> That hands the peer a message which is
        /// syntactically complete but quietly missing its tail, and that end-of-message flag is the only
        /// thing a client has to decide a message arrived whole. Every client would then need its own
        /// truncation detection — the server's correctness problem pushed onto all of them. Not acceptable.
        /// </para>
        /// <para>
        /// <b>为什么先 Close 再 Abort。</b>Close 是有序关闭：对端接收缓冲区里那些**已经完整送达**的
        /// 消息仍会被交付给它的应用，而且对端能拿到状态码与原因文本。Abort 是 RST，会把它们一并丢掉。
        /// 只有 Close 本身也失败时才 Abort——注意被取消的 CloseOutputAsync 不会替你中止连接。
        /// <b>Why Close before Abort.</b> A close is orderly: messages already fully delivered into the
        /// peer's receive buffer still reach its application, and the peer gets a status code and a reason.
        /// An Abort is an RST that discards them. Abort is only the fallback for a close that itself fails —
        /// and note that a cancelled CloseOutputAsync does not abort the connection for you.
        /// </para>
        /// <para>
        /// 已经在关闭流程中（CloseSent/Closed/Aborted）时直接返回：此时再发 RST 只会白丢对端缓冲区里
        /// 那些好消息。幂等，可重复调用。
        /// A connection already closing is left alone: an RST then only discards good messages still
        /// sitting in the peer's buffer. Idempotent and safe to call more than once.
        /// </para>
        /// </remarks>
        private static async Task TerminateAfterPartialWriteAsync(WebSocket socket, string reason)
        {
            var state = socket.State;
            if (state == WebSocketState.Closed || state == WebSocketState.Aborted || state == WebSocketState.CloseSent)
            {
                return;
            }

            try
            {
                using var closeTimeout = new CancellationTokenSource(CloseTimeout);
                await socket.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, reason, closeTimeout.Token).ConfigureAwait(false);
                return;
            }
            catch
            {
                // 优雅关闭没成功，往下走 Abort。/ The graceful close failed; fall through to Abort.
            }

            try { socket.Abort(); } catch { }
        }

        /// <summary>
        /// 写一帧。失败时直接抛出，由持有门闩的调用方在**释放门闩之后**终结连接。
        /// Writes one frame. On failure it simply throws; the caller holding the gate terminates the
        /// connection <b>after releasing it</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>数据帧永远用 <see cref="CancellationToken.None"/>。</b>取消一个飞行中的帧会让真实的
        /// ManagedWebSocket 直接 Abort 整条连接——于是一次普通的请求取消或主机关停就把连接断了。
        /// 调用方的令牌只在两个安全点生效：排队等发送门闩时，以及读取源数据时；那两处取消时一帧未发。
        /// <b>Data frames always use <see cref="CancellationToken.None"/>.</b> Cancelling an in-flight frame
        /// makes the real ManagedWebSocket abort the whole connection, so an ordinary request cancellation
        /// or a host shutdown would drop it. The caller's token applies at the two safe points instead:
        /// queueing for the send gate, and reading the source — at both, no frame has gone out.
        /// </para>
        /// <para>
        /// 终结不在这里做，是因为发 Close 帧最多要等 <see cref="CloseTimeout"/>。若在门闩之内等，一个半死
        /// 的连接会把门闩占满整个超时；而扇出每一波是 <c>Task.WhenAll</c> 等齐的，同一波里其余健康连接
        /// 会被一起拖住。
        /// Termination is not done here because writing a Close frame can take up to <see cref="CloseTimeout"/>.
        /// Waiting for that inside the gate lets one half-dead connection hold it for the whole timeout, and
        /// since each fan-out wave is awaited with <c>Task.WhenAll</c>, every healthy connection in that wave
        /// is held up with it.
        /// </para>
        /// </remarks>
        private static Task SendFrameAsync(WebSocket socket, ReadOnlyMemory<byte> frame, WebSocketMessageType messageType, bool endOfMessage)
        {
            return socket.SendAsync(frame, messageType, endOfMessage, CancellationToken.None).AsTask();
        }

        /// <summary>
        /// 把一段**已在内存里**的载荷按帧上限写出去，最后一帧携带 <c>endOfMessage: true</c>。
        /// Writes an <b>already in-memory</b> payload out under the frame cap, the last frame carrying
        /// <c>endOfMessage: true</c>.
        /// </summary>
        /// <remarks>
        /// 分帧与「消息是否完整」无关：数据已经在内存里，唯一可能的失败是写失败，而写失败一律终结连接，
        /// 产生不了带结束标志的短消息。这里不再发那个多余的空收尾帧——最后一帧自己带标志即可。
        /// Framing has nothing to do with completeness here: the data is already in memory, so the only
        /// possible failure is a write failure, which always terminates the connection and therefore cannot
        /// produce a short message carrying the end flag. The redundant empty terminator frame is gone —
        /// the last data frame carries the flag itself.
        /// </remarks>
        private static async Task SendFramedAsync(WebSocket socket, ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType)
        {
            int cap = MaxSendFrameBytes;
            if (cap <= 0 || buffer.Length <= cap)
            {
                await SendFrameAsync(socket, buffer, messageType, true).ConfigureAwait(false);
                return;
            }

            for (int offset = 0; ;)
            {
                int count = Math.Min(cap, buffer.Length - offset);
                bool last = offset + count == buffer.Length;
                await SendFrameAsync(socket, buffer.Slice(offset, count), messageType, last).ConfigureAwait(false);
                offset += count;
                if (last)
                {
                    return;
                }
            }
        }


        /// <summary>
        /// 超出物化上限时的流式发送：边读边发，绝不物化。
        /// The streaming path used above the materialization limit: read and write as it goes, never buffering.
        /// </summary>
        /// <remarks>
        /// <para>
        /// 这条路径无法做到「失败时一帧未发」——载荷太大，装不进内存。所以它的保证降一级但仍然成立：
        /// <b>只要发出过 <c>endOfMessage: true</c>，内容就一定完整</b>。中途失败时不补收尾帧，而是终结
        /// 连接，于是对端只会看到协议错误或连接关闭，绝不会看到一条内容被截断的「完整」消息。
        /// This path cannot promise "no frame written on failure" — the payload does not fit in memory. Its
        /// guarantee is one step weaker but still holds: <b>whenever <c>endOfMessage: true</c> goes out, the
        /// content is complete</b>. A mid-stream failure terminates the connection instead of the message.
        /// </para>
        /// <para>
        /// 收尾前会核对总量。可 Seek 的流若发生**静默短读**（Read 返回 0 但源其实没读完），这里会抓住
        /// 并抛出，而不是把它当成正常 EOF 发出收尾帧——后者正是「语法完整但少了一截」的经典来源。
        /// 不可 Seek 的流报不出长度，这条校验对它不成立，属已知残余漏洞（见文档）。
        /// The total is checked before terminating. On a seekable stream a <b>silent short read</b> is caught
        /// here rather than mistaken for a clean EOF. A non-seekable stream reports no length, so the check
        /// cannot apply to it — a known residual gap, documented as such.
        /// </para>
        /// </remarks>
        private static async Task SendStreamChunkedAsync(WebSocket socket, Stream stream, WebSocketMessageType messageType, Materialized prefix, uint readChunk, long expectedTotal, CancellationToken cancellationToken)
        {
            int frameCap = MaxSendFrameBytes > 0 ? MaxSendFrameBytes : (int)Math.Max(readChunk, 64 * 1024);
            var gate = GetSendLock(socket);

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(frameCap);
            bool anyFrameSent = false;
            long written = 0;
            try
            {
                try
                {
                    if (socket.State != WebSocketState.Open)
                    {
                        return;
                    }

                    // 不可 Seek 的流在探测上限时已经读进了一段前缀，先把它发出去，不要重读。
                    // A non-seekable stream already has a prefix read while probing the limit; send it rather
                    // than trying to read it again.
                    for (int offset = 0; offset < prefix.Length; offset += frameCap)
                    {
                        int slice = Math.Min(frameCap, prefix.Length - offset);
                        await SendFrameAsync(socket, prefix.Rented.AsMemory(offset, slice), messageType, false).ConfigureAwait(false);
                        anyFrameSent = true;
                        written += slice;
                    }

                    while (true)
                    {
                        // 读源用调用方的令牌；写帧永远用 None（见 SendFrameAsync）。
                        // The source is read under the caller's token; frames are always written with None.
                        int read = await stream.ReadAsync(buffer.AsMemory(0, frameCap), cancellationToken).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }

                        await SendFrameAsync(socket, buffer.AsMemory(0, read), messageType, false).ConfigureAwait(false);
                        anyFrameSent = true;
                        written += read;
                    }

                    if (expectedTotal >= 0 && written != expectedTotal)
                    {
                        throw new EndOfStreamException(I18nText.Send_SourceStreamEndedEarly(written, expectedTotal));
                    }

                    await SendFrameAsync(socket, Memory<byte>.Empty, messageType, true).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    prefix.Return();
                    gate.Release();
                }
            }
            catch
            {
                // 读失败、静默短读、写失败，统一收场：绝不收尾，终结连接。
                // 门闩此刻已经释放（上面的 finally 先于这里执行），所以 Close 的等待不会拖住别的发送。
                // Read failure, silent short read, write failure — one ending: never terminate the message,
                // terminate the connection. The gate is already free (the finally above ran first), so the
                // close does not hold up anything else.
                if (anyFrameSent)
                {
                    _ = TerminateAfterPartialWriteAsync(socket, I18nText.Send_PayloadSourceFailedMidStream);
                }

                throw;
            }
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
                sendTask = SendStreamCoreAsync(single, sendStream, messageType, sendAtOnce, sendBufferSize, timeout, cancellationToken);
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
        /// 一条流分发给多个 socket：先物化一次，再扇出。
        /// Buffers a stream once, then fans it out to many sockets.
        /// </summary>
        /// <remarks>
        /// 扇出**不施加** <see cref="MaxSendMaterializeBytes"/>：一条流只能读一次，无法为每个 socket 各读
        /// 一遍，所以这里没有流式降级可选，施加上限等于直接砍掉「向多个连接广播大流」这个既有能力。
        /// 与旧实现（无条件 <c>CopyToAsync</c> 到 MemoryStream）行为一致，只是改用池化缓冲，少一次整体拷贝。
        /// 真正需要为广播设内存上界时，用进程级的 <see cref="MaxTotalSendMaterializeBytes"/>。
        /// Fan-out does <b>not</b> apply <see cref="MaxSendMaterializeBytes"/>: a stream can only be read
        /// once, so there is no streaming fallback here and applying the limit would simply remove the
        /// existing ability to broadcast a large stream. This matches the old implementation (an
        /// unconditional CopyToAsync into a MemoryStream), just with a pooled buffer and one copy less.
        /// Bound broadcast memory with the process-wide <see cref="MaxTotalSendMaterializeBytes"/> instead.
        /// </remarks>
        private static async Task SendStreamToManyAsync(WebSocket[] sockets, Stream stream, WebSocketMessageType messageType, bool sendAtOnce, uint sendBufferSize, CancellationToken cancellationToken)
        {
            var materialized = await MaterializeAsync(stream, sendBufferSize, MaxMaterializableBytes, cancellationToken).ConfigureAwait(false);

            try
            {
                await SendBufferToManyAsync(sockets, materialized.AsMemory(), messageType, sendAtOnce, sendBufferSize, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                materialized.Return();
            }
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
                    catch (WebSocketMessageTooLargeException)
                    {
                        // 「太大所以一帧没发」必须与「socket 已关」「IO 错误」区分开：前者重试无用，
                        // 要么调高上限、要么在应用层分块。压成 false 会让调用方无从判断。
                        // "Too large, nothing sent" must be distinguishable from "the socket is gone" and
                        // "an IO error": retrying never helps the first, which needs a higher limit or
                        // application-level chunking. Flattening it into false hides that.
                        throw;
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