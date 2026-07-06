using Cyaim.WebSocketServer.Infrastructure;

namespace Cyaim.WebSocketServer.Tests
{
    // The governor is process-wide static state; serialize these tests and restore it afterwards.
    [Collection("StaticState")]
    public class ReceiveMemoryGovernorTests : IDisposable
    {
        private readonly long _savedMax;
        public ReceiveMemoryGovernorTests()
        {
            _savedMax = WebSocketReceiveMemoryGovernor.MaxBytes;
            Drain();
        }
        public void Dispose()
        {
            Drain();
            WebSocketReceiveMemoryGovernor.MaxBytes = _savedMax;
        }
        private static void Drain()
        {
            WebSocketReceiveMemoryGovernor.MaxBytes = long.MaxValue; // ensure Release actually decrements
            WebSocketReceiveMemoryGovernor.Release(WebSocketReceiveMemoryGovernor.CurrentBytes);
        }

        [Fact]
        public void Disabled_AlwaysAccepts_AndDoesNotCount()
        {
            WebSocketReceiveMemoryGovernor.MaxBytes = 0;
            Assert.True(WebSocketReceiveMemoryGovernor.TryReserve(1_000_000));
            Assert.Equal(0, WebSocketReceiveMemoryGovernor.CurrentBytes); // disabled => no accounting
        }

        [Fact]
        public void Reserve_And_Release_TrackCurrentBytes()
        {
            WebSocketReceiveMemoryGovernor.MaxBytes = 1000;
            Assert.True(WebSocketReceiveMemoryGovernor.TryReserve(400));
            Assert.True(WebSocketReceiveMemoryGovernor.TryReserve(400));
            Assert.Equal(800, WebSocketReceiveMemoryGovernor.CurrentBytes);

            WebSocketReceiveMemoryGovernor.Release(400);
            Assert.Equal(400, WebSocketReceiveMemoryGovernor.CurrentBytes);
        }

        [Fact]
        public void OverBudget_IsRejected_AndNotCounted()
        {
            WebSocketReceiveMemoryGovernor.MaxBytes = 1000;
            Assert.True(WebSocketReceiveMemoryGovernor.TryReserve(900));
            // 900 + 200 = 1100 > 1000 => rejected, and the 200 must not remain reserved.
            Assert.False(WebSocketReceiveMemoryGovernor.TryReserve(200));
            Assert.Equal(900, WebSocketReceiveMemoryGovernor.CurrentBytes);
            // A smaller request that fits is still accepted.
            Assert.True(WebSocketReceiveMemoryGovernor.TryReserve(100));
            Assert.Equal(1000, WebSocketReceiveMemoryGovernor.CurrentBytes);
        }

        [Fact]
        public void Reserve_AtExactBudget_IsAccepted()
        {
            WebSocketReceiveMemoryGovernor.MaxBytes = 500;
            Assert.True(WebSocketReceiveMemoryGovernor.TryReserve(500));
            Assert.False(WebSocketReceiveMemoryGovernor.TryReserve(1));
        }
    }
}
