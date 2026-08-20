using System.Net.WebSockets;
using System.Text;
using Cyaim.WebSocketServer.Infrastructure;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// 分批发送时，消息分帧必须始终收敛到一个确定状态：要么收尾，要么中止连接。
    /// A batched send must always leave framing in a decided state: message terminated, or connection aborted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 分批发送的形状是「若干 <c>endOfMessage:false</c> 帧 + 一个 <c>endOfMessage:true</c> 收尾帧」。
    /// 中途失败而不收尾，这条消息在 WebSocket 协议层就一直没有结束，<b>下一个发送者的帧会被对端当作
    /// 它的延续帧</b>——此后每条消息都被粘在这条残消息后面。该连接的分帧永久错乱，服务端零日志：
    /// 每一次发送调用看起来都成功了。
    /// Batched sending is "N frames with endOfMessage:false, then a terminator". Failing partway without
    /// terminating leaves the message open at the protocol level, so <b>the next sender's frames become
    /// continuation frames of it</b>. Every later message is glued onto the truncated one: framing is broken
    /// for the life of the connection with nothing logged — each send still looks successful.
    /// </para>
    /// <para>
    /// 失败来自哪里决定了怎么处理，两者不能混为一谈：
    /// <list type="bullet">
    /// <item><b>读流失败</b>（调用方 Dispose 了流、IO 错误）：socket 是好的，已上线的都是完整帧 →
    /// 补一个收尾帧，对端收到一条被截断的消息，解不开就丢掉，连接继续可用。</item>
    /// <item><b>写 socket 失败</b>：线上有没有字节、有几个字节都无从得知，帧可能被撕断 →
    /// 空收尾帧救不回来（对端还在等这一帧剩余载荷），只能 Abort。</item>
    /// </list>
    /// Where the failure came from decides the response, and the two must not be conflated: a failed read
    /// leaves a healthy socket and whole frames (terminate), while a failed write leaves the wire in an
    /// unknown state that an empty terminator cannot repair (abort).
    /// </para>
    /// </remarks>
    public class StreamSendFramingTests
    {
        private const int Chunk = 4096;

        [Fact]
        public async Task A_failed_read_closes_the_message_so_the_socket_stays_usable()
        {
            var socket = new RecordingWebSocket();
            var stream = new FailsAfterFirstReadStream(Chunk);

            await Assert.ThrowsAnyAsync<Exception>(() => SendStreamAsync(stream, socket));

            Assert.True(socket.LastFrameEndsMessage, "the message was left open for the next sender to continue");
            Assert.False(socket.Aborted, "a healthy socket must not be aborted just because the stream went away");
        }

        [Fact]
        public async Task The_socket_is_reusable_after_a_failed_read()
        {
            // 补收尾帧的意义就在这里：连接还能用。对端丢掉一条截断消息是可恢复的；
            // 后续帧被静默粘上去不是。
            // This is the point of terminating: the connection keeps working. A peer discarding one truncated
            // message is recoverable; later frames silently glued onto it are not.
            var socket = new RecordingWebSocket();
            await Assert.ThrowsAnyAsync<Exception>(() => SendStreamAsync(new FailsAfterFirstReadStream(Chunk), socket));

            byte[] next = Encoding.UTF8.GetBytes("the next message");
            await WebSocketManager.SendLocalAsync(
                next.AsMemory(), WebSocketMessageType.Text, sendAtOnce: true, CancellationToken.None, sockets: socket);

            var messages = socket.CompletedMessages();
            Assert.Equal(2, messages.Count);
            Assert.Equal(next, messages[1]);
        }

        [Fact]
        public async Task A_failed_write_aborts_instead_of_guessing_what_reached_the_wire()
        {
            // 写失败后帧可能被撕断，空收尾帧救不回来 —— 只能中止。
            var socket = new RecordingWebSocket { FailSendAfter = 1 };
            var stream = new FixedChunksStream(Chunk, chunks: 4);

            await Assert.ThrowsAnyAsync<Exception>(() => SendStreamAsync(stream, socket));

            Assert.True(socket.Aborted, "a torn frame cannot be repaired by a terminator; the connection must be aborted");
        }

        [Fact]
        public async Task A_terminator_that_cannot_be_written_aborts_rather_than_wedging_the_send_gate()
        {
            // 这是本修复最危险的一条自伤路径：收尾帧是在 per-socket 发送门闩之内写的。
            // 若它无限阻塞，门闩永不释放 —— 该连接此后任何发送都排不进去，而 State 仍是 Open、
            // 没有 Abort、没有日志。那比原本的分帧错乱更难恢复，所以收尾帧必须有界。
            // The self-inflicted failure this fix must not have: the terminator is written while the
            // per-socket send gate is held. If it blocked forever the gate would never be released — nothing
            // could ever be sent on this connection again, with State still Open and nothing logged. Worse
            // than the framing corruption it exists to prevent, so the terminator has to be bounded.
            var socket = new RecordingWebSocket { BlockTerminator = true };
            var stream = new FailsAfterFirstReadStream(Chunk);

            var send = SendStreamAsync(stream, socket);

            // 这个断言本身就是「门闩没被占死」的证明：收尾帧是在门闩内 await 的，
            // 若它无限阻塞，这个任务永远不会完成，gate.Release() 所在的 finally 也永远不会执行。
            // 能在有界时间内完成，就说明收尾帧确实有上限。
            // This assertion is itself the proof that the gate is not wedged: the terminator is awaited while
            // the gate is held, so if it blocked forever this task would never complete and the finally
            // holding gate.Release() would never run. Completing within a bound means the terminator is bounded.
            var finished = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.Same(send, finished);
            await Assert.ThrowsAnyAsync<Exception>(() => send);

            Assert.True(socket.Aborted, "an unwritable terminator must abort the connection");
        }

        [Fact]
        public async Task A_message_still_open_in_CloseReceived_is_terminated()
        {
            // .NET 的合法发送状态是 {Open, CloseReceived}：对端发了 Close 但我们还没发，
            // 消息同样开着，同样需要收尾。守卫只判 Open 会漏掉这一格。
            // .NET's valid send states are {Open, CloseReceived}: the peer sent Close but we have not, the
            // message is just as open and still needs terminating. A guard checking only Open misses it.
            var socket = new RecordingWebSocket { StateAfterFirstSend = WebSocketState.CloseReceived };
            var stream = new FailsAfterFirstReadStream(Chunk);

            await Assert.ThrowsAnyAsync<Exception>(() => SendStreamAsync(stream, socket));

            Assert.True(socket.LastFrameEndsMessage, "the message was left open in CloseReceived");
        }

        [Fact]
        public async Task A_completed_stream_send_is_unaffected()
        {
            // 阳性对照：正常路径不能被上面任何一条改变。
            var socket = new RecordingWebSocket();
            byte[] payload = new byte[10_000];
            new Random(7).NextBytes(payload);
            using var stream = new MemoryStream(payload);

            await WebSocketManager.SendLocalAsync(
                stream, WebSocketMessageType.Binary, CancellationToken.None,
                timeout: TimeSpan.FromSeconds(30), sendAtOnce: false, sendBufferSize: Chunk, sockets: socket);

            Assert.Equal(payload, Assert.Single(socket.CompletedMessages()));
            Assert.False(socket.Aborted);
        }

        private static Task SendStreamAsync(Stream stream, WebSocket socket)
            => WebSocketManager.SendLocalAsync(
                stream, WebSocketMessageType.Binary, CancellationToken.None,
                timeout: null, sendAtOnce: false, sendBufferSize: Chunk, sockets: socket);

        // ------------------------------------------------------------------ fakes

        /// <summary>
        /// 记录分帧、可按需让发送失败或让收尾帧卡住，并记录 Abort。
        /// 共享的 TestWebSocket 观察不到这些：它不理会取消、也从不 Abort。
        /// Records framing, can fail a send or wedge the terminator on demand, and records Abort. The shared
        /// TestWebSocket cannot observe any of that — it ignores cancellation and never aborts.
        /// </summary>
        private sealed class RecordingWebSocket : WebSocket
        {
            private readonly object _sync = new object();
            private readonly List<(byte[] Payload, bool EndOfMessage)> _frames = new List<(byte[], bool)>();
            private WebSocketState _state = WebSocketState.Open;
            private int _sends;

            /// <summary>Fail the Nth non-terminating send (1-based). 0 = never.</summary>
            public int FailSendAfter { get; set; }

            /// <summary>Make the terminating frame block until cancelled.</summary>
            public bool BlockTerminator { get; set; }

            /// <summary>Move to this state once the first frame has gone out.</summary>
            public WebSocketState? StateAfterFirstSend { get; set; }

            public bool Aborted { get; private set; }

            public bool LastFrameEndsMessage
            {
                get { lock (_sync) { return _frames.Count > 0 && _frames[_frames.Count - 1].EndOfMessage; } }
            }

            public List<byte[]> CompletedMessages()
            {
                lock (_sync)
                {
                    var messages = new List<byte[]>();
                    var current = new List<byte>();
                    foreach (var f in _frames)
                    {
                        current.AddRange(f.Payload);
                        if (f.EndOfMessage) { messages.Add(current.ToArray()); current = new List<byte>(); }
                    }
                    return messages;
                }
            }

            public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            {
                if (endOfMessage && BlockTerminator)
                {
                    // 无限阻塞，只有取消才能解开 —— 正是对端不排空时真实 socket 的行为。
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }

                if (!endOfMessage && FailSendAfter > 0 && ++_sends >= FailSendAfter)
                {
                    throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
                }

                lock (_sync) { _frames.Add((buffer.ToArray(), endOfMessage)); }

                if (StateAfterFirstSend.HasValue)
                {
                    _state = StateAfterFirstSend.Value;
                    StateAfterFirstSend = null;
                }
            }

            public override void Abort()
            {
                Aborted = true;
                _state = WebSocketState.Aborted;
            }

            public override WebSocketCloseStatus? CloseStatus => null;
            public override string CloseStatusDescription => null;
            public override WebSocketState State => _state;
            public override string SubProtocol => null;
            public override Task CloseAsync(WebSocketCloseStatus s, string d, CancellationToken c) { _state = WebSocketState.Closed; return Task.CompletedTask; }
            public override Task CloseOutputAsync(WebSocketCloseStatus s, string d, CancellationToken c) { _state = WebSocketState.CloseSent; return Task.CompletedTask; }
            public override void Dispose() { }
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        /// <summary>Yields one full chunk, then behaves like a stream somebody disposed.</summary>
        private sealed class FailsAfterFirstReadStream : ReadOnlyStream
        {
            private readonly int _chunk;
            private int _reads;

            public FailsAfterFirstReadStream(int chunk) => _chunk = chunk;

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_reads++ == 0)
                {
                    int n = Math.Min(count, _chunk);
                    Fill(buffer, offset, n);
                    return Task.FromResult(n);
                }

                throw new ObjectDisposedException(nameof(FailsAfterFirstReadStream));
            }
        }

        /// <summary>Yields a fixed number of full chunks, then ends cleanly.</summary>
        private sealed class FixedChunksStream : ReadOnlyStream
        {
            private readonly int _chunk;
            private int _remaining;

            public FixedChunksStream(int chunk, int chunks) { _chunk = chunk; _remaining = chunks; }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_remaining-- <= 0) return Task.FromResult(0);
                int n = Math.Min(count, _chunk);
                Fill(buffer, offset, n);
                return Task.FromResult(n);
            }
        }

        private abstract class ReadOnlyStream : Stream
        {
            /// <summary>Writes into the caller's buffer — a Span.ToArray() copy would fill nothing.</summary>
            protected static void Fill(byte[] buffer, int offset, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    buffer[offset + i] = (byte)(i % 251);
                }
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
