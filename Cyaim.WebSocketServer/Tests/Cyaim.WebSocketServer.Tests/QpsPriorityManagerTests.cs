using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    public class QpsPriorityManagerTests
    {
        private static PriorityListService NewListService()
            => new PriorityListService(NullLogger<PriorityListService>.Instance);

        private static QpsPriorityManager Create(QpsPriorityPolicy policy, PriorityListService listService = null)
            => new QpsPriorityManager(NullLogger<QpsPriorityManager>.Instance, policy, listService ?? NewListService());

        #region Constructor

        [Fact]
        public void Ctor_NullArguments_Throw()
        {
            var policy = new QpsPriorityPolicy();
            var listService = NewListService();

            Assert.Throws<ArgumentNullException>(() => new QpsPriorityManager(null, policy, listService));
            Assert.Throws<ArgumentNullException>(() => new QpsPriorityManager(NullLogger<QpsPriorityManager>.Instance, null, listService));
            Assert.Throws<ArgumentNullException>(() => new QpsPriorityManager(NullLogger<QpsPriorityManager>.Instance, policy, null));
        }

        #endregion

        #region GetEffectiveBandwidthLimit

        [Fact]
        public void Disabled_ReturnsMaxValue()
        {
            var manager = Create(new QpsPriorityPolicy { Enabled = false });

            Assert.Equal(long.MaxValue, manager.GetEffectiveBandwidthLimit("/ws", "c1", "1.2.3.4"));
        }

        [Fact]
        public void Enabled_NoTotalBandwidthConfigured_ReturnsMaxValue()
        {
            var manager = Create(new QpsPriorityPolicy { Enabled = true });

            Assert.Equal(long.MaxValue, manager.GetEffectiveBandwidthLimit("/ws", "c1", "1.2.3.4"));
        }

        [Fact]
        public void GlobalLimit_DefaultPriority_UsesDefaultRatio()
        {
            var policy = new QpsPriorityPolicy
            {
                Enabled = true,
                GlobalBandwidthLimit = 1000,
                DefaultPriorityBandwidthRatio = 0.1
            };
            var manager = Create(policy);

            Assert.Equal(100, manager.GetEffectiveBandwidthLimit("/ws", "unknown-conn", "9.9.9.9"));
        }

        [Fact]
        public void GlobalLimit_ConnectionInPriorityList_UsesConfiguredRatio()
        {
            var listService = NewListService();
            listService.AddConnectionToPriorityList("vip-conn", 3);
            var policy = new QpsPriorityPolicy
            {
                Enabled = true,
                GlobalBandwidthLimit = 1000,
                PriorityBandwidthRatios = { [3] = 0.6 }
            };
            var manager = Create(policy, listService);

            Assert.Equal(600, manager.GetEffectiveBandwidthLimit("/ws", "vip-conn", "9.9.9.9"));
        }

        [Fact]
        public void GlobalLimit_IpPriorityHigherThanConnection_UsesMax()
        {
            var listService = NewListService();
            listService.AddConnectionToPriorityList("conn", 2);
            listService.AddIpToPriorityList("5.5.5.5", 4);
            var policy = new QpsPriorityPolicy
            {
                Enabled = true,
                GlobalBandwidthLimit = 1000,
                PriorityBandwidthRatios = { [2] = 0.2, [4] = 0.8 }
            };
            var manager = Create(policy, listService);

            Assert.Equal(800, manager.GetEffectiveBandwidthLimit("/ws", "conn", "5.5.5.5"));
        }

        [Fact]
        public void ChannelPolicy_OverridesGlobalLimitAndRatios()
        {
            var policy = new QpsPriorityPolicy
            {
                Enabled = true,
                GlobalBandwidthLimit = 10_000,
                PriorityBandwidthRatios = { [1] = 0.9 },
                ChannelPolicies =
                {
                    ["/chat"] = new ChannelQpsPriorityPolicy
                    {
                        ChannelBandwidthLimit = 2000,
                        PriorityBandwidthRatios = { [1] = 0.5 }
                    }
                }
            };
            var manager = Create(policy);

            Assert.Equal(1000, manager.GetEffectiveBandwidthLimit("/chat", "c1", "9.9.9.9"));
        }

        [Fact]
        public void ChannelPolicy_DefaultRatioOverride_Used()
        {
            var policy = new QpsPriorityPolicy
            {
                Enabled = true,
                DefaultPriorityBandwidthRatio = 0.9,
                ChannelPolicies =
                {
                    ["/x"] = new ChannelQpsPriorityPolicy
                    {
                        ChannelBandwidthLimit = 1000,
                        DefaultPriorityBandwidthRatio = 0.2
                    }
                }
            };
            var manager = Create(policy);

            Assert.Equal(200, manager.GetEffectiveBandwidthLimit("/x", "c1", "9.9.9.9"));
        }

        [Fact]
        public void ChannelWithoutPolicy_FallsBackToGlobalLimit()
        {
            var policy = new QpsPriorityPolicy
            {
                Enabled = true,
                GlobalBandwidthLimit = 1000,
                DefaultPriorityBandwidthRatio = 0.5,
                ChannelPolicies = { ["/other"] = new ChannelQpsPriorityPolicy { ChannelBandwidthLimit = 1 } }
            };
            var manager = Create(policy);

            Assert.Equal(500, manager.GetEffectiveBandwidthLimit("/ws", "c1", "9.9.9.9"));
        }

        [Fact]
        public void MinBandwidthGuarantee_RaisesAllocation()
        {
            var policy = new QpsPriorityPolicy
            {
                Enabled = true,
                GlobalBandwidthLimit = 1000,
                DefaultPriorityBandwidthRatio = 0.1,
                MinBandwidthGuarantee = 300
            };
            var manager = Create(policy);

            Assert.Equal(300, manager.GetEffectiveBandwidthLimit("/ws", "c1", "9.9.9.9"));
        }

        [Fact]
        public void DynamicAdjustment_BorrowsBandwidth_WhenPerConnectionBelowGuarantee()
        {
            var policy = new QpsPriorityPolicy
            {
                Enabled = true,
                GlobalBandwidthLimit = 1000,
                DefaultPriorityBandwidthRatio = 0.1, // allocated = 100
                MinBandwidthGuarantee = 150,
                EnableDynamicBandwidthAdjustment = true
            };
            var manager = Create(policy);
            manager.RecordConnection("/ws", "c1", "9.9.9.9");

            // Per-connection 100 < 150 -> borrows up to 10% of the total, then the guarantee applies
            Assert.Equal(150, manager.GetEffectiveBandwidthLimit("/ws", "c1", "9.9.9.9"));
        }

        [Fact]
        public void DynamicAdjustment_Disabled_NoBorrowing()
        {
            var policy = new QpsPriorityPolicy
            {
                Enabled = true,
                GlobalBandwidthLimit = 1000,
                DefaultPriorityBandwidthRatio = 0.1,
                EnableDynamicBandwidthAdjustment = false
            };
            var manager = Create(policy);
            manager.RecordConnection("/ws", "c1", "9.9.9.9");

            Assert.Equal(100, manager.GetEffectiveBandwidthLimit("/ws", "c1", "9.9.9.9"));
        }

        #endregion

        #region Connection tracking

        [Fact]
        public void RecordConnection_Disabled_DoesNotTrack()
        {
            var manager = Create(new QpsPriorityPolicy { Enabled = false });

            manager.RecordConnection("/ws", "c1", "1.1.1.1");

            Assert.Empty(manager.GetChannelPriorityStatistics("/ws"));
        }

        [Fact]
        public void RecordConnection_TracksPriorityPerChannel()
        {
            var listService = NewListService();
            listService.AddConnectionToPriorityList("vip", 3);
            var manager = Create(new QpsPriorityPolicy { Enabled = true }, listService);

            manager.RecordConnection("/ws", "vip", "1.1.1.1");
            manager.RecordConnection("/ws", "normal-1", "2.2.2.2");
            manager.RecordConnection("/ws", "normal-2", "3.3.3.3");
            manager.RecordConnection("/other", "elsewhere", "4.4.4.4");

            var stats = manager.GetChannelPriorityStatistics("/ws");
            Assert.Equal(2, stats.Count);
            Assert.Equal(1, stats[3]);
            Assert.Equal(2, stats[1]);
        }

        [Fact]
        public void RecordConnection_SameConnection_UpdatesPriorityAndChannel()
        {
            var listService = NewListService();
            var manager = Create(new QpsPriorityPolicy { Enabled = true }, listService);

            manager.RecordConnection("/ws", "c1", "1.1.1.1");
            listService.AddConnectionToPriorityList("c1", 5);
            manager.RecordConnection("/chat", "c1", "1.1.1.1");

            Assert.Empty(manager.GetChannelPriorityStatistics("/ws"));
            var stats = manager.GetChannelPriorityStatistics("/chat");
            Assert.Equal(1, stats[5]);
        }

        [Fact]
        public void RemoveConnection_StopsTracking()
        {
            var manager = Create(new QpsPriorityPolicy { Enabled = true });
            manager.RecordConnection("/ws", "c1", "1.1.1.1");

            manager.RemoveConnection("c1");

            Assert.Empty(manager.GetChannelPriorityStatistics("/ws"));
        }

        [Fact]
        public void Clear_RemovesAllTrackers()
        {
            var manager = Create(new QpsPriorityPolicy { Enabled = true });
            manager.RecordConnection("/ws", "c1", "1.1.1.1");
            manager.RecordConnection("/chat", "c2", "2.2.2.2");

            manager.Clear();

            Assert.Empty(manager.GetChannelPriorityStatistics("/ws"));
            Assert.Empty(manager.GetChannelPriorityStatistics("/chat"));
        }

        #endregion

        #region Policy defaults

        [Fact]
        public void QpsPriorityPolicy_Defaults()
        {
            var policy = new QpsPriorityPolicy();

            Assert.False(policy.Enabled);
            Assert.Null(policy.GlobalBandwidthLimit);
            Assert.NotNull(policy.PriorityBandwidthRatios);
            Assert.Empty(policy.PriorityBandwidthRatios);
            Assert.Equal(1, policy.DefaultPriority);
            Assert.Equal(0.1, policy.DefaultPriorityBandwidthRatio);
            Assert.True(policy.EnableDynamicBandwidthAdjustment);
            Assert.Null(policy.MinBandwidthGuarantee);
            Assert.NotNull(policy.ChannelPolicies);
            Assert.Empty(policy.ChannelPolicies);
        }

        [Fact]
        public void ChannelQpsPriorityPolicy_Defaults()
        {
            var policy = new ChannelQpsPriorityPolicy();

            Assert.Null(policy.ChannelBandwidthLimit);
            Assert.NotNull(policy.PriorityBandwidthRatios);
            Assert.Null(policy.DefaultPriority);
            Assert.Null(policy.DefaultPriorityBandwidthRatio);
        }

        #endregion
    }
}
