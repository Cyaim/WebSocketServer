using System.Collections;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    /// <summary>
    /// One scripted receive operation for <see cref="ScriptedWebSocket"/>.
    /// </summary>
    public sealed class ScriptedReceive
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public WebSocketMessageType MessageType { get; set; } = WebSocketMessageType.Text;
        public bool EndOfMessage { get; set; } = true;
        public bool IsClose { get; set; }
        public Exception ThrowException { get; set; }

        public static ScriptedReceive Text(string text, bool endOfMessage = true)
            => new ScriptedReceive { Data = System.Text.Encoding.UTF8.GetBytes(text), EndOfMessage = endOfMessage };

        public static ScriptedReceive Bytes(byte[] data, bool endOfMessage = true)
            => new ScriptedReceive { Data = data, EndOfMessage = endOfMessage };

        public static ScriptedReceive Close() => new ScriptedReceive { IsClose = true };

        public static ScriptedReceive Throw(Exception ex) => new ScriptedReceive { ThrowException = ex };
    }

    /// <summary>
    /// WebSocket whose ReceiveAsync plays back a script of frames.
    /// After the script is exhausted a Close frame is returned.
    /// The state intentionally stays Open when a scripted close is received so that
    /// callers exercise their own close paths (e.g. ClusterChannelHandler's finally block).
    /// </summary>
    public sealed class ScriptedWebSocket : WebSocket
    {
        private readonly Queue<ScriptedReceive> _script;
        private WebSocketState _state = WebSocketState.Open;
        private readonly List<CapturedFrame> _frames = new List<CapturedFrame>();

        public ScriptedWebSocket(params ScriptedReceive[] script)
        {
            _script = new Queue<ScriptedReceive>(script);
        }

        public WebSocketCloseStatus? RecordedCloseStatus { get; private set; }
        public string RecordedCloseDescription { get; private set; }
        public IReadOnlyList<CapturedFrame> Frames => _frames;

        public override WebSocketCloseStatus? CloseStatus => RecordedCloseStatus;
        public override string CloseStatusDescription => RecordedCloseDescription;
        public override WebSocketState State => _state;
        public override string SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            RecordedCloseStatus = closeStatus;
            RecordedCloseDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            RecordedCloseStatus = closeStatus;
            RecordedCloseDescription = statusDescription;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var item = _script.Count > 0 ? _script.Dequeue() : ScriptedReceive.Close();

            if (item.ThrowException != null)
            {
                throw item.ThrowException;
            }

            if (item.IsClose)
            {
                // State intentionally stays Open (see class remarks) / 状态故意保持 Open（见类注释）
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "closing"));
            }

            if (item.Data.Length > buffer.Count)
            {
                throw new InvalidOperationException("Scripted frame larger than receive buffer.");
            }

            Array.Copy(item.Data, 0, buffer.Array, buffer.Offset, item.Data.Length);
            return Task.FromResult(new WebSocketReceiveResult(item.Data.Length, item.MessageType, item.EndOfMessage));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            _frames.Add(new CapturedFrame(buffer.ToArray(), messageType, endOfMessage));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Open WebSocket whose CloseAsync throws (SendAsync records normally).
    /// </summary>
    public sealed class ThrowingCloseWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
            => throw new InvalidOperationException("close failed");
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
            => throw new InvalidOperationException("close output failed");
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Open WebSocket whose CloseAsync completes after a configurable delay.
    /// </summary>
    public sealed class DelayCloseWebSocket : WebSocket
    {
        private readonly int _closeDelayMs;
        private WebSocketState _state = WebSocketState.Open;

        public DelayCloseWebSocket(int closeDelayMs)
        {
            _closeDelayMs = closeDelayMs;
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;

        public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            await Task.Delay(_closeDelayMs, cancellationToken);
            _state = WebSocketState.Closed;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Provider that returns an open socket but reports SendAsync failure.
    /// </summary>
    public sealed class FailingSendProvider : IWebSocketConnectionProvider
    {
        public ConcurrentDictionary<string, WebSocket> Connections { get; } = new ConcurrentDictionary<string, WebSocket>();

        public WebSocket GetConnection(string connectionId)
            => connectionId != null && Connections.TryGetValue(connectionId, out var ws) ? ws : null;

        public Task<bool> SendAsync(string connectionId, byte[] data, WebSocketMessageType messageType, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    /// <summary>
    /// Logger that throws for configured log levels — used to reach defensive catch blocks.
    /// </summary>
    public sealed class ThrowingLogger<T> : ILogger<T>
    {
        /// <summary>Levels that trigger a throw. Mutable so tests can arm/disarm mid-test.</summary>
        public HashSet<LogLevel> ThrowOn { get; } = new HashSet<LogLevel>();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (ThrowOn.Contains(logLevel))
            {
                throw new InvalidOperationException($"logger throws on {logLevel}");
            }
        }
    }

    /// <summary>
    /// Fully hookable IClusterTransport for fault injection.
    /// All behaviors default to benign no-ops that record calls.
    /// </summary>
    public sealed class ConfigurableClusterTransport : IClusterTransport
    {
        private readonly object _sync = new object();
        private readonly List<(string NodeId, ClusterMessage Message)> _sent = new List<(string, ClusterMessage)>();
        private readonly List<ClusterMessage> _broadcasts = new List<ClusterMessage>();

        public Func<Task> StartAsyncImpl { get; set; }
        public Func<Task> StopAsyncImpl { get; set; }
        public Func<string, ClusterMessage, Task> SendAsyncImpl { get; set; }
        public Func<ClusterMessage, Task> BroadcastAsyncImpl { get; set; }
        public Func<string, bool> IsNodeConnectedImpl { get; set; } = _ => true;
        public Func<string, string, Dictionary<string, string>, Task<bool>> StoreConnectionRouteAsyncImpl { get; set; }
        public Func<string, Task<string>> GetConnectionRouteAsyncImpl { get; set; }
        public Func<string, Task<bool>> RemoveConnectionRouteAsyncImpl { get; set; }
        public Func<string, string, Task<bool>> RefreshConnectionRouteAsyncImpl { get; set; }
        public bool ThrowOnRegisterNode { get; set; }

        public List<ClusterNode> RegisteredNodes { get; } = new List<ClusterNode>();

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

        public Task StartAsync() => StartAsyncImpl?.Invoke() ?? Task.CompletedTask;
        public Task StopAsync() => StopAsyncImpl?.Invoke() ?? Task.CompletedTask;

        public Task SendAsync(string nodeId, ClusterMessage message)
        {
            lock (_sync) { _sent.Add((nodeId, message)); }
            return SendAsyncImpl?.Invoke(nodeId, message) ?? Task.CompletedTask;
        }

        public Task BroadcastAsync(ClusterMessage message)
        {
            lock (_sync) { _broadcasts.Add(message); }
            return BroadcastAsyncImpl?.Invoke(message) ?? Task.CompletedTask;
        }

        public bool IsNodeConnected(string nodeId) => IsNodeConnectedImpl(nodeId);

        public Task<bool> StoreConnectionRouteAsync(string connectionId, string nodeId, Dictionary<string, string> metadata = null)
            => StoreConnectionRouteAsyncImpl?.Invoke(connectionId, nodeId, metadata) ?? Task.FromResult(false);

        public Task<string> GetConnectionRouteAsync(string connectionId)
            => GetConnectionRouteAsyncImpl?.Invoke(connectionId) ?? Task.FromResult<string>(null);

        public Task<bool> RemoveConnectionRouteAsync(string connectionId)
            => RemoveConnectionRouteAsyncImpl?.Invoke(connectionId) ?? Task.FromResult(false);

        public Task<bool> RefreshConnectionRouteAsync(string connectionId, string nodeId)
            => RefreshConnectionRouteAsyncImpl?.Invoke(connectionId, nodeId) ?? Task.FromResult(false);

        /// <summary>Discovered by ClusterManager's reflection fallback.</summary>
        public void RegisterNode(ClusterNode node)
        {
            if (ThrowOnRegisterNode)
            {
                throw new InvalidOperationException("register node failed");
            }
            RegisteredNodes.Add(node);
        }

        public void RaiseMessageReceived(ClusterMessage message, string fromNodeId)
            => MessageReceived?.Invoke(this, new ClusterMessageEventArgs { FromNodeId = fromNodeId, Message = message });

        public void RaiseNodeConnected(string nodeId)
            => NodeConnected?.Invoke(this, new ClusterNodeEventArgs { NodeId = nodeId });

        public void RaiseNodeDisconnected(string nodeId)
            => NodeDisconnected?.Invoke(this, new ClusterNodeEventArgs { NodeId = nodeId });

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Base transport with benign no-op implementations, used by the fake
    /// "HybridClusterTransport" variants below (RaftNode matches them by type NAME).
    /// </summary>
    public abstract class NoopTransportBase : IClusterTransport
    {
#pragma warning disable CS0067
        public event EventHandler<ClusterMessageEventArgs> MessageReceived;
        public event EventHandler<ClusterNodeEventArgs> NodeConnected;
        public event EventHandler<ClusterNodeEventArgs> NodeDisconnected;
#pragma warning restore CS0067

        public Task StartAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task SendAsync(string nodeId, ClusterMessage message) => Task.CompletedTask;
        public Task BroadcastAsync(ClusterMessage message) => Task.CompletedTask;
        public bool IsNodeConnected(string nodeId) => false;
        public Task<bool> StoreConnectionRouteAsync(string connectionId, string nodeId, Dictionary<string, string> metadata = null) => Task.FromResult(false);
        public Task<string> GetConnectionRouteAsync(string connectionId) => Task.FromResult<string>(null);
        public Task<bool> RemoveConnectionRouteAsync(string connectionId) => Task.FromResult(false);
        public Task<bool> RefreshConnectionRouteAsync(string connectionId, string nodeId) => Task.FromResult(false);
        public void Dispose() { }
    }

    /// <summary>Variant A: exposes a public GetKnownNodeIds() method.</summary>
    public static class HybridWithMethod
    {
        public sealed class HybridClusterTransport : NoopTransportBase
        {
            public List<string> KnownNodes { get; set; } = new List<string>();
            public List<string> GetKnownNodeIds() => KnownNodes;
        }
    }

    /// <summary>Variant B: no method, exposes a private _knownNodes dictionary field.</summary>
    public static class HybridWithField
    {
        public sealed class HybridClusterTransport : NoopTransportBase
        {
            private readonly Hashtable _knownNodes = new Hashtable();

            public void AddKnownNode(string nodeId) => _knownNodes[nodeId] = true;
        }
    }

    /// <summary>Variant C: GetKnownNodeIds() throws.</summary>
    public static class HybridThrowing
    {
        public sealed class HybridClusterTransport : NoopTransportBase
        {
            public List<string> GetKnownNodeIds() => throw new InvalidOperationException("discovery failed");
        }
    }

    /// <summary>Variant D: neither method nor field — falls back to cluster configuration.</summary>
    public static class HybridBare
    {
        public sealed class HybridClusterTransport : NoopTransportBase
        {
        }
    }

    /// <summary>
    /// IHttpWebSocketFeature returning a supplied WebSocket, so DefaultHttpContext
    /// behaves like a real WebSocket upgrade request.
    /// </summary>
    public sealed class FakeWebSocketFeature : Microsoft.AspNetCore.Http.Features.IHttpWebSocketFeature
    {
        private readonly WebSocket _socket;

        public FakeWebSocketFeature(WebSocket socket)
        {
            _socket = socket;
        }

        public bool IsWebSocketRequest => true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(_socket);
    }
}
