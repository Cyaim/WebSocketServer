using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Direct tests for WebSocketRouteMiddleware.Invoke (no statics touched).
    /// </summary>
    public class WebSocketRouteMiddlewareTests
    {
        [Fact]
        public async Task Invoke_MatchingChannel_CallsHandler_NotNext()
        {
            bool handlerCalled = false, nextCalled = false;
            var option = new WebSocketRouteOption
            {
                WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/ws"] = (context, logger, options) => { handlerCalled = true; return Task.CompletedTask; }
                }
            };
            var middleware = new WebSocketRouteMiddleware(
                _ => { nextCalled = true; return Task.CompletedTask; },
                NullLogger<WebSocketRouteMiddleware>.Instance,
                option);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/ws";

            await middleware.Invoke(httpContext);

            Assert.True(handlerCalled);
            Assert.False(nextCalled);
        }

        [Fact]
        public async Task Invoke_NonMatchingPath_CallsNext()
        {
            bool handlerCalled = false, nextCalled = false;
            var option = new WebSocketRouteOption
            {
                WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/ws"] = (context, logger, options) => { handlerCalled = true; return Task.CompletedTask; }
                }
            };
            var middleware = new WebSocketRouteMiddleware(
                _ => { nextCalled = true; return Task.CompletedTask; },
                NullLogger<WebSocketRouteMiddleware>.Instance,
                option);
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/other";

            await middleware.Invoke(httpContext);

            Assert.False(handlerCalled);
            Assert.True(nextCalled);
        }
    }

    /// <summary>
    /// Tests for the UseWebSocketServer application-builder extensions.
    /// These mutate WebSocketRouteOption.ApplicationServices / ServerAddresses, so they
    /// run in the StaticState collection and restore the statics afterwards.
    /// </summary>
    [Collection("StaticState")]
    public class WebSocketRouteMiddlewareExtensionsTests : IDisposable
    {
        private readonly IServiceProvider _previousServices;
        private readonly List<string> _previousAddresses;

        public WebSocketRouteMiddlewareExtensionsTests()
        {
            _previousServices = WebSocketRouteOption.ApplicationServices;
            _previousAddresses = WebSocketRouteOption.ServerAddresses;
        }

        public void Dispose()
        {
            WebSocketRouteOption.ApplicationServices = _previousServices;
            WebSocketRouteOption.ServerAddresses = _previousAddresses;
        }

        private static Task DummyHandler(HttpContext context, Microsoft.Extensions.Logging.ILogger<WebSocketRouteMiddleware> logger, WebSocketRouteOption options)
            => Task.CompletedTask;

        [Fact]
        public void UseWebSocketServer_SetsApplicationServices_AndReturnsApp()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(new WebSocketRouteOption());
            using var provider = services.BuildServiceProvider();
            var app = new ApplicationBuilder(provider);

            var result = app.UseWebSocketServer();

            Assert.Same(app, result);
            Assert.Same(provider, WebSocketRouteOption.ApplicationServices);
        }

        [Fact]
        public void UseWebSocketServer_Configure_NullArgs_Throw()
        {
            var services = new ServiceCollection();
            using var provider = services.BuildServiceProvider();
            var app = new ApplicationBuilder(provider);

            Assert.Throws<ArgumentNullException>(() =>
                ((IApplicationBuilder)null).UseWebSocketServer((o, sp) => { }));
            Assert.Throws<ArgumentNullException>(() =>
                app.UseWebSocketServer((Action<WebSocketRouteOption, IServiceProvider>)null));
        }

        [Fact]
        public void UseWebSocketServer_Configure_NoChannels_ThrowsInvalidOperation()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();
            var app = new ApplicationBuilder(provider);

            Assert.Throws<InvalidOperationException>(() => app.UseWebSocketServer((option, sp) => { }));
        }

        [Fact]
        public async Task UseWebSocketServer_Configure_NoRegisteredOption_WrapsMiddlewareViaClosure()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            using var provider = services.BuildServiceProvider();
            var app = new ApplicationBuilder(provider);
            bool handlerCalled = false;

            app.UseWebSocketServer((option, sp) =>
            {
                Assert.Same(provider, sp);
                option.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/custom"] = (context, logger, opt) => { handlerCalled = true; return Task.CompletedTask; }
                };
            });

            var pipeline = app.Build();
            var httpContext = new DefaultHttpContext { RequestServices = provider };
            httpContext.Request.Path = "/custom";
            await pipeline(httpContext);

            Assert.True(handlerCalled);
            Assert.Same(provider, WebSocketRouteOption.ApplicationServices);
        }

        [Fact]
        public void UseWebSocketServer_Configure_ExistingOption_IsReusedAndAugmented()
        {
            var existing = new WebSocketRouteOption
            {
                WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/ws"] = DummyHandler
                }
            };
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(existing);
            using var provider = services.BuildServiceProvider();
            var app = new ApplicationBuilder(provider);
            WebSocketRouteOption seen = null;

            app.UseWebSocketServer((option, sp) =>
            {
                seen = option;
                option.RequireRequestId = false;
            });

            Assert.Same(existing, seen);
            Assert.False(existing.RequireRequestId);
        }

        [Fact]
        public void UseWebSocketServer_Cluster_NullArgs_Throw()
        {
            var services = new ServiceCollection();
            using var provider = services.BuildServiceProvider();
            var app = new ApplicationBuilder(provider);

            Assert.Throws<ArgumentNullException>(() =>
                ((IApplicationBuilder)null).UseWebSocketServer(provider, o => { }));
            Assert.Throws<ArgumentNullException>(() =>
                app.UseWebSocketServer((IServiceProvider)null, o => { }));
            Assert.Throws<ArgumentNullException>(() =>
                app.UseWebSocketServer(provider, (Action<ClusterOption>)null));
        }

        [Fact]
        public void UseWebSocketServer_Cluster_MissingClusterChannel_Throws()
        {
            var option = new WebSocketRouteOption
            {
                WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/ws"] = DummyHandler
                }
            };
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(option);
            using var provider = services.BuildServiceProvider();
            var app = new ApplicationBuilder(provider);

            Assert.Throws<InvalidOperationException>(() =>
                app.UseWebSocketServer(provider, cluster => cluster.ChannelName = "/cluster"));
        }

        #region GetWebSocketAddress

        private sealed class FakeServer : IServer
        {
            public FakeServer(params string[] addresses)
            {
                var feature = new ServerAddressesFeature();
                foreach (var address in addresses)
                {
                    feature.Addresses.Add(address);
                }
                Features.Set<IServerAddressesFeature>(feature);
            }

            public IFeatureCollection Features { get; } = new FeatureCollection();

            Task IServer.StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
                => Task.CompletedTask;

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public void Dispose()
            {
            }
        }

        [Fact]
        public void GetWebSocketAddress_TranslatesHttpSchemesToWs()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IServer>(new FakeServer("http://localhost:5000", "https://localhost:5001"));
            using var provider = services.BuildServiceProvider();
            WebSocketRouteOption.ApplicationServices = provider;

            WebSocketRouteMiddlewareExtensions.GetWebSocketAddress();

            Assert.Equal(new List<string> { "ws://localhost:5000", "wss://localhost:5001" }, WebSocketRouteOption.ServerAddresses);
        }

        #endregion
    }
}
