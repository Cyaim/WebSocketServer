using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    public class PriorityListServiceTests
    {
        private static PriorityListService Create()
            => new PriorityListService(NullLogger<PriorityListService>.Instance);

        [Fact]
        public void Ctor_NullLogger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new PriorityListService(null));
        }

        #region IP list

        [Fact]
        public void AddIp_NullOrEmpty_ReturnsFalse()
        {
            var service = Create();

            Assert.False(service.AddIpToPriorityList(null, 2));
            Assert.False(service.AddIpToPriorityList(string.Empty, 2));
        }

        [Fact]
        public void AddIp_ThenGet_ReturnsPriority()
        {
            var service = Create();

            Assert.True(service.AddIpToPriorityList("1.2.3.4", 3));
            Assert.Equal(3, service.GetIpPriority("1.2.3.4"));
        }

        [Fact]
        public void AddIp_Twice_UpdatesPriority()
        {
            var service = Create();
            service.AddIpToPriorityList("1.2.3.4", 3);
            service.AddIpToPriorityList("1.2.3.4", 7);

            Assert.Equal(7, service.GetIpPriority("1.2.3.4"));
        }

        [Fact]
        public void AddIps_Batch_CountsSuccesses()
        {
            var service = Create();

            int added = service.AddIpsToPriorityList(new[] { "1.1.1.1", "", "2.2.2.2", null }, 2);

            Assert.Equal(2, added);
            Assert.Equal(2, service.GetAllIpPriorities().Count);
        }

        [Fact]
        public void AddIps_Null_ReturnsZero()
        {
            Assert.Equal(0, Create().AddIpsToPriorityList(null, 2));
        }

        [Fact]
        public void RemoveIp_ExistingRemoved_MissingFalse()
        {
            var service = Create();
            service.AddIpToPriorityList("1.2.3.4", 3);

            Assert.True(service.RemoveIpFromPriorityList("1.2.3.4"));
            Assert.False(service.RemoveIpFromPriorityList("1.2.3.4"));
            Assert.False(service.RemoveIpFromPriorityList(null));
            Assert.Equal(1, service.GetIpPriority("1.2.3.4"));
        }

        [Fact]
        public void RemoveIps_Batch_CountsSuccesses()
        {
            var service = Create();
            service.AddIpToPriorityList("1.1.1.1", 2);
            service.AddIpToPriorityList("2.2.2.2", 2);

            Assert.Equal(2, service.RemoveIpsFromPriorityList(new[] { "1.1.1.1", "2.2.2.2", "3.3.3.3" }));
            Assert.Equal(0, service.RemoveIpsFromPriorityList(null));
        }

        #endregion

        #region GetIpPriority CIDR matching

        [Fact]
        public void GetIpPriority_UnknownIp_ReturnsDefault()
        {
            var service = Create();

            Assert.Equal(1, service.GetIpPriority("9.9.9.9"));
            Assert.Equal(5, service.GetIpPriority("9.9.9.9", 5));
            Assert.Equal(5, service.GetIpPriority(null, 5));
        }

        [Fact]
        public void GetIpPriority_CidrEntry_MatchesContainedIp()
        {
            var service = Create();
            service.AddIpToPriorityList("10.0.0.0/8", 4);

            Assert.Equal(4, service.GetIpPriority("10.20.30.40"));
            Assert.Equal(1, service.GetIpPriority("11.0.0.1"));
        }

        [Fact]
        public void GetIpPriority_Ipv6Cidr_Matches()
        {
            var service = Create();
            service.AddIpToPriorityList("2001:db8::/32", 6);

            Assert.Equal(6, service.GetIpPriority("2001:db8:1:2::3"));
            Assert.Equal(1, service.GetIpPriority("2001:db9::1"));
        }

        [Fact]
        public void GetIpPriority_AddressFamilyMismatch_NoMatch()
        {
            var service = Create();
            service.AddIpToPriorityList("10.0.0.0/8", 4);

            Assert.Equal(1, service.GetIpPriority("2001:db8::1"));
        }

        [Fact]
        public void GetIpPriority_UnparsableIp_ReturnsDefault()
        {
            var service = Create();
            service.AddIpToPriorityList("10.0.0.0/8", 4);

            Assert.Equal(1, service.GetIpPriority("not-an-ip"));
        }

        [Fact]
        public void GetIpPriority_MalformedCidrEntries_AreIgnored()
        {
            var service = Create();
            service.AddIpToPriorityList("abc/24", 9);
            service.AddIpToPriorityList("10.0.0.0/x", 9);
            service.AddIpToPriorityList("10.0.0.0/8/9", 9);

            Assert.Equal(1, service.GetIpPriority("10.1.1.1"));
        }

        #endregion

        #region Connection list

        [Fact]
        public void AddConnection_NullOrEmpty_ReturnsFalse()
        {
            var service = Create();

            Assert.False(service.AddConnectionToPriorityList(null, 2));
            Assert.False(service.AddConnectionToPriorityList(string.Empty, 2));
        }

        [Fact]
        public void AddConnection_ThenGet_ReturnsPriority()
        {
            var service = Create();

            Assert.True(service.AddConnectionToPriorityList("conn-1", 3));
            Assert.Equal(3, service.GetConnectionPriority("conn-1"));
            Assert.Equal(1, service.GetConnectionPriority("conn-2"));
            Assert.Equal(9, service.GetConnectionPriority(null, 9));
        }

        [Fact]
        public void AddConnections_Batch_CountsSuccesses()
        {
            var service = Create();

            Assert.Equal(2, service.AddConnectionsToPriorityList(new[] { "a", null, "b" }, 2));
            Assert.Equal(0, service.AddConnectionsToPriorityList(null, 2));
        }

        [Fact]
        public void RemoveConnection_ExistingRemoved_MissingFalse()
        {
            var service = Create();
            service.AddConnectionToPriorityList("conn-1", 3);

            Assert.True(service.RemoveConnectionFromPriorityList("conn-1"));
            Assert.False(service.RemoveConnectionFromPriorityList("conn-1"));
            Assert.False(service.RemoveConnectionFromPriorityList(null));
        }

        [Fact]
        public void RemoveConnections_Batch_CountsSuccesses()
        {
            var service = Create();
            service.AddConnectionToPriorityList("a", 2);

            Assert.Equal(1, service.RemoveConnectionsFromPriorityList(new[] { "a", "missing" }));
            Assert.Equal(0, service.RemoveConnectionsFromPriorityList(null));
        }

        #endregion

        #region Queries and clearing

        [Fact]
        public void GetAllIpPriorities_FilterByPriority()
        {
            var service = Create();
            service.AddIpToPriorityList("1.1.1.1", 2);
            service.AddIpToPriorityList("2.2.2.2", 3);
            service.AddIpToPriorityList("3.3.3.3", 2);

            Assert.Equal(3, service.GetAllIpPriorities().Count);
            var filtered = service.GetAllIpPriorities(2);
            Assert.Equal(2, filtered.Count);
            Assert.All(filtered.Values, v => Assert.Equal(2, v));
        }

        [Fact]
        public void GetAllConnectionPriorities_FilterByPriority()
        {
            var service = Create();
            service.AddConnectionToPriorityList("a", 2);
            service.AddConnectionToPriorityList("b", 3);

            Assert.Equal(2, service.GetAllConnectionPriorities().Count);
            var filtered = service.GetAllConnectionPriorities(3);
            Assert.Single(filtered);
            Assert.Equal(3, filtered["b"]);
        }

        [Fact]
        public void ClearAll_RemovesEverything()
        {
            var service = Create();
            service.AddIpToPriorityList("1.1.1.1", 2);
            service.AddConnectionToPriorityList("a", 2);

            service.ClearAll();

            Assert.Empty(service.GetAllIpPriorities());
            Assert.Empty(service.GetAllConnectionPriorities());
        }

        #endregion
    }
}
