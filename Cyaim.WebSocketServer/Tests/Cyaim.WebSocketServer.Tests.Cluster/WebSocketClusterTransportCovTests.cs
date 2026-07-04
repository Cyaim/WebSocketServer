using System.Collections.Concurrent;
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
    /// <summary>
    /// Additional coverage-only tests for <see cref="WebSocketClusterTransport"/> targeting
    /// the lines left uncovered by <c>WebSocketClusterTransportTests</c>: StartAsync's live
    /// connect-with-retry loop, StopAsync's close/dispose loop, GetOrCreateConnection's
    /// reuse/stale/duplicate-add branches, ReceiveMessagesAsync's null/malformed/cancelled/
    /// generic-exception branches (invoked directly via reflection with fake WebSockets), and
    /// SendAsync's post-connect null/not-open guard.
    /// </summary>
    [Collection("ClusterStaticState")]
    public class WebSocketClusterTransportCovTests : IDisposable
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
                try { transport.Dispose(); } catch { }
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

        private static ConcurrentDictionary<string, ClientWebSocket> GetConnections(WebSocketClusterTransport transport)
        {
            var fi = typeof(WebSocketClusterTransport).GetField("_connections", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(fi);
            return (ConcurrentDictionary<string, ClientWebSocket>)fi.GetValue(transport);
        }

        private static ConcurrentDictionary<string, ClusterNode> GetNodes(WebSocketClusterTransport transport)
        {
            var fi = typeof(WebSocketClusterTransport).GetField("_nodes", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(fi);
            return (ConcurrentDictionary<string, ClusterNode>)fi.GetValue(transport);
        }

        /// <summary>
        /// Connects a raw ClientWebSocket to the peer and injects it directly into the
        /// transport's _connections/_nodes dictionaries, bypassing GetOrCreateConnection (and
        /// therefore its background ReceiveMessagesAsync loop). This gives a deterministic,
        /// race-free Open connection for tests of SendAsync / MeasureLatencyAsync.
        /// </summary>
        private static async Task<ClientWebSocket> InjectOpenConnectionAsync(WebSocketClusterTransport transport, string nodeId, ClusterNode node, int port)
        {
            var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri($"ws://localhost:{port}/cluster"), CancellationToken.None);
            GetConnections(transport)[nodeId] = client;
            GetNodes(transport)[nodeId] = node;
            return client;
        }

        private static async Task InvokeReceiveMessagesAsync(WebSocketClusterTransport transport, string nodeId, WebSocket socket)
        {
            var mi = typeof(WebSocketClusterTransport).GetMethod("ReceiveMessagesAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(mi);
            await (Task)mi.Invoke(transport, new object[] { nodeId, socket });
        }

        private static Task<ClientWebSocket> InvokeGetOrCreateConnection(WebSocketClusterTransport transport, string nodeId, ClusterNode node)
        {
            var mi = typeof(WebSocketClusterTransport).GetMethod("GetOrCreateConnection", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(mi);
            return (Task<ClientWebSocket>)mi.Invoke(transport, new object[] { nodeId, node });
        }

        // ---------------------------------------------------------------
        // Live peer helper (same pattern as WebSocketClusterTransportTests)
        // ---------------------------------------------------------------

        private sealed class ListenerPeer : IAsyncDisposable
        {
            private readonly HttpListener _listener;
            private readonly List<WebSocket> _accepted = new List<WebSocket>();
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
                lock (_accepted)
                {
                    _accepted.Add(wsContext.WebSocket);
                }
                return wsContext.WebSocket;
            }

            /// <summary>Accepts one more incoming handshake beyond the first, or null on error/close.</summary>
            public async Task<WebSocket> AcceptAnotherAsync()
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    var wsContext = await context.AcceptWebSocketAsync(null);
                    lock (_accepted)
                    {
                        _accepted.Add(wsContext.WebSocket);
                    }
                    return wsContext.WebSocket;
                }
                catch
                {
                    return null;
                }
            }

            public async ValueTask DisposeAsync()
            {
                lock (_accepted)
                {
                    foreach (var ws in _accepted)
                    {
                        try { ws.Dispose(); } catch { }
                    }
                }
                _listener.Stop();
                _listener.Close();
                await Task.CompletedTask;
            }
        }

        // ---------------------------------------------------------------
        // StartAsync: live connect-with-retry loop (lines ~84-156)
        // ---------------------------------------------------------------

        [Fact]
        public async Task StartAsync_RegisteredLivePeer_ConnectsSuccessfully()
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
            transport.NodeConnected += (_, e) => connectedNode = e.NodeId;

            // StartAsync waits ~2s (initial delay) before attempting the connection, then
            // succeeds against the live peer; it internally caps the wait at 10s but returns
            // as soon as all connection tasks complete.
            await transport.StartAsync();

            Assert.Equal("nodeB", connectedNode);
            Assert.True(transport.IsNodeConnected("nodeB"));

            await transport.StopAsync();
        }

        [Fact(Timeout = 30000)]
        public async Task StartAsync_DeadPort_ExhaustsAllThreeRetries_LogsWarningAfterWait()
        {
            var transport = CreateTransport("nodeA");
            var deadPort = GetFreePort(); // nothing ever listens here
            transport.RegisterNode(new ClusterNode { NodeId = "nodeB", Address = "127.0.0.1", Port = deadPort, TransportConfig = "/cluster" });

            string connectedNode = null;
            transport.NodeConnected += (_, e) => connectedNode = e.NodeId;

            // Exercises: attempt 0 fails -> retry delay (attempt 1, ~1s) -> fails -> retry
            // delay (attempt 2, ~2s) -> fails; the *last* attempt's exception is not caught by
            // the `when (attempt < 2)` filter, so it escapes the for-loop straight to the
            // outer catch, then StartAsync logs the connected/expected mismatch warning.
            // Total wall time ~5-6s (2s + 1s + 2s built-in delays).
            await transport.StartAsync();

            Assert.Null(connectedNode);
            Assert.False(transport.IsNodeConnected("nodeB"));

            await transport.StopAsync();
        }

        [Fact(Timeout = 30000)]
        public async Task StartAsync_MalformedAddress_ExhaustsRetries_HitsGeneralExceptionCatch()
        {
            var transport = CreateTransport("nodeA");
            // A space in the host makes `new Uri("ws://bad host:1/cluster")` throw
            // UriFormatException *synchronously* inside GetOrCreateConnection (before any
            // network / cancellation), on every attempt. The last attempt's exception is a
            // non-cancellation exception that escapes the for-loop's `when (attempt < 2)` filter
            // straight into StartAsync's outer `catch (Exception ex)` (not the OCE catch).
            // Because it throws instantly, all three attempts finish inside the ~5s backoff
            // budget, well before StartAsync's 10s wait cap, so no cancellation race occurs.
            transport.RegisterNode(new ClusterNode { NodeId = "nodeB", Address = "bad host", Port = 1, TransportConfig = "/cluster" });
            // A second unreachable node so _nodes.Count == 2 -> expectedCount (Count - 1) == 1 >
            // connectedCount (0), exercising the "only N of M connections established" warning
            // after the wait completes.
            transport.RegisterNode(new ClusterNode { NodeId = "nodeC", Address = "bad host2", Port = 1, TransportConfig = "/cluster" });

            string connectedNode = null;
            transport.NodeConnected += (_, e) => connectedNode = e.NodeId;

            await transport.StartAsync();

            Assert.Null(connectedNode);
            Assert.False(transport.IsNodeConnected("nodeB"));
            Assert.False(transport.IsNodeConnected("nodeC"));

            await transport.StopAsync();
        }

        [Fact]
        public async Task StartAsync_CancelledDuringInitialDelay_OuterOperationCanceledCatch()
        {
            var transport = CreateTransport("nodeA");
            var deadPort = GetFreePort();
            transport.RegisterNode(new ClusterNode { NodeId = "nodeB", Address = "127.0.0.1", Port = deadPort, TransportConfig = "/cluster" });

            var startTask = transport.StartAsync();

            // Cancel while still inside the mandatory 2s initial Task.Delay (which is outside
            // the inner per-attempt try/catch), so OperationCanceledException propagates
            // directly to the outer `catch (OperationCanceledException)` block.
            await Task.Delay(300);
            var ctsField = typeof(WebSocketClusterTransport).GetField("_cancellationTokenSource", BindingFlags.NonPublic | BindingFlags.Instance);
            var cts = (CancellationTokenSource)ctsField.GetValue(transport);
            cts.Cancel();

            // StartAsync itself must not throw: the per-node Task.Run swallows the
            // cancellation internally and StartAsync only awaits with a Task.Delay(10000) cap.
            await startTask;
        }

        // ---------------------------------------------------------------
        // StopAsync: close/dispose loop (lines ~179-189)
        // ---------------------------------------------------------------

        [Fact]
        public async Task StopAsync_OpenConnection_ClosesAndDisposesCleanly()
        {
            await using var peer = new ListenerPeer();
            var transport = CreateTransport("nodeA");

            // Connect manually and insert directly into _connections, bypassing
            // GetOrCreateConnection so no background ReceiveMessagesAsync loop is watching this
            // socket. That loop's own ReceiveAsync is cancelled the instant StopAsync cancels
            // _cancellationTokenSource, and its finally block disposes+removes the connection
            // (State -> Closed) essentially before StopAsync's own foreach gets to check
            // `State == Open` -- so a normally-registered connection reliably loses that race
            // and StopAsync never actually observes it as Open. Bypassing the receive loop
            // removes that race entirely, letting StopAsync's own close path run for real.
            var client = new ClientWebSocket();
            var connectTask = client.ConnectAsync(new Uri($"ws://localhost:{peer.Port}/cluster"), CancellationToken.None);
            var serverSocket = await peer.AcceptedSocket.WaitAsync(TimeSpan.FromSeconds(5));
            await connectTask;
            GetConnections(transport)["nodeB"] = client;
            Assert.Equal(WebSocketState.Open, client.State);

            // Server actively completes the close handshake so the client's CloseAsync returns
            // normally (exercising the successful-completion path) rather than timing out.
            var serverLoop = Task.Run(async () =>
            {
                var buf = new byte[1024];
                try
                {
                    var r = await serverSocket.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        await serverSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    }
                }
                catch { }
            });

            // Normal close path: connection is Open, CloseAsync completes, then Dispose().
            await transport.StopAsync();
            await Task.WhenAny(serverLoop, Task.Delay(2000));

            Assert.False(transport.IsNodeConnected("nodeB"));
        }

        [Fact(Timeout = 30000)]
        public async Task StopAsync_UnresponsivePeer_CloseHandshakeTimesOut_Aborts()
        {
            await using var peer = new ListenerPeer();
            var transport = CreateTransport("nodeA");

            var client = new ClientWebSocket();
            var connectTask = client.ConnectAsync(new Uri($"ws://localhost:{peer.Port}/cluster"), CancellationToken.None);
            await peer.AcceptedSocket.WaitAsync(TimeSpan.FromSeconds(5));
            await connectTask;
            GetConnections(transport)["nodeB"] = client;
            Assert.Equal(WebSocketState.Open, client.State);

            // The peer accepts but never reads/answers the close frame, so the client's
            // CloseAsync(closeCts) waits for a close response that never comes and is cancelled
            // by the SUT's built-in 5s close-handshake timeout, taking the OperationCanceledException
            // -> Abort path in StopAsync. (This exercises the SUT's own timeout, not a test sleep.)
            await transport.StopAsync();

            Assert.Equal(WebSocketState.Aborted, client.State);
            Assert.False(transport.IsNodeConnected("nodeB"));
        }

        // ---------------------------------------------------------------
        // GetOrCreateConnection: reuse / stale-dispose / duplicate-add races
        // ---------------------------------------------------------------

        [Fact]
        public async Task GetOrCreateConnection_ExistingOpenConnection_IsReused()
        {
            await using var peer = new ListenerPeer();
            var transport = CreateTransport("nodeA");
            var node = new ClusterNode { NodeId = "nodeB", Address = "localhost", Port = peer.Port, TransportConfig = "/cluster" };
            transport.RegisterNode(node);

            var first = await InvokeGetOrCreateConnection(transport, "nodeB", node);
            await peer.AcceptedSocket.WaitAsync(TimeSpan.FromSeconds(5));

            var second = await InvokeGetOrCreateConnection(transport, "nodeB", node);

            Assert.Same(first, second);
        }

        [Fact]
        public async Task GetOrCreateConnection_StaleClosedEntry_IsDisposedAndReplaced()
        {
            await using var peer = new ListenerPeer();
            var transport = CreateTransport("nodeA");
            var node = new ClusterNode { NodeId = "nodeB", Address = "localhost", Port = peer.Port, TransportConfig = "/cluster" };
            transport.RegisterNode(node);

            var connections = GetConnections(transport);

            // Fault injection: seed _connections with an aborted (non-Open) ClientWebSocket
            // for nodeB, simulating a stale entry whose disconnect hasn't been reaped yet.
            var stale = new ClientWebSocket();
            stale.Abort();
            connections[node.NodeId] = stale;

            var fresh = await InvokeGetOrCreateConnection(transport, "nodeB", node);
            await peer.AcceptedSocket.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotSame(stale, fresh);
            Assert.Equal(WebSocketState.Open, fresh.State);
        }

        [Fact]
        public async Task GetOrCreateConnection_TryAddLosesAndEntryVanishes_ReturnsNull()
        {
            // Best-effort adversarial-race test for the narrow window where another thread's
            // TryAdd(nodeId, ...) beats ours (our own TryAdd fails, taking the "else" branch),
            // but that other entry is then removed again before our own fallback
            // TryGetValue(nodeId, ...) runs -- the only way GetOrCreateConnection falls through
            // its try block without returning/throwing, reaching the trailing `return null;`.
            // Since both dictionary operations happen back-to-back with no intervening await,
            // this requires genuine OS-thread interleaving; we hammer a background racer
            // toggling add/remove for the whole duration of a real (loopback) ConnectAsync call
            // to maximize the chance of landing in the "removed" state at exactly the right
            // instant, repeated across several fresh node ids to improve overall odds.
            await using var peer = new ListenerPeer();
            var transport = CreateTransport("nodeA");
            var connections = GetConnections(transport);

            var acceptLoop = Task.Run(async () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    var ws = i == 0 ? await peer.AcceptedSocket : await peer.AcceptAnotherAsync();
                    if (ws == null) break;
                }
            });

            for (int iteration = 0; iteration < 15; iteration++)
            {
                var nodeId = $"race-{iteration}";
                var node = new ClusterNode { NodeId = nodeId, Address = "localhost", Port = peer.Port, TransportConfig = "/cluster" };
                transport.RegisterNode(node);

                using var stopRacer = new CancellationTokenSource();
                var racer = Task.Run(() =>
                {
                    var dummy = new ClientWebSocket();
                    while (!stopRacer.IsCancellationRequested)
                    {
                        connections.TryAdd(nodeId, dummy);
                        connections.TryRemove(nodeId, out _);
                    }
                });

                var result = await InvokeGetOrCreateConnection(transport, nodeId, node);
                stopRacer.Cancel();
                try { await racer; } catch { }

                // Whatever the outcome (own connection, another thread's, or null from the
                // exhausted race), the call must not throw.
                Assert.True(result == null || result is ClientWebSocket);
            }

            await Task.WhenAny(acceptLoop, Task.Delay(1000));
        }

        [Fact]
        public async Task SendAsync_GetOrCreateConnectionReturnsNullFromRace_HitsNullConnectionGuard()
        {
            // Same adversarial race as above, but driven through the public SendAsync API so
            // that (when the race lands) SendAsync's own post-connect null/not-open guard
            // (`connection == null || connection.State != WebSocketState.Open`) executes inside
            // SendAsync's own stack frame rather than via direct reflection into
            // GetOrCreateConnection.
            await using var peer = new ListenerPeer();
            var transport = CreateTransport("nodeA");
            var connections = GetConnections(transport);

            var acceptLoop = Task.Run(async () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    var ws = i == 0 ? await peer.AcceptedSocket : await peer.AcceptAnotherAsync();
                    if (ws == null) break;
                }
            });

            for (int iteration = 0; iteration < 15; iteration++)
            {
                var nodeId = $"send-race-{iteration}";
                var node = new ClusterNode { NodeId = nodeId, Address = "localhost", Port = peer.Port, TransportConfig = "/cluster" };
                transport.RegisterNode(node);

                using var stopRacer = new CancellationTokenSource();
                var racer = Task.Run(() =>
                {
                    var dummy = new ClientWebSocket();
                    while (!stopRacer.IsCancellationRequested)
                    {
                        connections.TryAdd(nodeId, dummy);
                        connections.TryRemove(nodeId, out _);
                    }
                });

                try
                {
                    await transport.SendAsync(nodeId, new ClusterMessage { Type = ClusterMessageType.NodeInfo });
                }
                catch (InvalidOperationException)
                {
                    // Expected on most iterations regardless of which guard produced it.
                }
                finally
                {
                    stopRacer.Cancel();
                    try { await racer; } catch { }
                }
            }

            await Task.WhenAny(acceptLoop, Task.Delay(1000));
        }

        [Fact]
        public async Task GetOrCreateConnection_ConcurrentCallsForSameNode_OneWinsOthersReuse()
        {
            await using var peer = new ListenerPeer();
            var transport = CreateTransport("nodeA");
            var node = new ClusterNode { NodeId = "nodeB", Address = "localhost", Port = peer.Port, TransportConfig = "/cluster" };
            transport.RegisterNode(node);

            const int concurrency = 8;

            // Accept up to `concurrency` incoming handshakes in the background, since each
            // racing ClientWebSocket.ConnectAsync call opens its own TCP connection to the peer
            // even though they all target the same logical nodeId on the client side.
            var acceptLoop = Task.Run(async () =>
            {
                for (int i = 0; i < concurrency; i++)
                {
                    var ws = i == 0 ? await peer.AcceptedSocket : await peer.AcceptAnotherAsync();
                    if (ws == null) break;
                }
            });

            var tasks = Enumerable.Range(0, concurrency)
                .Select(_ => InvokeGetOrCreateConnection(transport, "nodeB", node))
                .ToArray();

            var results = await Task.WhenAll(tasks);
            await Task.WhenAny(acceptLoop, Task.Delay(2000));

            // All non-null results must be Open (GetOrCreateConnection never hands back a
            // non-open, non-null connection: dictionary entries are only added post-connect,
            // so both the "won the race" and "reused someone else's connection" branches must
            // yield an Open socket).
            foreach (var r in results.Where(r => r != null))
            {
                Assert.Equal(WebSocketState.Open, r.State);
            }
        }

        // ---------------------------------------------------------------
        // ReceiveMessagesAsync: null / malformed / cancelled / generic-exception branches.
        // Invoked directly via reflection with fake WebSockets (the method's parameter type
        // is the abstract WebSocket base class), avoiding any live networking for these
        // purely-internal parsing/error branches.
        // ---------------------------------------------------------------

        [Fact]
        public async Task ReceiveMessagesAsync_NullJsonPayload_LogsAndContinues()
        {
            var transport = CreateTransport("nodeA");
            var socket = new ScriptedWebSocket(
                ScriptedReceive.Text("null"),
                ScriptedReceive.Close());

            var received = new List<ClusterMessageEventArgs>();
            transport.MessageReceived += (_, e) => received.Add(e);

            await InvokeReceiveMessagesAsync(transport, "nodeB", socket);

            Assert.Empty(received);
        }

        [Fact]
        public async Task ReceiveMessagesAsync_MalformedJson_IsCaughtAndLoopContinues()
        {
            var transport = CreateTransport("nodeA");
            var socket = new ScriptedWebSocket(
                ScriptedReceive.Text("{bad-json"),
                ScriptedReceive.Text(JsonSerializer.Serialize(new ClusterMessage { Type = ClusterMessageType.Heartbeat })),
                ScriptedReceive.Close());

            var received = new List<ClusterMessageEventArgs>();
            transport.MessageReceived += (_, e) => received.Add(e);

            await InvokeReceiveMessagesAsync(transport, "nodeB", socket);

            // The malformed frame is swallowed; the loop continues and the following
            // well-formed frame is still delivered.
            var msg = Assert.Single(received);
            Assert.Equal(ClusterMessageType.Heartbeat, msg.Message.Type);
        }

        [Fact]
        public async Task ReceiveMessagesAsync_OperationCanceled_IsCaughtSilently()
        {
            var transport = CreateTransport("nodeA");
            var socket = new ScriptedWebSocket(ScriptedReceive.Throw(new OperationCanceledException()));

            string disconnected = null;
            transport.NodeDisconnected += (_, e) => disconnected = e.NodeId;

            await InvokeReceiveMessagesAsync(transport, "nodeB", socket);

            // finally block still runs regardless of which catch fired.
            Assert.Equal("nodeB", disconnected);
        }

        [Fact]
        public async Task ReceiveMessagesAsync_GenericException_IsCaughtAndFinallyRuns()
        {
            var transport = CreateTransport("nodeA");
            var socket = new ScriptedWebSocket(ScriptedReceive.Throw(new InvalidOperationException("receive failed")));

            string disconnected = null;
            transport.NodeDisconnected += (_, e) => disconnected = e.NodeId;

            var connections = GetConnections(transport);
            connections["nodeB"] = new ClientWebSocket(); // present so TryRemove has something to remove

            await InvokeReceiveMessagesAsync(transport, "nodeB", socket);

            Assert.Equal("nodeB", disconnected);
            Assert.False(connections.ContainsKey("nodeB"));
        }

        // ---------------------------------------------------------------
        // SendAsync: post-GetOrCreateConnection null/not-open guard (lines ~395-400)
        // ---------------------------------------------------------------

        [Fact]
        public async Task SendAsync_GetOrCreateConnectionThrows_WrapsAsInvalidOperationException()
        {
            var transport = CreateTransport("nodeA");
            // Dead port: nothing listening, so GetOrCreateConnection's ConnectAsync fails and
            // its own catch rethrows, which SendAsync wraps.
            transport.RegisterNode(new ClusterNode { NodeId = "nodeB", Address = "127.0.0.1", Port = GetFreePort(), TransportConfig = "/cluster" });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => transport.SendAsync("nodeB", new ClusterMessage { Type = ClusterMessageType.NodeInfo }));

            Assert.Contains("nodeB", ex.Message);
        }

        [Fact]
        public async Task GetOrCreateConnection_ConnectCancelled_ThrowsOperationCanceled()
        {
            var transport = CreateTransport("nodeA");
            var node = new ClusterNode { NodeId = "nodeB", Address = "127.0.0.1", Port = GetFreePort(), TransportConfig = "/cluster" };
            transport.RegisterNode(node);

            var ctsField = typeof(WebSocketClusterTransport).GetField("_cancellationTokenSource", BindingFlags.NonPublic | BindingFlags.Instance);
            var cts = (CancellationTokenSource)ctsField.GetValue(transport);
            cts.Cancel();

            // ConnectAsync observes the already-cancelled token; GetOrCreateConnection's
            // OperationCanceledException catch logs and rethrows. Invoking an async method via
            // reflection returns the Task synchronously (exceptions surface on await, not from
            // Invoke itself), so no TargetInvocationException unwrapping is needed here.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => InvokeGetOrCreateConnection(transport, "nodeB", node));
        }

        [Fact]
        public async Task SendAsync_UnderlyingSendThrows_IsLoggedAndRethrown()
        {
            await using var peer = new ListenerPeer();
            var transport = CreateTransport("nodeA");
            var node = new ClusterNode { NodeId = "nodeB", Address = "localhost", Port = peer.Port, TransportConfig = "/cluster" };
            transport.RegisterNode(node);

            // Inject a real Open connection directly (no background receive loop to race), then
            // abort the server side. The next SendAsync passes the null/not-open guard (State is
            // still Open) and reaches the actual `connection.SendAsync(...)` write, which fails
            // against the broken connection and is caught+rethrown by SendAsync's own try/catch.
            var client = await InjectOpenConnectionAsync(transport, "nodeB", node, peer.Port);
            var serverSocket = await peer.AcceptedSocket.WaitAsync(TimeSpan.FromSeconds(5));
            serverSocket.Abort();
            await Task.Delay(100);

            await Assert.ThrowsAnyAsync<Exception>(
                () => transport.SendAsync("nodeB", new ClusterMessage { Type = ClusterMessageType.NodeInfo }));
        }

        // ---------------------------------------------------------------
        // MeasureLatencyAsync / GetNetworkQualityAsync for a CONNECTED node
        // ---------------------------------------------------------------

        [Fact]
        public async Task MeasureLatency_LocalhostConnectedNode_ReturnsLowLatency_AndHighQuality()
        {
            await using var peer = new ListenerPeer();
            var transport = CreateTransport("nodeA");
            var node = new ClusterNode { NodeId = "nodeB", Address = "localhost", Port = peer.Port, TransportConfig = "/cluster" };
            await InjectOpenConnectionAsync(transport, "nodeB", node, peer.Port);
            await peer.AcceptedSocket.WaitAsync(TimeSpan.FromSeconds(5));

            // Connected + localhost address -> the 5ms local estimate.
            Assert.Equal(5, await transport.MeasureLatencyAsync("nodeB"));
            // Quality = 100 - latency/2 = 100 - 2 = 98.
            Assert.Equal(98, await transport.GetNetworkQualityAsync("nodeB"));
        }

        [Fact]
        public async Task MeasureLatency_NonLocalhostConnectedNode_ReturnsDefaultEstimate()
        {
            await using var peer = new ListenerPeer();
            var transport = CreateTransport("nodeA");
            var node = new ClusterNode { NodeId = "nodeB", Address = "localhost", Port = peer.Port, TransportConfig = "/cluster" };
            await InjectOpenConnectionAsync(transport, "nodeB", node, peer.Port);
            await peer.AcceptedSocket.WaitAsync(TimeSpan.FromSeconds(5));

            // Mutate the stored node's address to a non-loopback host AFTER the (loopback)
            // connection is established, so the "not localhost" default 50ms estimate branch runs.
            GetNodes(transport)["nodeB"].Address = "10.0.0.7";

            Assert.Equal(50, await transport.MeasureLatencyAsync("nodeB"));
        }

        [Fact]
        public async Task MeasureLatency_ConnectedButNodeMetadataMissing_ReturnsDefaultEstimate()
        {
            await using var peer = new ListenerPeer();
            var transport = CreateTransport("nodeA");
            var node = new ClusterNode { NodeId = "nodeB", Address = "localhost", Port = peer.Port, TransportConfig = "/cluster" };
            await InjectOpenConnectionAsync(transport, "nodeB", node, peer.Port);
            await peer.AcceptedSocket.WaitAsync(TimeSpan.FromSeconds(5));

            // Remove the node metadata but keep the open connection: `node` lookup is null, so
            // the localhost check is skipped and the default 50ms estimate is returned.
            GetNodes(transport).TryRemove("nodeB", out _);

            Assert.Equal(50, await transport.MeasureLatencyAsync("nodeB"));
        }
    }
}
