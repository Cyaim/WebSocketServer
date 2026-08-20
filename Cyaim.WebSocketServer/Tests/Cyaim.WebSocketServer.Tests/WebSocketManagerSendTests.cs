using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Tests for WebSocketManager send paths.
    /// The unified SendAsync(connectionId, ...) tests read the static MvcChannelHandler.Clients map,
    /// so this class runs in the "StaticState" collection and restores every registration it adds.
    /// </summary>
    [Collection("StaticState")]
    public class WebSocketManagerSendTests
    {
        private sealed class ClientRegistration : IDisposable
        {
            public string ConnectionId { get; }

            public ClientRegistration(WebSocket socket)
            {
                ConnectionId = "test-" + Guid.NewGuid().ToString("N");
                Assert.True(MvcChannelHandler.Clients.TryAdd(ConnectionId, socket));
            }

            public void Dispose()
            {
                MvcChannelHandler.Clients.TryRemove(ConnectionId, out _);
            }
        }

        #region SendLocalAsync(buffer, ...)

        [Fact]
        public async Task SendLocalAsync_Buffer_SingleOpenSocket_SendsExactPayload()
        {
            var ws = new TestWebSocket();
            byte[] payload = Encoding.UTF8.GetBytes("hello world");

            await WebSocketManager.SendLocalAsync(payload.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None, sockets: ws);

            var frame = Assert.Single(ws.Frames);
            Assert.Equal(payload, frame.Payload);
            Assert.True(frame.EndOfMessage);
            Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
        }

        [Fact]
        public async Task SendLocalAsync_Buffer_IsFramedByMaxSendFrameBytes_LastFrameCarriesEndOfMessage()
        {
            // 分帧由 MaxSendFrameBytes 决定，与 sendAtOnce / sendBufferSize 无关——后两者对已在内存里的
            // buffer 不再影响分帧。最后一帧自己带结束标志，不再多发一个空收尾帧。
            // Framing is decided by MaxSendFrameBytes alone; sendAtOnce and sendBufferSize no longer affect
            // how an in-memory buffer is framed. The last data frame carries the end flag — no extra empty
            // terminator frame.
            int previous = WebSocketManager.MaxSendFrameBytes;
            WebSocketManager.MaxSendFrameBytes = 4096;
            try
            {
                var ws = new TestWebSocket();
                byte[] payload = new byte[10_000];
                new Random(42).NextBytes(payload);

                await WebSocketManager.SendLocalAsync(payload.AsMemory(), WebSocketMessageType.Binary, sendAtOnce: false, CancellationToken.None, sendBufferSize: 4096, sockets: ws);

                var frames = ws.Frames;
                Assert.Equal(3, frames.Count);
                Assert.Equal(4096, frames[0].Payload.Length);
                Assert.Equal(4096, frames[1].Payload.Length);
                Assert.Equal(1808, frames[2].Payload.Length);
                Assert.False(frames[0].EndOfMessage);
                Assert.False(frames[1].EndOfMessage);
                Assert.True(frames[2].EndOfMessage);
                Assert.Equal(payload, ws.AllPayloadBytes);
            }
            finally
            {
                WebSocketManager.MaxSendFrameBytes = previous;
            }
        }

        [Fact]
        public async Task SendLocalAsync_Buffer_FramingIgnoresSendAtOnceAndSendBufferSize()
        {
            // 三种传法必须产生完全相同的帧序列：这两个参数不再是分帧开关。
            // All three must produce an identical frame sequence: neither parameter is a framing switch now.
            int previous = WebSocketManager.MaxSendFrameBytes;
            WebSocketManager.MaxSendFrameBytes = 4096;
            try
            {
                byte[] payload = new byte[10_000];
                new Random(7).NextBytes(payload);

                var a = new TestWebSocket();
                var b = new TestWebSocket();
                var c = new TestWebSocket();

                await WebSocketManager.SendLocalAsync(payload.AsMemory(), WebSocketMessageType.Binary, sendAtOnce: true, CancellationToken.None, sendBufferSize: 4096, sockets: a);
                await WebSocketManager.SendLocalAsync(payload.AsMemory(), WebSocketMessageType.Binary, sendAtOnce: false, CancellationToken.None, sendBufferSize: 4096, sockets: b);
                await WebSocketManager.SendLocalAsync(payload.AsMemory(), WebSocketMessageType.Binary, sendAtOnce: false, CancellationToken.None, sendBufferSize: 1 << 20, sockets: c);

                Assert.Equal(a.Frames.Count, b.Frames.Count);
                Assert.Equal(a.Frames.Count, c.Frames.Count);
                Assert.Equal(payload, a.AllPayloadBytes);
                Assert.Equal(payload, b.AllPayloadBytes);
                Assert.Equal(payload, c.AllPayloadBytes);
            }
            finally
            {
                WebSocketManager.MaxSendFrameBytes = previous;
            }
        }

        [Fact]
        public async Task SendLocalAsync_Buffer_SendAtOnce_LargeBuffer_SingleFrame()
        {
            var ws = new TestWebSocket();
            byte[] payload = new byte[10_000];
            new Random(7).NextBytes(payload);

            await WebSocketManager.SendLocalAsync(payload.AsMemory(), WebSocketMessageType.Binary, sendAtOnce: true, CancellationToken.None, sendBufferSize: 4096, sockets: ws);

            var frame = Assert.Single(ws.Frames);
            Assert.Equal(payload, frame.Payload);
            Assert.True(frame.EndOfMessage);
        }

        [Fact]
        public async Task SendLocalAsync_Buffer_SmallBuffer_IgnoresSendAtOnceFalse_SingleFrame()
        {
            var ws = new TestWebSocket();
            byte[] payload = Encoding.UTF8.GetBytes("tiny");

            await WebSocketManager.SendLocalAsync(payload.AsMemory(), WebSocketMessageType.Text, sendAtOnce: false, CancellationToken.None, sendBufferSize: 4096, sockets: ws);

            var frame = Assert.Single(ws.Frames);
            Assert.Equal(payload, frame.Payload);
            Assert.True(frame.EndOfMessage);
        }

        [Fact]
        public async Task SendLocalAsync_Buffer_ClosedSingleSocket_ThrowsArgumentNullException()
        {
            var ws = new TestWebSocket(WebSocketState.Closed);

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                WebSocketManager.SendLocalAsync(new byte[] { 1, 2, 3 }.AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None, sockets: ws));
            Assert.Empty(ws.Frames);
        }

        [Fact]
        public async Task SendLocalAsync_Buffer_AllClosedSockets_ThrowsArgumentNullException()
        {
            var ws1 = new TestWebSocket(WebSocketState.Closed);
            var ws2 = new TestWebSocket(WebSocketState.Aborted);

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                WebSocketManager.SendLocalAsync(new byte[] { 1 }.AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None, sockets: new WebSocket[] { ws1, ws2 }));
        }

        [Fact]
        public async Task SendLocalAsync_Buffer_NoSockets_ReturnsSilently()
        {
            await WebSocketManager.SendLocalAsync(new byte[] { 1 }.AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None, sockets: Array.Empty<WebSocket>());
            await WebSocketManager.SendLocalAsync(new byte[] { 1 }.AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None, sockets: null);
        }

        [Fact]
        public async Task SendLocalAsync_Buffer_MixedSockets_SendsOnlyToOpen()
        {
            var open = new TestWebSocket();
            var closed = new TestWebSocket(WebSocketState.Closed);
            byte[] payload = Encoding.UTF8.GetBytes("fan-out");

            await WebSocketManager.SendLocalAsync(payload.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None, sockets: new WebSocket[] { closed, open });

            Assert.Empty(closed.Frames);
            var frame = Assert.Single(open.Frames);
            Assert.Equal(payload, frame.Payload);
        }

        #endregion

        #region SendLocalAsync(stream, ...)

        [Fact]
        public async Task SendLocalAsync_Stream_ClosedSocket_ReturnsSilently()
        {
            var ws = new TestWebSocket(WebSocketState.Closed);
            using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

            // Stream variant must NOT throw for a closed socket
            await WebSocketManager.SendLocalAsync(stream, WebSocketMessageType.Binary, CancellationToken.None, sockets: ws);

            Assert.Empty(ws.Frames);
        }

        [Fact]
        public async Task SendLocalAsync_Stream_SendsAllData_FinalFrameEndsMessage()
        {
            var ws = new TestWebSocket();
            byte[] payload = new byte[5000];
            new Random(3).NextBytes(payload);
            using var stream = new MemoryStream(payload);

            await WebSocketManager.SendLocalAsync(stream, WebSocketMessageType.Binary, CancellationToken.None, sendAtOnce: false, sendBufferSize: 1024, sockets: ws);

            var messages = ws.CompletedMessages();
            var message = Assert.Single(messages);
            Assert.Equal(payload, message);
            Assert.True(ws.Frames[ws.Frames.Count - 1].EndOfMessage);
        }

        [Fact]
        public async Task SendLocalAsync_Stream_SendAtOnce_SingleFrame()
        {
            var ws = new TestWebSocket();
            byte[] payload = Encoding.UTF8.GetBytes("stream-at-once");
            using var stream = new MemoryStream(payload);

            await WebSocketManager.SendLocalAsync(stream, WebSocketMessageType.Binary, CancellationToken.None, sendAtOnce: true, sockets: ws);

            var frame = Assert.Single(ws.Frames);
            Assert.Equal(payload, frame.Payload);
            Assert.True(frame.EndOfMessage);
        }

        [Fact]
        public async Task SendLocalAsync_Stream_MultipleSockets_BuffersOnceAndFansOut()
        {
            var ws1 = new TestWebSocket();
            var ws2 = new TestWebSocket();
            byte[] payload = new byte[3000];
            new Random(11).NextBytes(payload);
            using var stream = new MemoryStream(payload);

            await WebSocketManager.SendLocalAsync(stream, WebSocketMessageType.Binary, CancellationToken.None, sendAtOnce: false, sendBufferSize: 1024, sockets: new WebSocket[] { ws1, ws2 });

            Assert.Equal(payload, Assert.Single(ws1.CompletedMessages()));
            Assert.Equal(payload, Assert.Single(ws2.CompletedMessages()));
        }

        #endregion

        #region SendLocalAsync(string / object)

        [Fact]
        public async Task SendLocalAsync_String_EncodesUtf8ByDefault()
        {
            var ws = new TestWebSocket();
            const string data = "你好, WebSocket! ✓";

            await WebSocketManager.SendLocalAsync(data, WebSocketMessageType.Text, CancellationToken.None, socket: ws);

            var frame = Assert.Single(ws.Frames);
            Assert.Equal(Encoding.UTF8.GetBytes(data), frame.Payload);
            Assert.True(frame.EndOfMessage);
        }

        [Fact]
        public async Task SendLocalAsync_String_CustomEncoding()
        {
            var ws = new TestWebSocket();
            const string data = "abc";

            await WebSocketManager.SendLocalAsync(data, WebSocketMessageType.Text, CancellationToken.None, encoding: Encoding.Unicode, socket: ws);

            var frame = Assert.Single(ws.Frames);
            Assert.Equal(Encoding.Unicode.GetBytes(data), frame.Payload);
        }

        [Fact]
        public async Task SendLocalAsync_String_NullOrEmpty_SendsNothing()
        {
            var ws = new TestWebSocket();

            await WebSocketManager.SendLocalAsync((string)null, WebSocketMessageType.Text, CancellationToken.None, socket: ws);
            await WebSocketManager.SendLocalAsync(string.Empty, WebSocketMessageType.Text, CancellationToken.None, socket: ws);

            Assert.Empty(ws.Frames);
        }

        private class SendPoco
        {
            public string Name { get; set; }
            public int Value { get; set; }
        }

        [Fact]
        public async Task SendLocalAsync_Object_SerializesToJsonUtf8()
        {
            var ws = new TestWebSocket();
            var data = new SendPoco { Name = "n", Value = 5 };

            await ws.SendLocalAsync(data);

            var frame = Assert.Single(ws.Frames);
            var roundTripped = JsonSerializer.Deserialize<SendPoco>(frame.Payload);
            Assert.Equal("n", roundTripped.Name);
            Assert.Equal(5, roundTripped.Value);
            Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
        }

        [Fact]
        public async Task SendLocalAsync_Object_NullData_SendsNothing()
        {
            var ws = new TestWebSocket();

            await ws.SendLocalAsync<SendPoco>(null);

            Assert.Empty(ws.Frames);
        }

        #endregion

        #region Per-socket send gate / concurrency

        [Fact]
        public async Task ConcurrentMultiFrameSends_SameSocket_NeverInterleaveFrames()
        {
            var ws = new TestWebSocket();
            const int messageSize = 64 * 1024;
            const uint chunkSize = 1024;
            byte[][] payloads = new byte[4][];
            for (int i = 0; i < payloads.Length; i++)
            {
                payloads[i] = new byte[messageSize];
                Array.Fill(payloads[i], (byte)('A' + i));
            }

            var tasks = payloads.Select(p => Task.Run(() =>
                WebSocketManager.SendLocalAsync(p.AsMemory(), WebSocketMessageType.Binary, sendAtOnce: false, CancellationToken.None, sendBufferSize: chunkSize, sockets: ws)));

            await Task.WhenAll(tasks);

            // The per-socket gate must serialize SendAsync calls on the same socket
            Assert.Equal(1, ws.MaxObservedConcurrentSends);

            // Each reassembled message must be homogeneous (a single marker byte) and complete —
            // interleaved frames would produce mixed-content or wrongly sized messages.
            var messages = ws.CompletedMessages();
            Assert.Equal(payloads.Length, messages.Count);
            var seenMarkers = new HashSet<byte>();
            foreach (var message in messages)
            {
                Assert.Equal(messageSize, message.Length);
                byte marker = message[0];
                Assert.All(message, b => Assert.Equal(marker, b));
                Assert.True(seenMarkers.Add(marker), $"Marker {(char)marker} completed twice");
            }
        }

        [Fact]
        public async Task ConcurrentSends_DifferentSockets_AreIndependent()
        {
            var ws1 = new TestWebSocket();
            var ws2 = new TestWebSocket();
            byte[] p1 = Encoding.UTF8.GetBytes("socket-one");
            byte[] p2 = Encoding.UTF8.GetBytes("socket-two");

            await Task.WhenAll(
                WebSocketManager.SendLocalAsync(p1.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None, sockets: ws1),
                WebSocketManager.SendLocalAsync(p2.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None, sockets: ws2));

            Assert.Equal(p1, Assert.Single(ws1.Frames).Payload);
            Assert.Equal(p2, Assert.Single(ws2.Frames).Payload);
        }

        #endregion

        #region Unified SendAsync(connectionId, ...) — local (no cluster) path

        [Fact]
        public async Task SendAsync_ConnectionId_Bytes_LocalPath_SendsToRegisteredClient()
        {
            var ws = new TestWebSocket();
            using var registration = new ClientRegistration(ws);
            byte[] payload = Encoding.UTF8.GetBytes("unified");

            bool result = await WebSocketManager.SendAsync(registration.ConnectionId, payload, WebSocketMessageType.Text);

            Assert.True(result);
            var frame = Assert.Single(ws.Frames);
            Assert.Equal(payload, frame.Payload);
            Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
            Assert.True(frame.EndOfMessage);
        }

        [Fact]
        public async Task SendAsync_ConnectionId_UnknownConnection_ReturnsFalse()
        {
            bool result = await WebSocketManager.SendAsync("no-such-connection-" + Guid.NewGuid().ToString("N"), new byte[] { 1 }, WebSocketMessageType.Binary);

            Assert.False(result);
        }

        [Fact]
        public async Task SendAsync_ConnectionId_ClosedSocket_ReturnsFalse()
        {
            var ws = new TestWebSocket(WebSocketState.Closed);
            using var registration = new ClientRegistration(ws);

            bool result = await WebSocketManager.SendAsync(registration.ConnectionId, new byte[] { 1 }, WebSocketMessageType.Binary);

            Assert.False(result);
            Assert.Empty(ws.Frames);
        }

        [Fact]
        public async Task SendAsync_ConnectionId_EmptyData_ReturnsFalse()
        {
            Assert.False(await WebSocketManager.SendAsync("some-id", Array.Empty<byte>(), WebSocketMessageType.Binary));
            Assert.False(await WebSocketManager.SendAsync("some-id", (byte[])null, WebSocketMessageType.Binary));
            Assert.False(await WebSocketManager.SendAsync((string)null, new byte[] { 1 }, WebSocketMessageType.Binary));
        }

        [Fact]
        public async Task SendAsync_ConnectionId_Text_EncodesUtf8()
        {
            var ws = new TestWebSocket();
            using var registration = new ClientRegistration(ws);
            const string text = "文本 message";

            bool result = await WebSocketManager.SendAsync(registration.ConnectionId, text, Encoding.UTF8);

            Assert.True(result);
            var frame = Assert.Single(ws.Frames);
            Assert.Equal(Encoding.UTF8.GetBytes(text), frame.Payload);
        }

        [Fact]
        public async Task SendAsync_ConnectionId_Json_SerializesObject()
        {
            var ws = new TestWebSocket();
            using var registration = new ClientRegistration(ws);
            var data = new SendPoco { Name = "json", Value = 9 };

            bool result = await WebSocketManager.SendAsync(registration.ConnectionId, data);

            Assert.True(result);
            var frame = Assert.Single(ws.Frames);
            var back = JsonSerializer.Deserialize<SendPoco>(frame.Payload);
            Assert.Equal("json", back.Name);
            Assert.Equal(9, back.Value);
        }

        [Fact]
        public async Task SendAsync_Batch_MixedKnownAndUnknown_ReportsPerConnectionResult()
        {
            var ws = new TestWebSocket();
            using var registration = new ClientRegistration(ws);
            string unknown = "unknown-" + Guid.NewGuid().ToString("N");
            byte[] payload = Encoding.UTF8.GetBytes("batch");

            Dictionary<string, bool> results = await WebSocketManager.SendAsync(new[] { registration.ConnectionId, unknown }, payload, WebSocketMessageType.Text);

            Assert.Equal(2, results.Count);
            Assert.True(results[registration.ConnectionId]);
            Assert.False(results[unknown]);
            Assert.Single(ws.Frames);
        }

        [Fact]
        public async Task SendAsync_Batch_NullConnectionIds_ReturnsEmptyDictionary()
        {
            var results = await WebSocketManager.SendAsync((IEnumerable<string>)null, new byte[] { 1 }, WebSocketMessageType.Binary);

            Assert.Empty(results);
        }

        [Fact]
        public async Task SendAsync_ExtensionMethods_RouteToUnifiedSend()
        {
            var ws = new TestWebSocket();
            using var registration = new ClientRegistration(ws);

            Assert.True(await Encoding.UTF8.GetBytes("ext-bytes").SendAsync(registration.ConnectionId));
            Assert.True(await "ext-text".SendTextAsync(registration.ConnectionId));
            Assert.True(await new SendPoco { Name = "ext" }.SendJsonAsync(registration.ConnectionId));

            Assert.Equal(3, ws.Frames.Count);
        }

        [Fact]
        public async Task SendAsync_Stream_Extension_LocalPath()
        {
            var ws = new TestWebSocket();
            using var registration = new ClientRegistration(ws);
            byte[] payload = new byte[2048];
            new Random(9).NextBytes(payload);
            using var stream = new MemoryStream(payload);

            bool result = await stream.SendAsync(registration.ConnectionId, WebSocketMessageType.Binary, chunkSize: 512);

            Assert.True(result);
            Assert.Equal(payload, Assert.Single(ws.CompletedMessages()));
        }

        [Fact]
        public async Task SendAsync_Stream_Extension_UnknownConnection_ReturnsFalse()
        {
            using var stream = new MemoryStream(new byte[] { 1, 2 });

            Assert.False(await stream.SendAsync("unknown-" + Guid.NewGuid().ToString("N")));
        }

        #endregion
    }
}
