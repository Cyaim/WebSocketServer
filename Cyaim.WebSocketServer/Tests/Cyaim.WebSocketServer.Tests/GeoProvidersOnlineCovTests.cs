using System.Net;
using System.Reflection;
using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Remaining line-coverage for the online geo providers: empty HTTP responses,
    /// the outer catch (HttpClient throws), the top-level null-ip guard, and the private
    /// IsLocalOrPrivateIp guard branches (null and loopback) invoked via reflection.
    /// All network access is stubbed; no real requests are made.
    /// </summary>
    public class GeoProvidersOnlineCovTests
    {
        private const string StubUrl = "http://localhost:1/{ip}";

        /// <summary>Handler that returns a fixed body with 200 OK.</summary>
        private sealed class BodyHandler : HttpMessageHandler
        {
            private readonly string _body;
            public BodyHandler(string body) { _body = body; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) });
        }

        /// <summary>Handler that always throws, to drive each provider's outer catch.</summary>
        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => throw new HttpRequestException("boom-network");
        }

        private static HttpClient EmptyResponseClient() => new HttpClient(new BodyHandler(string.Empty));
        private static HttpClient ThrowingClient() => new HttpClient(new ThrowingHandler());

        private static bool IsLocalOrPrivate(object provider, object ip)
        {
            var m = provider.GetType().GetMethod("IsLocalOrPrivateIp", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(m);
            return (bool)m.Invoke(provider, new[] { ip });
        }

        // ---- ip-api.com ----
        [Fact]
        public async Task IpApiCom_EmptyResponse_Throws_And_Guards()
        {
            var p = new IpApiComGeoLocationProvider(NullLogger<IpApiComGeoLocationProvider>.Instance, EmptyResponseClient(), StubUrl);
            Assert.Null(await p.GetLocationAsync("8.8.8.8")); // empty body -> null

            var t = new IpApiComGeoLocationProvider(NullLogger<IpApiComGeoLocationProvider>.Instance, ThrowingClient(), StubUrl);
            Assert.Null(await t.GetLocationAsync("8.8.8.8")); // catch -> null

            Assert.True(IsLocalOrPrivate(p, null));
            Assert.True(IsLocalOrPrivate(p, "::1"));
        }

        // ---- ipapi.co ----
        [Fact]
        public async Task IpApiCo_NullIp_EmptyResponse_Throws_And_Guards()
        {
            var p = new IpApiCoGeoLocationProvider(NullLogger<IpApiCoGeoLocationProvider>.Instance, EmptyResponseClient(), apiUrl: StubUrl);
            Assert.Null(await p.GetLocationAsync(null));       // top guard
            Assert.Null(await p.GetLocationAsync("8.8.8.8"));  // empty body -> null

            var t = new IpApiCoGeoLocationProvider(NullLogger<IpApiCoGeoLocationProvider>.Instance, ThrowingClient(), apiUrl: StubUrl);
            Assert.Null(await t.GetLocationAsync("8.8.8.8"));  // catch -> null

            Assert.True(IsLocalOrPrivate(p, null));
            Assert.True(IsLocalOrPrivate(p, "127.0.0.1"));
        }

        // ---- ip-api.io ----
        [Fact]
        public async Task IpApiIo_EmptyResponse_Throws_And_Guards()
        {
            var p = new IpApiIoGeoLocationProvider(NullLogger<IpApiIoGeoLocationProvider>.Instance, EmptyResponseClient(), StubUrl);
            Assert.Null(await p.GetLocationAsync("8.8.8.8"));

            var t = new IpApiIoGeoLocationProvider(NullLogger<IpApiIoGeoLocationProvider>.Instance, ThrowingClient(), StubUrl);
            Assert.Null(await t.GetLocationAsync("8.8.8.8"));

            Assert.True(IsLocalOrPrivate(p, null));
            Assert.True(IsLocalOrPrivate(p, "localhost"));
        }

        // ---- ipwhois.app ----
        [Fact]
        public async Task IpWhoisApp_EmptyResponse_Throws_And_Guards()
        {
            var p = new IpWhoisAppGeoLocationProvider(NullLogger<IpWhoisAppGeoLocationProvider>.Instance, EmptyResponseClient(), StubUrl);
            Assert.Null(await p.GetLocationAsync("8.8.8.8"));

            var t = new IpWhoisAppGeoLocationProvider(NullLogger<IpWhoisAppGeoLocationProvider>.Instance, ThrowingClient(), StubUrl);
            Assert.Null(await t.GetLocationAsync("8.8.8.8"));

            Assert.True(IsLocalOrPrivate(p, null));
            Assert.True(IsLocalOrPrivate(p, "::1"));
        }

        // ---- ipinfo.io ----
        [Fact]
        public async Task IpInfoIo_NullIp_EmptyResponse_Throws_And_Guards()
        {
            var p = new IpInfoIoGeoLocationProvider(NullLogger<IpInfoIoGeoLocationProvider>.Instance, "tok", EmptyResponseClient(), StubUrl);
            Assert.Null(await p.GetLocationAsync(null));       // top guard
            Assert.Null(await p.GetLocationAsync("8.8.8.8"));  // empty body -> null

            var t = new IpInfoIoGeoLocationProvider(NullLogger<IpInfoIoGeoLocationProvider>.Instance, "tok", ThrowingClient(), StubUrl);
            Assert.Null(await t.GetLocationAsync("8.8.8.8"));  // catch -> null

            Assert.True(IsLocalOrPrivate(p, null));
            Assert.True(IsLocalOrPrivate(p, "127.0.0.1"));
        }

        // ---- freeapi.ipip.net (online) ----
        [Fact]
        public async Task IpIpNet_EmptyResponse_Throws_And_Guards()
        {
            var p = new IpIpNetGeoLocationProvider(NullLogger<IpIpNetGeoLocationProvider>.Instance, httpClient: EmptyResponseClient(), apiUrl: StubUrl);
            Assert.Null(await p.GetLocationAsync("8.8.8.8"));

            var t = new IpIpNetGeoLocationProvider(NullLogger<IpIpNetGeoLocationProvider>.Instance, httpClient: ThrowingClient(), apiUrl: StubUrl);
            Assert.Null(await t.GetLocationAsync("8.8.8.8"));

            // 127.0.0.1 hits the loopback return in IsLocalOrPrivateIp via the public path.
            Assert.Null(await p.GetLocationAsync("127.0.0.1"));
            Assert.True(IsLocalOrPrivate(p, null));

            // GetCountryCode's null/empty guard (only caller passes non-empty) via reflection.
            var gcc = p.GetType().GetMethod("GetCountryCode", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(gcc);
            Assert.Null(gcc.Invoke(p, new object[] { null }));
        }

        // ---- ChunZhen (online) ----
        [Fact]
        public async Task ChunZhen_EmptyResponse_Throws_And_Guards()
        {
            var p = new ChunZhenGeoLocationProvider(NullLogger<ChunZhenGeoLocationProvider>.Instance, EmptyResponseClient(), StubUrl);
            Assert.Null(await p.GetLocationAsync("8.8.8.8")); // empty body -> null

            var t = new ChunZhenGeoLocationProvider(NullLogger<ChunZhenGeoLocationProvider>.Instance, ThrowingClient(), StubUrl);
            Assert.Null(await t.GetLocationAsync("8.8.8.8")); // catch -> null

            Assert.True(IsLocalOrPrivate(p, null));
            Assert.True(IsLocalOrPrivate(p, "::1"));
        }
    }
}
