using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Cluster.Transports;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    [Collection("ClusterStaticState")]
    public class WebSocketClusterTransportTests : IDisposable
    {
        private readonly List<WebSocketClusterTransport> _transports = new List<WebSocketClusterTransport>();

        private WebSocketClusterTransport CreateTransport(string nodeId = "nodeA")
        {
            var transport = new WebSocketClusterTransport(NullLogger<WebSocketClusterTransport>.Instance, nodeId);
            _transports.Add(transport);
            return transport;
        }

        public void Dispose()
        {
            foreach (var transport in _transports)
            {
                try
                {
                    transport.Dispose();
                }
                catch
                {
                    // best effort
                }
            }
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static void RaisePeerMessage(WebSocketClusterTransport transport, ClusterMessage message, string fromNodeId = null)
        {
            var method = typeof(WebSocketClusterTransport).GetMethod("OnPeerMessage", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(transport, new object[] { message, fromNodeId });
        }

        // ---------------------------------------------------------------
        // Basics
        // ---------------------------------------------------------------

        [Fact]
        public void Constructor_NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => new WebSocketClusterTransport(null, "n"));
            Assert.Throws<ArgumentNullException>(() => new WebSocketClusterTransport(NullLogger<WebSocketClusterTransport>.Instance, null));
        }

        [Fact]
        public void MaxMessageSize_DefaultsTo64MB_AndIsSettable()
        {
            var transport = CreateTransport();
            Assert.Equal(64 * 1024 * 1024, transport.MaxMessageSize);

            transport.MaxMessageSize = 1024;
            Assert.Equal(1024, transport.MaxMessageSize);
        }

        [Fact]
        public void RegisterNode_Null_Throws()
        {
            var transport = CreateTransport();
            Assert.Throws<ArgumentNullException>(() => transport.RegisterNode(null));
        }

        [Fact]
        public void IsNodeConnected_RegisteredButNotConnected_ReturnsFalse()
        {
            var transport = CreateTransport();
            transport.RegisterNode(new ClusterNode { NodeId = "nodeB", Address = "127.0.0.1", Port = 1 });

            Assert.False(transport.IsNodeConnected("nodeB"));
            Assert.False(transport.IsNodeConnected("unknown"));
        }

        [Fact]
        public async Task StartAsync_NoRegisteredNodes_RunsStandalone()
        {
            var transport = CreateTransport();
            await transport.StartAsync();
            await transport.StopAsync();
        }

        // ---------------------------------------------------------------
        // SendAsync error behavior (pinned)
        // ---------------------------------------------------------------

        [Fact]
        public async Task SendAsync_NullNodeId_ThrowsArgumentNullException()
        {
            var transport = CreateTransport();
            await Assert.ThrowsAsync<ArgumentNullException>(() => transport.SendAsync(null, new ClusterMessage()));
        }

        [Fact]
        public async Task SendAsync_UnknownNode_ThrowsInvalidOperationException()
        {
            var transport = CreateTransport();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync("ghost", new ClusterMessage()));
            Assert.Contains("ghost", ex.Message);
        }

        [Fact]
        public async Task SendAsync_RegisteredButUnreachableNode_ThrowsInvalidOperationException()
        {
            var transport = CreateTransport();
            var port = GetFreePort(); // nothing is listening here
            transport.RegisterNode(new ClusterNode { NodeId = "nodeB", Address = "127.0.0.1", Port = port, TransportConfig = "/cluster" });

            await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync("nodeB", new ClusterMessage()));
        }

        [Fact]
        public async Task BroadcastAsync_NoNodes_Completes()
        {
            var transport = CreateTransport();
            await transport.BroadcastAsync(new ClusterMessage { Type = ClusterMessageType.NodeInfo });
        }

        [Fact]
        public async Task BroadcastAsync_UnreachableNode_SwallowsErrors()
        {
            var transport = CreateTransport();
            transport.RegisterNode(new ClusterNode { NodeId = "nodeB", Address = "127.0.0.1", Port = GetFreePort(), TransportConfig = "/cluster" });

            // Broadcast catches per-node send failures / 广播吞掉单节点发送失败
            await transport.BroadcastAsync(new ClusterMessage { Type = ClusterMessageType.NodeInfo });
        }

        // ---------------------------------------------------------------
        // Route storage: unsupported on WebSocket transport (pinned)
        // ---------------------------------------------------------------

        [Fact]
        public async Task RouteStorage_IsNotSupported()
        {
            var transport = CreateTransport();

            Assert.False(await transport.StoreConnectionRouteAsync("c1", "nodeA"));
            Assert.Null(await transport.GetConnectionRouteAsync("c1"));
            Assert.False(await transport.RemoveConnectionRouteAsync("c1"));
            Assert.False(await transport.RefreshConnectionRouteAsync("c1", "nodeA"));
        }

        // ---------------------------------------------------------------
        // OnPeerMessage (inbound /cluster injection point)
        // ---------------------------------------------------------------

        [Fact]
        public void OnPeerMessage_RaisesMessageReceived_WithExplicitFromNode()
        {
            var transport = CreateTransport();
            ClusterMessageEventArgs received = null;
            transport.MessageReceived += (_, e) => received = e;

            var message = new ClusterMessage { Type = ClusterMessageType.NodeInfo, FromNodeId = "payloadNode" };
            RaisePeerMessage(transport, message, "explicitNode");

            Assert.NotNull(received);
            Assert.Equal("explicitNode", received.FromNodeId);
            Assert.Same(message, received.Message);
        }

        [Fact]
        public void OnPeerMessage_FallsBackToMessageFromNodeId()
        {
            var transport = CreateTransport();
            ClusterMessageEventArgs received = null;
            transport.MessageReceived += (_, e) => received = e;

            RaisePeerMessage(transport, new ClusterMessage { Type = ClusterMessageType.NodeInfo, FromNodeId = "payloadNode" }, null);

            Assert.Equal("payloadNode", received.FromNodeId);
        }

        [Fact]
        public void OnPeerMessage_NullMessage_DoesNothing()
        {
            var transport = CreateTransport();
            var raised = false;
            transport.MessageReceived += (_, __) => raised = true;

            RaisePeerMessage(transport, null, "nodeB");

            Assert.False(raised);
        }

        // ---------------------------------------------------------------
        // Live peer over 127.0.0.1 (HttpListener acts as the /cluster endpoint)
        // ---------------------------------------------------------------

        private sealed class ListenerPeer : IAsyncDisposable
        {
            private readonly HttpListener _listener;
            public int Port { get; }
            public Task<WebSocket> AcceptedSocket { get; }

            public ListenerPeer()
            {
                Port = GetFreePort();
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{Port}/");
                _listener.Start();
                AcceptedSocket = AcceptAsync();
            }

            private async Task<WebSocket> AcceptAsync()
            {
                var context = await _listener.GetContextAsync();
                var wsContext = await context.AcceptWebSocketAsync(null);
                return wsContext.WebSocket;
            }

            public static async Task<(ClusterMessage Message, string Json)> ReceiveMessageAsync(WebSocket socket)
            {
                var buffer = new byte[64 * 1024];
                using var stream = new MemoryStream();
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return (null, null);
                    }
                    stream.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                    {
                        var json = Encoding.UTF8.GetString(stream.ToArray());
                        return (JsonSerializer.Deserialize<ClusterMessage>(json), json);
                    }
                }
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    if (AcceptedSocket.IsCompletedSuccessfully)
                    {
                        AcceptedSocket.Result.Dispose();
                    }
                }
                catch
                {
                }
                _listener.Stop();
                _listener.Close();
                await Task.CompletedTask;
            }
        }

        [Fact]
        public async Task LivePeer_SendReceive_MultiFrameReassembly_AndDisconnectEvents()
        {
            await using var peer = new ListenerPeer();
            var transport = CreateTransport("nodeA");
            transport.RegisterNode(new ClusterNode
            {
                NodeId = "nodeB",
                Address = "localhost",
                Port = peer.Port,
                TransportConfig = "/cluster"
            });

            string connectedNode = null;
            string disconnectedNode = null;
            var received = new List<ClusterMessageEventArgs>();
            transport.NodeConnected += (_, e) => connectedNode = e.NodeId;
            transport.NodeDisconnected += (_, e) => disconnectedNode = e.NodeId;
            transport.MessageReceived += (_, e) => { lock (received) { received.Add(e); } };

            // 1. SendAsync lazily connects and delivers the message / SendAsync 惰性建连并投递消息
            await transport.SendAsync("nodeB", new ClusterMessage
            {
                Type = ClusterMessageType.NodeInfo,
                Payload = "ping"
            });

            var serverSocket = await peer.AcceptedSocket.WaitAsync(TimeSpan.FromSeconds(5));
            var (serverReceived, _) = await ListenerPeer.ReceiveMessageAsync(serverSocket).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("nodeB", connectedNode);
            Assert.True(transport.IsNodeConnected("nodeB"));
            Assert.Equal(ClusterMessageType.NodeInfo, serverReceived.Type);
            Assert.Equal("nodeA", serverReceived.FromNodeId);
            Assert.Equal("nodeB", serverReceived.ToNodeId);
            Assert.Equal("ping", serverReceived.Payload);

            // 2. Server replies split across two frames -> reassembled into one message
            //    服务器把回复拆成两帧发送 -> 客户端重组为一条消息
            var reply = JsonSerializer.SerializeToUtf8Bytes(new ClusterMessage
            {
                Type = ClusterMessageType.Heartbeat,
                Payload = new string('x', 500)
            });
            await serverSocket.SendAsync(new ArraySegment<byte>(reply, 0, 100), WebSocketMessageType.Text, endOfMessage: false, CancellationToken.None);
            await serverSocket.SendAsync(new ArraySegment<byte>(reply, 100, reply.Length - 100), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

            Assert.True(await Wait.UntilAsync(() => { lock (received) { return received.Count == 1; } }, 5000));
            lock (received)
            {
                Assert.Equal(ClusterMessageType.Heartbeat, received[0].Message.Type);
                Assert.Equal(new string('x', 500), received[0].Message.Payload);
                // FromNodeId is stamped with the peer node id / FromNodeId 被标记为对端节点 ID
                Assert.Equal("nodeB", received[0].FromNodeId);
                Assert.Equal("nodeB", received[0].Message.FromNodeId);
            }

            // 3. Server closes -> NodeDisconnected raised / 服务器关闭 -> 触发 NodeDisconnected
            await serverSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

            Assert.True(await Wait.UntilAsync(() => disconnectedNode == "nodeB", 5000));
            Assert.False(transport.IsNodeConnected("nodeB"));
        }

        [Fact]
        public async Task LivePeer_MessageExceedingMaxMessageSize_ClosesConnection()
        {
            await using var peer = new ListenerPeer();
            var transport = CreateTransport("nodeA");
            transport.MaxMessageSize = 64; // tiny limit for the test / 测试用的极小限制
            transport.RegisterNode(new ClusterNode
            {
                NodeId = "nodeB",
                Address = "localhost",
                Port = peer.Port,
                TransportConfig = "/cluster"
            });

            string disconnectedNode = null;
            transport.NodeDisconnected += (_, e) => disconnectedNode = e.NodeId;

            await transport.SendAsync("nodeB", new ClusterMessage { Type = ClusterMessageType.NodeInfo });
            var serverSocket = await peer.AcceptedSocket.WaitAsync(TimeSpan.FromSeconds(5));
            await ListenerPeer.ReceiveMessageAsync(serverSocket).WaitAsync(TimeSpan.FromSeconds(5));

            // Keep reading server-side so the client's close handshake can complete
            // 服务器端保持读取，使客户端的关闭握手能够完成
            _ = Task.Run(async () =>
            {
                var buf = new byte[1024];
                try
                {
                    while (serverSocket.State == WebSocketState.Open || serverSocket.State == WebSocketState.CloseReceived)
                    {
                        var r = await serverSocket.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close)
                        {
                            await serverSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                            break;
                        }
                    }
                }
                catch
                {
                    // connection torn down
                }
            });

            // Server sends an oversized message / 服务器发送超大消息
            var big = Encoding.UTF8.GetBytes(new string('y', 512));
            await serverSocket.SendAsync(new ArraySegment<byte>(big), WebSocketMessageType.Text, true, CancellationToken.None);

            Assert.True(await Wait.UntilAsync(() => disconnectedNode == "nodeB", 5000));
            Assert.False(transport.IsNodeConnected("nodeB"));
        }

        // ---------------------------------------------------------------
        // Latency / quality helpers
        // ---------------------------------------------------------------

        [Fact]
        public async Task MeasureLatency_AndNetworkQuality_ForDisconnectedNode()
        {
            var transport = CreateTransport();

            Assert.Equal(-1, await transport.MeasureLatencyAsync("ghost"));
            Assert.Equal(0, await transport.GetNetworkQualityAsync("ghost"));
        }

        // ---------------------------------------------------------------
        // Dispose
        // ---------------------------------------------------------------

        [Fact]
        public void Dispose_IsIdempotent()
        {
            var transport = new WebSocketClusterTransport(NullLogger<WebSocketClusterTransport>.Instance, "nodeA");
            transport.Dispose();
            transport.Dispose();
        }
    }
}
