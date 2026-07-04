using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    public class AccessControlServiceTests
    {
        private sealed class FakeGeoProvider : IGeoLocationProvider
        {
            public GeoLocationInfo Result { get; set; }
            public int Calls { get; private set; }

            public Task<GeoLocationInfo> GetLocationAsync(string ipAddress)
            {
                Calls++;
                return Task.FromResult(Result);
            }
        }

        private static AccessControlService Create(AccessControlPolicy policy, IGeoLocationProvider provider = null)
            => new AccessControlService(NullLogger<AccessControlService>.Instance, policy, provider);

        #region Constructor

        [Fact]
        public void Ctor_NullLogger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new AccessControlService(null, new AccessControlPolicy()));
        }

        [Fact]
        public void Ctor_NullPolicy_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new AccessControlService(NullLogger<AccessControlService>.Instance, null));
        }

        #endregion

        #region Enabled / disabled and IP parsing

        [Fact]
        public async Task Disabled_AllowsEverything_EvenInvalidIp()
        {
            var service = Create(new AccessControlPolicy { Enabled = false });

            Assert.True(await service.IsAllowedAsync("1.2.3.4"));
            Assert.True(await service.IsAllowedAsync("not-an-ip"));
            Assert.True(await service.IsAllowedAsync(null));
        }

        [Fact]
        public async Task Enabled_NullOrEmptyIp_Denied()
        {
            var service = Create(new AccessControlPolicy { Enabled = true });

            Assert.False(await service.IsAllowedAsync(null));
            Assert.False(await service.IsAllowedAsync(string.Empty));
        }

        [Fact]
        public async Task Enabled_InvalidIpFormat_Denied()
        {
            var service = Create(new AccessControlPolicy { Enabled = true });

            Assert.False(await service.IsAllowedAsync("999.999.999.999"));
            Assert.False(await service.IsAllowedAsync("hello"));
        }

        [Fact]
        public async Task Enabled_NoLists_NoGeoProvider_Allows()
        {
            var service = Create(new AccessControlPolicy { Enabled = true });

            Assert.True(await service.IsAllowedAsync("8.8.8.8"));
        }

        #endregion

        #region IP whitelist / blacklist

        [Fact]
        public async Task Whitelist_ExactMatch_Allowed()
        {
            var policy = new AccessControlPolicy { Enabled = true };
            policy.IpWhitelist.Add("8.8.8.8");
            var service = Create(policy);

            Assert.True(await service.IsAllowedAsync("8.8.8.8"));
        }

        [Fact]
        public async Task Whitelist_NotInList_Denied()
        {
            var policy = new AccessControlPolicy { Enabled = true };
            policy.IpWhitelist.Add("8.8.8.8");
            var service = Create(policy);

            Assert.False(await service.IsAllowedAsync("8.8.4.4"));
        }

        [Fact]
        public async Task Whitelist_CidrMatch_Allowed()
        {
            var policy = new AccessControlPolicy { Enabled = true };
            policy.IpWhitelist.Add("192.168.1.0/24");
            var service = Create(policy);

            Assert.True(await service.IsAllowedAsync("192.168.1.55"));
            Assert.False(await service.IsAllowedAsync("192.168.2.55"));
        }

        [Fact]
        public async Task Whitelist_CidrZeroPrefix_MatchesEverything()
        {
            var policy = new AccessControlPolicy { Enabled = true };
            policy.IpWhitelist.Add("0.0.0.0/0");
            var service = Create(policy);

            Assert.True(await service.IsAllowedAsync("1.2.3.4"));
            Assert.True(await service.IsAllowedAsync("255.255.255.255"));
        }

        [Fact]
        public async Task Whitelist_NonOctetAlignedCidr_UsesPartialMaskByte()
        {
            var policy = new AccessControlPolicy { Enabled = true };
            policy.IpWhitelist.Add("10.0.0.0/12"); // 10.0.0.0 - 10.15.255.255
            var service = Create(policy);

            Assert.True(await service.IsAllowedAsync("10.15.1.1"));
            Assert.False(await service.IsAllowedAsync("10.16.0.1"));
        }

        [Fact]
        public async Task Whitelist_MalformedEntries_AreIgnored()
        {
            var policy = new AccessControlPolicy { Enabled = true };
            policy.IpWhitelist.Add("   ");             // whitespace: skipped
            policy.IpWhitelist.Add("abc/24");          // bad network address
            policy.IpWhitelist.Add("10.0.0.0/x");      // bad prefix
            policy.IpWhitelist.Add("10.0.0.0/8/9");    // too many parts
            policy.IpWhitelist.Add("not-an-ip");       // bad plain entry
            var service = Create(policy);

            // No usable entry matched -> denied
            Assert.False(await service.IsAllowedAsync("10.1.2.3"));
        }

        [Fact]
        public async Task Whitelist_Ipv6AgainstIpv4Cidr_NoMatch()
        {
            var policy = new AccessControlPolicy { Enabled = true };
            policy.IpWhitelist.Add("10.0.0.0/8");
            var service = Create(policy);

            Assert.False(await service.IsAllowedAsync("2001:db8::1"));
        }

        [Fact]
        public async Task Whitelist_Ipv6ExactMatch_Allowed()
        {
            var policy = new AccessControlPolicy { Enabled = true };
            policy.IpWhitelist.Add("2001:db8::1");
            var service = Create(policy);

            Assert.True(await service.IsAllowedAsync("2001:db8::1"));
        }

        [Fact]
        public async Task Blacklist_Match_Denied()
        {
            var policy = new AccessControlPolicy { Enabled = true };
            policy.IpBlacklist.Add("8.8.8.8");
            var service = Create(policy);

            Assert.False(await service.IsAllowedAsync("8.8.8.8"));
            Assert.True(await service.IsAllowedAsync("8.8.4.4"));
        }

        [Fact]
        public async Task Blacklist_CidrMatch_Denied()
        {
            var policy = new AccessControlPolicy { Enabled = true };
            policy.IpBlacklist.Add("10.0.0.0/8");
            var service = Create(policy);

            Assert.False(await service.IsAllowedAsync("10.200.1.1"));
            Assert.True(await service.IsAllowedAsync("11.0.0.1"));
        }

        [Fact]
        public async Task Whitelist_TakesPrecedence_BlacklistNeverEvaluated()
        {
            // When a whitelist exists, membership fully decides the outcome.
            var policy = new AccessControlPolicy { Enabled = true };
            policy.IpWhitelist.Add("8.8.8.8");
            policy.IpBlacklist.Add("8.8.8.8");
            var service = Create(policy);

            Assert.True(await service.IsAllowedAsync("8.8.8.8"));
        }

        #endregion

        #region Geo location policy

        private static AccessControlPolicy GeoPolicy()
            => new AccessControlPolicy { Enabled = true, EnableGeoLocationLookup = true };

        private static GeoLocationInfo BeijingCn()
            => new GeoLocationInfo { CountryCode = "CN", CountryName = "China", RegionName = "Beijing", CityName = "Beijing" };

        [Fact]
        public async Task Geo_ProviderReturnsNull_AllowedByDefault()
        {
            var provider = new FakeGeoProvider { Result = null };
            var policy = GeoPolicy();
            policy.CountryWhitelist.Add("US");
            var service = Create(policy, provider);

            Assert.True(await service.IsAllowedAsync("8.8.8.8"));
            Assert.Equal(1, provider.Calls);
        }

        [Fact]
        public async Task Geo_CountryWhitelist_MatchAllowed_MismatchDenied()
        {
            var provider = new FakeGeoProvider { Result = BeijingCn() };
            var policy = GeoPolicy();
            policy.CountryWhitelist.Add("cn"); // case-insensitive
            Assert.True(await Create(policy, provider).IsAllowedAsync("8.8.8.8"));

            var policy2 = GeoPolicy();
            policy2.CountryWhitelist.Add("US");
            Assert.False(await Create(policy2, new FakeGeoProvider { Result = BeijingCn() }).IsAllowedAsync("8.8.8.8"));
        }

        [Fact]
        public async Task Geo_CountryBlacklist_MatchDenied()
        {
            var policy = GeoPolicy();
            policy.CountryBlacklist.Add("CN");
            Assert.False(await Create(policy, new FakeGeoProvider { Result = BeijingCn() }).IsAllowedAsync("8.8.8.8"));

            var policy2 = GeoPolicy();
            policy2.CountryBlacklist.Add("US");
            Assert.True(await Create(policy2, new FakeGeoProvider { Result = BeijingCn() }).IsAllowedAsync("8.8.8.8"));
        }

        [Fact]
        public async Task Geo_CityWhitelist_SupportsCountryPrefixedAndPlainNames()
        {
            var policy = GeoPolicy();
            policy.CityWhitelist.Add("CN:Beijing");
            Assert.True(await Create(policy, new FakeGeoProvider { Result = BeijingCn() }).IsAllowedAsync("8.8.8.8"));

            var policy2 = GeoPolicy();
            policy2.CityWhitelist.Add("beijing"); // plain city name, case-insensitive
            Assert.True(await Create(policy2, new FakeGeoProvider { Result = BeijingCn() }).IsAllowedAsync("8.8.8.8"));

            var policy3 = GeoPolicy();
            policy3.CityWhitelist.Add("US:New York");
            Assert.False(await Create(policy3, new FakeGeoProvider { Result = BeijingCn() }).IsAllowedAsync("8.8.8.8"));
        }

        [Fact]
        public async Task Geo_CityBlacklist_MatchDenied()
        {
            var policy = GeoPolicy();
            policy.CityBlacklist.Add("CN:Beijing");
            Assert.False(await Create(policy, new FakeGeoProvider { Result = BeijingCn() }).IsAllowedAsync("8.8.8.8"));

            var policy2 = GeoPolicy();
            policy2.CityBlacklist.Add("Shanghai");
            Assert.True(await Create(policy2, new FakeGeoProvider { Result = BeijingCn() }).IsAllowedAsync("8.8.8.8"));
        }

        [Fact]
        public async Task Geo_RegionWhitelist_MatchAllowed_MismatchDenied()
        {
            var policy = GeoPolicy();
            policy.RegionWhitelist.Add("CN:Beijing");
            Assert.True(await Create(policy, new FakeGeoProvider { Result = BeijingCn() }).IsAllowedAsync("8.8.8.8"));

            var policy2 = GeoPolicy();
            policy2.RegionWhitelist.Add("US:California");
            Assert.False(await Create(policy2, new FakeGeoProvider { Result = BeijingCn() }).IsAllowedAsync("8.8.8.8"));
        }

        [Fact]
        public async Task Geo_RegionBlacklist_MatchDenied()
        {
            var policy = GeoPolicy();
            policy.RegionBlacklist.Add("Beijing"); // plain region name
            Assert.False(await Create(policy, new FakeGeoProvider { Result = BeijingCn() }).IsAllowedAsync("8.8.8.8"));

            var policy2 = GeoPolicy();
            policy2.RegionBlacklist.Add("US:California");
            Assert.True(await Create(policy2, new FakeGeoProvider { Result = BeijingCn() }).IsAllowedAsync("8.8.8.8"));
        }

        [Fact]
        public async Task Geo_LookupDisabled_ProviderNeverCalled()
        {
            var provider = new FakeGeoProvider { Result = BeijingCn() };
            var policy = GeoPolicy();
            policy.EnableGeoLocationLookup = false;
            policy.CountryBlacklist.Add("CN");
            var service = Create(policy, provider);

            Assert.True(await service.IsAllowedAsync("8.8.8.8"));
            Assert.Equal(0, provider.Calls);
        }

        [Fact]
        public async Task Geo_WhitelistedIp_SkipsGeoLookup()
        {
            var provider = new FakeGeoProvider { Result = BeijingCn() };
            var policy = GeoPolicy();
            policy.IpWhitelist.Add("8.8.8.8");
            policy.CountryBlacklist.Add("CN");
            var service = Create(policy, provider);

            Assert.True(await service.IsAllowedAsync("8.8.8.8"));
            Assert.Equal(0, provider.Calls);
        }

        #endregion

        #region Geo cache

        [Fact]
        public async Task GeoCache_SecondLookup_UsesCache()
        {
            var provider = new FakeGeoProvider { Result = BeijingCn() };
            var policy = GeoPolicy();
            policy.GeoLocationCacheSeconds = 3600;
            policy.CountryWhitelist.Add("CN");
            var service = Create(policy, provider);

            Assert.True(await service.IsAllowedAsync("8.8.8.8"));
            Assert.True(await service.IsAllowedAsync("8.8.8.8"));
            Assert.Equal(1, provider.Calls);
        }

        [Fact]
        public async Task GeoCache_ClearCache_ForcesNewLookup()
        {
            var provider = new FakeGeoProvider { Result = BeijingCn() };
            var policy = GeoPolicy();
            policy.CountryWhitelist.Add("CN");
            var service = Create(policy, provider);

            await service.IsAllowedAsync("8.8.8.8");
            service.ClearCache();
            await service.IsAllowedAsync("8.8.8.8");

            Assert.Equal(2, provider.Calls);
        }

        [Fact]
        public async Task GeoCache_ExpiredEntry_IsRefetched()
        {
            var provider = new FakeGeoProvider { Result = BeijingCn() };
            var policy = GeoPolicy();
            policy.GeoLocationCacheSeconds = -1; // entries expire immediately
            policy.CountryWhitelist.Add("CN");
            var service = Create(policy, provider);

            await service.IsAllowedAsync("8.8.8.8");
            await service.IsAllowedAsync("8.8.8.8");

            Assert.Equal(2, provider.Calls);
        }

        #endregion

        #region Policy defaults

        [Fact]
        public void AccessControlPolicy_Defaults()
        {
            var policy = new AccessControlPolicy();

            Assert.False(policy.Enabled);
            Assert.NotNull(policy.IpWhitelist);
            Assert.Empty(policy.IpWhitelist);
            Assert.NotNull(policy.IpBlacklist);
            Assert.NotNull(policy.CountryWhitelist);
            Assert.NotNull(policy.CountryBlacklist);
            Assert.NotNull(policy.CityWhitelist);
            Assert.NotNull(policy.CityBlacklist);
            Assert.NotNull(policy.RegionWhitelist);
            Assert.NotNull(policy.RegionBlacklist);
            Assert.Equal(AccessDeniedAction.CloseConnection, policy.DeniedAction);
            Assert.Equal("Access denied", policy.DenialMessage);
            Assert.True(policy.EnableGeoLocationLookup);
            Assert.Equal(3600, policy.GeoLocationCacheSeconds);
        }

        [Fact]
        public void AccessDeniedAction_HasExpectedValues()
        {
            Assert.Equal(0, (int)AccessDeniedAction.CloseConnection);
            Assert.Equal(1, (int)AccessDeniedAction.ReturnForbidden);
            Assert.Equal(2, (int)AccessDeniedAction.ReturnUnauthorized);
        }

        [Fact]
        public void GeoLocationInfo_Defaults_AllNull()
        {
            var info = new GeoLocationInfo();

            Assert.Null(info.CountryCode);
            Assert.Null(info.CountryName);
            Assert.Null(info.RegionName);
            Assert.Null(info.CityName);
            Assert.Null(info.Latitude);
            Assert.Null(info.Longitude);
        }

        #endregion
    }
}
