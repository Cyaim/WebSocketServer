using System.Collections.Concurrent;
using System.Net.WebSockets;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Infrastructure.Metrics;

namespace Cyaim.WebSocketServer.Tests.Cluster
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
    /// Fake WebSocket that records every SendAsync call and its close status.
    /// Modeled on the conventions in Tests/Cyaim.WebSocketServer.Tests/TestInfrastructure.cs.
    /// </summary>
    public sealed class TestWebSocket : WebSocket
    {
        private readonly object _sync = new object();
        private readonly List<CapturedFrame> _frames = new List<CapturedFrame>();
        private WebSocketState _state;

        public TestWebSocket(WebSocketState state = WebSocketState.Open)
        {
            _state = state;
        }

        public IReadOnlyList<CapturedFrame> Frames
        {
            get { lock (_sync) { return _frames.ToArray(); } }
        }

        /// <summary>All payload bytes concatenated in send order.</summary>
        public byte[] AllPayloadBytes
        {
            get { lock (_sync) { return _frames.SelectMany(f => f.Payload).ToArray(); } }
        }

        public WebSocketCloseStatus? LastCloseStatus { get; private set; }
        public string LastCloseDescription { get; private set; }

        public void SetState(WebSocketState state) => _state = state;

        public override WebSocketCloseStatus? CloseStatus => LastCloseStatus;
        public override string CloseStatusDescription => LastCloseDescription;
        public override WebSocketState State => _state;
        public override string SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            LastCloseStatus = closeStatus;
            LastCloseDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            LastCloseStatus = closeStatus;
            LastCloseDescription = statusDescription;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException("TestWebSocket does not support receiving.");

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _frames.Add(new CapturedFrame(buffer.ToArray(), messageType, endOfMessage));
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// WebSocket whose SendAsync always throws — used to exercise error branches.
    /// </summary>
    public sealed class ThrowingWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => throw new InvalidOperationException("send failed");
    }

    /// <summary>
    /// In-memory IWebSocketConnectionProvider backed by a dictionary of fake sockets.
    /// </summary>
    public sealed class FakeConnectionProvider : IWebSocketConnectionProvider
    {
        public ConcurrentDictionary<string, WebSocket> Connections { get; } = new ConcurrentDictionary<string, WebSocket>();

        private readonly object _sync = new object();
        private readonly List<(string ConnectionId, byte[] Data, WebSocketMessageType MessageType)> _sent =
            new List<(string, byte[], WebSocketMessageType)>();

        public IReadOnlyList<(string ConnectionId, byte[] Data, WebSocketMessageType MessageType)> Sent
        {
            get { lock (_sync) { return _sent.ToArray(); } }
        }

        public WebSocket GetConnection(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId))
            {
                return null;
            }
            return Connections.TryGetValue(connectionId, out var ws) ? ws : null;
        }

        public async Task<bool> SendAsync(string connectionId, byte[] data, WebSocketMessageType messageType, CancellationToken cancellationToken = default)
        {
            var ws = GetConnection(connectionId);
            if (ws == null || ws.State != WebSocketState.Open)
            {
                return false;
            }

            await ws.SendAsync(new ArraySegment<byte>(data), messageType, true, cancellationToken);
            lock (_sync)
            {
                _sent.Add((connectionId, data, messageType));
            }
            return true;
        }
    }

    /// <summary>
    /// In-memory IClusterTransport:
    /// - records sent/broadcast messages,
    /// - lets tests raise MessageReceived / NodeConnected / NodeDisconnected,
    /// - has a controllable route-storage support flag (Store/Get/Remove/RefreshConnectionRouteAsync),
    /// - exposes a public RegisterNode(ClusterNode) so ClusterManager's reflection fallback finds it.
    /// </summary>
    public sealed class FakeClusterTransport : IClusterTransport
    {
        private readonly object _sync = new object();
        private readonly List<(string NodeId, ClusterMessage Message)> _sent = new List<(string, ClusterMessage)>();
        private readonly List<ClusterMessage> _broadcasts = new List<ClusterMessage>();
        private readonly List<ClusterNode> _registeredNodes = new List<ClusterNode>();

        /// <summary>Whether Store/Get/Remove/Refresh route calls are supported (Redis-like transport).</summary>
        public bool SupportsRouteStorage { get; set; }

        /// <summary>Backing store used when <see cref="SupportsRouteStorage"/> is true.</summary>
        public ConcurrentDictionary<string, string> StoredRoutes { get; } = new ConcurrentDictionary<string, string>();

        /// <summary>Metadata captured by StoreConnectionRouteAsync.</summary>
        public ConcurrentDictionary<string, Dictionary<string, string>> StoredMetadata { get; } = new ConcurrentDictionary<string, Dictionary<string, string>>();

        /// <summary>Controls IsNodeConnected per node id. Defaults to connected.</summary>
        public Func<string, bool> NodeConnectedFunc { get; set; } = _ => true;

        /// <summary>Hook invoked synchronously inside SendAsync (for scripted responses).</summary>
        public Action<string, ClusterMessage> OnSendAsync { get; set; }

        /// <summary>Hook invoked synchronously inside BroadcastAsync (for scripted responses).</summary>
        public Action<ClusterMessage> OnBroadcastAsync { get; set; }

        public int StartCount;
        public int StopCount;
        public bool Disposed;

        public event EventHandler<ClusterMessageEventArgs> MessageReceived;
        public event EventHandler<ClusterNodeEventArgs> NodeConnected;
        public event EventHandler<ClusterNodeEventArgs> NodeDisconnected;

        public IReadOnlyList<(string NodeId, ClusterMessage Message)> Sent
        {
            get { lock (_sync) { return _sent.ToArray(); } }
        }

        public IReadOnlyList<ClusterMessage> Broadcasts
        {
            get { lock (_sync) { return _broadcasts.ToArray(); } }
        }

        public IReadOnlyList<ClusterNode> RegisteredNodes
        {
            get { lock (_sync) { return _registeredNodes.ToArray(); } }
        }

        public Task StartAsync()
        {
            Interlocked.Increment(ref StartCount);
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Interlocked.Increment(ref StopCount);
            return Task.CompletedTask;
        }

        public Task SendAsync(string nodeId, ClusterMessage message)
        {
            lock (_sync)
            {
                _sent.Add((nodeId, message));
            }
            OnSendAsync?.Invoke(nodeId, message);
            return Task.CompletedTask;
        }

        public Task BroadcastAsync(ClusterMessage message)
        {
            lock (_sync)
            {
                _broadcasts.Add(message);
            }
            OnBroadcastAsync?.Invoke(message);
            return Task.CompletedTask;
        }

        public bool IsNodeConnected(string nodeId) => NodeConnectedFunc(nodeId);

        public Task<bool> StoreConnectionRouteAsync(string connectionId, string nodeId, Dictionary<string, string> metadata = null)
        {
            if (!SupportsRouteStorage)
            {
                return Task.FromResult(false);
            }

            StoredRoutes[connectionId] = nodeId;
            if (metadata != null)
            {
                StoredMetadata[connectionId] = metadata;
            }
            return Task.FromResult(true);
        }

        public Task<string> GetConnectionRouteAsync(string connectionId)
        {
            if (!SupportsRouteStorage)
            {
                return Task.FromResult<string>(null);
            }
            return Task.FromResult(StoredRoutes.TryGetValue(connectionId, out var nodeId) ? nodeId : null);
        }

        public Task<bool> RemoveConnectionRouteAsync(string connectionId)
        {
            if (!SupportsRouteStorage)
            {
                return Task.FromResult(false);
            }
            return Task.FromResult(StoredRoutes.TryRemove(connectionId, out _));
        }

        public Task<bool> RefreshConnectionRouteAsync(string connectionId, string nodeId)
        {
            return Task.FromResult(SupportsRouteStorage && StoredRoutes.ContainsKey(connectionId));
        }

        /// <summary>
        /// Public RegisterNode discovered by ClusterManager.RegisterNodeInTransport via reflection.
        /// </summary>
        public void RegisterNode(ClusterNode node)
        {
            lock (_sync)
            {
                _registeredNodes.Add(node);
            }
        }

        public void RaiseMessageReceived(ClusterMessage message, string fromNodeId)
        {
            if (message != null && message.FromNodeId == null)
            {
                message.FromNodeId = fromNodeId;
            }
            MessageReceived?.Invoke(this, new ClusterMessageEventArgs { FromNodeId = fromNodeId, Message = message });
        }

        public void RaiseNodeConnected(string nodeId)
            => NodeConnected?.Invoke(this, new ClusterNodeEventArgs { NodeId = nodeId });

        public void RaiseNodeDisconnected(string nodeId)
            => NodeDisconnected?.Invoke(this, new ClusterNodeEventArgs { NodeId = nodeId });

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Snapshots and restores static state that cluster code depends on:
    /// GlobalClusterCenter, MvcChannelHandler.Clients and ClusterChannelHandler.MaxMessageSize.
    /// Test classes touching statics derive from this and live in the "ClusterStaticState" collection.
    /// </summary>
    public abstract class StaticStateGuard : IDisposable
    {
        private readonly ClusterOption _clusterContext;
        private readonly ClusterManager _clusterManager;
        private readonly IWebSocketConnectionProvider _connectionProvider;
        private readonly IWebSocketStatisticsRecorder _statisticsRecorder;
        private readonly ConcurrentDictionary<string, WebSocket> _clients;
        private readonly int _maxMessageSize;

        protected StaticStateGuard()
        {
            _clusterContext = GlobalClusterCenter.ClusterContext;
            _clusterManager = GlobalClusterCenter.ClusterManager;
            _connectionProvider = GlobalClusterCenter.ConnectionProvider;
            _statisticsRecorder = GlobalClusterCenter.StatisticsRecorder;
            _clients = MvcChannelHandler.Clients;
            _maxMessageSize = ClusterChannelHandler.MaxMessageSize;

            GlobalClusterCenter.ClusterContext = null;
            GlobalClusterCenter.ClusterManager = null;
            GlobalClusterCenter.ConnectionProvider = null;
            GlobalClusterCenter.StatisticsRecorder = null;
            MvcChannelHandler.Clients = new ConcurrentDictionary<string, WebSocket>();
        }

        public virtual void Dispose()
        {
            GlobalClusterCenter.ClusterContext = _clusterContext;
            GlobalClusterCenter.ClusterManager = _clusterManager;
            GlobalClusterCenter.ConnectionProvider = _connectionProvider;
            GlobalClusterCenter.StatisticsRecorder = _statisticsRecorder;
            MvcChannelHandler.Clients = _clients;
            ClusterChannelHandler.MaxMessageSize = _maxMessageSize;
        }
    }

    /// <summary>
    /// Polling helper for asynchronous assertions.
    /// </summary>
    public static class Wait
    {
        public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 3000, int pollMs = 10)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (condition())
                {
                    return true;
                }
                await Task.Delay(pollMs);
            }
            return condition();
        }
    }

    /// <summary>
    /// Serializes all cluster tests: they share static state
    /// (GlobalClusterCenter, MvcChannelHandler.Clients, ClusterChannelHandler.MaxMessageSize).
    /// </summary>
    [CollectionDefinition("ClusterStaticState")]
    public class ClusterStaticStateCollection
    {
    }
}
