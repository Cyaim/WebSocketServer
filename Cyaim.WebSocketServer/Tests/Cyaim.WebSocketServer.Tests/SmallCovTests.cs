using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Cyaim.WebSocketServer.Infrastructure.Cluster;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Logger whose Information-level Log throws (used to drive the catch blocks in
    /// PriorityListService.Add*). Error-level logging is a no-op so the catch can complete.
    /// </summary>
    internal sealed class ThrowOnInformationLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (logLevel == LogLevel.Information)
            {
                throw new InvalidOperationException("info-log-throws");
            }
        }
    }

    public class PriorityListServiceCovTests
    {
        [Fact]
        public void AddIp_LoggerThrows_CatchReturnsFalse()
        {
            var svc = new PriorityListService(new ThrowOnInformationLogger<PriorityListService>());
            // AddOrUpdate succeeds, LogInformation throws -> catch -> LogError -> return false.
            Assert.False(svc.AddIpToPriorityList("1.2.3.4", 5));
        }

        [Fact]
        public void AddConnection_LoggerThrows_CatchReturnsFalse()
        {
            var svc = new PriorityListService(new ThrowOnInformationLogger<PriorityListService>());
            Assert.False(svc.AddConnectionToPriorityList("conn-1", 5));
        }
    }

    public class AccessControlServiceCovTests
    {
        [Fact]
        public async Task IsAllowed_CidrParseOverflow_CatchReturnsFalse_DeniesByWhitelist()
        {
            var policy = new AccessControlPolicy { Enabled = true };
            // prefix length 999 makes the mask loop walk past the 4-byte array -> IndexOutOfRange
            // inside IsIpInCidrRange -> its catch -> false. IP is then not whitelisted -> deny.
            policy.IpWhitelist.Add("1.2.3.4/999");
            var svc = new AccessControlService(NullLogger<AccessControlService>.Instance, policy);

            Assert.False(await svc.IsAllowedAsync("5.6.7.8"));
        }
    }

    public class ServiceCollectionExtensionsCovTests
    {
        [Fact]
        public void ConfigureWebSocketRoute_NullServices_Throws()
        {
            // Reaches ConfigureWebSocketRouteCore's services == null guard.
            Assert.Throws<ArgumentNullException>(() =>
                ((IServiceCollection)null).ConfigureWebSocketRoute(o => { }));
        }

        [Fact]
        public void ConfigureWebSocketRoute_NoChannels_PrintsWarning_StillConfigures()
        {
            var services = new ServiceCollection();
            services.ConfigureWebSocketRoute(o =>
            {
                o.ApplicationServiceCollection = services;
                // Empty (non-null) channel table triggers the "no channel defined" warning branch
                // while still allowing the later Keys enumeration to run.
                o.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>();
            });

            using var provider = services.BuildServiceProvider();
            Assert.NotNull(provider.GetService<WebSocketRouteOption>());
        }

        [Fact]
        public void AddWebSocketServer_ConfigureNullsRequiredMembers_SafetyNetRestores()
        {
            var services = new ServiceCollection();
            // configure sets both required members to null -> the safety-net branches restore them.
            var builder = services.AddWebSocketServer(o =>
            {
                o.ApplicationServiceCollection = null;
                o.WebSocketChannels = null;
            });

            Assert.Same(services, builder.Options.ApplicationServiceCollection);
            Assert.NotNull(builder.Options.WebSocketChannels);
            Assert.True(builder.Options.WebSocketChannels.ContainsKey("/ws"));
        }

        [Fact]
        public void TryBuildTaskResultGetter_NonTaskGeneric_ReturnsFalse()
        {
            var m = typeof(WebSocketRouteServiceCollectionExtensions)
                .GetMethod("TryBuildTaskResultGetter", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);

            var args = new object[] { typeof(List<int>), null };
            var result = (bool)m.Invoke(null, args);
            Assert.False(result);
        }

        [Fact]
        public void TryBuildTaskResultGetter_TaskOfVoidTaskResult_ReturnsFalse()
        {
            var m = typeof(WebSocketRouteServiceCollectionExtensions)
                .GetMethod("TryBuildTaskResultGetter", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(m);

            var voidTaskResult = typeof(Task).Assembly.GetType("System.Threading.Tasks.VoidTaskResult");
            Assert.NotNull(voidTaskResult);
            var taskOfVoid = typeof(Task<>).MakeGenericType(voidTaskResult);

            var args = new object[] { taskOfVoid, null };
            var result = (bool)m.Invoke(null, args);
            Assert.False(result);
        }
    }

    /// <summary>
    /// Covers the cluster-start UseWebSocketServer overload. Mutates GlobalClusterCenter.ClusterContext
    /// and WebSocketRouteOption.ApplicationServices, so it runs in the StaticState collection.
    /// </summary>
    [Collection("StaticState")]
    public class MiddlewareClusterExtensionCovTests : IDisposable
    {
        private readonly IServiceProvider _previousServices;
        private readonly ClusterOption _previousClusterContext;

        public MiddlewareClusterExtensionCovTests()
        {
            _previousServices = WebSocketRouteOption.ApplicationServices;
            _previousClusterContext = GlobalClusterCenter.ClusterContext;
        }

        public void Dispose()
        {
            WebSocketRouteOption.ApplicationServices = _previousServices;
            GlobalClusterCenter.ClusterContext = _previousClusterContext;
        }

        private static Task DummyHandler(HttpContext context, ILogger<WebSocketRouteMiddleware> logger, WebSocketRouteOption options)
            => Task.CompletedTask;

        [Fact]
        public void UseWebSocketServer_Cluster_ChannelPresent_WiresMiddlewareAndContext()
        {
            var option = new WebSocketRouteOption
            {
                WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/cluster"] = DummyHandler
                }
            };
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(option);
            using var provider = services.BuildServiceProvider();
            var app = new ApplicationBuilder(provider);

            var result = app.UseWebSocketServer(provider, cluster =>
            {
                cluster.ChannelName = "/cluster";
                cluster.NodeLevel = ServiceLevel.Master;
            });

            Assert.Same(app, result);
            Assert.NotNull(GlobalClusterCenter.ClusterContext);
            Assert.Equal("/cluster", GlobalClusterCenter.ClusterContext.ChannelName);
            Assert.Same(provider, WebSocketRouteOption.ApplicationServices);
        }
    }
}
