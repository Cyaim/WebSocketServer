using System.Net.WebSockets;
using Cyaim.WebSocketServer.Dashboard.Models;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Dashboard.Tests
{
    // Dashboard controllers/services read process-wide static state (MvcChannelHandler.Clients,
    // GlobalClusterCenter). Serialize the tests to avoid cross-test interference.
    [CollectionDefinition("DashboardStatic", DisableParallelization = true)]
    public class DashboardStaticCollection { }

    /// <summary>Minimal in-memory WebSocket double for populating MvcChannelHandler.Clients in tests.</summary>
    public sealed class FakeWebSocket : WebSocket
    {
        public WebSocketState StateValue = WebSocketState.Open;
        public bool Closed;
        public bool Aborted;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override WebSocketState State => StateValue;
        public override string SubProtocol => null;
        public override void Abort() { Aborted = true; StateValue = WebSocketState.Aborted; }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        { Closed = true; StateValue = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        { Closed = true; return Task.CompletedTask; }
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Text, true));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>Shared helpers: reset static state, seed connections, unwrap ActionResult&lt;ApiResponse&lt;T&gt;&gt;.</summary>
    public static class T
    {
        public static NullLogger<X> Log<X>() => NullLogger<X>.Instance;

        /// <summary>Reset process-wide state to a clean standalone (no-cluster) baseline.</summary>
        public static void ResetStandalone()
        {
            GlobalClusterCenter.ClusterManager = null;
            GlobalClusterCenter.ClusterContext = null;
            MvcChannelHandler.Clients?.Clear();
        }

        /// <summary>Seed N local connections; returns the fakes so tests can assert on them.</summary>
        public static Dictionary<string, FakeWebSocket> Seed(params string[] ids)
        {
            var map = new Dictionary<string, FakeWebSocket>();
            foreach (var id in ids)
            {
                var ws = new FakeWebSocket();
                MvcChannelHandler.Clients[id] = ws;
                map[id] = ws;
            }
            return map;
        }

        /// <summary>Unwrap the ApiResponse&lt;T&gt; from an ActionResult&lt;ApiResponse&lt;T&gt;&gt; (Ok/NotFound/etc.).</summary>
        public static ApiResponse<TData> Unwrap<TData>(ActionResult<ApiResponse<TData>> result)
        {
            if (result.Value != null) return result.Value;
            if (result.Result is ObjectResult obj && obj.Value is ApiResponse<TData> api) return api;
            return null;
        }
    }
}
