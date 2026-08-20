using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using Cyaim.WebSocketServer.Infrastructure;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Regression tests for the lifetime of the buffer that
    /// <see cref="WebSocketManager.SendLocalAsync(string, WebSocketMessageType, CancellationToken, TimeSpan?, Encoding, int, WebSocket[])"/>
    /// hands to the send.
    ///
    /// AwaitWithTimeoutAsync detaches when a finite timeout wins the race: it returns while the send
    /// is still running and still reading the caller's memory. The string overload used to encode
    /// into an ArrayPool rental and return that rental from a finally block the moment the await came
    /// back, so the pool could hand the very same array to the next renter while the frame was still
    /// on its way out. Whatever the next renter wrote is what reached the wire, and nothing threw.
    ///
    /// 这些测试锁定"借出内存的所有权契约"：只有当发送保证在返回前结束（无超时 / InfiniteTimeSpan）
    /// 时才可以借用池化缓冲；传了有限超时，就必须把独占内存的所有权交给可能脱离的发送。
    /// </summary>
    public class PooledSendLifetimeTests
    {
        /// <summary>Byte the "next renter" scribbles over pooled memory. UTF-8 never produces 0xFF.</summary>
        private const byte Sentinel = 0xFF;

        /// <summary>
        /// Payload that is small enough to go out as one frame (&lt;= 4 KiB) but long enough that the
        /// pooled path rents a 4096-byte bucket rather than an array sized to the exact byte count.
        /// </summary>
        private static string BuildPayload()
        {
            var sb = new StringBuilder(1024);
            for (int i = 0; i < 1000; i++)
            {
                sb.Append((char)('a' + (i % 26)));
            }
            sb.Append("-中文尾巴-");
            return sb.ToString();
        }

        #region Detaching send (finite timeout)

        /// <summary>
        /// With a finite timeout the send detaches and keeps reading the buffer after SendLocalAsync
        /// returns. The next ArrayPool renter must not be able to reach that buffer: if the caller
        /// lent a pooled rental and returned it, the renter's sentinel bytes land in the frame that
        /// is still being written.
        /// </summary>
        [Fact]
        public async Task SendLocalAsync_String_WithTimeout_DetachedSendKeepsSeeingOriginalBytes()
        {
            string payload = BuildPayload();
            byte[] expected = Encoding.UTF8.GetBytes(payload);
            int rentSize = Encoding.UTF8.GetMaxByteCount(payload.Length);

            var socket = new LateReadingWebSocket();
            using var pump = new PumpSynchronizationContext();

            bool sendWasStillRunningOnReturn = false;
            bool poolHandedBackTheLentBuffer = false;

            await pump.RunAsync(async () =>
            {
                try
                {
                    Task send = WebSocketManager.SendLocalAsync(
                        payload,
                        WebSocketMessageType.Text,
                        CancellationToken.None,
                        timeout: TimeSpan.FromMilliseconds(50),
                        encoding: Encoding.UTF8,
                        sendBufferSize: 4 * 1024,
                        socket: socket);

                    // The fake blocks inside SendAsync and never completes on its own, so the timeout
                    // always wins the WhenAny race and the send always takes the detached branch.
                    await WaitOrFail(socket.Entered, "SendAsync to be entered");
                    await send;

                    // Guard against a vacuous pass: if the send had already finished here, nothing
                    // would have detached and the rest of this test would prove nothing.
                    sendWasStillRunningOnReturn = !socket.SendCompleted;

                    // Play the next renter. We are on the same thread that just ran the caller's
                    // finally block, so ArrayPool's thread-local slot hands back exactly the array
                    // that was returned there -- if one was returned at all.
                    var rentals = new List<byte[]>(8);
                    for (int i = 0; i < 8; i++)
                    {
                        byte[] rented = ArrayPool<byte>.Shared.Rent(rentSize);
                        rentals.Add(rented);
                        if (ReferenceEquals(rented, socket.LentArray))
                        {
                            poolHandedBackTheLentBuffer = true;
                        }
                        rented.AsSpan().Fill(Sentinel);
                    }
                    foreach (byte[] rented in rentals)
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }
                finally
                {
                    // Always unblock the fake, even on failure, so nothing is left hanging.
                    socket.Release();
                }

                await WaitOrFail(socket.Completed, "the detached send to finish");
            });

            Assert.True(sendWasStillRunningOnReturn,
                "SendLocalAsync returned only after the send had finished, so the detach path was never exercised.");

            Assert.False(poolHandedBackTheLentBuffer,
                "The buffer handed to a detached send was returned to ArrayPool while the send was still reading it: " +
                "the next renter got the same array back and overwrote a frame that was still in flight.");

            Assert.Equal(expected, socket.BytesReadAfterRelease);
        }

        /// <summary>
        /// The same contract stated structurally, without depending on pool timing: the memory lent
        /// to a detachable send must be exclusively owned, i.e. an array sized exactly to the encoded
        /// payload rather than a slice of an oversized pool rental the caller still owns.
        /// </summary>
        [Fact]
        public async Task SendLocalAsync_String_WithTimeout_LendsExclusivelyOwnedBuffer()
        {
            string payload = BuildPayload();
            byte[] expected = Encoding.UTF8.GetBytes(payload);

            var socket = new LateReadingWebSocket();

            Task send = WebSocketManager.SendLocalAsync(
                payload,
                WebSocketMessageType.Text,
                CancellationToken.None,
                timeout: TimeSpan.FromMilliseconds(50),
                encoding: Encoding.UTF8,
                sendBufferSize: 4 * 1024,
                socket: socket);

            await WaitOrFail(socket.Entered, "SendAsync to be entered");
            await send;
            socket.Release();
            await WaitOrFail(socket.Completed, "the detached send to finish");

            Assert.NotNull(socket.LentArray);
            Assert.Equal(0, socket.LentOffset);
            Assert.Equal(expected.Length, socket.LentCount);
            Assert.Equal(expected.Length, socket.LentArray.Length);
            Assert.Equal(expected, socket.BytesReadAfterRelease);
        }

        #endregion

        #region Non-detaching send (no timeout / infinite timeout)

        /// <summary>
        /// Positive control, and the other half of the contract: with no timeout the send is finished
        /// by the time the await returns, so the pooled fast path is still taken and returning the
        /// rental is safe. Renting right afterwards on this same thread gets that rental back -- which
        /// is also what proves the pool-reuse detection used by the detach test actually works.
        /// </summary>
        [Fact]
        public async Task SendLocalAsync_String_WithoutTimeout_UsesPooledBuffer_AndSendsExactBytes()
        {
            string payload = BuildPayload();
            byte[] expected = Encoding.UTF8.GetBytes(payload);
            int rentSize = Encoding.UTF8.GetMaxByteCount(payload.Length);

            var socket = new LateReadingWebSocket(releaseImmediately: true);
            using var pump = new PumpSynchronizationContext();

            bool sendFinishedBeforeReturn = false;
            bool poolHandedBackTheLentBuffer = false;

            await pump.RunAsync(async () =>
            {
                await WebSocketManager.SendLocalAsync(
                    payload,
                    WebSocketMessageType.Text,
                    CancellationToken.None,
                    timeout: null,
                    encoding: Encoding.UTF8,
                    sendBufferSize: 4 * 1024,
                    socket: socket);

                sendFinishedBeforeReturn = socket.SendCompleted;

                var rentals = new List<byte[]>(8);
                for (int i = 0; i < 8; i++)
                {
                    byte[] rented = ArrayPool<byte>.Shared.Rent(rentSize);
                    rentals.Add(rented);
                    if (ReferenceEquals(rented, socket.LentArray))
                    {
                        poolHandedBackTheLentBuffer = true;
                    }
                }
                foreach (byte[] rented in rentals)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            });

            Assert.True(sendFinishedBeforeReturn,
                "Without a timeout the send must be complete when SendLocalAsync returns, otherwise lending pooled memory is unsafe.");

            Assert.True(poolHandedBackTheLentBuffer,
                "Expected the no-timeout path to lend an ArrayPool rental and return it afterwards; " +
                "if it no longer does, the detach test's pool-reuse detection is no longer meaningful.");

            // A rental is a bucket-sized array, so the lent slice is shorter than the array holding it.
            Assert.True(socket.LentArray.Length >= rentSize);
            Assert.Equal(expected.Length, socket.LentCount);
            Assert.Equal(expected, socket.BytesReadAfterRelease);
        }

        /// <summary>
        /// InfiniteTimeSpan is the other non-detaching case and must behave like no timeout at all.
        /// </summary>
        [Fact]
        public async Task SendLocalAsync_String_InfiniteTimeout_SendsExactBytes()
        {
            string payload = BuildPayload();
            byte[] expected = Encoding.UTF8.GetBytes(payload);

            var socket = new LateReadingWebSocket(releaseImmediately: true);

            await WebSocketManager.SendLocalAsync(
                payload,
                WebSocketMessageType.Text,
                CancellationToken.None,
                timeout: Timeout.InfiniteTimeSpan,
                encoding: Encoding.UTF8,
                sendBufferSize: 4 * 1024,
                socket: socket);

            Assert.True(socket.SendCompleted);
            Assert.Equal(expected, socket.BytesReadAfterRelease);
        }

        #endregion

        #region Helpers

        private static async Task WaitOrFail(Task task, string what, int milliseconds = 15000)
        {
            // ConfigureAwait(true) on purpose: callers run on the pump thread and must stay there.
            Task finished = await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(true);
            if (!ReferenceEquals(finished, task))
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }
            await task.ConfigureAwait(true);
        }

        /// <summary>
        /// Fake socket that models a real transport: it takes the caller's memory, keeps a reference
        /// to it while the write is in flight, and only reads it when the write completes. It never
        /// copies eagerly, which is what makes a use-after-return observable.
        /// </summary>
        private sealed class LateReadingWebSocket : WebSocket
        {
            private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

            private ReadOnlyMemory<byte> _lent;
            private byte[] _bytesReadAfterRelease;
            private int _sendCompleted;

            public LateReadingWebSocket(bool releaseImmediately = false)
            {
                if (releaseImmediately)
                {
                    _release.TrySetResult();
                }
            }

            /// <summary>Completes as soon as SendAsync has taken the caller's memory.</summary>
            public Task Entered => _entered.Task;

            /// <summary>Completes once SendAsync has read the memory and returned.</summary>
            public Task Completed => _completed.Task;

            public bool SendCompleted => Volatile.Read(ref _sendCompleted) != 0;

            /// <summary>Bytes the send actually saw, read only after <see cref="Release"/>.</summary>
            public byte[] BytesReadAfterRelease => Volatile.Read(ref _bytesReadAfterRelease);

            /// <summary>The array backing the lent memory, so tests can ask the pool for it.</summary>
            public byte[] LentArray { get; private set; }

            public int LentOffset { get; private set; }

            public int LentCount { get; private set; }

            public void Release() => _release.TrySetResult();

            public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            {
                _lent = buffer;
                if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment))
                {
                    LentArray = segment.Array;
                    LentOffset = segment.Offset;
                    LentCount = segment.Count;
                }

                _entered.TrySetResult();
                await _release.Task.ConfigureAwait(false);

                // Read the caller's memory now, the way a transport reads it while the write is still
                // in flight. If the caller already handed that buffer back to the pool, this is where
                // the next renter's bytes show up.
                Volatile.Write(ref _bytesReadAfterRelease, _lent.ToArray());
                Volatile.Write(ref _sendCompleted, 1);
                _completed.TrySetResult();
            }

            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
                => SendAsync((ReadOnlyMemory<byte>)buffer, messageType, endOfMessage, cancellationToken).AsTask();

            public override WebSocketCloseStatus? CloseStatus => null;
            public override string CloseStatusDescription => null;
            public override WebSocketState State => WebSocketState.Open;
            public override string SubProtocol => null;

            public override void Abort() { }
            public override Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
            public override void Dispose() { }
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
                => throw new NotSupportedException("LateReadingWebSocket does not receive.");
        }

        /// <summary>
        /// Single-threaded synchronization context.
        ///
        /// SendLocalAsync(string, ...) awaits the inner send without ConfigureAwait(false), so its
        /// continuation -- the finally block that returns the pooled buffer -- runs on the captured
        /// context. Pinning that context to one thread puts the pool's Return and the test's following
        /// Rent on the same thread, and ArrayPool's per-thread slot then hands the array straight
        /// back. That turns "the next renter grabs the buffer" from a race into a certainty.
        /// </summary>
        private sealed class PumpSynchronizationContext : SynchronizationContext, IDisposable
        {
            private readonly BlockingCollection<(SendOrPostCallback Callback, object State)> _queue = new();
            private readonly Thread _thread;

            public PumpSynchronizationContext()
            {
                _thread = new Thread(Pump)
                {
                    IsBackground = true,
                    Name = nameof(PumpSynchronizationContext)
                };
                _thread.Start();
            }

            public override void Post(SendOrPostCallback d, object state)
            {
                try
                {
                    _queue.Add((d, state));
                }
                catch (InvalidOperationException)
                {
                    // Pump already shut down; the test is over.
                }
            }

            public override void Send(SendOrPostCallback d, object state)
                => throw new NotSupportedException("Synchronous Send would deadlock the pump.");

            private void Pump()
            {
                SetSynchronizationContext(this);
                foreach ((SendOrPostCallback callback, object state) in _queue.GetConsumingEnumerable())
                {
                    callback(state);
                }
            }

            /// <summary>Runs <paramref name="scenario"/> on the pump thread and awaits its outcome.</summary>
            public Task RunAsync(Func<Task> scenario)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Post(_ =>
                {
                    Task work;
                    try
                    {
                        work = scenario();
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                        return;
                    }

                    work.ContinueWith(
                        static (finished, state) =>
                        {
                            var tcs = (TaskCompletionSource)state;
                            if (finished.IsFaulted)
                            {
                                tcs.TrySetException(finished.Exception.InnerExceptions);
                            }
                            else if (finished.IsCanceled)
                            {
                                tcs.TrySetCanceled();
                            }
                            else
                            {
                                tcs.TrySetResult();
                            }
                        },
                        completion,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }, null);

                return completion.Task;
            }

            public void Dispose() => _queue.CompleteAdding();
        }

        #endregion
    }
}
