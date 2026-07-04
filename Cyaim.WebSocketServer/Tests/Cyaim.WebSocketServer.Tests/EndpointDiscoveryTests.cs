using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cyaim.WebSocketServer.Tests.DiscoveryControllers
{
    /// <summary>
    /// Controller discovered by the assembly-scanning tests below. Lives in a dedicated
    /// namespace so WatchAssemblyNamespacePrefix only picks up this type.
    /// </summary>
    public class DiscoveryController
    {
        [WebSocket]
        public string Ping() => "pong";

        [WebSocket("alias")]
        public string Named() => "named";

        [WebSocket]
        public async Task<int> Compute()
        {
            await Task.Yield();
            return 1;
        }

        [WebSocket]
        public async Task Fire()
        {
            await Task.Yield();
        }

        public string NotAnEndpoint() => "no";
    }
}

namespace Cyaim.WebSocketServer.Tests
{
    using Cyaim.WebSocketServer.Tests.DiscoveryControllers;

    public class EndpointDiscoveryTests
    {
        private static WebSocketRouteOption Discover(IServiceCollection services = null, IConfiguration configuration = null)
        {
            services ??= new ServiceCollection();
            WebSocketRouteOption captured = null;
            services.ConfigureWebSocketRoute(configuration, o =>
            {
                o.ApplicationServiceCollection = services;
                o.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/ws"] = (context, logger, options) => Task.CompletedTask
                };
                o.WatchAssemblyPath = typeof(DiscoveryController).Assembly.Location;
                o.WatchAssemblyNamespacePrefix = "Cyaim.WebSocketServer.Tests.DiscoveryControllers";
                captured = o;
            });
            return captured;
        }

        [Fact]
        public void Discovery_FindsWebSocketAttributedMethods_LowercasePaths()
        {
            var option = Discover();
            var context = option.WatchAssemblyContext;

            Assert.True(context.WatchMethods.ContainsKey("discovery.ping"));
            Assert.True(context.WatchMethods.ContainsKey("discovery.compute"));
            Assert.True(context.WatchMethods.ContainsKey("discovery.fire"));
            // [WebSocket("alias")] renames the endpoint
            Assert.True(context.WatchMethods.ContainsKey("discovery.alias"));
            Assert.False(context.WatchMethods.ContainsKey("discovery.named"));
            // Unattributed methods are not endpoints
            Assert.False(context.WatchMethods.ContainsKey("discovery.notanendpoint"));
        }

        [Fact]
        public void Discovery_WatchMethods_AreCaseInsensitive()
        {
            var option = Discover();

            Assert.True(option.WatchAssemblyContext.WatchMethods.ContainsKey("Discovery.Ping"));
            Assert.True(option.WatchAssemblyContext.WatchMethods.ContainsKey("DISCOVERY.ALIAS"));
        }

        [Fact]
        public void Discovery_PopulatesEndpointMetadata()
        {
            var option = Discover();
            var context = option.WatchAssemblyContext;

            var ping = context.WatchEndPoint.Single(e => e.MethodPath == "discovery.ping");
            Assert.Equal("DiscoveryController", ping.Controller);
            Assert.Equal("Ping", ping.Action);
            Assert.NotNull(ping.MethodInfo);
            Assert.Equal(typeof(DiscoveryController).FullName, ping.Class.FullName);
            Assert.Same(ping.Class, context.GetEndpointClass("discovery.ping"));
        }

        [Fact]
        public void Discovery_ComputesConstructorAndMethodParameterTables()
        {
            var option = Discover();
            var context = option.WatchAssemblyContext;

            var controllerType = context.WatchEndPoint.First().Class;
            Assert.True(context.AssemblyConstructors.ContainsKey(controllerType));
            Assert.True(context.MaxConstructorParameters.ContainsKey(controllerType));
            Assert.Empty(context.MaxConstructorParameters[controllerType].ParameterInfos);

            var pingMethod = context.WatchMethods["discovery.ping"];
            Assert.True(context.MethodParameters.ContainsKey(pingMethod));
            Assert.Empty(context.MethodParameters[pingMethod]);
        }

        [Fact]
        public void Discovery_TaskResultGetters_OnlyForGenericTaskEndpoints()
        {
            var option = Discover();
            var context = option.WatchAssemblyContext;

            var compute = context.WatchMethods["discovery.compute"];
            var fire = context.WatchMethods["discovery.fire"];
            var ping = context.WatchMethods["discovery.ping"];

            Assert.True(context.MethodTaskResultGetters.ContainsKey(compute));
            Assert.False(context.MethodTaskResultGetters.ContainsKey(fire));   // plain Task
            Assert.False(context.MethodTaskResultGetters.ContainsKey(ping));   // sync

            // The compiled getter must read Task<T>.Result
            var task = Task.FromResult(41);
            Assert.Equal(41, context.MethodTaskResultGetters[compute](task));
        }

        [Fact]
        public void ConfigureWebSocketRoute_WithBandwidthConfiguration_RegistersOptions()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["BandwidthLimitPolicy:Enabled"] = "true",
                    ["BandwidthLimitPolicy:GlobalChannelBandwidthLimit:/ws"] = "4096",
                })
                .Build();
            var services = new ServiceCollection();

            Discover(services, configuration);

            using var provider = services.BuildServiceProvider();
            var policy = provider.GetRequiredService<IOptions<BandwidthLimitPolicy>>().Value;
            Assert.True(policy.Enabled);
            Assert.Equal(4096, policy.GlobalChannelBandwidthLimit["/ws"]);
        }

        [Fact]
        public void GetClassAccessParam_Generic_ExtractsAttributedMethods()
        {
            var endpoints = WebSocketRouteServiceCollectionExtensions.GetClassAccessParam<DiscoveryController>();

            Assert.Equal(4, endpoints.Length);
            Assert.All(endpoints, e => Assert.Equal("DiscoveryController", e.Controller));
            var named = endpoints.Single(e => e.Action == "Named");
            Assert.Contains("alias", named.Methods);
            Assert.DoesNotContain(endpoints, e => e.Action == "NotAnEndpoint");
        }

        [Fact]
        public void WebSocketAttribute_MethodProperty()
        {
            Assert.Null(new WebSocketAttribute().Method);
            Assert.Equal("m", new WebSocketAttribute("m").Method);
            Assert.Equal("x", new WebSocketAttribute { Method = "x" }.Method);
        }
    }

    public class I18nUpdateRoundTripTests
    {
        [Fact]
        public void UpdateI18nText_ExistingFile_AppliesValues_AndKeepsMissingKeys()
        {
            // Fixed: text fields are now writable ("public static string"), so resource files
            // actually load; keys missing from the file keep their default text instead of
            // being nulled out.
            var fields = typeof(I18nText)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(string)
                            && !f.IsLiteral
                            && !f.GetCustomAttributes<JsonIgnoreAttribute>().Any())
                .ToArray();
            Assert.NotEmpty(fields);
            var snapshot = fields.ToDictionary(f => f.Name, f => (string)f.GetValue(null));

            var overriddenField = fields[0];
            var resource = new Dictionary<string, string> { [overriddenField.Name] = "OVERRIDDEN_BY_TEST" };

            var path = Path.Combine(Path.GetTempPath(), "cyaim-i18n-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(resource));
            try
            {
                I18nText.UpdateI18nText(path);

                Assert.Equal("OVERRIDDEN_BY_TEST", (string)overriddenField.GetValue(null));
                // Keys missing from the resource file keep their default values
                foreach (var field in fields.Skip(1))
                {
                    Assert.Equal(snapshot[field.Name], (string)field.GetValue(null));
                }
            }
            finally
            {
                File.Delete(path);
                // Restore the overridden text for other tests
                overriddenField.SetValue(null, snapshot[overriddenField.Name]);
            }
        }
    }
}
