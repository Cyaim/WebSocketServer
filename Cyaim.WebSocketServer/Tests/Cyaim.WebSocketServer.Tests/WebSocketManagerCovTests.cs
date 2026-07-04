using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Cluster;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// A WebSocket that reports Open but throws from SendAsync, to drive the send-helper
    /// catch/throw blocks in WebSocketManager.
    /// </summary>
    internal sealed class ThrowingWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string d, CancellationToken c) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken c)
            => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool eom, CancellationToken c)
            => throw new IOException("send-fails");
    }

    [Collection("StaticState")]
    public class WebSocketManagerCovTests : IDisposable
    {
        private readonly int _prevBatchLimit;
        private readonly ClusterManager _prevClusterManager;

        public WebSocketManagerCovTests()
        {
            _prevBatchLimit = WebSocketManager.BatchProcessingWebsocketLimit;
            _prevClusterManager = GlobalClusterCenter.ClusterManager;
        }

        public void Dispose()
        {
            WebSocketManager.BatchProcessingWebsocketLimit = _prevBatchLimit;
            GlobalClusterCenter.ClusterManager = _prevClusterManager;
        }

        private static Task InvokePrivateStatic(string name, params object[] args)
        {
            var m = typeof(WebSocketManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);
            return (Task)m.Invoke(null, args);
        }

        [Fact]
        public async Task SendBufferCoreAsync_NotOpenSocket_ReturnsEarly()
        {
            var closed = new TestWebSocket(WebSocketState.Closed);
            await InvokePrivateStatic("SendBufferCoreAsync",
                closed, new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }), WebSocketMessageType.Binary, true, (uint)4096, CancellationToken.None);
            Assert.Empty(closed.Frames);
        }

        [Fact]
        public async Task SendStreamCoreAsync_NotOpenSocket_ReturnsEarly()
        {
            var closed = new TestWebSocket(WebSocketState.Closed);
            using var ms = new MemoryStream(new byte[] { 1, 2, 3 });
            await InvokePrivateStatic("SendStreamCoreAsync",
                closed, (Stream)ms, WebSocketMessageType.Binary, true, (uint)4096, CancellationToken.None);
            Assert.Empty(closed.Frames);
        }

        [Fact]
        public async Task SendBufferToMany_BatchLimitOne_FansOutInWaves()
        {
            WebSocketManager.BatchProcessingWebsocketLimit = 1;
            var a = new TestWebSocket();
            var b = new TestWebSocket();
            var payload = new byte[] { 9, 8, 7 };

            await WebSocketManager.SendLocalAsync(new ReadOnlyMemory<byte>(payload), WebSocketMessageType.Binary,
                sendAtOnce: true, cancellationToken: CancellationToken.None, sockets: new WebSocket[] { a, b });

            Assert.NotEmpty(a.Frames);
            Assert.NotEmpty(b.Frames);
        }

        [Fact]
        public async Task SendStreamDataAsync_SendThrows_PropagatesThroughCatch()
        {
            using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4 });
            // sendAtOnce:true -> SendStreamDataAsync; throwing socket exercises its catch/throw.
            // With timeout:null the exception propagates to the awaiting caller (single-socket path).
            await Assert.ThrowsAnyAsync<Exception>(() => WebSocketManager.SendLocalAsync((Stream)ms, WebSocketMessageType.Binary, CancellationToken.None,
                timeout: null, sendAtOnce: true, sendBufferSize: 4096, sockets: new ThrowingWebSocket()));
        }

        [Fact]
        public async Task SendStreamDataInBatchesAsync_SendThrows_PropagatesThroughCatch()
        {
            using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6 });
            await Assert.ThrowsAnyAsync<Exception>(() => WebSocketManager.SendLocalAsync((Stream)ms, WebSocketMessageType.Binary, CancellationToken.None,
                timeout: null, sendAtOnce: false, sendBufferSize: 2, sockets: new ThrowingWebSocket()));
        }

        [Fact]
        public async Task SendBufferedDataInBatchesAsync_SendThrows_PropagatesThroughCatch()
        {
            var buffer = new byte[100];
            // buffer.Length(100) > sendBufferSize(10) and sendAtOnce:false -> batched buffer path.
            await Assert.ThrowsAnyAsync<Exception>(() => WebSocketManager.SendLocalAsync(new ReadOnlyMemory<byte>(buffer), WebSocketMessageType.Binary,
                sendAtOnce: false, cancellationToken: CancellationToken.None, timeout: null, sendBufferSize: 10,
                sockets: new ThrowingWebSocket()));
        }

        [Fact]
        public async Task SendStreamCoreChunks_CapturedFramesShowChunking()
        {
            var ws = new TestWebSocket();
            using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7 });
            await WebSocketManager.SendLocalAsync((Stream)ms, WebSocketMessageType.Binary, CancellationToken.None,
                timeout: null, sendAtOnce: false, sendBufferSize: 3, sockets: ws);
            // 7 bytes over a 3-byte window -> at least 3 data frames + a terminating empty frame.
            Assert.True(ws.Frames.Count >= 3);
        }

        [Fact]
        public async Task SendLocalStream_NoSockets_ReturnsEarly()
        {
            using var ms = new MemoryStream(new byte[] { 1 });
            await WebSocketManager.SendLocalAsync((Stream)ms, WebSocketMessageType.Binary, CancellationToken.None);
        }

        [Fact]
        public async Task SendLocalAsync_GenericExtension_NullData_ReturnsEarly()
        {
            MvcTestSupport.PocoInput data = null;
            await WebSocketManager.SendLocalAsync(data, null, WebSocketMessageType.Text, null, null, null, 4096,
                new WebSocket[] { new TestWebSocket() });
        }

        [Fact]
        public async Task SendLocalAsync_GenericExtension_EmptySockets_ReturnsEarly()
        {
            var data = new MvcTestSupport.PocoInput { Text = "x", Number = 1 };
            await WebSocketManager.SendLocalAsync(data, null, WebSocketMessageType.Text, null, null, null, 4096,
                Array.Empty<WebSocket>());
        }

        [Fact]
        public async Task SendLocalAsync_GenericExtension_Sends()
        {
            var ws = new TestWebSocket();
            var data = new MvcTestSupport.PocoInput { Text = "x", Number = 1 };
            await WebSocketManager.SendLocalAsync(data, null, WebSocketMessageType.Text, null, null, null, 4096, ws);
            Assert.NotEmpty(ws.Frames);
        }

        [Fact]
        public async Task SendAsync_EmptyText_ReturnsFalse()
        {
            Assert.False(await WebSocketManager.SendAsync(connectionId: "id", text: string.Empty));
        }

        [Fact]
        public async Task SendAsync_NullJsonData_ReturnsFalse()
        {
            Assert.False(await WebSocketManager.SendAsync<MvcTestSupport.PocoInput>("id", null));
        }

        [Fact]
        public async Task StreamSendAsync_EmptyConnectionId_ResolvesNoSocket_ReturnsFalse()
        {
            GlobalClusterCenter.ClusterManager = null;
            using var ms = new MemoryStream(new byte[] { 1, 2, 3 });
            Assert.False(await ms.SendAsync(string.Empty));
        }
    }
}
