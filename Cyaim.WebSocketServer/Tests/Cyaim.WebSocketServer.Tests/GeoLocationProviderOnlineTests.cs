using System.Net;
using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Tests for the online geo location providers WITHOUT any network access:
    /// the HttpClient is backed by an in-memory stub handler, so only the providers'
    /// input validation and JSON parsing logic is exercised.
    /// </summary>
    public class GeoLocationProviderOnlineTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly string _json;
            public Uri LastRequestUri { get; private set; }

            public StubHandler(string json)
            {
                _json = json;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequestUri = request.RequestUri;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_json)
                });
            }
        }

        private const string StubUrl = "http://localhost:1/{ip}";

        private static (HttpClient client, StubHandler handler) ClientReturning(string json)
        {
            var handler = new StubHandler(json);
            return (new HttpClient(handler), handler);
        }

        #region ip-api.com

        [Fact]
        public void IpApiCom_Ctor_NullLogger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new IpApiComGeoLocationProvider(null));
        }

        [Fact]
        public async Task IpApiCom_NullEmptyAndPrivateIps_ReturnNull_WithoutHttpCall()
        {
            var (client, handler) = ClientReturning("{}");
            var provider = new IpApiComGeoLocationProvider(NullLogger<IpApiComGeoLocationProvider>.Instance, client, StubUrl);

            Assert.Null(await provider.GetLocationAsync(null));
            Assert.Null(await provider.GetLocationAsync(string.Empty));
            Assert.Null(await provider.GetLocationAsync("127.0.0.1"));
            Assert.Null(await provider.GetLocationAsync("192.168.1.1"));
            Assert.Null(await provider.GetLocationAsync("10.1.1.1"));
            Assert.Null(await provider.GetLocationAsync("172.20.0.1"));
            Assert.Null(handler.LastRequestUri);
        }

        [Fact]
        public async Task IpApiCom_SuccessResponse_Parsed()
        {
            var (client, _) = ClientReturning(
                "{\"status\":\"success\",\"countryCode\":\"US\",\"country\":\"United States\",\"regionName\":\"California\",\"city\":\"Mountain View\",\"lat\":37.4,\"lon\":-122.1}");
            var provider = new IpApiComGeoLocationProvider(NullLogger<IpApiComGeoLocationProvider>.Instance, client, StubUrl);

            var info = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(info);
            Assert.Equal("US", info.CountryCode);
            Assert.Equal("United States", info.CountryName);
            Assert.Equal("California", info.RegionName);
            Assert.Equal("Mountain View", info.CityName);
        }

        [Fact]
        public async Task IpApiCom_FailedStatus_ReturnsNull()
        {
            var (client, _) = ClientReturning("{\"status\":\"fail\",\"message\":\"private range\"}");
            var provider = new IpApiComGeoLocationProvider(NullLogger<IpApiComGeoLocationProvider>.Instance, client, StubUrl);

            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        [Fact]
        public async Task IpApiCom_InvalidJson_ReturnsNull()
        {
            var (client, _) = ClientReturning("not json");
            var provider = new IpApiComGeoLocationProvider(NullLogger<IpApiComGeoLocationProvider>.Instance, client, StubUrl);

            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        #endregion

        #region ipapi.co

        [Fact]
        public void IpApiCo_Ctor_NullLogger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new IpApiCoGeoLocationProvider(null));
        }

        [Fact]
        public async Task IpApiCo_SuccessResponse_Parsed_AndApiKeyAppended()
        {
            var (client, handler) = ClientReturning(
                "{\"country_code\":\"CN\",\"country_name\":\"China\",\"region\":\"Beijing\",\"city\":\"Beijing\",\"latitude\":39.9,\"longitude\":116.4}");
            var provider = new IpApiCoGeoLocationProvider(NullLogger<IpApiCoGeoLocationProvider>.Instance, client, "secret-key", StubUrl);

            var info = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(info);
            Assert.Equal("CN", info.CountryCode);
            Assert.Equal("China", info.CountryName);
            Assert.Equal("Beijing", info.RegionName);
            Assert.Equal("Beijing", info.CityName);
            Assert.Equal(39.9, info.Latitude);
            Assert.Equal(116.4, info.Longitude);
            Assert.Contains("key=secret-key", handler.LastRequestUri.ToString());
            Assert.Contains("8.8.8.8", handler.LastRequestUri.ToString());
        }

        [Fact]
        public async Task IpApiCo_ErrorResponse_ReturnsNull()
        {
            var (client, _) = ClientReturning("{\"error\":true,\"reason\":\"RateLimited\"}");
            var provider = new IpApiCoGeoLocationProvider(NullLogger<IpApiCoGeoLocationProvider>.Instance, client, apiUrl: StubUrl);

            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        [Fact]
        public async Task IpApiCo_PrivateIp_ReturnsNull()
        {
            var (client, handler) = ClientReturning("{}");
            var provider = new IpApiCoGeoLocationProvider(NullLogger<IpApiCoGeoLocationProvider>.Instance, client, apiUrl: StubUrl);

            Assert.Null(await provider.GetLocationAsync("192.168.99.1"));
            Assert.Null(handler.LastRequestUri);
        }

        #endregion

        #region ip-api.io

        [Fact]
        public void IpApiIo_Ctor_NullLogger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new IpApiIoGeoLocationProvider(null));
        }

        [Fact]
        public async Task IpApiIo_SuccessResponse_Parsed()
        {
            var (client, _) = ClientReturning(
                "{\"country_code\":\"JP\",\"country_name\":\"Japan\",\"region_name\":\"Tokyo\",\"city\":\"Tokyo\",\"latitude\":35.6,\"longitude\":139.6}");
            var provider = new IpApiIoGeoLocationProvider(NullLogger<IpApiIoGeoLocationProvider>.Instance, client, StubUrl);

            var info = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(info);
            Assert.Equal("JP", info.CountryCode);
            Assert.Equal("Japan", info.CountryName);
            Assert.Equal("Tokyo", info.RegionName);
            Assert.Equal("Tokyo", info.CityName);
            Assert.Equal(35.6, info.Latitude);
        }

        [Fact]
        public async Task IpApiIo_FailedStatus_ReturnsNull()
        {
            var (client, _) = ClientReturning("{\"status\":\"fail\"}");
            var provider = new IpApiIoGeoLocationProvider(NullLogger<IpApiIoGeoLocationProvider>.Instance, client, StubUrl);

            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        [Fact]
        public async Task IpApiIo_NullAndPrivateIp_ReturnNull()
        {
            var (client, _) = ClientReturning("{}");
            var provider = new IpApiIoGeoLocationProvider(NullLogger<IpApiIoGeoLocationProvider>.Instance, client, StubUrl);

            Assert.Null(await provider.GetLocationAsync(null));
            Assert.Null(await provider.GetLocationAsync("10.0.0.1"));
        }

        #endregion

        #region ipinfo.io

        [Fact]
        public void IpInfoIo_Ctor_NullLoggerOrApiKey_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new IpInfoIoGeoLocationProvider(null, "key"));
            Assert.Throws<ArgumentNullException>(() => new IpInfoIoGeoLocationProvider(NullLogger<IpInfoIoGeoLocationProvider>.Instance, null));
        }

        [Fact]
        public async Task IpInfoIo_SuccessResponse_ParsesLocPair()
        {
            var (client, handler) = ClientReturning(
                "{\"country\":\"DE\",\"region\":\"Hesse\",\"city\":\"Frankfurt\",\"loc\":\"50.11,8.68\"}");
            var provider = new IpInfoIoGeoLocationProvider(NullLogger<IpInfoIoGeoLocationProvider>.Instance, "tok", client, "http://localhost:1/{ip}?token={key}");

            var info = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(info);
            Assert.Equal("DE", info.CountryCode);
            Assert.Equal("Hesse", info.RegionName);
            Assert.Equal("Frankfurt", info.CityName);
            Assert.Equal(50.11, info.Latitude);
            Assert.Equal(8.68, info.Longitude);
            Assert.Contains("token=tok", handler.LastRequestUri.ToString());
        }

        [Fact]
        public async Task IpInfoIo_MalformedLoc_LatLonNull()
        {
            var (client, _) = ClientReturning("{\"country\":\"DE\",\"loc\":\"not-a-pair\"}");
            var provider = new IpInfoIoGeoLocationProvider(NullLogger<IpInfoIoGeoLocationProvider>.Instance, "tok", client, StubUrl);

            var info = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(info);
            Assert.Null(info.Latitude);
            Assert.Null(info.Longitude);
        }

        [Fact]
        public async Task IpInfoIo_ErrorResponse_ReturnsNull()
        {
            var (client, _) = ClientReturning("{\"error\":{\"title\":\"Unknown token\"}}");
            var provider = new IpInfoIoGeoLocationProvider(NullLogger<IpInfoIoGeoLocationProvider>.Instance, "tok", client, StubUrl);

            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        [Fact]
        public async Task IpInfoIo_PrivateIp_ReturnsNull()
        {
            var (client, handler) = ClientReturning("{}");
            var provider = new IpInfoIoGeoLocationProvider(NullLogger<IpInfoIoGeoLocationProvider>.Instance, "tok", client, StubUrl);

            Assert.Null(await provider.GetLocationAsync("172.16.5.5"));
            Assert.Null(handler.LastRequestUri);
        }

        #endregion

        #region ipwhois.app

        [Fact]
        public void IpWhoisApp_Ctor_NullLogger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new IpWhoisAppGeoLocationProvider(null));
        }

        [Fact]
        public async Task IpWhoisApp_SuccessResponse_Parsed()
        {
            var (client, _) = ClientReturning(
                "{\"success\":true,\"country_code\":\"FR\",\"country\":\"France\",\"region\":\"IDF\",\"city\":\"Paris\",\"latitude\":48.85,\"longitude\":2.35}");
            var provider = new IpWhoisAppGeoLocationProvider(NullLogger<IpWhoisAppGeoLocationProvider>.Instance, client, StubUrl);

            var info = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(info);
            Assert.Equal("FR", info.CountryCode);
            Assert.Equal("France", info.CountryName);
            Assert.Equal("IDF", info.RegionName);
            Assert.Equal("Paris", info.CityName);
            Assert.Equal(48.85, info.Latitude);
        }

        [Fact]
        public async Task IpWhoisApp_SuccessFalse_ReturnsNull()
        {
            var (client, _) = ClientReturning("{\"success\":false,\"message\":\"reserved range\"}");
            var provider = new IpWhoisAppGeoLocationProvider(NullLogger<IpWhoisAppGeoLocationProvider>.Instance, client, StubUrl);

            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        [Fact]
        public async Task IpWhoisApp_NullAndPrivateIp_ReturnNull()
        {
            var (client, _) = ClientReturning("{}");
            var provider = new IpWhoisAppGeoLocationProvider(NullLogger<IpWhoisAppGeoLocationProvider>.Instance, client, StubUrl);

            Assert.Null(await provider.GetLocationAsync(null));
            Assert.Null(await provider.GetLocationAsync("192.168.0.100"));
        }

        #endregion

        #region freeapi.ipip.net (online)

        [Fact]
        public void IpIpNet_Ctor_NullLogger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new IpIpNetGeoLocationProvider(null));
        }

        [Fact]
        public async Task IpIpNet_ArrayResponse_Parsed_WithCountryCodeMapping()
        {
            var (client, _) = ClientReturning("[\"中国\",\"北京\",\"北京\",\"联通\"]");
            var provider = new IpIpNetGeoLocationProvider(NullLogger<IpIpNetGeoLocationProvider>.Instance, httpClient: client, apiUrl: StubUrl);

            var info = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(info);
            Assert.Equal("中国", info.CountryName);
            Assert.Equal("CN", info.CountryCode);
            Assert.Equal("北京", info.RegionName);
            Assert.Equal("北京", info.CityName);
        }

        [Fact]
        public async Task IpIpNet_ArrayResponse_UnknownCountry_CountryCodeNull()
        {
            var (client, _) = ClientReturning("[\"Atlantis\",\"Somewhere\",\"Deep\"]");
            var provider = new IpIpNetGeoLocationProvider(NullLogger<IpIpNetGeoLocationProvider>.Instance, httpClient: client, apiUrl: StubUrl);

            var info = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(info);
            Assert.Equal("Atlantis", info.CountryName);
            Assert.Null(info.CountryCode);
        }

        [Fact]
        public async Task IpIpNet_ObjectResponse_Parsed()
        {
            var (client, handler) = ClientReturning(
                "{\"country_code\":\"US\",\"country\":\"United States\",\"region\":\"CA\",\"city\":\"SF\",\"latitude\":37.7,\"longitude\":-122.4}");
            var provider = new IpIpNetGeoLocationProvider(NullLogger<IpIpNetGeoLocationProvider>.Instance, apiKey: "tk", httpClient: client, apiUrl: StubUrl);

            var info = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(info);
            Assert.Equal("US", info.CountryCode);
            Assert.Equal("United States", info.CountryName);
            Assert.Equal("CA", info.RegionName);
            Assert.Equal("SF", info.CityName);
            Assert.Equal(37.7, info.Latitude);
            Assert.Contains("token=tk", handler.LastRequestUri.ToString());
        }

        [Fact]
        public async Task IpIpNet_UnexpectedResponseShape_ReturnsNull()
        {
            var (client, _) = ClientReturning("\"just a string\"");
            var provider = new IpIpNetGeoLocationProvider(NullLogger<IpIpNetGeoLocationProvider>.Instance, httpClient: client, apiUrl: StubUrl);

            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        [Fact]
        public async Task IpIpNet_ShortArray_ReturnsNull()
        {
            var (client, _) = ClientReturning("[\"中国\"]");
            var provider = new IpIpNetGeoLocationProvider(NullLogger<IpIpNetGeoLocationProvider>.Instance, httpClient: client, apiUrl: StubUrl);

            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        [Fact]
        public async Task IpIpNet_NullAndPrivateIp_ReturnNull()
        {
            var (client, _) = ClientReturning("[]");
            var provider = new IpIpNetGeoLocationProvider(NullLogger<IpIpNetGeoLocationProvider>.Instance, httpClient: client, apiUrl: StubUrl);

            Assert.Null(await provider.GetLocationAsync(null));
            Assert.Null(await provider.GetLocationAsync("10.10.10.10"));
        }

        #endregion

        #region ChunZhen (online placeholder)

        [Fact]
        public void ChunZhenOnline_Ctor_NullLogger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ChunZhenGeoLocationProvider(null));
        }

        [Fact]
        public async Task ChunZhenOnline_NoApiUrlConfigured_ReturnsNull()
        {
            var provider = new ChunZhenGeoLocationProvider(NullLogger<ChunZhenGeoLocationProvider>.Instance);

            Assert.Null(await provider.GetLocationAsync("8.8.8.8"));
        }

        [Fact]
        public async Task ChunZhenOnline_WithApiUrl_ParsesResponse()
        {
            var (client, _) = ClientReturning(
                "{\"country_code\":\"CN\",\"country\":\"中国\",\"region\":\"北京\",\"city\":\"北京\"}");
            var provider = new ChunZhenGeoLocationProvider(NullLogger<ChunZhenGeoLocationProvider>.Instance, client, StubUrl);

            var info = await provider.GetLocationAsync("8.8.8.8");

            Assert.NotNull(info);
            Assert.Equal("CN", info.CountryCode);
            Assert.Equal("中国", info.CountryName);
            Assert.Equal("北京", info.RegionName);
            Assert.Equal("北京", info.CityName);
        }

        [Fact]
        public async Task ChunZhenOnline_NullAndPrivateIp_ReturnNull()
        {
            var provider = new ChunZhenGeoLocationProvider(NullLogger<ChunZhenGeoLocationProvider>.Instance);

            Assert.Null(await provider.GetLocationAsync(null));
            Assert.Null(await provider.GetLocationAsync("127.0.0.1"));
            Assert.Null(await provider.GetLocationAsync("192.168.1.2"));
        }

        #endregion
    }
}
