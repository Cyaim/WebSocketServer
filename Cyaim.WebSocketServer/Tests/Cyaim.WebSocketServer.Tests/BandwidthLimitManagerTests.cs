using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    public class BandwidthLimitManagerTests
    {
        private static BandwidthLimitManager Create(BandwidthLimitPolicy policy)
            => new BandwidthLimitManager(NullLogger<BandwidthLimitManager>.Instance, policy);

        private static ConcurrentDictionary<string, int> GetChannelCounts(BandwidthLimitManager manager)
        {
            var field = typeof(BandwidthLimitManager).GetField("_channelConnectionCounts", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            return (ConcurrentDictionary<string, int>)field.GetValue(manager);
        }

        private static System.Collections.IDictionary GetConnectionTrackers(BandwidthLimitManager manager)
        {
            var field = typeof(BandwidthLimitManager).GetField("_connectionTrackers", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            return (System.Collections.IDictionary)field.GetValue(manager);
        }

        [Fact]
        public async Task DisabledPolicy_NoWait_NoTrackersCreated()
        {
            var manager = Create(new BandwidthLimitPolicy { Enabled = false });

            var sw = Stopwatch.StartNew();
            await manager.WaitForBandwidthAsync("/ws", "conn-1", null, 10_000_000);
            sw.Stop();

            // Disabled policy must return without any throttling delay
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Disabled policy waited {sw.Elapsed}");
            Assert.Empty(GetConnectionTrackers(manager).Keys.Cast<object>());
            Assert.Empty(GetChannelCounts(manager));
        }

        [Fact]
        public async Task Enabled_ZeroDataSize_NoTracking()
        {
            var manager = Create(new BandwidthLimitPolicy { Enabled = true });

            await manager.WaitForBandwidthAsync("/ws", "conn-1", null, 0);
            await manager.WaitForBandwidthAsync("/ws", "conn-1", null, -5);

            Assert.Empty(GetConnectionTrackers(manager).Keys.Cast<object>());
        }

        [Fact]
        public async Task Enabled_CountsActiveConnectionsPerChannel()
        {
            var manager = Create(new BandwidthLimitPolicy { Enabled = true });

            await manager.WaitForBandwidthAsync("/ws", "conn-1", null, 100);
            await manager.WaitForBandwidthAsync("/ws", "conn-2", null, 100);
            await manager.WaitForBandwidthAsync("/chat", "conn-3", null, 100);
            // Repeated traffic on an existing connection must not double-count
            await manager.WaitForBandwidthAsync("/ws", "conn-1", null, 100);

            var counts = GetChannelCounts(manager);
            Assert.Equal(2, counts["/ws"]);
            Assert.Equal(1, counts["/chat"]);
        }

        [Fact]
        public async Task RemoveConnection_DecrementsChannelCount_AndRemovesTracker()
        {
            var manager = Create(new BandwidthLimitPolicy { Enabled = true });
            await manager.WaitForBandwidthAsync("/ws", "conn-1", null, 100);
            await manager.WaitForBandwidthAsync("/ws", "conn-2", null, 100);

            manager.RemoveConnection("conn-1");

            var counts = GetChannelCounts(manager);
            Assert.Equal(1, counts["/ws"]);
            Assert.Single(GetConnectionTrackers(manager).Keys.Cast<object>());
        }

        [Fact]
        public void RemoveConnection_Unknown_DoesNotThrow()
        {
            var manager = Create(new BandwidthLimitPolicy { Enabled = true });

            manager.RemoveConnection("never-seen");
        }

        [Fact]
        public void UpdatePolicy_RaisesPolicyUpdated_AndCopiesValues()
        {
            var manager = Create(new BandwidthLimitPolicy { Enabled = false });
            BandwidthLimitPolicy observed = null;
            int calls = 0;
            manager.PolicyUpdated += p => { observed = p; calls++; };

            manager.UpdatePolicy(new BandwidthLimitPolicy
            {
                Enabled = true,
                GlobalChannelBandwidthLimit = new Dictionary<string, long> { ["/ws"] = 1234 }
            });

            Assert.Equal(1, calls);
            Assert.NotNull(observed);
            Assert.True(observed.Enabled);
            Assert.Equal(1234, observed.GlobalChannelBandwidthLimit["/ws"]);
        }

        [Fact]
        public void UpdatePolicy_NullDictionaries_ReplacedWithEmpty()
        {
            var manager = Create(new BandwidthLimitPolicy());
            BandwidthLimitPolicy observed = null;
            manager.PolicyUpdated += p => observed = p;

            manager.UpdatePolicy(new BandwidthLimitPolicy
            {
                Enabled = true,
                GlobalChannelBandwidthLimit = null,
                ChannelMinBandwidthGuarantee = null,
                ChannelMaxBandwidthLimit = null,
                ChannelEnableAverageBandwidth = null,
                ChannelConnectionMinBandwidthGuarantee = null,
                ChannelConnectionMaxBandwidthLimit = null,
                EndPointMaxBandwidthLimit = null,
                EndPointMinBandwidthGuarantee = null
            });

            Assert.NotNull(observed);
            Assert.NotNull(observed.GlobalChannelBandwidthLimit);
            Assert.Empty(observed.GlobalChannelBandwidthLimit);
            Assert.NotNull(observed.EndPointMinBandwidthGuarantee);
        }

        [Fact]
        public void UpdatePolicy_Null_DoesNothing()
        {
            var manager = Create(new BandwidthLimitPolicy());
            int calls = 0;
            manager.PolicyUpdated += _ => calls++;

            manager.UpdatePolicy(null);

            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task Clear_RemovesAllTrackersAndCounts()
        {
            var manager = Create(new BandwidthLimitPolicy { Enabled = true });
            await manager.WaitForBandwidthAsync("/ws", "conn-1", "ep.a", 100);
            await manager.WaitForBandwidthAsync("/chat", "conn-2", "ep.b", 100);

            manager.Clear();

            Assert.Empty(GetConnectionTrackers(manager).Keys.Cast<object>());
            Assert.Empty(GetChannelCounts(manager));

            // Manager remains usable after Clear
            await manager.WaitForBandwidthAsync("/ws", "conn-3", null, 100);
            Assert.Equal(1, GetChannelCounts(manager)["/ws"]);
        }

        [Fact]
        public async Task GlobalChannelLimit_ThrottlesSecondBurst()
        {
            var manager = Create(new BandwidthLimitPolicy
            {
                Enabled = true,
                GlobalChannelBandwidthLimit = new Dictionary<string, long> { ["/ws"] = 1_000_000 }
            });

            // First call records 100 KB in the current 1-second window
            await manager.WaitForBandwidthAsync("/ws", "conn-1", null, 100_000);

            // Second 100 KB burst immediately afterwards exceeds the per-second rate
            // and must be delayed by roughly (recorded + requested) / limit seconds (~0.2 s)
            var sw = Stopwatch.StartNew();
            await manager.WaitForBandwidthAsync("/ws", "conn-1", null, 100_000);
            sw.Stop();

            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(100), $"Expected throttling delay, waited only {sw.Elapsed}");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Throttling delay unexpectedly long: {sw.Elapsed}");
        }

        [Fact]
        public async Task NoConfiguredLimits_DoesNotDelay()
        {
            var manager = Create(new BandwidthLimitPolicy { Enabled = true });

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++)
            {
                await manager.WaitForBandwidthAsync("/ws", "conn-1", "ep.a", 1_000_000);
            }
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Unexpected delay without limits: {sw.Elapsed}");
        }

        [Fact]
        public async Task NullPolicyInConstructor_UsesDefaultPolicy_NoThrow()
        {
            var manager = new BandwidthLimitManager(NullLogger<BandwidthLimitManager>.Instance, null);

            // Default policy is disabled -> no tracking
            await manager.WaitForBandwidthAsync("/ws", "c", null, 100);
            Assert.Empty(GetChannelCounts(manager));
        }
    }
}
