using System.Net.WebSockets;
using System.Text.Json;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Cluster.Transports;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    [Collection("ClusterStaticState")]
    public class ClusterChannelHandlerMoreTests : StaticStateGuard
    {
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private readonly List<ClusterRouter> _routers = new List<ClusterRouter>();

        public override void Dispose()
        {
            foreach (var r in _routers)
            {
                try { r.Dispose(); } catch { }
            }
            foreach (var d in _disposables)
            {
                try { d.Dispose(); } catch { }
            }
            base.Dispose();
        }

        private static DefaultHttpContext ContextFor(WebSocket socket)
        {
            var context = new DefaultHttpContext();
            context.Features.Set<IHttpWebSocketFeature>(new FakeWebSocketFeature(socket));
            return context;
        }

        private static Task Run(WebSocket socket)
            => ClusterChannelHandler.ConnectionEntry(ContextFor(socket), NullLogger<WebSocketRouteMiddleware>.Instance, new WebSocketRouteOption());

        private static string MessageJson(ClusterMessageType type, string fromNodeId = "peer1", string payload = null)
            => JsonSerializer.Serialize(new ClusterMessage { Type = type, FromNodeId = fromNodeId, Payload = payload });

        /// <summary>
        /// Builds a ClusterManager whose transport is a real WebSocketClusterTransport
        /// so the inbound handler forwards messages through OnPeerMessage.
        /// </summary>
        private (ClusterManager Manager, WebSocketClusterTransport Transport) CreateWsManager()
        {
            var transport = new WebSocketClusterTransport(NullLogger<WebSocketClusterTransport>.Instance, "srv");
            var raft = new RaftNode(NullLogger<RaftNode>.Instance, transport, "srv");
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, transport, raft, "srv");
            _routers.Add(router);
            _disposables.Add(transport);
            var manager = new ClusterManager(NullLogger<ClusterManager>.Instance, transport, raft, router, "srv", new ClusterOption());
            return (manager, transport);
        }

        [Fact]
        public async Task ConnectionEntry_ForwardsInboundMessage_ToWsTransport_AndIdentifiesNode()
        {
            var (manager, transport) = CreateWsManager();
            GlobalClusterCenter.ClusterManager = manager;

            var received = new List<ClusterMessageEventArgs>();
            transport.MessageReceived += (_, e) => { lock (received) { received.Add(e); } };

            // Message split across two frames -> reassembled / 消息分两帧发送 -> 重组
            var json = MessageJson(ClusterMessageType.NodeInfo, "peerX", "hello");
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            var socket = new ScriptedWebSocket(
                ScriptedReceive.Bytes(bytes.Take(5).ToArray(), endOfMessage: false),
                ScriptedReceive.Bytes(bytes.Skip(5).ToArray(), endOfMessage: true),
                ScriptedReceive.Close());

            await Run(socket);

            var e1 = Assert.Single(received);
            Assert.Equal(ClusterMessageType.NodeInfo, e1.Message.Type);
            Assert.Equal("peerX", e1.FromNodeId);
            Assert.Equal("hello", e1.Message.Payload);

            // Connection is removed and closed in the finally block / finally 中移除并关闭连接
            Assert.Empty(ClusterChannelHandler.IncomingConnections);
            Assert.Equal(WebSocketState.Closed, socket.State);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.RecordedCloseStatus);
        }

        [Fact]
        public async Task ConnectionEntry_EmptyFrame_NullJson_AndBadJson_AreSkipped()
        {
            var (manager, transport) = CreateWsManager();
            GlobalClusterCenter.ClusterManager = manager;

            var received = 0;
            transport.MessageReceived += (_, __) => Interlocked.Increment(ref received);

            var socket = new ScriptedWebSocket(
                // Zero-length final frame -> skipped / 空帧 -> 跳过
                ScriptedReceive.Bytes(Array.Empty<byte>(), endOfMessage: true),
                // JSON "null" -> deserializes to null -> warning path / "null" -> 反序列化为 null
                ScriptedReceive.Text("null"),
                // Invalid JSON -> JsonException catch / 非法 JSON -> JsonException 分支
                ScriptedReceive.Text("{not-json"),
                // Then a valid message proves the loop survived / 随后有效消息证明循环仍然存活
                ScriptedReceive.Text(MessageJson(ClusterMessageType.Heartbeat)),
                ScriptedReceive.Close());

            await Run(socket);

            Assert.Equal(1, received);
        }

        [Fact]
        public async Task ConnectionEntry_MessageExceedingMaxMessageSize_ClosesWithMessageTooBig()
        {
            ClusterChannelHandler.MaxMessageSize = 16;
            var socket = new ScriptedWebSocket(
                ScriptedReceive.Bytes(new byte[32], endOfMessage: false));

            await Run(socket);

            Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.RecordedCloseStatus);
            Assert.Equal(WebSocketState.Closed, socket.State);
            Assert.Empty(ClusterChannelHandler.IncomingConnections);
        }

        [Fact]
        public async Task ConnectionEntry_NoClusterManager_LogsAndContinues()
        {
            GlobalClusterCenter.ClusterManager = null;

            var socket = new ScriptedWebSocket(
                ScriptedReceive.Text(MessageJson(ClusterMessageType.NodeInfo)),
                ScriptedReceive.Close());

            await Run(socket);

            Assert.Equal(WebSocketState.Closed, socket.State);
        }

        [Fact]
        public async Task ConnectionEntry_NonWsTransport_LogsUnsupportedTransport()
        {
            var transport = new FakeClusterTransport();
            var raft = new RaftNode(NullLogger<RaftNode>.Instance, transport, "srv");
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, transport, raft, "srv");
            try
            {
                GlobalClusterCenter.ClusterManager = new ClusterManager(
                    NullLogger<ClusterManager>.Instance, transport, raft, router, "srv", new ClusterOption());

                var socket = new ScriptedWebSocket(
                    ScriptedReceive.Text(MessageJson(ClusterMessageType.NodeInfo)),
                    ScriptedReceive.Close());

                await Run(socket);

                Assert.Equal(WebSocketState.Closed, socket.State);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Fact]
        public async Task ConnectionEntry_HandlerThrowingOnMessage_HitsGenericCatch()
        {
            var (manager, transport) = CreateWsManager();
            GlobalClusterCenter.ClusterManager = manager;
            transport.MessageReceived += (_, __) => throw new ApplicationException("subscriber failure");

            var socket = new ScriptedWebSocket(
                ScriptedReceive.Text(MessageJson(ClusterMessageType.NodeInfo)),
                ScriptedReceive.Close());

            // Generic exception is swallowed per message; connection continues then closes
            // 每条消息的通用异常被吞掉；连接继续运行然后关闭
            await Run(socket);

            Assert.Equal(WebSocketState.Closed, socket.State);
        }

        [Fact]
        public async Task ConnectionEntry_ReceiveThrows_IsCaught_AndConnectionCleanedUp()
        {
            var socket = new ScriptedWebSocket(
                ScriptedReceive.Throw(new WebSocketException(WebSocketError.ConnectionClosedPrematurely)));

            await Run(socket);

            Assert.Empty(ClusterChannelHandler.IncomingConnections);
            // Still open when the exception escaped -> closed in finally / 异常逃逸时仍为 Open -> finally 中关闭
            Assert.Equal(WebSocketState.Closed, socket.State);
        }
    }
}
