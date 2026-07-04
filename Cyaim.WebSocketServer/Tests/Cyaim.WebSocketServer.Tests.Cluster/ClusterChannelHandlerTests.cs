using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    [Collection("ClusterStaticState")]
    public class ClusterChannelHandlerTests : StaticStateGuard
    {
        [Fact]
        public void MaxMessageSize_DefaultsTo64MB_AndIsSettable()
        {
            Assert.Equal(64 * 1024 * 1024, ClusterChannelHandler.MaxMessageSize);

            ClusterChannelHandler.MaxMessageSize = 1024;
            Assert.Equal(1024, ClusterChannelHandler.MaxMessageSize);
            // Restored by StaticStateGuard.Dispose / 由 StaticStateGuard.Dispose 恢复
        }

        [Fact]
        public void IncomingConnections_IsSharedStaticDictionary()
        {
            Assert.NotNull(ClusterChannelHandler.IncomingConnections);
            Assert.Same(ClusterChannelHandler.IncomingConnections, ClusterChannelHandler.IncomingConnections);
        }

        [Fact]
        public async Task ConnectionEntry_NonWebSocketRequest_Returns400()
        {
            var context = new DefaultHttpContext();

            await ClusterChannelHandler.ConnectionEntry(
                context,
                NullLogger<WebSocketRouteMiddleware>.Instance,
                new WebSocketRouteOption());

            Assert.Equal(400, context.Response.StatusCode);
            Assert.Empty(ClusterChannelHandler.IncomingConnections);
        }
    }
}
