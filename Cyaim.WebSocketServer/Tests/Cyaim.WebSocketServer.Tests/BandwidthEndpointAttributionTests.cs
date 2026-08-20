using System.Collections;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// 回归测试：MvcChannelHandler 接收循环里的逐帧带宽限速必须
    /// (a) 把本帧归属到消息头里的 target（首帧的字节还在 buffer 里、尚未写入 wsReceiveReader，
    ///     所以不能只从 wsReceiveReader 解析端点），并且
    /// (b) 同一批字节只计费一次（不能循环内逐帧计一遍、循环后再按整条消息补计一遍）。
    ///
    /// Regression coverage for the per-frame bandwidth throttle in MvcChannelHandler's receive loop:
    /// (a) every frame must be attributed to the message's target — the first frame's bytes still live in
    ///     `buffer` and have not reached wsReceiveReader, so resolving the endpoint from wsReceiveReader
    ///     alone leaves the only frame that carries the header (and the whole of a single-frame message)
    ///     with a null endpoint, and
    /// (b) the same bytes must be charged exactly once — the loop used to charge every frame and then
    ///     charge the whole message again after the loop, and WaitForBandwidthAsync charges the channel
    ///     and connection buckets unconditionally, so both buckets counted every byte twice.
    ///
    /// BandwidthLimitManager is not substitutable (WaitForBandwidthAsync is non-virtual and the trackers
    /// are internal), so these tests drive the real manager and read what actually landed in its buckets:
    /// the channel / connection / endpoint trackers' `_totalBytes`. That is enough to pin both properties:
    /// the channel bucket counts every call regardless of endpoint, the endpoint bucket counts only the
    /// calls that carried a non-null endPoint, so "endpoint bytes == channel bytes == message length"
    /// says exactly "there was one charge, of the whole message, and it named the endpoint".
    /// </summary>
    [Collection("StaticState")]
    public class BandwidthEndpointAttributionTests : IDisposable
    {
        private const string Channel = "/ws";
        private const string Target = "wstest.echo";

        private readonly IServiceProvider _previousServices;
        private readonly ServiceProvider _provider;
        private readonly MvcTestSupport.StubLifetime _lifetime = new MvcTestSupport.StubLifetime();

        public BandwidthEndpointAttributionTests()
        {
            _previousServices = WebSocketRouteOption.ApplicationServices;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHostApplicationLifetime>(_lifetime);
            services.AddSingleton<MvcTestSupport.IGreetService, MvcTestSupport.GreetService>();
            _provider = services.BuildServiceProvider();
            WebSocketRouteOption.ApplicationServices = _provider;
            MvcTestSupport.ResetCachedScopeFactory();
        }

        public void Dispose()
        {
            WebSocketRouteOption.ApplicationServices = _previousServices;
            MvcTestSupport.ResetCachedScopeFactory();
            _provider.Dispose();
        }

        #region harness

        private static readonly MethodInfo MvcForwardMethod =
            typeof(MvcChannelHandler).GetMethod("MvcForward", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// Enabled policy with no configured limits: WaitForBandwidthAsync records into every bucket but
        /// never delays, so the buckets show the raw accounting without timing noise.
        /// </summary>
        private static BandwidthLimitManager NewManager()
            => new BandwidthLimitManager(NullLogger<BandwidthLimitManager>.Instance, new BandwidthLimitPolicy { Enabled = true });

        private MvcChannelHandler NewHandler(WebSocketRouteOption options, BandwidthLimitManager manager)
        {
            var handler = new MvcChannelHandler();
            typeof(MvcChannelHandler).GetField("logger", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(handler, NullLogger<WebSocketRouteMiddleware>.Instance);
            typeof(MvcChannelHandler).GetField("webSocketOption", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(handler, options);
            // ConnectionEntry builds this from DI; MvcForward is driven directly here, so inject it.
            typeof(MvcChannelHandler).GetField("bandwidthLimitManager", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(handler, manager);
            return handler;
        }

        private static WebSocketRouteOption Options()
            => new WebSocketRouteOption
            {
                WatchAssemblyContext = MvcTestSupport.BuildContext(typeof(MvcTestSupport.WsTestController)),
                // Dispatch the message inline so nothing is still running when the buckets are read.
                EnableForwardTaskSyncProcessingMode = true
            };

        private static DefaultHttpContext Ctx(string connectionId)
        {
            var c = new DefaultHttpContext();
            c.Connection.Id = connectionId;
            c.Request.Path = Channel;
            return c;
        }

        private Task RunForward(MvcChannelHandler handler, HttpContext ctx, ScriptedWebSocket ws, WebSocketRouteOption options)
            => (Task)MvcForwardMethod.Invoke(handler, new object[] { ctx, ws, options, _lifetime });

        /// <summary>
        /// Feeds the frames as one WebSocket message (all but the last with EndOfMessage=false),
        /// then a Close frame so the receive loop terminates, and returns the manager it charged.
        /// </summary>
        private async Task<BandwidthLimitManager> ReceiveMessageAsync(string connectionId, params string[] frames)
        {
            var options = Options();
            var manager = NewManager();
            var handler = NewHandler(options, manager);

            var ws = new ScriptedWebSocket();
            for (int i = 0; i < frames.Length; i++)
            {
                ws.Text(frames[i], eom: i == frames.Length - 1);
            }
            ws.CloseFrame();

            await RunForward(handler, Ctx(connectionId), ws, options);
            return manager;
        }

        #endregion

        #region bucket readers

        private static IDictionary Trackers(BandwidthLimitManager manager, string fieldName)
        {
            var field = typeof(BandwidthLimitManager).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            return (IDictionary)field.GetValue(manager);
        }

        private static string[] TrackerKeys(BandwidthLimitManager manager, string fieldName)
            => Trackers(manager, fieldName).Keys.Cast<object>().Select(k => (string)k).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        /// <summary>Bytes the given tracker recorded in its current window.</summary>
        private static long RecordedBytes(BandwidthLimitManager manager, string fieldName, string key)
        {
            var trackers = Trackers(manager, fieldName);
            Assert.True(trackers.Contains(key), $"{fieldName} has no tracker for '{key}'; keys: [{string.Join(", ", TrackerKeys(manager, fieldName))}]");
            object tracker = trackers[key];
            var totalBytes = tracker.GetType().GetField("_totalBytes", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(totalBytes);
            return (long)totalBytes.GetValue(tracker);
        }

        private static long ChannelBytes(BandwidthLimitManager manager) => RecordedBytes(manager, "_channelTrackers", Channel);

        private static long ConnectionBytes(BandwidthLimitManager manager, string connectionId) => RecordedBytes(manager, "_connectionTrackers", connectionId);

        private static long EndPointBytes(BandwidthLimitManager manager, string endPoint) => RecordedBytes(manager, "_endPointTrackers", endPoint);

        private static int Utf8Len(params string[] frames) => frames.Sum(f => Encoding.UTF8.GetByteCount(f));

        #endregion

        /// <summary>
        /// 单帧消息：整条消息在一次 ReceiveAsync 里收全，wsReceiveReader 自始至终为空。
        /// 端点桶必须恰好收到这条消息的字节，且与通道桶相等 —— 也就是说唯一的那次计费带上了 target。
        ///
        /// A single-frame message never touches wsReceiveReader, so resolving the endpoint from it yields
        /// null for the one and only per-frame charge. Asserting endpoint bytes == channel bytes == message
        /// length pins that the single charge carried endPoint = "wstest.echo" rather than null: the channel
        /// bucket counts every charge, the endpoint bucket only the ones that named an endpoint.
        /// Before the fix the loop charged the channel with a null endpoint and a second, whole-message call
        /// after the loop charged it again with the endpoint, leaving channel = 2x endpoint.
        /// </summary>
        [Fact]
        public async Task SingleFrameMessage_ChargesBandwidthAgainstTheMessageTarget()
        {
            string connectionId = Guid.NewGuid().ToString("N");
            string message = "{\"id\":\"1\",\"target\":\"" + Target + "\",\"body\":{\"text\":\"hi\"}}";
            int messageBytes = Utf8Len(message);

            var manager = await ReceiveMessageAsync(connectionId, message);

            // The endpoint was resolved, and to the message's target — not null, and nothing else.
            Assert.Equal(new[] { Target }, TrackerKeys(manager, "_endPointTrackers"));

            long endPointBytes = EndPointBytes(manager, Target);
            long channelBytes = ChannelBytes(manager);

            Assert.Equal(messageBytes, endPointBytes);
            // Every byte the channel bucket saw was attributed to the endpoint: no charge slipped
            // through with endPoint == null.
            Assert.Equal(endPointBytes, channelBytes);
            Assert.Equal(messageBytes, ConnectionBytes(manager, connectionId));
        }

        /// <summary>
        /// 同一批字节只计费一次：WaitForBandwidthAsync 对 channel 与 connection 两个桶是无条件计费的，
        /// 循环内逐帧计一次、循环后再按整条消息补计一次，会让配置的限额实际只有一半生效。
        ///
        /// WaitForBandwidthAsync charges the channel and connection buckets unconditionally, so the removed
        /// post-loop whole-message call made both buckets count the same bytes twice — halving every
        /// configured channel/connection limit in practice.
        /// </summary>
        [Fact]
        public async Task SingleFrameMessage_ChargesChannelAndConnectionExactlyOnce()
        {
            string connectionId = Guid.NewGuid().ToString("N");
            string message = "{\"id\":\"1\",\"target\":\"" + Target + "\",\"body\":{\"text\":\"hi\"}}";
            int messageBytes = Utf8Len(message);

            var manager = await ReceiveMessageAsync(connectionId, message);

            Assert.Equal(messageBytes, ChannelBytes(manager));
            Assert.Equal(messageBytes, ConnectionBytes(manager, connectionId));
        }

        /// <summary>
        /// 多帧消息：头部只在第一帧里，而第一帧时数据还在 buffer 中。修复前首帧拿不到端点，
        /// 于是端点桶少收了首帧的字节，而通道/连接桶又因循环后的重复调用多收了一整条消息。
        ///
        /// The header lives in the first frame only, and at that moment the bytes are still in `buffer`.
        /// Before the fix the first frame resolved no endpoint (endpoint bucket short by the first frame)
        /// while the channel and connection buckets were charged the whole message a second time after
        /// the loop. All three buckets must now hold exactly the message length.
        /// </summary>
        [Fact]
        public async Task MultiFrameMessage_FirstFrameBytesAreAttributedToTheEndpoint()
        {
            string connectionId = Guid.NewGuid().ToString("N");
            // The target property is complete within the first frame; the rest arrives later.
            string frame1 = "{\"id\":\"1\",\"target\":\"" + Target + "\",";
            string frame2 = "\"body\":{\"text\":\"";
            string frame3 = "hello\"}}";
            int messageBytes = Utf8Len(frame1, frame2, frame3);

            var manager = await ReceiveMessageAsync(connectionId, frame1, frame2, frame3);

            Assert.Equal(new[] { Target }, TrackerKeys(manager, "_endPointTrackers"));

            // Short by exactly the first frame if the header frame is charged with a null endpoint.
            Assert.Equal(messageBytes, EndPointBytes(manager, Target));
            Assert.Equal(messageBytes, ChannelBytes(manager));
            Assert.Equal(messageBytes, ConnectionBytes(manager, connectionId));
        }

        /// <summary>
        /// target 落在**最后一帧**的多帧消息：整条消息收完之前一个字节都归属不到端点。
        /// target 在 JSON 里的位置由客户端决定 —— 任何按键名排序的序列化器都会把 target 排到 body 之后，
        /// 攻击者更是可以刻意把它放到末尾。如果只对"解析出 target 之后到达的帧"计端点，
        /// 这类消息的端点桶就是 0 字节，端点级限额可被完全绕过，而且恰恰绕开的是它最该管的大上传。
        ///
        /// A multi-frame message whose `target` is the last property: not one byte can be attributed to the
        /// endpoint until the whole message has landed. Where `target` sits is the client's choice — any
        /// key-sorting serializer puts it after `body`, and an attacker can place it last deliberately. If
        /// only the frames arriving after the target resolves were charged, the endpoint bucket would hold
        /// zero for this message and the per-endpoint limit could be bypassed outright — precisely on the
        /// large uploads it exists to govern. The bytes must be settled once the target becomes known.
        /// </summary>
        [Fact]
        public async Task MultiFrameMessage_WithTargetInTheLastFrame_StillChargesTheEndpointInFull()
        {
            string connectionId = Guid.NewGuid().ToString("N");
            // Nothing before the final frame lets FindJsonPropertyValue see "target".
            string frame1 = "{\"id\":\"1\",\"body\":{\"text\":\"" + new string('x', 2048);
            string frame2 = new string('y', 2048);
            string frame3 = "\"},\"target\":\"" + Target + "\"}";
            int messageBytes = Utf8Len(frame1, frame2, frame3);

            var manager = await ReceiveMessageAsync(connectionId, frame1, frame2, frame3);

            // Zero-byte endpoint bucket before the catch-up: the tracker would not even exist.
            Assert.Equal(new[] { Target }, TrackerKeys(manager, "_endPointTrackers"));
            Assert.Equal(messageBytes, EndPointBytes(manager, Target));

            // And the catch-up must not touch the other two buckets — charging them again is the
            // double-counting this change removed.
            Assert.Equal(messageBytes, ChannelBytes(manager));
            Assert.Equal(messageBytes, ConnectionBytes(manager, connectionId));
        }
    }
}
