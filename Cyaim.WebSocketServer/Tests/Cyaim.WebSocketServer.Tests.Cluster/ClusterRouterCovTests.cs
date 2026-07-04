using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    /// <summary>
    /// Coverage-only tests for <see cref="ClusterRouter"/> targeting the error/edge branches
    /// left uncovered by <c>ClusterRouterTests</c>: forward failures, stream local/leader paths,
    /// register/unregister/query handler error and Redis branches, node-health check timer,
    /// route-refresh timer, transfer failures, and graceful-shutdown redirect building.
    /// Private methods and timer callbacks are invoked directly via reflection.
    /// </summary>
    [Collection("ClusterStaticState")]
    public class ClusterRouterCovTests : StaticStateGuard
    {
        private const string NodeA = "nodeA";
        private const string NodeB = "nodeB";
        private const string NodeC = "nodeC";

        private readonly FakeClusterTransport _transport = new FakeClusterTransport();
        private readonly FakeConnectionProvider _provider = new FakeConnectionProvider();
        private readonly RaftNode _raft;
        private readonly ClusterRouter _router;

        public ClusterRouterCovTests()
        {
            _raft = new RaftNode(NullLogger<RaftNode>.Instance, _transport, NodeA);
            _router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, _transport, _raft, NodeA);
            _router.SetConnectionProvider(_provider);
        }

        public override void Dispose()
        {
            try { _raft.StopAsync().GetAwaiter().GetResult(); } catch { }
            _router.Dispose();
            base.Dispose();
        }

        // ---- reflection helpers ----
        private static object Invoke(object target, string method, params object[] args)
        {
            var mi = target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(mi);
            return mi.Invoke(target, args);
        }

        private static Task InvokeAsync(object target, string method, params object[] args)
            => (Task)Invoke(target, method, args);

        private static void SetField(object target, string field, object value)
        {
            var fi = target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(fi);
            fi.SetValue(target, value);
        }

        private static T GetField<T>(object target, string field)
        {
            var fi = target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(fi);
            return (T)fi.GetValue(target);
        }

        private static ClusterMessage RegistrationMessage(string connectionId, string nodeId, string toNodeId = null,
            string endpoint = null, string remoteIp = null, int remotePort = 0, DateTime? registeredAt = null)
        {
            return new ClusterMessage
            {
                Type = ClusterMessageType.RegisterWebSocketConnection,
                FromNodeId = nodeId,
                ToNodeId = toNodeId,
                Payload = JsonSerializer.Serialize(new WebSocketConnectionRegistration
                {
                    ConnectionId = connectionId,
                    NodeId = nodeId,
                    Endpoint = endpoint,
                    RemoteIpAddress = remoteIp,
                    RemotePort = remotePort,
                    RegisteredAt = registeredAt ?? DateTime.UtcNow
                })
            };
        }

        private static ClusterMessage ForwardMessage(string connectionId, string targetNodeId, byte[] data, string payloadOverride = null)
            => new ClusterMessage
            {
                Type = ClusterMessageType.ForwardWebSocketMessage,
                FromNodeId = NodeB,
                Payload = payloadOverride ?? JsonSerializer.Serialize(new ForwardWebSocketMessage
                {
                    ConnectionId = connectionId,
                    TargetNodeId = targetNodeId,
                    Data = data,
                    MessageType = (int)WebSocketMessageType.Text
                })
            };

        private static ClusterMessage StreamMessage(string connectionId, string targetNodeId, string streamId, int index, bool last, byte[] data, string payloadOverride = null)
            => new ClusterMessage
            {
                Type = ClusterMessageType.ForwardWebSocketStream,
                FromNodeId = NodeB,
                Payload = payloadOverride ?? JsonSerializer.Serialize(new ForwardWebSocketStream
                {
                    ConnectionId = connectionId,
                    TargetNodeId = targetNodeId,
                    StreamId = streamId,
                    ChunkIndex = index,
                    IsLastChunk = last,
                    Data = data,
                    MessageType = (int)WebSocketMessageType.Binary
                })
            };

        private static ClusterMessage QueryMessage(string connectionId, string fromNodeId = NodeB)
            => new ClusterMessage
            {
                Type = ClusterMessageType.QueryWebSocketConnection,
                FromNodeId = fromNodeId,
                Payload = JsonSerializer.Serialize(new { ConnectionId = connectionId })
            };

        // =====================================================================
        // RegisterConnectionAsync: update lambda when connection moves to us
        // =====================================================================

        [Fact]
        public async Task RegisterConnection_PreviouslyCachedRemote_LogsTransferInUpdateLambda()
        {
            // Cache a remote route r1 -> NodeB (storage-less transport).
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB), NodeB);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.TryGetValue("r1", out var n) && n == NodeB));

            // Now the connection registers locally -> AddOrUpdate's update lambda runs with
            // oldValue == NodeB != _nodeId, hitting the "transferred from another node" log.
            await _router.RegisterConnectionAsync("r1", "/ws");

            Assert.Equal(NodeA, _router.ConnectionRoutes["r1"]);
        }

        // =====================================================================
        // RouteMessageAsync remote: IsNodeConnected throws, then forward paths
        // =====================================================================

        [Fact]
        public async Task RouteMessage_RemoteNodeConnectedCheckThrows_StillAttemptsForward()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB), NodeB);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.ContainsKey("r1")));

            // IsNodeConnected throws both in RouteMessageAsync's pre-check and inside
            // ForwardToNodeAsync; both catch blocks swallow and continue. Send then succeeds.
            _transport.NodeConnectedFunc = _ => throw new InvalidOperationException("check down");

            var ok = await _router.RouteMessageAsync("r1", new byte[] { 1 }, (int)WebSocketMessageType.Text, null);

            Assert.True(ok);
            Assert.Contains(_transport.Sent, s => s.Message.Type == ClusterMessageType.ForwardWebSocketMessage);
        }

        [Fact]
        public async Task RouteMessage_RemoteForwardSendThrowsInvalidOperation_ReturnsFalse()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB), NodeB);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.ContainsKey("r1")));

            _transport.NodeConnectedFunc = _ => true;
            _transport.OnSendAsync = (_, __) => throw new InvalidOperationException("send down");

            var ok = await _router.RouteMessageAsync("r1", new byte[] { 1 }, (int)WebSocketMessageType.Text, null);

            Assert.False(ok);
        }

        [Fact]
        public async Task RouteMessage_RemoteForwardSendThrowsGeneric_ReturnsFalse()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB), NodeB);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.ContainsKey("r1")));

            _transport.NodeConnectedFunc = _ => true;
            _transport.OnSendAsync = (_, __) => throw new TimeoutException("send timed out");

            var ok = await _router.RouteMessageAsync("r1", new byte[] { 1 }, (int)WebSocketMessageType.Text, null);

            Assert.False(ok);
        }

        [Fact]
        public async Task RouteMessage_StorageResolvedRemote_ForwardFails_ReturnsFalse()
        {
            _transport.SupportsRouteStorage = true;
            _transport.StoredRoutes["r1"] = NodeB;
            _transport.NodeConnectedFunc = _ => true;
            _transport.OnSendAsync = (_, __) => throw new InvalidOperationException("send down");

            var ok = await _router.RouteMessageAsync("r1", new byte[] { 1 }, (int)WebSocketMessageType.Text, null);

            Assert.False(ok);
        }

        [Fact]
        public async Task RouteMessage_BroadcastResolvedRemote_ForwardFails_ReturnsFalse()
        {
            _transport.OnBroadcastAsync = message =>
            {
                if (message.Type != ClusterMessageType.QueryWebSocketConnection)
                {
                    return;
                }
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    _transport.RaiseMessageReceived(RegistrationMessage("q1", NodeB, toNodeId: NodeA), NodeB);
                });
            };
            _transport.NodeConnectedFunc = _ => true;
            // Forward send fails only for the ForwardWebSocketMessage.
            _transport.OnSendAsync = (_, msg) =>
            {
                if (msg.Type == ClusterMessageType.ForwardWebSocketMessage)
                {
                    throw new InvalidOperationException("forward send down");
                }
            };

            var ok = await _router.RouteMessageAsync("q1", new byte[] { 1 }, (int)WebSocketMessageType.Text, null);

            Assert.False(ok);
        }

        [Fact]
        public async Task ForwardToNode_TargetNotConnected_ReturnsFalse()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB), NodeB);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.ContainsKey("r1")));

            // Directly exercise ForwardToNodeAsync's "node not connected" early-false branch.
            _transport.NodeConnectedFunc = _ => false;
            var result = await (Task<bool>)Invoke(_router, "ForwardToNodeAsync", NodeB, "r1", new byte[] { 1 }, (int)WebSocketMessageType.Text);

            Assert.False(result);
        }

        // =====================================================================
        // RouteStreamAsync: local paths
        // =====================================================================

        [Fact]
        public async Task RouteStream_LocalConnection_SendsThroughWebSocketManager()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("c1");

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("local-stream-data"));
            var ok = await _router.RouteStreamAsync("c1", stream, (int)WebSocketMessageType.Binary, 4);

            Assert.True(ok);
            Assert.NotEmpty(socket.Frames);
        }

        private sealed class ThrowingReadStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => 100;
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new IOException("read failed");
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => throw new IOException("read failed");
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => throw new IOException("read failed");
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [Fact]
        public async Task RouteStream_LocalSendThrows_ReturnsFalse()
        {
            // An open local socket, but the source stream's ReadAsync throws inside
            // WebSocketManager.SendLocalAsync, which propagates into RouteStreamAsync's local
            // try/catch (SendLocalAsync swallows socket-level send errors, but a read failure
            // upstream of any send is not swallowed).
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("c1");

            using var stream = new ThrowingReadStream();
            var ok = await _router.RouteStreamAsync("c1", stream, (int)WebSocketMessageType.Binary, 2);

            Assert.False(ok);
        }

        [Fact]
        public async Task RouteStream_LocalConnectionClosed_RemovesRoute_ReturnsFalse()
        {
            var socket = new TestWebSocket(WebSocketState.Closed);
            _provider.Connections["c1"] = socket;
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("c1");

            using var stream = new MemoryStream(new byte[] { 1 });
            var ok = await _router.RouteStreamAsync("c1", stream, (int)WebSocketMessageType.Binary);

            Assert.False(ok);
            Assert.False(_router.ConnectionRoutes.ContainsKey("c1"));
        }

        [Fact]
        public async Task RouteStream_LocalRoute_NoProvider_ReturnsFalse()
        {
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, _transport, _raft, NodeA);
            try
            {
                _transport.SupportsRouteStorage = true;
                await router.RegisterConnectionAsync("c1"); // route local, but provider never set

                using var stream = new MemoryStream(new byte[] { 1 });
                var ok = await router.RouteStreamAsync("c1", stream, (int)WebSocketMessageType.Binary);

                Assert.False(ok);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Fact]
        public async Task RouteStream_UnknownConnection_NotLeader_ReturnsFalse()
        {
            using var stream = new MemoryStream(new byte[] { 1 });
            var ok = await _router.RouteStreamAsync("ghost", stream, (int)WebSocketMessageType.Binary);

            Assert.False(ok);
        }

        [Fact]
        public async Task RouteStream_UnknownConnection_Leader_QueriesAndForwards()
        {
            // Make this single-node cluster's raft node the leader.
            Invoke(_raft, "BecomeCandidate");
            Assert.True(await Wait.UntilAsync(() => _raft.IsLeader(), 3000));

            // Storage answers the query immediately with a remote node.
            _transport.SupportsRouteStorage = true;
            _transport.StoredRoutes["s1"] = NodeB;
            _transport.NodeConnectedFunc = _ => true;

            using var stream = new MemoryStream(new byte[150]);
            var ok = await _router.RouteStreamAsync("s1", stream, (int)WebSocketMessageType.Binary, 64);

            Assert.True(ok);
            Assert.Contains(_transport.Sent, s => s.Message.Type == ClusterMessageType.ForwardWebSocketStream);
        }

        // =====================================================================
        // HandleForwardStream: local send throws / missing / no provider / malformed
        // =====================================================================

        [Fact]
        public async Task HandleForwardStream_LocalSendThrows_IsCaught()
        {
            var socket = new ThrowingWebSocket();
            _provider.Connections["c1"] = socket;

            var streamId = Guid.NewGuid().ToString("N");
            _transport.RaiseMessageReceived(StreamMessage("c1", NodeA, streamId, 0, true, new byte[] { 1, 2, 3 }), NodeB);

            // The reassembled send runs on a background Task.Run whose catch swallows the throw.
            await Task.Delay(200);
            Assert.Equal(WebSocketState.Open, socket.State);
        }

        [Fact]
        public async Task HandleForwardStream_LocalConnectionMissing_RemovesRoute()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("c1", NodeB), NodeB);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.ContainsKey("c1")));

            // Target is us, but provider has no such socket -> stale route removed.
            var streamId = Guid.NewGuid().ToString("N");
            _transport.RaiseMessageReceived(StreamMessage("c1", NodeA, streamId, 0, true, new byte[] { 1 }), NodeB);

            Assert.True(await Wait.UntilAsync(() => !_router.ConnectionRoutes.ContainsKey("c1")));
        }

        [Fact]
        public async Task HandleForwardStream_NoProvider_IsHandledGracefully()
        {
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, _transport, _raft, NodeA);
            try
            {
                var streamId = Guid.NewGuid().ToString("N");
                _transport.RaiseMessageReceived(StreamMessage("c1", NodeA, streamId, 0, true, new byte[] { 1 }), NodeB);
                await Task.Delay(100);
                // Nothing to assert beyond "no crash"; the no-provider branch was executed.
            }
            finally
            {
                router.Dispose();
            }
        }

        [Fact]
        public async Task HandleForwardStream_MalformedPayload_IsCaught()
        {
            _transport.RaiseMessageReceived(StreamMessage("c1", NodeA, "s", 0, true, new byte[] { 1 }, payloadOverride: "{bad-json"), NodeB);
            await Task.Delay(100);
            // Reached HandleForwardStream's catch block.
        }

        // =====================================================================
        // HandleForwardMessage: send returns false / no provider / malformed
        // =====================================================================

        [Fact]
        public async Task HandleForwardMessage_SendReturnsFalse_LogsFailure()
        {
            var failingProvider = new FailingSendProvider();
            var socket = new TestWebSocket();
            failingProvider.Connections["c1"] = socket;
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, _transport, _raft, NodeA);
            router.SetConnectionProvider(failingProvider);
            try
            {
                _transport.RaiseMessageReceived(ForwardMessage("c1", NodeA, new byte[] { 1 }), NodeB);
                await Task.Delay(150);
                // Send returned false; the failure branch executed.
            }
            finally
            {
                router.Dispose();
            }
        }

        [Fact]
        public async Task HandleForwardMessage_NoProvider_LogsError()
        {
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, _transport, _raft, NodeA);
            try
            {
                _transport.RaiseMessageReceived(ForwardMessage("c1", NodeA, new byte[] { 1 }), NodeB);
                await Task.Delay(100);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Fact]
        public async Task HandleForwardMessage_MalformedPayload_IsCaught()
        {
            _transport.RaiseMessageReceived(ForwardMessage("c1", NodeA, null, payloadOverride: "{bad-json"), NodeB);
            await Task.Delay(100);
        }

        // =====================================================================
        // HandleRegisterConnection: transfer, metadata-preserve, malformed
        // =====================================================================

        [Fact]
        public async Task HandleRegister_ConnectionMovesBetweenRemoteNodes_LogsTransfer()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB), NodeB);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.TryGetValue("r1", out var n) && n == NodeB));

            // Same connection re-registers from NodeC -> previousNodeId (NodeB) != NodeC.
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeC), NodeC);

            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.TryGetValue("r1", out var n) && n == NodeC));
        }

        [Fact]
        public async Task HandleRegister_ReRegisterWithMissingMetadata_PreservesExistingFields()
        {
            var ts = DateTime.UtcNow;
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB, remoteIp: "10.0.0.5", remotePort: 4321, registeredAt: ts), NodeB);
            Assert.True(await Wait.UntilAsync(() =>
            {
                var m = _router.GetConnectionMetadata("r1");
                return m != null && m.RemoteIpAddress == "10.0.0.5";
            }));

            // Re-register from the same node with empty metadata -> update lambda preserves old.
            // (DateTime.MinValue == default(DateTime); passing it explicitly forces the
            // ConnectedAt-preserve branch, unlike passing null which coalesces to UtcNow.)
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB, remoteIp: null, remotePort: 0, registeredAt: DateTime.MinValue), NodeB);

            Assert.True(await Wait.UntilAsync(() =>
            {
                var m = _router.GetConnectionMetadata("r1");
                return m != null && m.RemoteIpAddress == "10.0.0.5" && m.RemotePort == 4321 && m.ConnectedAt == ts;
            }));
        }

        [Fact]
        public async Task HandleRegister_MalformedPayload_IsCaught()
        {
            _transport.RaiseMessageReceived(new ClusterMessage
            {
                Type = ClusterMessageType.RegisterWebSocketConnection,
                FromNodeId = NodeB,
                Payload = "{bad-json"
            }, NodeB);
            await Task.Delay(100);
        }

        // =====================================================================
        // HandleUnregisterConnection: malformed payload
        // =====================================================================

        [Fact]
        public async Task HandleUnregister_MalformedPayload_IsCaught()
        {
            _transport.RaiseMessageReceived(new ClusterMessage
            {
                Type = ClusterMessageType.UnregisterWebSocketConnection,
                FromNodeId = NodeB,
                Payload = "{ }" // missing ConnectionId/NodeId properties -> GetProperty throws
            }, NodeB);
            await Task.Delay(100);
        }

        // =====================================================================
        // HandleQueryConnection: Redis-resolved local/remote, pending query, malformed
        // =====================================================================

        [Fact]
        public async Task HandleQuery_ResolvedFromStorageLocal_AddsRouteAndResponds()
        {
            var socket = new TestWebSocket();
            _provider.Connections["c1"] = socket;
            _transport.SupportsRouteStorage = true;
            _transport.StoredRoutes["c1"] = NodeA; // local per storage, not yet in memory routes

            _transport.RaiseMessageReceived(QueryMessage("c1"), NodeB);

            Assert.True(await Wait.UntilAsync(() =>
                _transport.Sent.Any(s => s.NodeId == NodeB && s.Message.Type == ClusterMessageType.RegisterWebSocketConnection)));
            Assert.Equal(NodeA, _router.ConnectionRoutes["c1"]);
        }

        [Fact]
        public async Task HandleQuery_ResolvedFromStorageRemote_RespondsWithoutCaching()
        {
            _transport.SupportsRouteStorage = true;
            _transport.StoredRoutes["c2"] = NodeC;

            _transport.RaiseMessageReceived(QueryMessage("c2"), NodeB);

            Assert.True(await Wait.UntilAsync(() =>
                _transport.Sent.Any(s => s.NodeId == NodeB && s.Message.Type == ClusterMessageType.RegisterWebSocketConnection)));
            Assert.False(_router.ConnectionRoutes.ContainsKey("c2"));
        }

        [Fact]
        public async Task HandleQuery_WithPendingLocalQuery_SetsResult()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeC), NodeC);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.ContainsKey("r1")));

            // Seed a pending query for r1, then deliver a query message: the found-branch sets it.
            var pending = GetField<ConcurrentDictionary<string, TaskCompletionSource<string>>>(_router, "_pendingConnectionQueries");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending["r1"] = tcs;

            _transport.RaiseMessageReceived(QueryMessage("r1"), NodeB);

            Assert.True(await Wait.UntilAsync(() => tcs.Task.IsCompleted));
            Assert.Equal(NodeC, await tcs.Task);
        }

        [Fact]
        public async Task HandleQuery_MalformedPayload_IsCaught()
        {
            _transport.RaiseMessageReceived(new ClusterMessage
            {
                Type = ClusterMessageType.QueryWebSocketConnection,
                FromNodeId = NodeB,
                Payload = "{bad-json"
            }, NodeB);
            await Task.Delay(100);
        }

        [Fact]
        public async Task OnTransportMessage_NullMessage_TopLevelCatchLogs()
        {
            // A null Message makes the very first line of the OnTransportMessageReceived task
            // body (`e.Message.Type` in the LogDebug) throw NullReferenceException, exercising
            // the top-level try/catch that guards the whole dispatch loop.
            // Use a transport dedicated to this router (not shared with _raft) so RaftNode's own
            // MessageReceived handler (which has no null-guard) doesn't throw synchronously first.
            var transport = new FakeClusterTransport();
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, transport, _raft, NodeA);
            try
            {
                transport.RaiseMessageReceived(null, NodeB);
                await Task.Delay(100);
            }
            finally
            {
                router.Dispose();
            }
        }

        [Fact]
        public async Task OnTransportMessage_UnknownType_HitsDefaultBranch()
        {
            _transport.RaiseMessageReceived(new ClusterMessage { Type = (ClusterMessageType)9999 }, NodeB);
            await Task.Delay(100);
        }

        // =====================================================================
        // CheckNodeHealth timer callback
        // =====================================================================

        [Fact]
        public void CheckNodeHealth_Cancelled_ReturnsImmediately()
        {
            var cts = GetField<CancellationTokenSource>(_router, "_cancellationTokenSource");
            cts.Cancel();

            Invoke(_router, "CheckNodeHealth", new object[] { null });
        }

        [Fact]
        public void CheckNodeHealth_NoKnownNodes_ReturnsImmediately()
        {
            Invoke(_router, "CheckNodeHealth", new object[] { null });
        }

        [Fact]
        public async Task CheckNodeHealth_NodeDisconnectedWithConnections_TriggersCleanup()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB), NodeB);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.ContainsKey("r1")));

            _transport.NodeConnectedFunc = _ => false;
            Invoke(_router, "CheckNodeHealth", new object[] { null });

            // Cleanup fires OnNodeDisconnected -> route eventually removed.
            Assert.True(await Wait.UntilAsync(() => !_router.ConnectionRoutes.ContainsKey("r1")));
        }

        [Fact]
        public async Task CheckNodeHealth_IsNodeConnectedThrows_IsCaught()
        {
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB), NodeB);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.ContainsKey("r1")));

            _transport.NodeConnectedFunc = _ => throw new InvalidOperationException("health down");
            Invoke(_router, "CheckNodeHealth", new object[] { null });
        }

        // =====================================================================
        // TransferConnectionsAsync: empty short-circuit + broadcast failure catch
        // =====================================================================

        [Fact]
        public async Task TransferConnections_EmptyList_ReturnsImmediately()
        {
            await InvokeAsync(_router, "TransferConnectionsAsync", new List<string>(), NodeB);
        }

        [Fact]
        public async Task TransferConnections_BroadcastThrows_IsCaught()
        {
            _transport.OnBroadcastAsync = _ => throw new InvalidOperationException("broadcast down");
            await InvokeAsync(_router, "TransferConnectionsAsync", new List<string> { "r1" }, NodeB);
        }

        // =====================================================================
        // RefreshConnectionRoutes timer + RefreshConnectionRoutesAsync
        // =====================================================================

        [Fact]
        public async Task RefreshRoutes_Cancelled_ReturnsImmediately()
        {
            var cts = GetField<CancellationTokenSource>(_router, "_cancellationTokenSource");
            cts.Cancel();

            await InvokeAsync(_router, "RefreshConnectionRoutesAsync");
        }

        [Fact]
        public async Task RefreshRoutes_NoLocalConnections_ReturnsImmediately()
        {
            await InvokeAsync(_router, "RefreshConnectionRoutesAsync");
        }

        [Fact]
        public async Task RefreshRoutes_RefreshSucceeds_CountsRefreshed()
        {
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("c1"); // StoredRoutes["c1"] set -> refresh returns true

            await InvokeAsync(_router, "RefreshConnectionRoutesAsync");
        }

        [Fact]
        public async Task RefreshRoutes_RefreshFalse_ReStoresViaMetadata()
        {
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("c1", "/ws", "1.2.3.4", 99);

            // Force RefreshConnectionRouteAsync to return false (route not present in store) while
            // keeping the in-memory route + metadata, so the re-store-via-metadata branch runs.
            _transport.StoredRoutes.TryRemove("c1", out _);

            await InvokeAsync(_router, "RefreshConnectionRoutesAsync");

            // Re-store branch put the route back into storage.
            Assert.True(_transport.StoredRoutes.ContainsKey("c1"));
        }

        [Fact]
        public async Task RefreshRoutes_RefreshThrows_CountsFailed()
        {
            var throwingTransport = new ConfigurableClusterTransport
            {
                RefreshConnectionRouteAsyncImpl = (_, __) => throw new InvalidOperationException("refresh down")
            };
            var raft = new RaftNode(NullLogger<RaftNode>.Instance, throwingTransport, NodeA);
            var router = new ClusterRouter(NullLogger<ClusterRouter>.Instance, throwingTransport, raft, NodeA);
            router.SetConnectionProvider(_provider);
            try
            {
                await router.RegisterConnectionAsync("c1"); // storage-less -> local route recorded
                await InvokeAsync(router, "RefreshConnectionRoutesAsync");
            }
            finally
            {
                router.Dispose();
                try { await raft.StopAsync(); } catch { }
            }
        }

        [Fact]
        public async Task RefreshRoutes_TimerWrapper_RunsWithoutThrowing()
        {
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("c1");

            // Fire the timer callback wrapper (Task.Run fire-and-forget).
            Invoke(_router, "RefreshConnectionRoutes", new object[] { null });
            await Task.Delay(100);
        }

        // =====================================================================
        // GracefulShutdown: redirect URL building + normal-delay batch branch
        // =====================================================================

        [Fact]
        public async Task GracefulShutdown_WithRemoteNode_BuildsRedirectUrl_AndClosesLocal()
        {
            // A remote node NodeB holds a connection and is reachable -> optimal transfer target.
            _transport.RaiseMessageReceived(RegistrationMessage("r1", NodeB), NodeB);
            Assert.True(await Wait.UntilAsync(() => _router.ConnectionRoutes.ContainsKey("r1")));
            _transport.NodeConnectedFunc = _ => true;

            // A local connection to be closed with a redirect.
            var socket = new TestWebSocket();
            MvcChannelHandler.Clients["g1"] = socket;
            _provider.Connections["g1"] = socket;
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("g1", "/ws");

            var option = new ClusterOption { Nodes = new[] { "ws://127.0.0.1:5000/nodeB" } };
            await _router.GracefulShutdownAsync(option);

            Assert.Equal(WebSocketState.Closed, socket.State);
            Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, socket.LastCloseStatus);
            Assert.Contains("redirect", socket.LastCloseDescription);
        }

        [Fact]
        public async Task GracefulShutdown_ModeratelySlowClose_HitsNormalDelayBranch()
        {
            // A ~120ms close puts batchElapsedMs into the (50ms, 500ms) "normal delay" window,
            // exercising the else-branch of the adaptive per-batch delay logic.
            var socket = new DelayCloseWebSocket(120);
            MvcChannelHandler.Clients["g1"] = socket;
            _provider.Connections["g1"] = socket;
            _transport.SupportsRouteStorage = true;
            await _router.RegisterConnectionAsync("g1", "/ws");

            await _router.GracefulShutdownAsync(new ClusterOption());

            Assert.Equal(WebSocketState.Closed, socket.State);
        }
    }
}
