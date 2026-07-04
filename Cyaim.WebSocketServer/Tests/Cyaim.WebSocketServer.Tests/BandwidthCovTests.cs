using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Coverage-focused tests for BandwidthLimitManager internals and QpsPriorityManager's tracker.
    ///
    /// The bandwidth trackers are internal classes, so they are driven via reflection with
    /// crafted window state:
    ///  - window start in the past   => deterministically hits the "window expired -> reset" branches
    ///  - window start in the future => elapsed is negative, so the wait-time math deterministically
    ///    yields a positive wait (no dependence on real elapsed wall-clock time)
    ///
    /// Whenever a positive wait is provoked through the public WaitForBandwidthAsync path, a
    /// pre-canceled token is passed so Task.Delay throws immediately instead of sleeping;
    /// the skipped RecordData afterwards (unchanged _totalBytes) proves the throttle path ran.
    /// </summary>
    public class BandwidthCovTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly Assembly Lib = typeof(BandwidthLimitManager).Assembly;
        private static readonly Type ChannelTrackerType = Lib.GetType("Cyaim.WebSocketServer.Infrastructure.ChannelBandwidthTracker", true);
        private static readonly Type ConnectionTrackerType = Lib.GetType("Cyaim.WebSocketServer.Infrastructure.ConnectionBandwidthTracker", true);
        private static readonly Type EndPointTrackerType = Lib.GetType("Cyaim.WebSocketServer.Infrastructure.EndPointBandwidthTracker", true);

        #region Helpers

        private static BandwidthLimitManager Create(BandwidthLimitPolicy policy, QpsPriorityManager qps = null)
            => new BandwidthLimitManager(NullLogger<BandwidthLimitManager>.Instance, policy, qps);

        private static QpsPriorityManager CreateQps(QpsPriorityPolicy policy, PriorityListService listService = null)
            => new QpsPriorityManager(
                NullLogger<QpsPriorityManager>.Instance,
                policy,
                listService ?? new PriorityListService(NullLogger<PriorityListService>.Instance));

        private static CancellationToken Canceled() => new CancellationToken(canceled: true);

        /// <summary>Window start 10 seconds in the past: window is always expired.</summary>
        private static long PastWindowTicks() => DateTime.UtcNow.Ticks - TimeSpan.FromSeconds(10).Ticks;

        /// <summary>
        /// Window start 10 minutes in the future: elapsed is negative for the whole test run,
        /// so the window never expires and the rate math is deterministic.
        /// </summary>
        private static long FutureWindowTicks() => DateTime.UtcNow.Ticks + TimeSpan.FromMinutes(10).Ticks;

        private static object NewTracker(string kind)
        {
            var policy = new BandwidthLimitPolicy();
            return kind switch
            {
                "channel" => Activator.CreateInstance(ChannelTrackerType, "/ws", policy),
                "connection" => Activator.CreateInstance(ConnectionTrackerType, "conn-1", "/ws", policy),
                "endpoint" => Activator.CreateInstance(EndPointTrackerType, "ctrl.action", policy),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }

        private static void SetTrackerState(object tracker, long windowStartTicks, long totalBytes)
        {
            tracker.GetType().GetField("_windowStartTicks", All).SetValue(tracker, windowStartTicks);
            tracker.GetType().GetField("_totalBytes", All).SetValue(tracker, totalBytes);
        }

        private static long TotalBytes(object tracker)
            => (long)tracker.GetType().GetField("_totalBytes", All).GetValue(tracker);

        private static object Invoke(object tracker, string method, params object[] args)
            => tracker.GetType().GetMethod(method, All).Invoke(tracker, args);

        private static IDictionary ManagerDictionary(BandwidthLimitManager manager, string fieldName)
            => (IDictionary)typeof(BandwidthLimitManager).GetField(fieldName, All).GetValue(manager);

        private static object GetConnectionTracker(BandwidthLimitManager manager, string connectionId)
            => ManagerDictionary(manager, "_connectionTrackers")[connectionId];

        private static object GetEndPointTracker(BandwidthLimitManager manager, string endPoint)
            => ManagerDictionary(manager, "_endPointTrackers")[endPoint];

        #endregion

        #region Tracker internals: RecordData / CalculateWaitTime window handling

        [Theory]
        [InlineData("channel")]
        [InlineData("connection")]
        [InlineData("endpoint")]
        public void Tracker_RecordData_ExpiredWindow_ResetsWindowAndBytes(string kind)
        {
            var tracker = NewTracker(kind);
            SetTrackerState(tracker, PastWindowTicks(), 500);

            Invoke(tracker, "RecordData", 5);

            // Window expired -> bytes replaced (not accumulated)
            Assert.Equal(5, TotalBytes(tracker));
        }

        [Theory]
        [InlineData("channel")]
        [InlineData("connection")]
        [InlineData("endpoint")]
        public void Tracker_CalculateWaitTime_ExpiredWindow_ReturnsZeroAndResets(string kind)
        {
            var tracker = NewTracker(kind);
            SetTrackerState(tracker, PastWindowTicks(), 500);

            var wait = (TimeSpan)Invoke(tracker, "CalculateWaitTime", 10, 1000L);

            Assert.Equal(TimeSpan.Zero, wait);
            Assert.Equal(0, TotalBytes(tracker));
        }

        [Theory]
        [InlineData("channel")]
        [InlineData("connection")]
        [InlineData("endpoint")]
        public void Tracker_CalculateWaitTime_OverBudget_ReturnsPositiveWait(string kind)
        {
            var tracker = NewTracker(kind);
            // Future window start => elapsed < 0 => excess bytes are deterministically positive
            SetTrackerState(tracker, FutureWindowTicks(), 1_000_000);

            var wait = (TimeSpan)Invoke(tracker, "CalculateWaitTime", 1000, 1000L);

            Assert.True(wait > TimeSpan.Zero, $"Expected positive wait, got {wait}");
            // State untouched by a pure calculation
            Assert.Equal(1_000_000, TotalBytes(tracker));
        }

        [Theory]
        [InlineData("connection")]
        [InlineData("endpoint")]
        public void Tracker_Guarantee_ExpiredWindow_ReturnsZeroAndResets(string kind)
        {
            var tracker = NewTracker(kind);
            SetTrackerState(tracker, PastWindowTicks(), 500);

            var wait = (TimeSpan)Invoke(tracker, "CalculateWaitTimeForGuarantee", 10, 1000L);

            Assert.Equal(TimeSpan.Zero, wait);
            Assert.Equal(0, TotalBytes(tracker));
        }

        [Theory]
        [InlineData("connection")]
        [InlineData("endpoint")]
        public void Tracker_Guarantee_RateBelowGuarantee_ReturnsZero(string kind)
        {
            var tracker = NewTracker(kind);
            // No bytes recorded -> current rate 0 < guarantee
            SetTrackerState(tracker, FutureWindowTicks(), 0);

            var wait = (TimeSpan)Invoke(tracker, "CalculateWaitTimeForGuarantee", 10, 1000L);

            Assert.Equal(TimeSpan.Zero, wait);
        }

        [Theory]
        [InlineData("connection")]
        [InlineData("endpoint")]
        public void Tracker_Guarantee_RateAboveGuarantee_ReturnsPositiveWait(string kind)
        {
            var tracker = NewTracker(kind);
            SetTrackerState(tracker, FutureWindowTicks(), 1_000_000);

            var wait = (TimeSpan)Invoke(tracker, "CalculateWaitTimeForGuarantee", 10, 1000L);

            Assert.True(wait > TimeSpan.Zero, $"Expected positive wait, got {wait}");
        }

        [Theory]
        [InlineData("connection")]
        [InlineData("endpoint")]
        public void Tracker_Guarantee_RateNotBelowGuarantee_ButNoExcess_ReturnsZero(string kind)
        {
            var tracker = NewTracker(kind);
            // rate = 0 which is NOT below a negative guarantee, while
            // excess = 0 - (-1000 * negativeElapsed) < 0 -> falls through to the final Zero return
            SetTrackerState(tracker, FutureWindowTicks(), 0);

            var wait = (TimeSpan)Invoke(tracker, "CalculateWaitTimeForGuarantee", 10, -1000L);

            Assert.Equal(TimeSpan.Zero, wait);
        }

        [Fact]
        public void ConnectionTracker_ExposesConnectionIdAndChannel()
        {
            var tracker = NewTracker("connection");

            Assert.Equal("conn-1", (string)tracker.GetType().GetProperty("ConnectionId").GetValue(tracker));
            Assert.Equal("/ws", (string)tracker.GetType().GetProperty("Channel").GetValue(tracker));
        }

        #endregion

        #region Manager: throttle branches via crafted tracker state + canceled token

        [Fact]
        public async Task ChannelMaxLimit_PositiveWait_CanceledToken_SkipsDelayAndRecord()
        {
            var manager = Create(new BandwidthLimitPolicy
            {
                Enabled = true,
                ChannelMaxBandwidthLimit = new Dictionary<string, long> { ["/ws"] = 1000 }
            });
            await manager.WaitForBandwidthAsync("/ws", "c1", null, 10);
            var tracker = GetConnectionTracker(manager, "c1");
            SetTrackerState(tracker, FutureWindowTicks(), 100_000_000);

            var sw = Stopwatch.StartNew();
            await manager.WaitForBandwidthAsync("/ws", "c1", null, 10, null, Canceled());
            sw.Stop();

            // OperationCanceledException swallowed, no real delay, RecordData never reached
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Unexpected delay {sw.Elapsed}");
            Assert.Equal(100_000_000, TotalBytes(tracker));
        }

        [Fact]
        public async Task ChannelMinGuarantee_PositiveWait_CanceledToken_SkipsRecord()
        {
            var manager = Create(new BandwidthLimitPolicy
            {
                Enabled = true,
                ChannelMinBandwidthGuarantee = new Dictionary<string, long> { ["/ws"] = 1000 }
            });
            await manager.WaitForBandwidthAsync("/ws", "c1", null, 10);
            var tracker = GetConnectionTracker(manager, "c1");
            SetTrackerState(tracker, FutureWindowTicks(), 100_000_000);

            await manager.WaitForBandwidthAsync("/ws", "c1", null, 10, null, Canceled());

            Assert.Equal(100_000_000, TotalBytes(tracker));
        }

        [Fact]
        public async Task AverageBandwidth_MultipleConnections_PositiveWait()
        {
            var manager = Create(new BandwidthLimitPolicy
            {
                Enabled = true,
                GlobalChannelBandwidthLimit = new Dictionary<string, long> { ["/ws"] = 1_000_000 },
                ChannelEnableAverageBandwidth = new Dictionary<string, bool> { ["/ws"] = true }
            });
            await manager.WaitForBandwidthAsync("/ws", "c1", null, 10);
            await manager.WaitForBandwidthAsync("/ws", "c2", null, 10);
            var tracker = GetConnectionTracker(manager, "c2");
            SetTrackerState(tracker, FutureWindowTicks(), 100_000_000);

            await manager.WaitForBandwidthAsync("/ws", "c2", null, 10, null, Canceled());

            Assert.Equal(100_000_000, TotalBytes(tracker));
        }

        [Fact]
        public async Task ConnectionMinGuarantee_MultipleConnections_PositiveWait()
        {
            var manager = Create(new BandwidthLimitPolicy
            {
                Enabled = true,
                ChannelConnectionMinBandwidthGuarantee = new Dictionary<string, long> { ["/ws"] = 1000 }
            });
            await manager.WaitForBandwidthAsync("/ws", "c1", null, 10);
            await manager.WaitForBandwidthAsync("/ws", "c2", null, 10);
            var tracker = GetConnectionTracker(manager, "c2");
            SetTrackerState(tracker, FutureWindowTicks(), 100_000_000);

            await manager.WaitForBandwidthAsync("/ws", "c2", null, 10, null, Canceled());

            Assert.Equal(100_000_000, TotalBytes(tracker));
        }

        [Fact]
        public async Task ConnectionMaxLimit_MultipleConnections_PositiveWait()
        {
            var manager = Create(new BandwidthLimitPolicy
            {
                Enabled = true,
                ChannelConnectionMaxBandwidthLimit = new Dictionary<string, long> { ["/ws"] = 1000 }
            });
            await manager.WaitForBandwidthAsync("/ws", "c1", null, 10);
            await manager.WaitForBandwidthAsync("/ws", "c2", null, 10);
            var tracker = GetConnectionTracker(manager, "c2");
            SetTrackerState(tracker, FutureWindowTicks(), 100_000_000);

            await manager.WaitForBandwidthAsync("/ws", "c2", null, 10, null, Canceled());

            Assert.Equal(100_000_000, TotalBytes(tracker));
        }

        [Fact]
        public async Task EndPointMaxAndGuarantee_PositiveWaits()
        {
            var manager = Create(new BandwidthLimitPolicy
            {
                Enabled = true,
                EndPointMaxBandwidthLimit = new Dictionary<string, long> { ["ctrl.act"] = 1000 },
                EndPointMinBandwidthGuarantee = new Dictionary<string, long> { ["ctrl.act"] = 500 }
            });
            await manager.WaitForBandwidthAsync("/ws", "c1", "ctrl.act", 10);
            var epTracker = GetEndPointTracker(manager, "ctrl.act");
            SetTrackerState(epTracker, FutureWindowTicks(), 100_000_000);

            await manager.WaitForBandwidthAsync("/ws", "c1", "ctrl.act", 10, null, Canceled());

            Assert.Equal(100_000_000, TotalBytes(epTracker));
        }

        [Fact]
        public async Task CalculateWaitTimeThrows_ExceptionIsSwallowedAndLogged()
        {
            var policy = new BandwidthLimitPolicy { Enabled = true };
            var manager = Create(policy);
            await manager.WaitForBandwidthAsync("/ws", "c1", null, 42);
            var tracker = GetConnectionTracker(manager, "c1");
            SetTrackerState(tracker, FutureWindowTicks(), 7);

            // Force a NullReferenceException inside CalculateWaitTime
            policy.GlobalChannelBandwidthLimit = null;
            await manager.WaitForBandwidthAsync("/ws", "c1", null, 10);

            // Exception swallowed before RecordData -> crafted byte count untouched
            Assert.Equal(7, TotalBytes(tracker));
        }

        #endregion

        #region Manager: QPS priority interplay

        [Fact]
        public async Task QpsPriority_RecordsConnection_AndAppliesPriorityLimit()
        {
            var qps = CreateQps(new QpsPriorityPolicy
            {
                Enabled = true,
                GlobalBandwidthLimit = 1000,
                DefaultPriorityBandwidthRatio = 0.1 // effective limit = 100 B/s
            });
            var manager = Create(new BandwidthLimitPolicy { Enabled = true }, qps);

            // First call: connection registered with QPS manager, priority wait is zero (10 <= 100)
            await manager.WaitForBandwidthAsync("/ws", "c1", null, 10, "9.9.9.9");
            var stats = qps.GetChannelPriorityStatistics("/ws");
            Assert.Equal(1, stats[1]);

            // Second call with crafted state: the priority limit produces a positive wait
            var tracker = GetConnectionTracker(manager, "c1");
            SetTrackerState(tracker, FutureWindowTicks(), 100_000_000);
            await manager.WaitForBandwidthAsync("/ws", "c1", null, 10, "9.9.9.9", Canceled());

            Assert.Equal(100_000_000, TotalBytes(tracker));
        }

        [Fact]
        public async Task RemoveConnection_WithQpsManager_RemovesFromBoth()
        {
            var qps = CreateQps(new QpsPriorityPolicy { Enabled = true });
            var manager = Create(new BandwidthLimitPolicy { Enabled = true }, qps);
            await manager.WaitForBandwidthAsync("/ws", "c1", null, 10, "1.2.3.4");
            Assert.NotEmpty(qps.GetChannelPriorityStatistics("/ws"));

            manager.RemoveConnection("c1");

            Assert.Empty(qps.GetChannelPriorityStatistics("/ws"));
            Assert.Empty(ManagerDictionary(manager, "_connectionTrackers").Keys.Cast<object>());
        }

        [Fact]
        public void RemoveConnection_NullOrEmpty_ReturnsWithoutError()
        {
            var manager = Create(new BandwidthLimitPolicy { Enabled = true });

            manager.RemoveConnection(null);
            manager.RemoveConnection(string.Empty);

            Assert.Empty(ManagerDictionary(manager, "_connectionTrackers").Keys.Cast<object>());
        }

        #endregion

        #region Manager: concurrent tracker creation (TryAdd race)

        [Fact]
        public void ConcurrentSameConnection_TrackerCreation_KeepsExactChannelCount()
        {
            // Hammers the TryGetValue-miss + TryAdd-fail race in WaitForBandwidthAsync:
            // barrier-synchronized threads request the same brand-new connection id
            // simultaneously, thousands of times. A losing thread disposes its own tracker
            // and re-reads the winner's. The invariants below hold whether or not a given
            // iteration actually raced, so the test always passes deterministically.
            var manager = Create(new BandwidthLimitPolicy { Enabled = true });
            int participants = Math.Clamp(Environment.ProcessorCount, 2, 4);
            const int Groups = 12000;

            using var barrier = new Barrier(participants);
            Exception failure = null;
            var threads = new Thread[participants];
            for (int t = 0; t < participants; t++)
            {
                threads[t] = new Thread(() =>
                {
                    try
                    {
                        for (int g = 0; g < Groups; g++)
                        {
                            barrier.SignalAndWait();
                            manager.WaitForBandwidthAsync("/race", "race-" + g, null, 1).GetAwaiter().GetResult();
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref failure, ex, null);
                        barrier.RemoveParticipant();
                    }
                })
                { IsBackground = true };
                threads[t].Start();
            }
            foreach (var thread in threads)
            {
                thread.Join();
            }

            Assert.Null(failure);
            var counts = (ConcurrentDictionary<string, int>)typeof(BandwidthLimitManager)
                .GetField("_channelConnectionCounts", All).GetValue(manager);
            Assert.Equal(Groups, counts["/race"]);
            Assert.Equal(Groups, ManagerDictionary(manager, "_connectionTrackers").Count);
        }

        #endregion

        #region QpsPriorityManager: PriorityBandwidthTracker.ConnectionId

        [Fact]
        public void PriorityBandwidthTracker_ExposesConnectionId()
        {
            var qps = CreateQps(new QpsPriorityPolicy { Enabled = true });
            qps.RecordConnection("/ws", "c1", "1.1.1.1");

            var trackers = (IDictionary)typeof(QpsPriorityManager).GetField("_connectionTrackers", All).GetValue(qps);
            var tracker = trackers["c1"];
            Assert.NotNull(tracker);

            var connectionId = (string)tracker.GetType().GetProperty("ConnectionId").GetValue(tracker);
            Assert.Equal("c1", connectionId);
        }

        #endregion
    }
}
