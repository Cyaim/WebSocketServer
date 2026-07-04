using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Infrastructure.Metrics;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Unit-level coverage for ConnectionEntry's non-websocket path and MvcChannel_OnBeforeConnection
    /// edge branches. Mutates WebSocketRouteOption.ApplicationServices -> StaticState collection.
    /// </summary>
    [Collection("StaticState")]
    public class MvcConnectionEntryUnitCovTests : IDisposable
    {
        private readonly IServiceProvider _previousServices;

        public MvcConnectionEntryUnitCovTests()
        {
            _previousServices = WebSocketRouteOption.ApplicationServices;
        }

        public void Dispose()
        {
            WebSocketRouteOption.ApplicationServices = _previousServices;
            MvcTestSupport.ResetCachedScopeFactory();
        }

        /// <summary>Returns null for everything except IOptions&lt;BandwidthLimitPolicy&gt;, which throws.</summary>
        private sealed class ThrowingOptionsProvider : IServiceProvider
        {
            public object GetService(Type serviceType)
            {
                if (serviceType == typeof(IOptions<BandwidthLimitPolicy>))
                {
                    throw new InvalidOperationException("options-resolution-fails");
                }
                return null;
            }
        }

        /// <summary>Throws when AccessControlService is requested.</summary>
        private sealed class ThrowingAccessProvider : IServiceProvider
        {
            public object GetService(Type serviceType)
            {
                if (serviceType == typeof(AccessControlService))
                {
                    throw new InvalidOperationException("access-service-resolution-fails");
                }
                return null;
            }
        }

        [Fact]
        public async Task NonWebSocketRequest_AssignsId_AndBandwidthOptionsCatch_Returns400()
        {
            WebSocketRouteOption.ApplicationServices = new ThrowingOptionsProvider();
            var handler = new MvcChannelHandler();
            var options = new WebSocketRouteOption
            {
                WatchAssemblyContext = MvcTestSupport.BuildContext(typeof(MvcTestSupport.WsTestController))
            };
            var ctx = new DefaultHttpContext(); // not a websocket request, Connection.Id null

            await handler.ConnectionEntry(ctx, NullLogger<WebSocketRouteMiddleware>.Instance, options);

            Assert.False(string.IsNullOrEmpty(ctx.Connection.Id)); // Id was generated
            Assert.Equal(400, ctx.Response.StatusCode);
        }

        [Fact]
        public async Task OnBeforeConnection_DeniedButNoPolicyRegistered_LogsAndReturnsFalse()
        {
            var policy = new AccessControlPolicy { Enabled = true };
            policy.IpWhitelist.Add("9.9.9.9"); // 1.2.3.4 not whitelisted -> denied
            var accessService = new AccessControlService(NullLogger<AccessControlService>.Instance, policy);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(accessService); // AccessControlService present, AccessControlPolicy NOT registered
            using var provider = services.BuildServiceProvider();
            WebSocketRouteOption.ApplicationServices = provider;

            var handler = new MvcChannelHandler();
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
            ctx.Request.Path = "/ws";

            var result = await handler.MvcChannel_OnBeforeConnection(ctx, new WebSocketRouteOption(), "/ws",
                NullLogger<WebSocketRouteMiddleware>.Instance);

            Assert.False(result);
        }

        [Fact]
        public async Task OnBeforeConnection_ServiceResolutionThrows_IsCaught_AllowsConnection()
        {
            WebSocketRouteOption.ApplicationServices = new ThrowingAccessProvider();
            var handler = new MvcChannelHandler();
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
            ctx.Request.Path = "/ws";

            var result = await handler.MvcChannel_OnBeforeConnection(ctx, new WebSocketRouteOption(), "/ws",
                NullLogger<WebSocketRouteMiddleware>.Instance);

            Assert.True(result); // outer catch allows connection on resolution error
        }
    }

    /// <summary>
    /// End-to-end coverage for ConnectionEntry branches that require a real websocket handshake:
    /// cluster register/unregister (success and failing transport), access-control denial,
    /// duplicate connection id, and a throwing before-connection event.
    /// </summary>
    [Collection("StaticState")]
    public class MvcConnectionEntryE2ECovTests : IDisposable
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);
        private readonly IServiceProvider _previousServices;
        private readonly ClusterManager _previousManager;
        private readonly IWebSocketConnectionProvider _previousProvider;
        private ClusterRouter _router;

        public MvcConnectionEntryE2ECovTests()
        {
            _previousServices = WebSocketRouteOption.ApplicationServices;
            _previousManager = GlobalClusterCenter.ClusterManager;
            _previousProvider = GlobalClusterCenter.ConnectionProvider;
            MvcTestSupport.ResetCachedScopeFactory();
        }

        public void Dispose()
        {
            WebSocketRouteOption.ApplicationServices = _previousServices;
            GlobalClusterCenter.ClusterManager = _previousManager;
            GlobalClusterCenter.ConnectionProvider = _previousProvider;
            try { _router?.Dispose(); } catch { }
            MvcTestSupport.ResetCachedScopeFactory();
        }

        private static async Task<IHost> StartHostAsync(WebSocketRouteOption option, Action<IServiceCollection> extraServices = null, string forcedConnectionId = null)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(option);
                        services.AddSingleton<MvcTestSupport.IGreetService, MvcTestSupport.GreetService>();
                        extraServices?.Invoke(services);
                    })
                    .Configure(app =>
                    {
                        app.Use(async (context, next) =>
                        {
                            context.Connection.Id = forcedConnectionId ?? (context.Connection.Id ?? Guid.NewGuid().ToString("N"));
                            await next();
                        });
                        app.UseWebSockets();
                        app.UseWebSocketServer();
                    }))
                .Build();
            await host.StartAsync();
            return host;
        }

        private static WebSocketRouteOption CreateOption(Action<WebSocketRouteOption> configure = null)
        {
            var option = new WebSocketRouteOption
            {
                WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/ws"] = new MvcChannelHandler().ConnectionEntry
                },
                WatchAssemblyContext = MvcTestSupport.BuildContext(typeof(MvcTestSupport.WsTestController))
            };
            configure?.Invoke(option);
            return option;
        }

        private static async Task<WebSocket> ConnectAsync(IHost host)
        {
            var server = host.GetTestServer();
            var client = server.CreateWebSocketClient();
            using var cts = new CancellationTokenSource(TestTimeout);
            return await client.ConnectAsync(new Uri(server.BaseAddress, "/ws"), cts.Token);
        }

        private static async Task EchoRoundTripAsync(WebSocket socket)
        {
            using var cts = new CancellationTokenSource(TestTimeout);
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"id\":\"1\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"hi\"}}"),
                WebSocketMessageType.Text, true, cts.Token);
            var buffer = new byte[4096];
            await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
        }

        private ClusterManager SetupCluster(bool throwing)
        {
            var transport = new StubClusterTransport { ThrowOnRouteStorage = throwing };
            var mgr = ClusterTestFactory.Create(transport, "node-1", out _router, out _);
            mgr.SetConnectionProvider(new StubConnectionProvider(new TestWebSocket()));
            GlobalClusterCenter.ClusterManager = mgr;
            return mgr;
        }

        [Fact]
        public async Task ClusterEnabled_Register_And_Unregister_SuccessPaths()
        {
            SetupCluster(throwing: false);
            using var host = await StartHostAsync(CreateOption());
            var socket = await ConnectAsync(host);
            await EchoRoundTripAsync(socket);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();
        }

        [Fact]
        public async Task ClusterEnabled_Register_And_Unregister_TransportThrows_CatchesAreHit()
        {
            SetupCluster(throwing: true);
            using var host = await StartHostAsync(CreateOption());
            var socket = await ConnectAsync(host);
            await EchoRoundTripAsync(socket);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();
        }

        [Fact]
        public async Task InnerWsHandling_LifetimeResolutionThrows_InnerCatchHandlesIt()
        {
            using var host = await StartHostAsync(CreateOption());

            // Swap ApplicationServices for a provider that lacks IHostApplicationLifetime, so the
            // GetRequiredService<IHostApplicationLifetime> call inside the accepted-socket try throws
            // and the inner catch handles it.
            var svc = new ServiceCollection();
            svc.AddLogging();
            svc.AddSingleton<MvcTestSupport.IGreetService, MvcTestSupport.GreetService>();
            using var noLifetimeProvider = svc.BuildServiceProvider();
            WebSocketRouteOption.ApplicationServices = noLifetimeProvider;
            MvcTestSupport.ResetCachedScopeFactory();

            var socket = await ConnectAsync(host);
            try
            {
                var buffer = new byte[256];
                using var cts = new CancellationTokenSource(TestTimeout);
                await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            }
            catch { /* socket aborted by the inner catch/finally */ }

            await host.StopAsync();
        }

        [Fact]
        public async Task BeforeConnectionEvent_Throws_OuterCatchHandlesIt()
        {
            using var host = await StartHostAsync(CreateOption(o =>
                o.BeforeConnectionEvent += (context, opt, channel, logger) => throw new InvalidOperationException("before-throws")));

            await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(host));
            await host.StopAsync();
        }

        [Fact]
        public async Task AccessControlDenies_HandshakeRejected()
        {
            using var host = await StartHostAsync(CreateOption(), services =>
                services.AddAccessControl(p =>
                {
                    p.Enabled = true;
                    p.EnableGeoLocationLookup = false;
                    p.DeniedAction = AccessDeniedAction.CloseConnection;
                }));

            // TestServer leaves RemoteIpAddress null -> IsAllowedAsync denies -> handshake rejected.
            await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(host));
            await host.StopAsync();
        }

        [Fact]
        public async Task DuplicateConnectionId_NotAllowed_RejectsSecondRegistration()
        {
            const string fixedId = "dup-conn-id";
            var snapshot = MvcChannelHandler.Clients.Keys.ToArray();
            var placeholder = new TestWebSocket();
            MvcChannelHandler.Clients.TryAdd(fixedId, placeholder);
            try
            {
                using var host = await StartHostAsync(CreateOption(o => o.AllowSameConnectionIdAccess = false),
                    forcedConnectionId: fixedId);

                var socket = await ConnectAsync(host);
                // The handshake completes but the server rejects the duplicate id and abandons the socket.
                try
                {
                    var buffer = new byte[256];
                    using var cts = new CancellationTokenSource(TestTimeout);
                    await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                }
                catch { /* connection dropped */ }

                await host.StopAsync();
            }
            finally
            {
                MvcChannelHandler.Clients.TryRemove(fixedId, out _);
                // Ensure we did not leak any other entries.
                foreach (var key in MvcChannelHandler.Clients.Keys.ToArray())
                {
                    if (!snapshot.Contains(key))
                    {
                        MvcChannelHandler.Clients.TryRemove(key, out _);
                    }
                }
            }
        }
    }
}
