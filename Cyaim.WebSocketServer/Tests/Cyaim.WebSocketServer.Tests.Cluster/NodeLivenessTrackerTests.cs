using Cyaim.WebSocketServer.Infrastructure.Cluster;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    [Collection("ClusterStaticState")]
    public class NodeLivenessTrackerTests
    {
        private static NodeLivenessTracker CreateFastTracker(int timeoutMs = 200, int checkMs = 50)
            => new NodeLivenessTracker(TimeSpan.FromMilliseconds(timeoutMs), TimeSpan.FromMilliseconds(checkMs));

        [Fact]
        public void Defaults_AreFifteenSecondTimeout_FiveSecondCheck()
        {
            Assert.Equal(TimeSpan.FromSeconds(15), NodeLivenessTracker.DefaultNodeTimeout);
            Assert.Equal(TimeSpan.FromSeconds(5), NodeLivenessTracker.DefaultCheckInterval);
        }

        [Fact]
        public void Touch_MakesNodeSeenAndAlive()
        {
            using var tracker = CreateFastTracker();

            tracker.Touch("nodeB");

            Assert.True(tracker.HasBeenSeen("nodeB"));
            Assert.True(tracker.IsAlive("nodeB"));
        }

        [Fact]
        public void UnknownOrNullNode_IsNeitherSeenNorAlive()
        {
            using var tracker = CreateFastTracker();

            Assert.False(tracker.HasBeenSeen("ghost"));
            Assert.False(tracker.IsAlive("ghost"));
            Assert.False(tracker.HasBeenSeen(null));
            Assert.False(tracker.IsAlive(null));
            Assert.False(tracker.HasBeenSeen(string.Empty));

            // Null/empty touches are no-ops / 空节点 ID 的 Touch 是空操作
            tracker.Touch(null);
            tracker.Touch(string.Empty);
        }

        [Fact]
        public async Task Node_TimesOut_RaisesNodeTimedOutOnce()
        {
            using var tracker = CreateFastTracker(timeoutMs: 150, checkMs: 40);
            var timedOut = new List<string>();
            tracker.NodeTimedOut += (_, e) => { lock (timedOut) { timedOut.Add(e.NodeId); } };

            tracker.Touch("nodeB");

            Assert.True(await Wait.UntilAsync(() => { lock (timedOut) { return timedOut.Count == 1; } }, 2000));
            Assert.Equal("nodeB", timedOut[0]);
            Assert.False(tracker.IsAlive("nodeB"));
            Assert.True(tracker.HasBeenSeen("nodeB"));

            // No duplicate report for the same outage / 同一次故障不会重复触发
            await Task.Delay(300);
            lock (timedOut) { Assert.Single(timedOut); }
        }

        [Fact]
        public async Task TimedOutNode_TouchedAgain_RaisesNodeRecovered_AndCanTimeOutAgain()
        {
            using var tracker = CreateFastTracker(timeoutMs: 150, checkMs: 40);
            int timedOutCount = 0;
            var recovered = new List<string>();
            tracker.NodeTimedOut += (_, __) => Interlocked.Increment(ref timedOutCount);
            tracker.NodeRecovered += (_, e) => { lock (recovered) { recovered.Add(e.NodeId); } };

            tracker.Touch("nodeB");
            Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref timedOutCount) == 1, 2000));

            tracker.Touch("nodeB");
            lock (recovered) { Assert.Equal(new[] { "nodeB" }, recovered); }
            Assert.True(tracker.IsAlive("nodeB"));

            // Second outage raises a second timeout event / 第二次故障再次触发超时事件
            Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref timedOutCount) == 2, 2000));
        }

        [Fact]
        public async Task Remove_StopsTrackingNode_NoTimeoutRaised()
        {
            using var tracker = CreateFastTracker(timeoutMs: 150, checkMs: 40);
            int timedOutCount = 0;
            tracker.NodeTimedOut += (_, __) => Interlocked.Increment(ref timedOutCount);

            tracker.Touch("nodeB");
            tracker.Remove("nodeB");

            Assert.False(tracker.HasBeenSeen("nodeB"));
            Assert.False(tracker.IsAlive("nodeB"));

            await Task.Delay(400);
            Assert.Equal(0, Volatile.Read(ref timedOutCount));

            // Removing unknown/null is a no-op / 移除未知或空节点是空操作
            tracker.Remove("ghost");
            tracker.Remove(null);
        }

        [Fact]
        public async Task Dispose_StopsTimerAndClearsState()
        {
            var tracker = CreateFastTracker(timeoutMs: 100, checkMs: 30);
            int timedOutCount = 0;
            tracker.NodeTimedOut += (_, __) => Interlocked.Increment(ref timedOutCount);

            tracker.Touch("nodeB");
            tracker.Dispose();

            Assert.False(tracker.HasBeenSeen("nodeB"));
            await Task.Delay(300);
            Assert.Equal(0, Volatile.Read(ref timedOutCount));

            // Double dispose is safe / 重复释放是安全的
            tracker.Dispose();
        }
    }
}
