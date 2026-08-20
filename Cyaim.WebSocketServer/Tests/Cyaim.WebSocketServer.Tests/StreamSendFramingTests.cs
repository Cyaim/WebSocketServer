using System.Net.WebSockets;
using System.Text;
using Cyaim.WebSocketServer.Infrastructure;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// 发送侧的核心不变式：**只要发出过 <c>endOfMessage: true</c>，这条消息的内容就一定完整。**
    /// The send-side invariant: <b>whenever <c>endOfMessage: true</c> goes out, the message is complete.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这条不变式存在的理由是产品性的：客户端判断「一条消息到齐了」的唯一依据，就是 WebSocket 层的
    /// 结束标志。如果服务端可能发出一条带着结束标志、内容却少了一截的消息，那么**每一个**客户端都得
    /// 自己去实现截断检测——把服务端的正确性问题变成所有客户端的负担。所以库宁可终结连接，也绝不
    /// 发出这样的消息。
    /// The invariant exists for a product reason: a client's only means of deciding a message arrived whole
    /// is the WebSocket end-of-message flag. If the server could emit a message carrying that flag with its
    /// tail missing, <b>every</b> client would need its own truncation detection — the server's correctness
    /// problem pushed onto all of them. So the library terminates the connection rather than emit one.
    /// </para>
    /// <para>
    /// 两条收场，没有第三条：
    /// <list type="number">
    /// <item><b>一帧未发</b>：载荷在写第一帧之前就已全部读进内存，读源失败时调用方拿到异常，
    /// 对端什么都没收到，连接毫发无伤。这是绝大多数消息走的路。</item>
    /// <item><b>终结连接</b>：载荷大到无法物化时才边读边发，中途失败就留下一条永不收尾的消息并关闭
    /// 连接（Close 1011 优先，Abort 兜底）。对端看到的是协议错误或连接关闭，而不是一条「完整」消息。</item>
    /// </list>
    /// Two endings, never a third: nothing written and the caller gets an exception (the common path), or
    /// the connection is terminated with the message deliberately left unterminated.
    /// </para>
    /// </remarks>
    public class StreamSendFramingTests : IDisposable
    {
        private const int Chunk = 4096;

        private readonly long _materializeLimit = WebSocketManager.MaxSendMaterializeBytes;
        private readonly int _frameBytes = WebSocketManager.MaxSendFrameBytes;
        private readonly bool _allowChunked = WebSocketManager.AllowChunkedSendAboveMaterializeLimit;

        public void Dispose()
        {
            WebSocketManager.MaxSendMaterializeBytes = _materializeLimit;
            WebSocketManager.MaxSendFrameBytes = _frameBytes;
            WebSocketManager.AllowChunkedSendAboveMaterializeLimit = _allowChunked;
        }

        // ---------------------------------------------------------------- 收场一：一帧未发

        [Fact]
        public async Task A_failing_source_below_the_limit_writes_nothing_and_leaves_the_connection_intact()
        {
            // 物化的全部意义：读源失败时第一帧都还没写，所以没有任何东西需要善后。
            // The whole point of materialising: the source fails before the first frame, so there is
            // nothing to clean up.
            var socket = new RecordingWebSocket();
            var stream = new FailsAfterFirstReadStream(Chunk);

            await Assert.ThrowsAnyAsync<Exception>(() => SendStreamAsync(stream, socket));

            Assert.Empty(socket.Frames);
            Assert.Equal(WebSocketState.Open, socket.State);
            Assert.False(socket.Aborted);
            Assert.False(socket.CloseOutputCalled);
        }

        [Fact]
        public async Task A_silent_short_read_is_caught_before_anything_is_written()
        {
            // 最阴险的一种：流声明了长度，却在读到一半时返回 0 而不抛异常（网络文件系统抖动就是这样）。
            // 不校验总量的话，它会变成一条语法完整、内容却少了一截的消息，而两端都不知道。
            // The nastiest case: the stream declares a length but returns 0 midway without throwing, as a
            // flaky network filesystem does. Without a total check it becomes a syntactically complete
            // message quietly missing its tail, with neither end the wiser.
            var socket = new RecordingWebSocket();
            var stream = new ShortReadStream(declaredLength: 65536, actualBytes: 20000);

            await Assert.ThrowsAsync<EndOfStreamException>(() => SendStreamAsync(stream, socket));

            Assert.Empty(socket.Frames);
            Assert.Equal(WebSocketState.Open, socket.State);
        }

        [Fact]
        public async Task A_stream_positioned_midway_sends_only_the_remainder()
        {
            // Length 而非 Length - Position 会让剩余量算多，进而误报短读。
            // Using Length instead of Length - Position overstates the remainder and false-alarms as a short read.
            var socket = new RecordingWebSocket();
            var payload = new byte[4096];
            new Random(3).NextBytes(payload);
            using var stream = new MemoryStream(payload) { Position = 3000 };

            await SendStreamAsync(stream, socket);

            Assert.Equal(payload.AsSpan(3000).ToArray(), Assert.Single(socket.CompletedMessages()));
        }

        // ---------------------------------------------------------------- 收场二：终结连接

        [Fact]
        public async Task A_failing_source_above_the_limit_never_terminates_the_message()
        {
            // 超过物化上限只能边读边发。中途失败时**绝不**补收尾帧——那正是会被客户端误当成完整消息的东西。
            // Above the limit the send must stream. A mid-stream failure must never terminate the message —
            // that is precisely what a client would mistake for a complete one.
            WebSocketManager.MaxSendMaterializeBytes = 1024;
            var socket = new RecordingWebSocket();
            var stream = new FailsAfterFirstReadStream(Chunk, seekable: false);

            await Assert.ThrowsAnyAsync<Exception>(() => SendStreamAsync(stream, socket));

            Assert.DoesNotContain(socket.Frames, f => f.EndOfMessage);
            Assert.True(socket.CloseOutputCalled || socket.Aborted, "the connection must be terminated");
        }

        [Fact]
        public async Task A_silent_short_read_above_the_limit_never_terminates_the_message()
        {
            WebSocketManager.MaxSendMaterializeBytes = 1024;
            var socket = new RecordingWebSocket();
            var stream = new ShortReadStream(declaredLength: 8192, actualBytes: 3000);

            await Assert.ThrowsAsync<EndOfStreamException>(() => SendStreamAsync(stream, socket));

            Assert.DoesNotContain(socket.Frames, f => f.EndOfMessage);
            Assert.True(socket.CloseOutputCalled || socket.Aborted);
        }

        [Fact]
        public async Task Termination_prefers_a_graceful_close_so_delivered_messages_survive()
        {
            // Close 是有序关闭：对端缓冲区里那些已经完整送达的消息仍会交付给它的应用。
            // Abort 是 RST，会把它们一并丢掉——所以只在 Close 失败时才用。
            // A close is orderly: messages already fully delivered still reach the peer's application.
            // An Abort is an RST that discards them, so it is only the fallback.
            WebSocketManager.MaxSendMaterializeBytes = 1024;
            var socket = new RecordingWebSocket();

            await Assert.ThrowsAnyAsync<Exception>(() =>
                SendStreamAsync(new FailsAfterFirstReadStream(Chunk, seekable: false), socket));

            Assert.True(socket.CloseOutputCalled);
            Assert.False(socket.Aborted);
            Assert.Equal(WebSocketCloseStatus.InternalServerError, socket.CloseStatusSent);
        }

        [Fact]
        public async Task Termination_falls_back_to_Abort_when_the_close_cannot_be_sent()
        {
            // 被取消或失败的 CloseOutputAsync 不会替你中止连接，必须自己兜底。
            // A cancelled or failed CloseOutputAsync does not abort for you; the fallback must be explicit.
            WebSocketManager.MaxSendMaterializeBytes = 1024;
            var socket = new RecordingWebSocket { FailCloseOutput = true };

            await Assert.ThrowsAnyAsync<Exception>(() =>
                SendStreamAsync(new FailsAfterFirstReadStream(Chunk, seekable: false), socket));

            Assert.True(socket.Aborted);
        }

        [Fact]
        public async Task Termination_leaves_a_connection_that_is_already_closing_alone()
        {
            // 已经在关闭流程里再发 RST，只会白丢对端缓冲区里那些好消息。
            // An RST on a connection already closing only discards good messages still in the peer's buffer.
            WebSocketManager.MaxSendMaterializeBytes = 1024;
            var socket = new RecordingWebSocket { StateAfterFirstSend = WebSocketState.CloseSent };

            await Assert.ThrowsAnyAsync<Exception>(() =>
                SendStreamAsync(new FailsAfterFirstReadStream(Chunk, seekable: false), socket));

            Assert.False(socket.Aborted);
            Assert.False(socket.CloseOutputCalled);
        }

        // ---------------------------------------------------------------- 上限与降级

        [Fact]
        public async Task Above_the_limit_without_the_chunked_fallback_refuses_before_writing_anything()
        {
            WebSocketManager.MaxSendMaterializeBytes = 1024;
            WebSocketManager.AllowChunkedSendAboveMaterializeLimit = false;
            var socket = new RecordingWebSocket();
            using var stream = new MemoryStream(new byte[8192]);

            var ex = await Assert.ThrowsAsync<WebSocketMessageTooLargeException>(() => SendStreamAsync(stream, socket));

            Assert.Equal(8192, ex.PayloadBytes);
            Assert.Equal(1024, ex.LimitBytes);
            Assert.Empty(socket.Frames);
            Assert.Equal(WebSocketState.Open, socket.State);
        }

        [Fact]
        public async Task Above_the_limit_a_finite_timeout_still_sends_rather_than_refusing()
        {
            // 曾经在这里拒绝过：有限超时会让发送脱离等待，调用方的 using 随即 Dispose 流，而后台还在读它。
            // 但那是**回归**——旧实现在同样调用下会把整条流发出去，而且默认配置就会触发。
            // 更重要的是：放行并不破坏不变式。流被 Dispose 后读失败，已上线的帧不会被收尾，连接被终结，
            // 对端拿不到任何「完整」消息。既然正确性不依赖拒绝，就不该拿掉一个既有能力。
            // This used to refuse: a finite timeout detaches the send while the caller's `using` disposes the
            // stream under it. But refusing was a regression — the old implementation sent the whole stream
            // for the same call, and the default configuration triggers it. More to the point, allowing it
            // does not break the invariant: the disposed stream fails the read, the frames already out are
            // never terminated, the connection is, and the peer receives nothing that looks complete. With
            // correctness not resting on the refusal, removing a working capability was not justified.
            WebSocketManager.MaxSendMaterializeBytes = 1024;
            WebSocketManager.MaxSendFrameBytes = 1024;
            var socket = new RecordingWebSocket();
            var payload = new byte[8192];
            new Random(13).NextBytes(payload);
            using var stream = new MemoryStream(payload);

            await WebSocketManager.SendLocalAsync(stream, WebSocketMessageType.Binary, CancellationToken.None,
                timeout: TimeSpan.FromSeconds(30), sendAtOnce: false, sendBufferSize: Chunk, sockets: socket);

            Assert.Equal(payload, Assert.Single(socket.CompletedMessages()));
        }

        [Fact]
        public async Task Above_the_limit_the_chunked_fallback_still_delivers_the_whole_payload()
        {
            // 加上限不能等于砍掉「能发大流」这个能力。
            // Adding a limit must not amount to removing the ability to send large streams.
            WebSocketManager.MaxSendMaterializeBytes = 4096;
            WebSocketManager.MaxSendFrameBytes = 1024;
            var socket = new RecordingWebSocket();
            var payload = new byte[20_000];
            new Random(11).NextBytes(payload);
            using var stream = new MemoryStream(payload);

            await SendStreamAsync(stream, socket);

            Assert.Equal(payload, Assert.Single(socket.CompletedMessages()));
            Assert.True(socket.Frames.Count > 1, "the payload should have been streamed in frames");
            Assert.All(socket.Frames.Take(socket.Frames.Count - 1), f => Assert.False(f.EndOfMessage));
        }

        // ---------------------------------------------------------------- 结构性护栏

        [Fact]
        public void The_message_terminating_helper_is_permanently_gone()
        {
            // 回归护栏：CloseOpenMessageAsync 是全仓唯一能制造「截断但语法完整」消息的函数。
            // Regression guard: CloseOpenMessageAsync was the only function able to emit a message that is
            // truncated yet syntactically complete.
            var revived = typeof(WebSocketManager).GetMethod(
                "CloseOpenMessageAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            Assert.Null(revived);
        }

        [Fact]
        public async Task A_completed_stream_send_is_unaffected()
        {
            var socket = new RecordingWebSocket();
            var payload = new byte[10_000];
            new Random(7).NextBytes(payload);
            using var stream = new MemoryStream(payload);

            await SendStreamAsync(stream, socket);

            Assert.Equal(payload, Assert.Single(socket.CompletedMessages()));
            Assert.False(socket.Aborted);
        }

        private static Task SendStreamAsync(Stream stream, WebSocket socket)
            => WebSocketManager.SendLocalAsync(
                stream, WebSocketMessageType.Binary, CancellationToken.None,
                timeout: null, sendAtOnce: false, sendBufferSize: Chunk, sockets: socket);

        // ------------------------------------------------------------------ fakes

        /// <summary>
        /// 记录分帧与关闭方式的假 socket。共享的 TestWebSocket 观察不到 Abort / CloseOutput，
        /// 用它写的测试会「全绿但什么都没证明」。
        /// A fake that records framing and how the connection was closed. The shared TestWebSocket sees
        /// neither Abort nor CloseOutput, so tests built on it go green without proving anything.
        /// </summary>
        private sealed class RecordingWebSocket : WebSocket
        {
            private readonly object _sync = new object();
            private readonly List<(byte[] Payload, bool EndOfMessage)> _frames = new List<(byte[], bool)>();
            private WebSocketState _state = WebSocketState.Open;

            public bool FailCloseOutput { get; set; }
            public WebSocketState? StateAfterFirstSend { get; set; }

            public bool Aborted { get; private set; }
            public bool CloseOutputCalled { get; private set; }
            public WebSocketCloseStatus? CloseStatusSent { get; private set; }

            public IReadOnlyList<(byte[] Payload, bool EndOfMessage)> Frames
            {
                get { lock (_sync) { return _frames.ToArray(); } }
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

            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            {
                lock (_sync) { _frames.Add((buffer.ToArray(), endOfMessage)); }

                if (StateAfterFirstSend.HasValue)
                {
                    _state = StateAfterFirstSend.Value;
                    StateAfterFirstSend = null;
                }

                return Task.CompletedTask;
            }

            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
            {
                CloseOutputCalled = true;
                if (FailCloseOutput)
                {
                    throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
                }

                CloseStatusSent = closeStatus;
                _state = WebSocketState.CloseSent;
                return Task.CompletedTask;
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
            public override void Dispose() { }
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        /// <summary>Yields one full chunk, then behaves like a stream somebody disposed.</summary>
        private sealed class FailsAfterFirstReadStream : ReadOnlyStream
        {
            private readonly int _chunk;
            private readonly bool _seekable;
            private int _reads;

            public FailsAfterFirstReadStream(int chunk, bool seekable = true)
            {
                _chunk = chunk;
                _seekable = seekable;
            }

            public override bool CanSeek => _seekable;
            public override long Length => _seekable ? _chunk * 4L : throw new NotSupportedException();
            public override long Position { get => 0; set { } }

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

        /// <summary>Declares a length but quietly stops early — without throwing.</summary>
        private sealed class ShortReadStream : ReadOnlyStream
        {
            private readonly long _declared;
            private readonly int _actual;
            private int _served;

            public ShortReadStream(long declaredLength, int actualBytes)
            {
                _declared = declaredLength;
                _actual = actualBytes;
            }

            public override bool CanSeek => true;
            public override long Length => _declared;
            public override long Position { get => 0; set { } }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                int remaining = _actual - _served;
                if (remaining <= 0)
                {
                    return Task.FromResult(0);   // 静默结束，不抛 / stops quietly, never throws
                }

                int n = Math.Min(Math.Min(count, remaining), 4096);
                Fill(buffer, offset, n);
                _served += n;
                return Task.FromResult(n);
            }
        }

        private abstract class ReadOnlyStream : Stream
        {
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
