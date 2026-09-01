using System.Net.WebSockets;
using System.Text;
using Cyaim.WebSocketServer.Infrastructure;
using Xunit;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// That a peer which stops reading is disconnected rather than buffered without limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sends on one socket are serialised by a per-socket gate, which is necessary and was already
    /// there. What was missing is a depth for the queue <i>behind</i> it: one peer that stops reading
    /// parks the send holding the gate, and every send after it waits — each holding its own
    /// serialised payload — with nothing bounding how many pile up. The wait was also handed
    /// <c>CancellationToken.None</c> by the request path, so the one escape hatch the gate offered
    /// was closed.
    /// 一条 socket 上的发送由每 socket 的门闩串行化，这是必要的、本来就有。缺的是门闩**后面**那条
    /// 队列的深度：一个不读的对端会让持有门闩的那次发送停住，其后每次发送都在等，
    /// 每个都攥着自己那份序列化好的载荷，而没有任何东西约束它们能堆多少。
    /// 请求路径传给那次等待的还是 CancellationToken.None，于是门闩本来提供的唯一逃生口也被关上了。
    /// </para>
    /// <para>
    /// <b>Bounding in-flight requests does not close this, and that was measured rather than assumed.</b>
    /// With the in-flight cap at 3 and a client that never read a byte, all 40 pipelined requests were
    /// still dispatched and their responses still queued: the transport buffered the sends, so every
    /// request completed and returned its permit. The earlier appearance of backpressure came from a
    /// different bug — permits that were never returned at all — not from this queue. The two bounds
    /// are independent and both are needed.
    /// **把在途请求封顶并不能关掉这一条，而这是量出来的、不是假设的**：在途上限设为 3、
    /// 客户端一个字节都不读时，40 条流水线请求仍然全部被派发、响应仍然全部排上队——
    /// 传输层缓冲了发送，于是每条请求都完成并还了票。此前看起来像背压的那一幕，
    /// 来自另一个缺陷（票根本没还回来），不是这条队列。两条界互相独立，都需要。
    /// </para>
    /// <para>
    /// Abort, not close: a close handshake is itself a send, and the send path is exactly what is
    /// wedged. Dropping the payload silently was rejected as the alternative — the client would then
    /// wait for a reply that is never coming, which trades an honest disconnect for a hang.
    /// 用 Abort 而不是 Close：关闭握手本身也是一次发送，而卡住的正是发送路径。
    /// 备选方案「静默丢弃载荷」被否决了：那会让客户端一直等一个不会来的回复，
    /// 等于把一次诚实的断连换成一次挂起。
    /// </para>
    /// </remarks>
    public class SendQueueDepthTests : IDisposable
    {
        private readonly int _previousLimit = WebSocketManager.MaxQueuedSendsPerSocket;

        public void Dispose() => WebSocketManager.MaxQueuedSendsPerSocket = _previousLimit;

        /// <summary>A socket whose sends park until released, and that records being aborted.</summary>
        private sealed class ParkingWebSocket : WebSocket
        {
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _aborted;
            private int _started;

            public bool Aborted => Volatile.Read(ref _aborted) != 0;

            /// <summary>Sends that actually reached the socket, as opposed to being queued or refused.</summary>
            public int Started => Volatile.Read(ref _started);

            public void ReleaseSends() => _release.TrySetResult();

            public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _started);
                await _release.Task.ConfigureAwait(false);
            }

            public override void Abort() => Interlocked.Exchange(ref _aborted, 1);

            public override WebSocketCloseStatus? CloseStatus => null;
            public override string CloseStatusDescription => null;
            public override WebSocketState State => WebSocketState.Open;
            public override string SubProtocol => null;
            public override Task CloseAsync(WebSocketCloseStatus s, string d, CancellationToken c) => Task.CompletedTask;
            public override Task CloseOutputAsync(WebSocketCloseStatus s, string d, CancellationToken c) => Task.CompletedTask;
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
                new TaskCompletionSource<WebSocketReceiveResult>().Task;
            public override void Dispose() { }
        }

        [Fact]
        public async Task A_peer_that_stops_reading_is_disconnected_once_its_send_queue_is_full()
        {
            const int limit = 8;
            const int attempted = 40;

            WebSocketManager.MaxQueuedSendsPerSocket = limit;

            var socket = new ParkingWebSocket();
            var payload = Encoding.UTF8.GetBytes("response");

            var sends = new List<Task>();
            for (int i = 0; i < attempted; i++)
            {
                sends.Add(WebSocketManager.SendLocalAsync(
                    payload, WebSocketMessageType.Text, false, CancellationToken.None, sockets: socket));
            }

            // The refused sends return promptly; the admitted ones stay parked, which is the point.
            // 被拒的那些很快返回；被放行的那些停住不动，这正是要的效果。
            for (int i = 0; i < 100 && !socket.Aborted; i++)
            {
                await Task.Delay(20);
            }

            Assert.True(
                socket.Aborted,
                $"a peer that never reads had {attempted} sends attempted behind a queue limit of {limit} "
                + "and was never disconnected; the queue is unbounded");

            // Only one send can be in the socket at a time — the gate guarantees that — and the rest
            // are either queued within the limit or refused. Nothing beyond the limit got in.
            // 同一时刻只有一次发送进得了 socket（门闩保证），其余要么在限额内排队、要么被拒。
            // 超出限额的一个都没进来。
            Assert.True(
                socket.Started <= 1,
                $"{socket.Started} sends reached a socket whose first send never completed");

            socket.ReleaseSends();
            await Task.WhenAll(sends.Select(t => t.ContinueWith(static _ => { })));
        }

        /// <summary>
        /// Setting the limit to zero turns the bound off, and says so.
        /// </summary>
        /// <remarks>
        /// Kept configurable because "disconnect a slow reader" is a product decision, not a universal
        /// truth: a deployment that fronts mobile clients on bad networks may want a deeper queue. Zero
        /// restores the old unbounded behaviour for anyone who needs it, deliberately and in one place,
        /// rather than by patching the library.
        /// 保留可配置，是因为「断开慢读的对端」是一个产品决定而不是普适真理：
        /// 面向弱网移动客户端的部署可能想要更深的队列。设为 0 恢复旧的无界行为——
        /// 让需要它的人显式地、在一个地方做这件事，而不是去改库。
        /// </remarks>
        [Fact]
        public async Task A_zero_limit_restores_the_unbounded_behaviour()
        {
            WebSocketManager.MaxQueuedSendsPerSocket = 0;

            var socket = new ParkingWebSocket();
            var payload = Encoding.UTF8.GetBytes("response");

            var sends = new List<Task>();
            for (int i = 0; i < 32; i++)
            {
                sends.Add(WebSocketManager.SendLocalAsync(
                    payload, WebSocketMessageType.Text, false, CancellationToken.None, sockets: socket));
            }

            await Task.Delay(300);

            Assert.False(socket.Aborted, "a zero limit must not disconnect anybody");

            socket.ReleaseSends();
            await Task.WhenAll(sends.Select(t => t.ContinueWith(static _ => { })));
        }
    }
}
