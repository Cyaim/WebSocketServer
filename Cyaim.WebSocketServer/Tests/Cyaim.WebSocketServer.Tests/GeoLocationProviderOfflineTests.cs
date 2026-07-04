using System.Text;
using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Tests for the offline geo location providers.
    /// Only constructor validation and network-free lookup paths are exercised;
    /// the online (HTTP) providers are intentionally not tested here.
    /// </summary>
    public class GeoLocationProviderOfflineTests : IDisposable
    {
        private readonly string _tempDir;

        public GeoLocationProviderOfflineTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "cyaim-geo-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        private string WriteFile(string name, byte[] content)
        {
            var path = Path.Combine(_tempDir, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        private string MissingFile => Path.Combine(_tempDir, "does-not-exist.dat");

        #region Constructor validation

        [Fact]
        public void ChunZhen_Ctor_NullArgs_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => new ChunZhenOfflineGeoLocationProvider(null, "x.dat"));
            Assert.Throws<ArgumentNullException>(() => new ChunZhenOfflineGeoLocationProvider(NullLogger<ChunZhenOfflineGeoLocationProvider>.Instance, null));
        }

        [Fact]
        public void ChunZhen_Ctor_MissingFile_Throws()
        {
            Assert.Throws<FileNotFoundException>(() => new ChunZhenOfflineGeoLocationProvider(NullLogger<ChunZhenOfflineGeoLocationProvider>.Instance, MissingFile));
        }

        [Fact]
        public void MaxMind_Ctor_NullArgs_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => new MaxMindOfflineGeoLocationProvider(null, "x.mmdb"));
            Assert.Throws<ArgumentNullException>(() => new MaxMindOfflineGeoLocationProvider(NullLogger<MaxMindOfflineGeoLocationProvider>.Instance, null));
        }

        [Fact]
        public void MaxMind_Ctor_MissingFile_Throws()
        {
            Assert.Throws<FileNotFoundException>(() => new MaxMindOfflineGeoLocationProvider(NullLogger<MaxMindOfflineGeoLocationProvider>.Instance, MissingFile));
        }

        [Fact]
        public void IpIpNet_Ctor_NullArgs_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => new IpIpNetOfflineGeoLocationProvider(null, "x.datx"));
            Assert.Throws<ArgumentNullException>(() => new IpIpNetOfflineGeoLocationProvider(NullLogger<IpIpNetOfflineGeoLocationProvider>.Instance, null));
        }

        [Fact]
        public void IpIpNet_Ctor_MissingFile_Throws()
        {
            Assert.Throws<FileNotFoundException>(() => new IpIpNetOfflineGeoLocationProvider(NullLogger<IpIpNetOfflineGeoLocationProvider>.Instance, MissingFile));
        }

        #endregion

        #region ChunZhen lookup

        /// <summary>
        /// Builds a minimal qqwry.dat-shaped database with a single index record
        /// starting at IP 0.0.0.0 whose location string is stored at offset 8.
        /// </summary>
        private static byte[] BuildChunZhenDatabase(string location)
        {
            var locationBytes = Encoding.UTF8.GetBytes(location);
            uint indexOffset = (uint)(8 + locationBytes.Length + 1);
            var data = new byte[indexOffset + 7];

            // Header: first index offset, last index offset (= first + 7 -> one record)
            BitConverter.GetBytes(indexOffset).CopyTo(data, 0);
            BitConverter.GetBytes(indexOffset + 7).CopyTo(data, 4);

            // Location string (NUL terminated) at offset 8
            locationBytes.CopyTo(data, 8);
            data[8 + locationBytes.Length] = 0;

            // Index record: start IP 0 (4 bytes LE), location offset 8 (3 bytes LE)
            data[indexOffset + 0] = 0;
            data[indexOffset + 1] = 0;
            data[indexOffset + 2] = 0;
            data[indexOffset + 3] = 0;
            data[indexOffset + 4] = 8;
            data[indexOffset + 5] = 0;
            data[indexOffset + 6] = 0;

            return data;
        }

        private ChunZhenOfflineGeoLocationProvider CreateChunZhen(byte[] db)
        {
            var path = WriteFile("qqwry-" + Guid.NewGuid().ToString("N") + ".dat", db);
            return new ChunZhenOfflineGeoLocationProvider(NullLogger<ChunZhenOfflineGeoLocationProvider>.Instance, path);
        }

        [Fact]
        public async Task ChunZhen_NullOrEmptyIp_ReturnsNull()
        {
            var provider = CreateChunZhen(BuildChunZhenDatabase("China Beijing Haidian"));

            Assert.Null(await provider.GetLocationAsync(null));
            Assert.Null(await provider.GetLocationAsync(string.Empty));
        }

        [Theory]
        [InlineData("127.0.0.1")]
        [InlineData("::1")]
        [InlineData("10.1.2.3")]
        [InlineData("172.16.0.1")]
        [InlineData("172.31.255.255")]
        [InlineData("192.168.0.1")]
        public async Task ChunZhen_LocalOrPrivateIp_ReturnsNull(string ip)
        {
            var provider = CreateChunZhen(BuildChunZhenDatabase("China Beijing Haidian"));

            Assert.Null(await provider.GetLocationAsync(ip));
        }

        [Fact]
        public async Task ChunZhen_InvalidOrIpv6Address_ReturnsNull()
        {
            var provider = CreateChunZhen(BuildChunZhenDatabase("China Beijing Haidian"));

            Assert.Null(await provider.GetLocationAsync("not-an-ip"));
            Assert.Null(await provider.GetLocationAsync("2001:4860:4860::8888"));
        }

        [Fact]
        public async Task ChunZhen_PublicIp_ParsesLocationString()
        {
            var provider = CreateChunZhen(BuildChunZhenDatabase("China Beijing Haidian"));

            var info = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(info);
            Assert.Equal("China", info.CountryName);
            Assert.Equal("CN", info.CountryCode);
            Assert.Equal("Beijing", info.RegionName);
            Assert.Equal("Haidian", info.CityName);
        }

        [Fact]
        public async Task ChunZhen_TwoPartLocation_CityFallsBackToRegion()
        {
            var provider = CreateChunZhen(BuildChunZhenDatabase("Japan Tokyo"));

            var info = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(info);
            Assert.Equal("Japan", info.CountryName);
            Assert.Equal("JP", info.CountryCode);
            Assert.Equal("Tokyo", info.RegionName);
            Assert.Equal("Tokyo", info.CityName);
        }

        [Fact]
        public async Task ChunZhen_UnknownCountryName_CountryCodeNull()
        {
            var provider = CreateChunZhen(BuildChunZhenDatabase("Atlantis Ocean Deep"));

            var info = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(info);
            Assert.Equal("Atlantis", info.CountryName);
            Assert.Null(info.CountryCode);
        }

        [Fact]
        public async Task ChunZhen_TruncatedDatabase_ReturnsNull()
        {
            // Database shorter than the 8-byte header -> SearchLocation bails out
            var provider = CreateChunZhen(new byte[] { 1, 2, 3 });

            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        #endregion

        #region ipip.net lookup (placeholder implementation)

        [Fact]
        public async Task IpIpNet_PublicIp_ReturnsNull_SearchNotImplemented()
        {
            // The offline ipip.net provider's SearchLocation is a documented placeholder
            // returning null; this test pins that behavior.
            var path = WriteFile("ipip.datx", new byte[] { 1, 2, 3, 4 });
            var provider = new IpIpNetOfflineGeoLocationProvider(NullLogger<IpIpNetOfflineGeoLocationProvider>.Instance, path);

            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        [Fact]
        public async Task IpIpNet_NullPrivateOrInvalidIp_ReturnsNull()
        {
            var path = WriteFile("ipip2.datx", new byte[] { 1, 2, 3, 4 });
            var provider = new IpIpNetOfflineGeoLocationProvider(NullLogger<IpIpNetOfflineGeoLocationProvider>.Instance, path);

            Assert.Null(await provider.GetLocationAsync(null));
            Assert.Null(await provider.GetLocationAsync("192.168.5.5"));
            Assert.Null(await provider.GetLocationAsync("bogus"));
        }

        #endregion

        #region MaxMind lookup (reflection-based, package not installed)

        [Fact]
        public async Task MaxMind_PublicIp_WithoutMaxMindPackage_ReturnsNull()
        {
            // MaxMind.GeoIP2 is not referenced by the test project, so the reflection-based
            // loader cannot find the assembly and the provider must return null gracefully.
            var path = WriteFile("geo.mmdb", new byte[] { 0xAB, 0xCD });
            var provider = new MaxMindOfflineGeoLocationProvider(NullLogger<MaxMindOfflineGeoLocationProvider>.Instance, path);

            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
            // Second call goes through the "already loaded" fast path
            Assert.Null(await provider.GetLocationAsync("8.8.4.4"));
        }

        [Fact]
        public async Task MaxMind_NullPrivateOrInvalidIp_ReturnsNull()
        {
            var path = WriteFile("geo2.mmdb", new byte[] { 0xAB });
            var provider = new MaxMindOfflineGeoLocationProvider(NullLogger<MaxMindOfflineGeoLocationProvider>.Instance, path);

            Assert.Null(await provider.GetLocationAsync(null));
            Assert.Null(await provider.GetLocationAsync("10.0.0.1"));
            Assert.Null(await provider.GetLocationAsync("nope"));
        }

        #endregion
    }
}
