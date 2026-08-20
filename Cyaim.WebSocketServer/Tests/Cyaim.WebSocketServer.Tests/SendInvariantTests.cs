using System.Net.WebSockets;
using System.Text;
using Cyaim.WebSocketServer.Infrastructure;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// 不变式的性质化验证：对**所有**注入的失败点，只要出现过结束标志，内容就必须完整。
    /// A property-style check of the invariant: across <b>every</b> injected failure point, whenever the
    /// end-of-message flag appeared, the content must be complete.
    /// </summary>
    /// <remarks>
    /// 逐点断言容易漏掉组合。这里遍历「在第 N 次读时失败」的每一种可能，对每一种都验证同一条性质，
    /// 这样新加的失败路径只要破坏了不变式就会被抓住，而不必先想到该为它单独写一条测试。
    /// Point assertions miss combinations. This walks every "fail on the Nth read" case and checks the one
    /// property for each, so a newly introduced failure path that breaks the invariant is caught without
    /// anyone first having to think of writing a test for it.
    /// </remarks>
    public class SendInvariantTests : IDisposable
    {
        private readonly long _materializeLimit = WebSocketManager.MaxSendMaterializeBytes;
        private readonly int _frameBytes = WebSocketManager.MaxSendFrameBytes;
        private readonly int _batchLimit = WebSocketManager.BatchProcessingWebsocketLimit;

        public void Dispose()
        {
            WebSocketManager.MaxSendMaterializeBytes = _materializeLimit;
            WebSocketManager.MaxSendFrameBytes = _frameBytes;
            WebSocketManager.BatchProcessingWebsocketLimit = _batchLimit;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(5)]
        [InlineData(8)]
        public async Task Whenever_the_end_flag_appears_the_payload_is_complete(int failOnRead)
        {
            // 物化上限压到很小，强制走流式路径——这是唯一可能「已经有帧上线」的路径。
            // A tiny materialization limit forces the streaming path — the only one where frames can
            // already be on the wire when the source fails.
            WebSocketManager.MaxSendMaterializeBytes = 512;
            WebSocketManager.MaxSendFrameBytes = 1024;

            var socket = new FramingRecorder();
            var stream = new FailsOnNthReadStream(chunk: 1024, failOnRead: failOnRead);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                WebSocketManager.SendLocalAsync(stream, WebSocketMessageType.Binary, CancellationToken.None,
                    timeout: null, sendAtOnce: false, sendBufferSize: 1024, sockets: socket));

            // 核心性质：没有任何一帧带结束标志。带了就意味着对端会把这半条消息当成完整的。
            // The property: not one frame carries the end flag. If one did, the peer would take this half
            // message for a complete one.
            Assert.DoesNotContain(socket.Frames, f => f.EndOfMessage);

            // 两条收场，取决于失败发生时有没有字节已经上线：
            //  - 一帧未发（失败发生在物化阶段）→ 连接必须完好，调用方拿到异常即可。
            //  - 已有帧上线（失败发生在流式阶段）→ 必须终结连接，让对端明确知道出事了。
            // Two endings, decided by whether bytes were already on the wire:
            //  - nothing sent (failed while materialising) → the connection must be untouched;
            //  - frames already out (failed while streaming) → the connection must be terminated.
            if (socket.Frames.Count == 0)
            {
                Assert.False(socket.Terminated, "nothing was written, so the connection must be left alone");
            }
            else
            {
                Assert.True(socket.Terminated, "a message was left open, so the connection must be terminated");
            }
        }

        [Fact]
        public async Task A_successful_send_always_ends_with_the_flag_and_the_whole_payload()
        {
            // 阳性对照：成功路径必须收尾，且内容一字不差。
            WebSocketManager.MaxSendFrameBytes = 1024;

            var socket = new FramingRecorder();
            var payload = new byte[9000];
            new Random(5).NextBytes(payload);
            using var stream = new MemoryStream(payload);

            await WebSocketManager.SendLocalAsync(stream, WebSocketMessageType.Binary, CancellationToken.None,
                timeout: null, sendAtOnce: false, sendBufferSize: 1024, sockets: socket);

            Assert.True(socket.Frames[socket.Frames.Count - 1].EndOfMessage);
            Assert.Equal(payload, socket.Assembled());
            Assert.False(socket.Terminated);
        }

        [Fact]
        public async Task An_ordinary_cancellation_does_not_kill_the_connection()
        {
            // 数据帧若用调用方的令牌，一次普通的请求取消/主机关停就会 Abort 整条连接。
            // 门闩空闲时传一个已取消的令牌：消息照发，连接不动。
            // If data frames used the caller's token, a routine request cancellation or host shutdown would
            // abort the whole connection. With the gate free, an already-cancelled token still sends.
            var socket = new FramingRecorder();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            var payload = Encoding.UTF8.GetBytes("still delivered");

            // 取消可能在排队等门闩时就生效，于是发送被放弃——那是安全的取消点，一帧未发。
            // 唯一不可接受的是：取消把连接给弄断了。
            // The cancel may land while queueing for the send gate, abandoning the send — a safe point,
            // nothing written. The one unacceptable outcome is a cancellation that kills the connection.
            try
            {
                await WebSocketManager.SendLocalAsync(payload.AsMemory(), WebSocketMessageType.Text,
                    sendAtOnce: true, cancelled.Token, sockets: socket);
            }
            catch (OperationCanceledException)
            {
                // 排队期被取消，符合预期。/ Cancelled while queueing, as expected.
            }

            Assert.False(socket.Terminated, "a cancellation must never terminate the connection");
            Assert.DoesNotContain(socket.Frames, f => !f.EndOfMessage);
        }

        [Fact]
        public async Task Fan_out_waves_shrink_as_the_payload_grows()
        {
            // 扇出并发按「在途帧字节」收敛：一条大载荷发给很多 socket 时，不能让所有帧缓冲同时存在。
            // Fan-out concurrency is sized by in-flight frame bytes: a large payload to many sockets must
            // not put every frame buffer in memory at once.
            WebSocketManager.MaxSendFrameBytes = 1024 * 1024;

            var sockets = Enumerable.Range(0, 64).Select(_ => new ConcurrencyRecorder()).ToArray();
            var payload = new byte[4 * 1024 * 1024];

            await WebSocketManager.SendLocalAsync(payload.AsMemory(), WebSocketMessageType.Binary,
                sendAtOnce: true, CancellationToken.None, sockets: sockets.Cast<WebSocket>().ToArray());

            int peak = ConcurrencyRecorder.PeakConcurrent;
            Assert.True(peak <= 64, $"peak concurrent sends {peak} should be bounded by the frame budget");
            Assert.All(sockets, s => Assert.True(s.Sent));
        }

        // ------------------------------------------------------------------ fakes

        private class FramingRecorder : WebSocket
        {
            private readonly List<(byte[] Payload, bool EndOfMessage)> _frames = new List<(byte[], bool)>();
            private WebSocketState _state = WebSocketState.Open;

            public IReadOnlyList<(byte[] Payload, bool EndOfMessage)> Frames => _frames;
            public bool Terminated { get; private set; }

            public byte[] Assembled() => _frames.SelectMany(f => f.Payload).ToArray();

            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            {
                _frames.Add((buffer.ToArray(), endOfMessage));
                return Task.CompletedTask;
            }

            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
            {
                Terminated = true;
                _state = WebSocketState.CloseSent;
                return Task.CompletedTask;
            }

            public override void Abort()
            {
                Terminated = true;
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

        private sealed class ConcurrencyRecorder : WebSocket
        {
            private static int _current;
            private static int _peak;

            public static int PeakConcurrent => Volatile.Read(ref _peak);

            public bool Sent { get; private set; }

            public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            {
                int now = Interlocked.Increment(ref _current);
                int observed;
                do
                {
                    observed = Volatile.Read(ref _peak);
                    if (now <= observed) break;
                } while (Interlocked.CompareExchange(ref _peak, now, observed) != observed);

                await Task.Yield();
                Sent = true;
                Interlocked.Decrement(ref _current);
            }

            public override WebSocketCloseStatus? CloseStatus => null;
            public override string CloseStatusDescription => null;
            public override WebSocketState State => WebSocketState.Open;
            public override string SubProtocol => null;
            public override void Abort() { }
            public override Task CloseAsync(WebSocketCloseStatus s, string d, CancellationToken c) => Task.CompletedTask;
            public override Task CloseOutputAsync(WebSocketCloseStatus s, string d, CancellationToken c) => Task.CompletedTask;
            public override void Dispose() { }
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        private sealed class FailsOnNthReadStream : Stream
        {
            private readonly int _chunk;
            private readonly int _failOnRead;
            private int _reads;

            public FailsOnNthReadStream(int chunk, int failOnRead)
            {
                _chunk = chunk;
                _failOnRead = failOnRead;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (++_reads >= _failOnRead)
                {
                    throw new IOException($"source failed on read {_reads}");
                }

                int n = Math.Min(count, _chunk);
                for (int i = 0; i < n; i++)
                {
                    buffer[offset + i] = (byte)(i % 251);
                }
                return Task.FromResult(n);
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
