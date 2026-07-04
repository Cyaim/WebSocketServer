using System.Net.WebSockets;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// A single WebSocket frame captured by <see cref="TestWebSocket"/>.
    /// </summary>
    public sealed class CapturedFrame
    {
        public CapturedFrame(byte[] payload, WebSocketMessageType messageType, bool endOfMessage)
        {
            Payload = payload;
            MessageType = messageType;
            EndOfMessage = endOfMessage;
        }

        public byte[] Payload { get; }
        public WebSocketMessageType MessageType { get; }
        public bool EndOfMessage { get; }
    }

    /// <summary>
    /// Fake WebSocket that records every SendAsync call.
    /// System.Net.WebSockets.WebSocket is abstract, so tests use this capturing subclass.
    /// It also tracks the maximum number of concurrent SendAsync calls observed, which lets tests
    /// verify the per-socket send gate in WebSocketManager.
    /// </summary>
    public sealed class TestWebSocket : WebSocket
    {
        private readonly object _sync = new object();
        private readonly List<CapturedFrame> _frames = new List<CapturedFrame>();
        private WebSocketState _state;
        private int _currentConcurrentSends;
        private int _maxConcurrentSends;

        public TestWebSocket(WebSocketState state = WebSocketState.Open)
        {
            _state = state;
        }

        /// <summary>Optional artificial delay inside SendAsync to widen race windows.</summary>
        public TimeSpan SendDelay { get; set; } = TimeSpan.Zero;

        /// <summary>Highest number of SendAsync calls that were in flight at the same time.</summary>
        public int MaxObservedConcurrentSends => Volatile.Read(ref _maxConcurrentSends);

        public IReadOnlyList<CapturedFrame> Frames
        {
            get { lock (_sync) { return _frames.ToArray(); } }
        }

        /// <summary>All payload bytes concatenated in send order.</summary>
        public byte[] AllPayloadBytes
        {
            get { lock (_sync) { return _frames.SelectMany(f => f.Payload).ToArray(); } }
        }

        /// <summary>
        /// Reassemble messages from the recorded frame sequence:
        /// frames accumulate until a frame with EndOfMessage == true completes a message.
        /// If two multi-frame sends interleaved, the reassembled messages would contain mixed content.
        /// </summary>
        public List<byte[]> CompletedMessages()
        {
            lock (_sync)
            {
                var messages = new List<byte[]>();
                var current = new List<byte>();
                foreach (var frame in _frames)
                {
                    current.AddRange(frame.Payload);
                    if (frame.EndOfMessage)
                    {
                        messages.Add(current.ToArray());
                        current = new List<byte>();
                    }
                }
                return messages;
            }
        }

        public void SetState(WebSocketState state) => _state = state;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException("TestWebSocket does not support receiving.");

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref _currentConcurrentSends);
            try
            {
                // Track max concurrency
                int max;
                do
                {
                    max = Volatile.Read(ref _maxConcurrentSends);
                    if (current <= max)
                    {
                        break;
                    }
                } while (Interlocked.CompareExchange(ref _maxConcurrentSends, current, max) != max);

                if (SendDelay > TimeSpan.Zero)
                {
                    await Task.Delay(SendDelay, cancellationToken);
                }
                else
                {
                    await Task.Yield();
                }

                lock (_sync)
                {
                    _frames.Add(new CapturedFrame(buffer.ToArray(), messageType, endOfMessage));
                }
            }
            finally
            {
                Interlocked.Decrement(ref _currentConcurrentSends);
            }
        }
    }

    /// <summary>
    /// Collection for tests that touch shared static state
    /// (MvcChannelHandler.Clients, GlobalClusterCenter, WebSocketRouteOption.ApplicationServices).
    /// Classes in this collection never run in parallel with each other.
    /// </summary>
    [CollectionDefinition("StaticState")]
    public class StaticStateCollection
    {
    }
}
