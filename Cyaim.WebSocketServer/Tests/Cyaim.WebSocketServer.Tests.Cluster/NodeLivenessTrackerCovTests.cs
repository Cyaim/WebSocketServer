using System.Reflection;
using Cyaim.WebSocketServer.Infrastructure.Cluster;

namespace Cyaim.WebSocketServer.Tests.Cluster
{
    /// <summary>
    /// Coverage-only tests exercising <see cref="NodeLivenessTracker"/>'s private
    /// CheckLiveness timer callback directly via reflection, to reach:
    /// - the disposed-guard early return,
    /// - the Monitor.TryEnter contention early return,
    /// - the defensive catch/finally around the timed-out reporting loop.
    /// </summary>
    [Collection("ClusterStaticState")]
    public class NodeLivenessTrackerCovTests
    {
        private static NodeLivenessTracker CreateTracker(int timeoutMs = 100000, int checkMs = 100000)
            => new NodeLivenessTracker(TimeSpan.FromMilliseconds(timeoutMs), TimeSpan.FromMilliseconds(checkMs));

        private static void InvokeCheckLiveness(NodeLivenessTracker tracker)
        {
            var method = typeof(NodeLivenessTracker).GetMethod("CheckLiveness", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(tracker, new object[] { null });
        }

        [Fact]
        public void CheckLiveness_WhenDisposed_ReturnsImmediately()
        {
            var tracker = CreateTracker();
            tracker.Dispose();

            // Direct call after Dispose exercises the `if (_disposed) return;` guard
            // without a live Timer callback racing the test.
            InvokeCheckLiveness(tracker);
        }

        [Fact]
        public void CheckLiveness_LockContention_ReturnsImmediately()
        {
            using var tracker = CreateTracker();
            var lockField = typeof(NodeLivenessTracker).GetField("_checkLock", BindingFlags.NonPublic | BindingFlags.Instance);
            var checkLock = lockField.GetValue(tracker);

            var holding = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);

            var holder = new Thread(() =>
            {
                lock (checkLock)
                {
                    holding.Set();
                    release.Wait(5000);
                }
            });
            holder.IsBackground = true;
            holder.Start();

            Assert.True(holding.Wait(2000));
            try
            {
                // Another thread holds _checkLock, so Monitor.TryEnter fails and
                // CheckLiveness should return without doing any work.
                InvokeCheckLiveness(tracker);
            }
            finally
            {
                release.Set();
                holder.Join(2000);
            }
        }

        [Fact]
        public void CheckLiveness_HandlerThrows_IsCaught_AndLockIsReleased()
        {
            using var tracker = CreateTracker(timeoutMs: 0, checkMs: 100000);
            tracker.NodeTimedOut += (_, __) => throw new InvalidOperationException("boom");

            tracker.Touch("nodeZ");

            // nodeTimeout is zero, so the node is immediately considered timed out.
            // The throwing handler is invoked inside the try block and must be swallowed
            // by the catch, after which the lock must still be released (finally).
            InvokeCheckLiveness(tracker);

            // Lock must have been released: a second direct call should proceed
            // (not hit the TryEnter-contention early return) and be a no-op since the
            // node was already reported down.
            InvokeCheckLiveness(tracker);

            Assert.True(tracker.HasBeenSeen("nodeZ"));
        }
    }
}
