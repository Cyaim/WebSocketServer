using System;
using System.Collections.Generic;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Xunit;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// That the header probe cannot be turned into a quadratic amount of scanning by a client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The probe resolves <c>target</c> out of the accumulated receive buffer and re-runs on every
    /// receive iteration until it succeeds. On the success path that is one scan. On the failure
    /// path — a message that simply never contains <c>target</c> — it used to be one scan of the
    /// <i>whole accumulated buffer</i> per iteration, and any client that had completed the
    /// handshake could take that path by omitting one property.
    /// 探测在每次接收迭代上重跑直到解析出 target。成功路径只扫一次；失败路径——消息里压根没有
    /// target——曾经是每次迭代全量重扫一遍已累积缓冲区，而任何完成握手的客户端只要少写一个属性
    /// 就走这条路径。
    /// </para>
    /// <para>
    /// <b>What is asserted, and why it is not "total ≤ budget".</b> The caller charges the budget
    /// <i>before</i> scanning, so the last admitted scan can overshoot by its own span. The property
    /// that matters is therefore not a flat ceiling but the shape of the growth: total scanning is
    /// linear in the message size instead of quadratic, and — this is the part a client controls —
    /// <b>independent of how the message is chunked</b>. Chunking was the whole attack: the same
    /// 1 MiB in 1 KiB pieces cost four times as much as in 4 KiB pieces.
    /// 断言的不是「总量 ≤ 预算」：预算是**扫描前**扣的，最后一次被放行的扫描可以超出它自己那一段。
    /// 要紧的性质不是一条平坦的天花板，而是增长的形状——总扫描量对消息大小是线性而不是平方级，
    /// 并且**与客户端如何分块无关**。分块正是这次攻击的全部：同样 1 MiB，切成 1 KiB 比切成 4 KiB 贵四倍。
    /// </para>
    /// </remarks>
    public class HeaderProbeBudgetTests
    {
        /// <summary>
        /// Replays what the receive loop does to the budget: charge, then scan, until the predicate
        /// says stop. Returns the total bytes the probe would have scanned.
        /// </summary>
        private static long ScannedBytesFor(int messageSize, int chunkSize)
        {
            long budget = MvcChannelHandler.HeaderProbeBudgetBytes;
            long scanned = 0;
            long accumulated = 0;
            bool resolved = false;

            while (accumulated < messageSize)
            {
                accumulated = Math.Min(accumulated + chunkSize, messageSize);

                if (!MvcChannelHandler.ShouldProbeHeader(resolved, budget))
                {
                    continue;
                }

                // The span the probe is handed is the whole accumulated buffer — that is the point.
                // 探测拿到的是整个已累积缓冲区，这正是问题所在。
                budget -= accumulated;
                scanned += accumulated;

                // The message never contains `target`, so the probe never succeeds and the caller
                // only stops because the budget ran out.
                // 消息里没有 target，探测永远不成功，调用方只能靠预算耗尽才停下。
                if (budget <= 0)
                {
                    resolved = true;
                }
            }

            return scanned;
        }

        /// <summary>The 1 MiB message that used to cost ~134 MB of scanning now costs a fraction of it.</summary>
        [Fact]
        public void A_message_that_never_names_a_target_does_not_cost_quadratic_scanning()
        {
            const int oneMiB = 1024 * 1024;
            const int receiveBuffer = 4096;

            long scanned = ScannedBytesFor(oneMiB, receiveBuffer);

            // What it would have been without the budget: 4096 * (1+2+...+256).
            // 不设界时的数字：4096 × (1+2+…+256)。
            long chunks = oneMiB / receiveBuffer;
            long unbounded = (long)receiveBuffer * chunks * (chunks + 1) / 2;

            Assert.True(
                scanned * 20 < unbounded,
                $"expected the bound to cut scanning by more than 20x; scanned {scanned:N0} of an unbounded {unbounded:N0}");

            Assert.True(
                scanned <= MvcChannelHandler.HeaderProbeBudgetBytes + oneMiB,
                $"scanning must stay within the budget plus one final span; scanned {scanned:N0}");
        }

        /// <summary>
        /// The attack was chunking, so the bound has to hold across chunk sizes — including the
        /// pathological one-byte frame.
        /// </summary>
        /// <remarks>
        /// A prefix cap ("only ever scan the first 64 KiB") would pass the test above and fail this
        /// one: 1,048,576 one-byte frames each triggering a 64 KiB scan is 64 GiB. The bound has to
        /// be cumulative, and this is the case that says so.
        /// 「只扫前 64 KiB」这种前缀上限能通过上面那条、却过不了这一条：1,048,576 个一字节帧、
        /// 每个触发一次 64 KiB 扫描就是 64 GiB。界必须是累计的，而这条用例就是说这句话的。
        /// </remarks>
        [Theory]
        [InlineData(1)]
        [InlineData(64)]
        [InlineData(1024)]
        [InlineData(4096)]
        [InlineData(64 * 1024)]
        public void The_bound_does_not_depend_on_how_the_client_chunks_the_message(int chunkSize)
        {
            const int oneMiB = 1024 * 1024;

            long scanned = ScannedBytesFor(oneMiB, chunkSize);

            Assert.True(
                scanned <= MvcChannelHandler.HeaderProbeBudgetBytes + oneMiB,
                $"chunk size {chunkSize} scanned {scanned:N0}, above the budget plus one final span");
        }

        /// <summary>
        /// Splitting a message finer must not buy the client more scanning than sending it whole.
        /// </summary>
        /// <remarks>
        /// This is the assertion that would have caught the original defect on the day it was
        /// written. Unbounded, 1 KiB chunks cost four times what 4 KiB chunks cost for the same
        /// payload — the ratio <i>is</i> the vulnerability, and a fix that lowered the constant
        /// without removing the ratio would still be exploitable by chunking further.
        /// 这条断言在缺陷写下的当天就会抓住它：不设界时，同样的载荷切成 1 KiB 比切成 4 KiB 贵四倍，
        /// **那个比值就是漏洞本身**。只把常数调小、却没消掉比值的修复，客户端继续切细就照样能打。
        /// </remarks>
        [Fact]
        public void Finer_chunking_does_not_multiply_the_work()
        {
            const int oneMiB = 1024 * 1024;

            long coarse = ScannedBytesFor(oneMiB, 4096);
            long fine = ScannedBytesFor(oneMiB, 64);

            Assert.True(
                fine <= coarse * 2,
                $"chunking 64x finer must not multiply scanning: 4 KiB chunks scanned {coarse:N0}, 64 B chunks scanned {fine:N0}");
        }

        /// <summary>A probe that has resolved its target never runs again, whatever the budget says.</summary>
        [Fact]
        public void A_resolved_probe_stops_immediately()
        {
            Assert.False(MvcChannelHandler.ShouldProbeHeader(true, MvcChannelHandler.HeaderProbeBudgetBytes));
            Assert.False(MvcChannelHandler.ShouldProbeHeader(true, 1));
        }

        /// <summary>An exhausted budget stops the probe even though no target was found.</summary>
        [Fact]
        public void An_exhausted_budget_stops_the_probe()
        {
            Assert.False(MvcChannelHandler.ShouldProbeHeader(false, 0));
            Assert.False(MvcChannelHandler.ShouldProbeHeader(false, -1));
            Assert.True(MvcChannelHandler.ShouldProbeHeader(false, 1));
        }
    }
}
