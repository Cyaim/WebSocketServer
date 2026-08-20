using System.Net.WebSockets;
using System.Text;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Behaviour of the send-by-connection-id API — <c>WebSocketManager.SendAsync(connectionId, ...)</c>
    /// and its batch overload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the API an application uses to push to a client it did not receive a request from:
    /// a chat message arriving for a user, a presence change, a server notification. It is therefore
    /// the API most likely to be called concurrently for the same connection, because the events it
    /// carries are produced by unrelated things happening at the same time.
    /// 这是应用向"没有向自己发过请求的客户端"推送时使用的 API，因此也是最容易被并发调用到同一连接的 API：
    /// 它承载的事件本来就由互不相关的来源同时产生。
    /// </para>
    /// <para>
    /// <b>A <see cref="WebSocket"/> permits exactly one outstanding send per instance.</b> Two
    /// overlapping <c>SendAsync</c> calls on one socket throw
    /// <see cref="InvalidOperationException"/> in the framework implementation. The library already
    /// has a per-socket send gate for this reason; these tests exist to keep the connection-id
    /// paths using it, because they are the ones where the caller cannot see that a second send is
    /// already in flight.
    /// WebSocket 同一实例只允许一个未完成的发送；库里已经为此准备了 per-socket 门闩，
    /// 这些测试的作用是保证按连接 ID 发送的路径确实走了它——调用方在这条路径上根本看不到"已有发送在飞"。
    /// </para>
    /// </remarks>
    [Collection("StaticState")]
    public class WebSocketManagerConnectionSendTests
    {
        private sealed class ClientRegistration : IDisposable
        {
            public string ConnectionId { get; }

            public ClientRegistration(WebSocket socket)
            {
                ConnectionId = "conn-" + Guid.NewGuid().ToString("N");
                Assert.True(MvcChannelHandler.Clients.TryAdd(ConnectionId, socket));
            }

            public void Dispose() => MvcChannelHandler.Clients.TryRemove(ConnectionId, out _);
        }

        [Fact]
        public async Task Concurrent_sends_to_one_connection_never_overlap_on_the_socket()
        {
            // The everyday case this protects: a broadcast and a direct message reaching the same
            // user at the same moment, from two unrelated call sites.
            var socket = new TestWebSocket { SendDelay = TimeSpan.FromMilliseconds(25) };
            using var registration = new ClientRegistration(socket);

            var sends = Enumerable.Range(0, 8)
                .Select(i => WebSocketManager.SendAsync(
                    registration.ConnectionId, Encoding.UTF8.GetBytes($"message-{i}"), WebSocketMessageType.Text))
                .ToArray();

            var results = await Task.WhenAll(sends);

            Assert.All(results, Assert.True);
            Assert.Equal(1, socket.MaxObservedConcurrentSends);
            Assert.Equal(8, socket.CompletedMessages().Count);
        }

        [Fact]
        public async Task A_duplicated_connection_id_in_one_batch_does_not_overlap_either()
        {
            // A caller that builds a recipient list by unioning "the room" with "the mentioned
            // users" can hand us the same id twice. That must not become two overlapping sends.
            var socket = new TestWebSocket { SendDelay = TimeSpan.FromMilliseconds(25) };
            using var registration = new ClientRegistration(socket);

            var ids = new[] { registration.ConnectionId, registration.ConnectionId, registration.ConnectionId };
            var results = await WebSocketManager.SendAsync(ids, Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text);

            Assert.True(results[registration.ConnectionId]);
            Assert.Equal(1, socket.MaxObservedConcurrentSends);
        }

        [Fact]
        public async Task Multi_frame_sends_to_one_connection_do_not_interleave()
        {
            // Interleaving here is worse than an exception: the frames arrive, the socket stays
            // open, and the client reassembles two messages into garbage. Nothing logs an error.
            // 交叠比抛异常更糟：帧都到了、连接还开着，客户端把两条消息拼成乱码，而且没有任何错误日志。
            var socket = new TestWebSocket { SendDelay = TimeSpan.FromMilliseconds(5) };
            using var registration = new ClientRegistration(socket);

            byte[] first = Enumerable.Repeat((byte)'A', 9_000).ToArray();
            byte[] second = Enumerable.Repeat((byte)'B', 9_000).ToArray();

            await Task.WhenAll(
                WebSocketManager.SendAsync(registration.ConnectionId, first, WebSocketMessageType.Binary),
                WebSocketManager.SendAsync(registration.ConnectionId, second, WebSocketMessageType.Binary));

            foreach (var message in socket.CompletedMessages())
            {
                // Every reassembled message must be all A or all B, never a mixture.
                Assert.True(
                    message.All(b => b == (byte)'A') || message.All(b => b == (byte)'B'),
                    "two sends interleaved on one socket; the client would receive a corrupted message");
            }
        }

        [Fact]
        public async Task One_dead_connection_does_not_fail_the_whole_batch()
        {
            // A fan-out to a room always races a disconnect. Reporting per-connection results is
            // the whole point of returning a dictionary — throwing loses the outcome for everyone
            // else in the batch, including the ones that were delivered.
            // 群发必然会撞上掉线。返回字典的意义就是逐连接给出结果；抛异常会把其余人的结果一并丢掉。
            var healthy = new TestWebSocket();
            var closed = new TestWebSocket(WebSocketState.Closed);

            using var healthyRegistration = new ClientRegistration(healthy);
            using var closedRegistration = new ClientRegistration(closed);

            var results = await WebSocketManager.SendAsync(
                new[] { healthyRegistration.ConnectionId, closedRegistration.ConnectionId },
                Encoding.UTF8.GetBytes("fan-out"),
                WebSocketMessageType.Text);

            Assert.True(results[healthyRegistration.ConnectionId]);
            Assert.False(results[closedRegistration.ConnectionId]);
            Assert.Single(healthy.Frames);
        }

        [Fact]
        public async Task A_socket_that_closes_mid_batch_is_reported_as_failed_not_thrown()
        {
            // The state check and the send cannot be atomic, so the socket may close in between.
            // That is an ordinary race, not an error the caller should have to catch.
            var closing = new ClosesOnFirstSendWebSocket();
            var healthy = new TestWebSocket();

            using var closingRegistration = new ClientRegistration(closing);
            using var healthyRegistration = new ClientRegistration(healthy);

            var results = await WebSocketManager.SendAsync(
                new[] { closingRegistration.ConnectionId, healthyRegistration.ConnectionId },
                Encoding.UTF8.GetBytes("fan-out"),
                WebSocketMessageType.Text);

            Assert.False(results[closingRegistration.ConnectionId]);
            Assert.True(results[healthyRegistration.ConnectionId]);
        }

        [Fact]
        public async Task A_connection_that_closes_while_queued_is_reported_as_not_delivered()
        {
            // Serializing sends means the second one can be waiting on the gate when the socket
            // closes. Nothing is written for it — so it must be reported as false. Reporting true
            // because "the send call returned" would tell the caller a message was delivered that
            // never left the process, which is the one wrong answer a delivery result can give.
            // 串行化意味着第二条发送可能正等在门闩上时连接关闭：那一条什么都没发出去，必须报 false。
            // 因为"调用返回了"就报 true，等于告诉调用方一条从未离开进程的消息已经投递。
            var socket = new TestWebSocket { SendDelay = TimeSpan.FromMilliseconds(50) };
            using var registration = new ClientRegistration(socket);

            var first = WebSocketManager.SendAsync(
                registration.ConnectionId, Encoding.UTF8.GetBytes("first"), WebSocketMessageType.Text);
            var second = WebSocketManager.SendAsync(
                registration.ConnectionId, Encoding.UTF8.GetBytes("second"), WebSocketMessageType.Text);

            // Close it while the second send is still behind the gate.
            await Task.Delay(10);
            socket.SetState(WebSocketState.Closed);

            Assert.True(await first);
            Assert.False(await second);
            Assert.Single(socket.Frames);
        }

        [Fact]
        public async Task An_unknown_connection_id_reports_false_rather_than_throwing()
        {
            var results = await WebSocketManager.SendAsync(
                new[] { "no-such-connection" },
                Encoding.UTF8.GetBytes("hello"),
                WebSocketMessageType.Text);

            Assert.False(results["no-such-connection"]);
        }

        /// <summary>A socket that goes away exactly when it is written to.</summary>
        private sealed class ClosesOnFirstSendWebSocket : WebSocket
        {
            private WebSocketState _state = WebSocketState.Open;

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
                => throw new NotSupportedException();

            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            {
                _state = WebSocketState.Aborted;
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
            }
        }
    }
}
