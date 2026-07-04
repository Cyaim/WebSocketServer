using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Additional WebSocketManager coverage: batch text/JSON sends, stream fan-out to
    /// multiple connection ids and the GetLocalWebSocket fallback through
    /// GlobalClusterCenter.ConnectionProvider. Runs in the StaticState collection because
    /// it registers clients in MvcChannelHandler.Clients and swaps ConnectionProvider.
    /// </summary>
    [Collection("StaticState")]
    public class WebSocketManagerMoreTests
    {
        private sealed class ClientRegistration : IDisposable
        {
            public string ConnectionId { get; }

            public ClientRegistration(WebSocket socket)
            {
                ConnectionId = "more-" + Guid.NewGuid().ToString("N");
                Assert.True(MvcChannelHandler.Clients.TryAdd(ConnectionId, socket));
            }

            public void Dispose()
            {
                MvcChannelHandler.Clients.TryRemove(ConnectionId, out _);
            }
        }

        private sealed class MapConnectionProvider : IWebSocketConnectionProvider
        {
            private readonly Dictionary<string, WebSocket> _map = new Dictionary<string, WebSocket>();

            public void Add(string connectionId, WebSocket socket) => _map[connectionId] = socket;

            public WebSocket GetConnection(string connectionId)
                => _map.TryGetValue(connectionId, out var socket) ? socket : null;

            public Task<bool> SendAsync(string connectionId, byte[] data, WebSocketMessageType messageType, CancellationToken cancellationToken = default)
                => Task.FromResult(false);
        }

        private sealed class MorePoco
        {
            public string Name { get; set; }
        }

        [Fact]
        public async Task SendAsync_Batch_Text_SendsToAllKnownConnections()
        {
            var ws1 = new TestWebSocket();
            var ws2 = new TestWebSocket();
            using var reg1 = new ClientRegistration(ws1);
            using var reg2 = new ClientRegistration(ws2);

            var results = await WebSocketManager.SendAsync(new[] { reg1.ConnectionId, reg2.ConnectionId }, "batch-text", Encoding.UTF8);

            Assert.True(results[reg1.ConnectionId]);
            Assert.True(results[reg2.ConnectionId]);
            Assert.Equal(Encoding.UTF8.GetBytes("batch-text"), Assert.Single(ws1.Frames).Payload);
            Assert.Equal(Encoding.UTF8.GetBytes("batch-text"), Assert.Single(ws2.Frames).Payload);
        }

        [Fact]
        public async Task SendAsync_Batch_Text_NullOrEmptyText_ReturnsEmptyDictionary()
        {
            Assert.Empty(await WebSocketManager.SendAsync(new[] { "x" }, (string)null, Encoding.UTF8));
            Assert.Empty(await WebSocketManager.SendAsync(new[] { "x" }, string.Empty, Encoding.UTF8));
        }

        [Fact]
        public async Task SendAsync_Batch_Json_SerializesOncePerConnection()
        {
            var ws = new TestWebSocket();
            using var reg = new ClientRegistration(ws);

            var results = await WebSocketManager.SendAsync(new[] { reg.ConnectionId }, new MorePoco { Name = "j" });

            Assert.True(results[reg.ConnectionId]);
            var back = JsonSerializer.Deserialize<MorePoco>(Assert.Single(ws.Frames).Payload);
            Assert.Equal("j", back.Name);
        }

        [Fact]
        public async Task SendAsync_Batch_Json_NullData_ReturnsEmptyDictionary()
        {
            Assert.Empty(await WebSocketManager.SendAsync(new[] { "x" }, (MorePoco)null));
        }

        [Fact]
        public async Task SendAsync_Batch_SkipsNullOrEmptyConnectionIds()
        {
            var ws = new TestWebSocket();
            using var reg = new ClientRegistration(ws);

            var results = await WebSocketManager.SendAsync(new[] { reg.ConnectionId, null, string.Empty }, new byte[] { 1 }, WebSocketMessageType.Binary);

            Assert.Single(results);
            Assert.True(results[reg.ConnectionId]);
        }

        [Fact]
        public async Task SendJsonAsync_Extension_MultipleConnections()
        {
            var ws1 = new TestWebSocket();
            var ws2 = new TestWebSocket();
            using var reg1 = new ClientRegistration(ws1);
            using var reg2 = new ClientRegistration(ws2);

            var results = await new MorePoco { Name = "multi" }.SendJsonAsync(new[] { reg1.ConnectionId, reg2.ConnectionId });

            Assert.True(results[reg1.ConnectionId]);
            Assert.True(results[reg2.ConnectionId]);
        }

        [Fact]
        public async Task SendTextAsync_Extension_MultipleConnections()
        {
            var ws = new TestWebSocket();
            using var reg = new ClientRegistration(ws);
            string unknown = "unknown-" + Guid.NewGuid().ToString("N");

            var results = await "fan-out-text".SendTextAsync(new[] { reg.ConnectionId, unknown });

            Assert.True(results[reg.ConnectionId]);
            Assert.False(results[unknown]);
        }

        [Fact]
        public async Task SendAsync_ByteExtension_MultipleConnections()
        {
            var ws = new TestWebSocket();
            using var reg = new ClientRegistration(ws);

            var results = await new byte[] { 1, 2, 3 }.SendAsync(new[] { reg.ConnectionId });

            Assert.True(results[reg.ConnectionId]);
        }

        #region Stream extensions

        [Fact]
        public async Task SendAsync_Stream_MultipleConnections_BuffersAndFansOut()
        {
            var ws1 = new TestWebSocket();
            var ws2 = new TestWebSocket();
            using var reg1 = new ClientRegistration(ws1);
            using var reg2 = new ClientRegistration(ws2);
            byte[] payload = new byte[4000];
            new Random(21).NextBytes(payload);
            using var stream = new MemoryStream(payload);

            var results = await stream.SendAsync(new[] { reg1.ConnectionId, reg2.ConnectionId }, WebSocketMessageType.Binary, chunkSize: 1024);

            Assert.True(results[reg1.ConnectionId]);
            Assert.True(results[reg2.ConnectionId]);
            Assert.Equal(payload, Assert.Single(ws1.CompletedMessages()));
            Assert.Equal(payload, Assert.Single(ws2.CompletedMessages()));
        }

        [Fact]
        public async Task SendAsync_Stream_MultipleConnections_MixedKnownAndUnknown()
        {
            var ws = new TestWebSocket();
            using var reg = new ClientRegistration(ws);
            string unknown = "unknown-" + Guid.NewGuid().ToString("N");
            using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

            var results = await stream.SendAsync(new[] { reg.ConnectionId, unknown });

            Assert.True(results[reg.ConnectionId]);
            Assert.False(results[unknown]);
        }

        [Fact]
        public async Task SendAsync_Stream_SingleIdInList_StreamsDirectly()
        {
            var ws = new TestWebSocket();
            using var reg = new ClientRegistration(ws);
            byte[] payload = new byte[512];
            new Random(5).NextBytes(payload);
            using var stream = new MemoryStream(payload);

            var results = await stream.SendAsync(new[] { reg.ConnectionId }, WebSocketMessageType.Binary, chunkSize: 128);

            Assert.True(results[reg.ConnectionId]);
            Assert.Equal(payload, Assert.Single(ws.CompletedMessages()));
        }

        [Fact]
        public async Task SendAsync_Stream_SingleIdInList_ClosedSocket_ReturnsFalse()
        {
            var ws = new TestWebSocket(WebSocketState.Closed);
            using var reg = new ClientRegistration(ws);
            using var stream = new MemoryStream(new byte[] { 1 });

            var results = await stream.SendAsync(new[] { reg.ConnectionId });

            Assert.False(results[reg.ConnectionId]);
        }

        [Fact]
        public async Task SendAsync_Stream_NullOrUnreadable_ReturnsEmptyOrFalse()
        {
            Assert.False(await ((Stream)null).SendAsync("any-id"));
            Assert.Empty(await ((Stream)null).SendAsync(new[] { "any-id" }));

            var closedStream = new MemoryStream(new byte[] { 1 });
            closedStream.Dispose(); // CanRead == false
            Assert.False(await closedStream.SendAsync("any-id"));
            Assert.Empty(await closedStream.SendAsync(new[] { "any-id" }));
        }

        [Fact]
        public async Task SendAsync_Stream_EmptyConnectionIdList_ReturnsEmpty()
        {
            using var stream = new MemoryStream(new byte[] { 1 });

            Assert.Empty(await stream.SendAsync(new[] { (string)null, string.Empty }));
        }

        #endregion

        #region GetLocalWebSocket fallback via GlobalClusterCenter.ConnectionProvider

        [Fact]
        public async Task SendAsync_ConnectionNotInClients_FallsBackToConnectionProvider()
        {
            var previousProvider = GlobalClusterCenter.ConnectionProvider;
            try
            {
                var ws = new TestWebSocket();
                string connectionId = "provider-" + Guid.NewGuid().ToString("N");
                var provider = new MapConnectionProvider();
                provider.Add(connectionId, ws);
                GlobalClusterCenter.ConnectionProvider = provider;

                bool result = await WebSocketManager.SendAsync(connectionId, Encoding.UTF8.GetBytes("via-provider"), WebSocketMessageType.Text);

                Assert.True(result);
                Assert.Equal(Encoding.UTF8.GetBytes("via-provider"), Assert.Single(ws.Frames).Payload);
            }
            finally
            {
                GlobalClusterCenter.ConnectionProvider = previousProvider;
            }
        }

        [Fact]
        public async Task SendAsync_ClientsTakesPrecedenceOverConnectionProvider()
        {
            var previousProvider = GlobalClusterCenter.ConnectionProvider;
            try
            {
                var clientSocket = new TestWebSocket();
                var providerSocket = new TestWebSocket();
                using var reg = new ClientRegistration(clientSocket);
                var provider = new MapConnectionProvider();
                provider.Add(reg.ConnectionId, providerSocket);
                GlobalClusterCenter.ConnectionProvider = provider;

                bool result = await WebSocketManager.SendAsync(reg.ConnectionId, new byte[] { 9 }, WebSocketMessageType.Binary);

                Assert.True(result);
                Assert.Single(clientSocket.Frames);
                Assert.Empty(providerSocket.Frames);
            }
            finally
            {
                GlobalClusterCenter.ConnectionProvider = previousProvider;
            }
        }

        [Fact]
        public async Task SendAsync_ProviderReturnsNull_ResultFalse()
        {
            var previousProvider = GlobalClusterCenter.ConnectionProvider;
            try
            {
                GlobalClusterCenter.ConnectionProvider = new MapConnectionProvider();

                bool result = await WebSocketManager.SendAsync("nowhere-" + Guid.NewGuid().ToString("N"), new byte[] { 1 }, WebSocketMessageType.Binary);

                Assert.False(result);
            }
            finally
            {
                GlobalClusterCenter.ConnectionProvider = previousProvider;
            }
        }

        [Fact]
        public async Task SendAsync_Stream_FallsBackToConnectionProvider()
        {
            var previousProvider = GlobalClusterCenter.ConnectionProvider;
            try
            {
                var ws = new TestWebSocket();
                string connectionId = "provider-stream-" + Guid.NewGuid().ToString("N");
                var provider = new MapConnectionProvider();
                provider.Add(connectionId, ws);
                GlobalClusterCenter.ConnectionProvider = provider;
                byte[] payload = new byte[256];
                new Random(2).NextBytes(payload);
                using var stream = new MemoryStream(payload);

                bool result = await stream.SendAsync(connectionId, WebSocketMessageType.Binary, chunkSize: 64);

                Assert.True(result);
                Assert.Equal(payload, Assert.Single(ws.CompletedMessages()));
            }
            finally
            {
                GlobalClusterCenter.ConnectionProvider = previousProvider;
            }
        }

        #endregion

        [Fact]
        public void BatchProcessingWebsocketLimit_DefaultIs1000()
        {
            Assert.Equal(1000, WebSocketManager.BatchProcessingWebsocketLimit);
        }

        [Fact]
        public async Task SendLocalAsync_Timeout_ReturnsBeforeSendCompletes_SendFinishesDetached()
        {
            var ws = new TestWebSocket { SendDelay = TimeSpan.FromMilliseconds(500) };
            byte[] payload = Encoding.UTF8.GetBytes("slow");

            await WebSocketManager.SendLocalAsync(payload.AsMemory(), WebSocketMessageType.Text, true,
                CancellationToken.None, timeout: TimeSpan.FromMilliseconds(50), sockets: ws);

            // The awaited call returned on timeout while the send was still in its delay
            Assert.Empty(ws.Frames);

            // The detached send eventually completes
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (ws.Frames.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }
            Assert.Equal(payload, Assert.Single(ws.Frames).Payload);
        }

        [Fact]
        public async Task SendLocalAsync_InfiniteTimeout_WaitsForCompletion()
        {
            var ws = new TestWebSocket { SendDelay = TimeSpan.FromMilliseconds(50) };
            byte[] payload = Encoding.UTF8.GetBytes("wait");

            await WebSocketManager.SendLocalAsync(payload.AsMemory(), WebSocketMessageType.Text, true,
                CancellationToken.None, timeout: Timeout.InfiniteTimeSpan, sockets: ws);

            Assert.Equal(payload, Assert.Single(ws.Frames).Payload);
        }

        [Fact]
        public async Task SendLocalAsync_Stream_WithTimeout_CompletesWithinTimeout()
        {
            var ws = new TestWebSocket();
            byte[] payload = Encoding.UTF8.GetBytes("stream-timeout");
            using var stream = new MemoryStream(payload);

            await WebSocketManager.SendLocalAsync(stream, WebSocketMessageType.Binary, CancellationToken.None,
                timeout: TimeSpan.FromSeconds(10), sendAtOnce: true, sockets: ws);

            Assert.Equal(payload, Assert.Single(ws.Frames).Payload);
        }
    }
}
