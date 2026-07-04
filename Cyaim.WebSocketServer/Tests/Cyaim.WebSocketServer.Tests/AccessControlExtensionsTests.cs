using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cyaim.WebSocketServer.Tests
{
    public class AccessControlExtensionsTests
    {
        private sealed class FakeGeoProvider : IGeoLocationProvider
        {
            public Task<GeoLocationInfo> GetLocationAsync(string ipAddress) => Task.FromResult<GeoLocationInfo>(null);
        }

        [Fact]
        public void AddAccessControl_WithConfigureAction_RegistersPolicyAndService()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var result = services.AddAccessControl(p =>
            {
                p.Enabled = true;
                p.IpWhitelist.Add("1.2.3.4");
            });

            Assert.Same(services, result);
            using var provider = services.BuildServiceProvider();
            var policy = provider.GetRequiredService<AccessControlPolicy>();
            Assert.True(policy.Enabled);
            Assert.Contains("1.2.3.4", policy.IpWhitelist);
            Assert.NotNull(provider.GetRequiredService<AccessControlService>());
        }

        [Fact]
        public void AddAccessControl_NoConfigureAction_RegistersDefaultPolicy()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddAccessControl();

            using var provider = services.BuildServiceProvider();
            Assert.False(provider.GetRequiredService<AccessControlPolicy>().Enabled);
        }

        [Fact]
        public void AddAccessControl_FromConfiguration_BindsSection()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["AccessControlPolicy:Enabled"] = "true",
                    ["AccessControlPolicy:DenialMessage"] = "nope",
                    ["AccessControlPolicy:DeniedAction"] = "ReturnForbidden",
                    ["AccessControlPolicy:IpWhitelist:0"] = "10.0.0.0/8",
                    ["AccessControlPolicy:CountryBlacklist:0"] = "XX",
                    ["AccessControlPolicy:GeoLocationCacheSeconds"] = "120",
                })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddAccessControl(configuration);

            using var provider = services.BuildServiceProvider();
            var policy = provider.GetRequiredService<AccessControlPolicy>();
            Assert.True(policy.Enabled);
            Assert.Equal("nope", policy.DenialMessage);
            Assert.Equal(AccessDeniedAction.ReturnForbidden, policy.DeniedAction);
            Assert.Contains("10.0.0.0/8", policy.IpWhitelist);
            Assert.Contains("XX", policy.CountryBlacklist);
            Assert.Equal(120, policy.GeoLocationCacheSeconds);
            Assert.NotNull(provider.GetRequiredService<AccessControlService>());
        }

        [Fact]
        public void AddAccessControl_FromConfiguration_CustomSectionName()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["MySection:Enabled"] = "true",
                })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddAccessControl(configuration, "MySection");

            using var provider = services.BuildServiceProvider();
            Assert.True(provider.GetRequiredService<AccessControlPolicy>().Enabled);
        }

        [Fact]
        public void AddGeoLocationProvider_RegistersProvider()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var result = services.AddGeoLocationProvider<FakeGeoProvider>();

            Assert.Same(services, result);
            using var provider = services.BuildServiceProvider();
            Assert.IsType<FakeGeoProvider>(provider.GetRequiredService<IGeoLocationProvider>());
        }

        [Fact]
        public void AddGeoLocationProvider_ServiceReceivesProvider()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddGeoLocationProvider<FakeGeoProvider>();
            services.AddAccessControl(p => p.Enabled = true);

            using var provider = services.BuildServiceProvider();
            // AccessControlService takes the provider as an optional dependency
            Assert.NotNull(provider.GetRequiredService<AccessControlService>());
        }

        [Fact]
        public void AddQpsPriorityPolicy_WithConfigureAction_RegistersServices()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var result = services.AddQpsPriorityPolicy(p =>
            {
                p.Enabled = true;
                p.GlobalBandwidthLimit = 1234;
            });

            Assert.Same(services, result);
            using var provider = services.BuildServiceProvider();
            var policy = provider.GetRequiredService<QpsPriorityPolicy>();
            Assert.True(policy.Enabled);
            Assert.Equal(1234, policy.GlobalBandwidthLimit);
            Assert.NotNull(provider.GetRequiredService<PriorityListService>());
            Assert.NotNull(provider.GetRequiredService<QpsPriorityManager>());
        }

        [Fact]
        public void AddQpsPriorityPolicy_FromConfiguration_BindsSection()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["QpsPriorityPolicy:Enabled"] = "true",
                    ["QpsPriorityPolicy:GlobalBandwidthLimit"] = "5000",
                    ["QpsPriorityPolicy:DefaultPriority"] = "2",
                    ["QpsPriorityPolicy:PriorityBandwidthRatios:3"] = "0.5",
                })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddQpsPriorityPolicy(configuration);

            using var provider = services.BuildServiceProvider();
            var policy = provider.GetRequiredService<QpsPriorityPolicy>();
            Assert.True(policy.Enabled);
            Assert.Equal(5000, policy.GlobalBandwidthLimit);
            Assert.Equal(2, policy.DefaultPriority);
            Assert.Equal(0.5, policy.PriorityBandwidthRatios[3]);
            Assert.NotNull(provider.GetRequiredService<QpsPriorityManager>());
        }

        [Fact]
        public void AddQpsPriorityPolicy_NoConfigureAction_RegistersDefaults()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddQpsPriorityPolicy();

            using var provider = services.BuildServiceProvider();
            Assert.False(provider.GetRequiredService<QpsPriorityPolicy>().Enabled);
        }
    }
}
