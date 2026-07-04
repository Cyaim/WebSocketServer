using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Tests
{
    public class AddWebSocketServerTests
    {
        private static Task DummyHandler(HttpContext context, ILogger<WebSocketRouteMiddleware> logger, WebSocketRouteOption options)
            => Task.CompletedTask;

        private static Task OtherHandler(HttpContext context, ILogger<WebSocketRouteMiddleware> logger, WebSocketRouteOption options)
            => Task.CompletedTask;

        [Fact]
        public void AddWebSocketServer_Default_WiresWsChannel_AndSetsServiceCollection()
        {
            var services = new ServiceCollection();

            var builder = services.AddWebSocketServer();

            Assert.NotNull(builder);
            Assert.Same(services, builder.Services);
            Assert.Same(services, builder.Options.ApplicationServiceCollection);
            var channel = Assert.Single(builder.Options.WebSocketChannels);
            Assert.Equal(WebSocketRouteServiceCollectionExtensions.DefaultWebSocketChannel, channel.Key);
            Assert.Equal("/ws", channel.Key);
        }

        /// <summary>
        /// Stub lifetime. The library calls services.TryAddSingleton&lt;IHostApplicationLifetime&gt;(),
        /// which registers the interface as its own implementation type — an un-instantiable
        /// descriptor that makes BuildServiceProvider throw unless the host (or this test)
        /// registered a real IHostApplicationLifetime first.
        /// </summary>
        private sealed class StubLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
        {
            public CancellationToken ApplicationStarted => CancellationToken.None;
            public CancellationToken ApplicationStopping => CancellationToken.None;
            public CancellationToken ApplicationStopped => CancellationToken.None;
            public void StopApplication() { }
        }

        [Fact]
        public void AddWebSocketServer_RegistersOptionSingleton()
        {
            var services = new ServiceCollection();
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(new StubLifetime());

            var builder = services.AddWebSocketServer();

            using var provider = services.BuildServiceProvider();
            Assert.Same(builder.Options, provider.GetRequiredService<WebSocketRouteOption>());
        }

        [Fact]
        public void AddWebSocketServer_NullServices_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null).AddWebSocketServer());
        }

        [Fact]
        public void AddWebSocketServer_ConfigureOverload_OverridesDefaults()
        {
            var services = new ServiceCollection();

            var builder = services.AddWebSocketServer(o =>
            {
                o.InjectionHttpContextPropertyName = "MyContext";
                o.RequireRequestId = false;
                o.MaxConnectionLimit = 100;
            });

            Assert.Equal("MyContext", builder.Options.InjectionHttpContextPropertyName);
            Assert.False(builder.Options.RequireRequestId);
            Assert.Equal(100UL, builder.Options.MaxConnectionLimit);
            // No channel configured by the user -> default "/ws" still auto-wired
            Assert.True(builder.Options.WebSocketChannels.ContainsKey("/ws"));
        }

        [Fact]
        public void AddWebSocketServer_UserConfiguredChannel_SuppressesAutoDefault()
        {
            var services = new ServiceCollection();

            var builder = services.AddWebSocketServer(o =>
            {
                o.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/custom"] = DummyHandler
                };
            });

            Assert.False(builder.Options.WebSocketChannels.ContainsKey("/ws"));
            var channel = Assert.Single(builder.Options.WebSocketChannels);
            Assert.Equal("/custom", channel.Key);
        }

        [Fact]
        public void AddWebSocketServer_UserConfiguredChannel_BuilderAddChannel_DoesNotRemoveIt()
        {
            var services = new ServiceCollection();
            var builder = services.AddWebSocketServer(o =>
            {
                o.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/custom"] = DummyHandler
                };
            });

            builder.AddChannel("/extra", DummyHandler);

            Assert.True(builder.Options.WebSocketChannels.ContainsKey("/custom"));
            Assert.True(builder.Options.WebSocketChannels.ContainsKey("/extra"));
            Assert.Equal(2, builder.Options.WebSocketChannels.Count);
        }

        [Fact]
        public void Builder_FirstExplicitChannel_ReplacesAutoDefaultWs()
        {
            var services = new ServiceCollection();
            var builder = services.AddWebSocketServer();
            Assert.True(builder.Options.WebSocketChannels.ContainsKey("/ws"));

            builder.AddChannel("/chat", DummyHandler);

            Assert.False(builder.Options.WebSocketChannels.ContainsKey("/ws"));
            var channel = Assert.Single(builder.Options.WebSocketChannels);
            Assert.Equal("/chat", channel.Key);
        }

        [Fact]
        public void Builder_AddMvcChannel_ReplacesAutoDefault_ThenAccumulates()
        {
            var services = new ServiceCollection();
            var builder = services.AddWebSocketServer();

            builder.AddMvcChannel("/chat").AddMvcChannel("/game");

            Assert.False(builder.Options.WebSocketChannels.ContainsKey("/ws"));
            Assert.Equal(2, builder.Options.WebSocketChannels.Count);
            Assert.True(builder.Options.WebSocketChannels.ContainsKey("/chat"));
            Assert.True(builder.Options.WebSocketChannels.ContainsKey("/game"));
        }

        [Fact]
        public void Builder_AddMvcChannel_DefaultPath_KeepsWsChannel()
        {
            var services = new ServiceCollection();
            var builder = services.AddWebSocketServer();

            // Explicitly registering "/ws" replaces the auto default with the explicit handler
            builder.AddMvcChannel();

            var channel = Assert.Single(builder.Options.WebSocketChannels);
            Assert.Equal("/ws", channel.Key);
        }

        [Fact]
        public void Builder_DuplicateChannelPath_LastRegistrationWins()
        {
            var services = new ServiceCollection();
            var builder = services.AddWebSocketServer();

            builder.AddChannel("/chat", DummyHandler);
            builder.AddChannel("/chat", OtherHandler);

            var channel = Assert.Single(builder.Options.WebSocketChannels);
            Assert.Equal("/chat", channel.Key);
            Assert.Equal((WebSocketRouteOption.WebSocketChannelHandler)OtherHandler, channel.Value);
        }

        [Fact]
        public void Builder_AddMvcChannel_OverwritesEarlierHandlerOnSamePath()
        {
            var services = new ServiceCollection();
            var builder = services.AddWebSocketServer();

            builder.AddChannel("/chat", DummyHandler);
            var before = builder.Options.WebSocketChannels["/chat"];
            builder.AddMvcChannel("/chat");
            var after = builder.Options.WebSocketChannels["/chat"];

            Assert.NotEqual(before, after);
            Assert.Single(builder.Options.WebSocketChannels);
        }

        [Fact]
        public void Builder_AddChannel_NullArgs_Throw()
        {
            var services = new ServiceCollection();
            var builder = services.AddWebSocketServer();

            Assert.Throws<ArgumentNullException>(() => builder.AddChannel(null, DummyHandler));
            Assert.Throws<ArgumentNullException>(() => builder.AddChannel(string.Empty, DummyHandler));
            Assert.Throws<ArgumentNullException>(() => builder.AddChannel("/x", null));
        }

        [Fact]
        public void AddWebSocketServer_WatchAssemblyContext_IsPopulated()
        {
            var services = new ServiceCollection();

            var builder = services.AddWebSocketServer();

            Assert.NotNull(builder.Options.WatchAssemblyContext);
            Assert.NotNull(builder.Options.WatchAssemblyContext.WatchEndPoint);
            Assert.NotNull(builder.Options.WatchAssemblyContext.WatchMethods);
            Assert.False(string.IsNullOrEmpty(builder.Options.WatchAssemblyPath));
        }

        [Fact]
        public void ConfigureWebSocketRoute_NullSetupAction_Throws()
        {
            var services = new ServiceCollection();

            Assert.Throws<ArgumentNullException>(() => services.ConfigureWebSocketRoute(null));
        }

        [Fact]
        public void ConfigureWebSocketRoute_MissingApplicationServiceCollection_Throws()
        {
            var services = new ServiceCollection();

            Assert.Throws<ArgumentNullException>(() => services.ConfigureWebSocketRoute(o => { }));
        }
    }
}
