using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// 一条消息从进到出总共分配了多少字节。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **这个数决定的是"同样的内存能装多少条连接"，而不是快慢。** .NET 的 GC 在两次 gen2 之间
    /// 让堆按分配速率往上涨，涨到的那个尖顶才是网关容器真正要给到的内存——
    /// 上游 2026-08-30 的单机压测里，网关的存活集只有 4.45 GB 而进程在 6.5 GB 的尖顶上被
    /// `Out of memory` 打死，差额全部来自"两次回收之间又分配了多少"。
    /// 每条消息分配得越少，同一台机器就能多持有越多连接。
    /// </para>
    /// <para>
    /// **测的是收发循环之外的那一段**：请求 JSON 已经解析成 <see cref="MvcRequestScheme"/> 与
    /// <c>JsonObject</c>，从这里到端点返回值为止。刻意不测整条 socket 往返——
    /// 那要跨线程，而 <see cref="GC.GetTotalAllocatedBytes(bool)"/> 是进程级的，
    /// 与并行跑的其它测试混在一起就不再是这条路径的数。
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> 只数当前线程，因此要求整段同步完成，
    /// 下面用 <c>IsCompleted</c> 把这一点断言掉——一旦某天它开始异步让出，这个测量会悄悄变小，
    /// 而断言会先红。
    /// </para>
    /// <para>
    /// Measures bytes allocated dispatching one already-parsed message to its endpoint. This decides
    /// how many connections a given amount of memory holds, not latency: the GC lets the heap climb
    /// between gen2 collections at the allocation rate, and it is that peak the container must fit.
    /// </para>
    /// </remarks>
    [Collection("StaticState")]
    public class MessageAllocationTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly IServiceProvider _previousServices;

        public MessageAllocationTests(ITestOutputHelper output)
        {
            _output = output;
            _previousServices = WebSocketRouteOption.ApplicationServices;
        }

        public void Dispose()
        {
            WebSocketRouteOption.ApplicationServices = _previousServices;
            MvcTestSupport.ResetCachedScopeFactory();
        }

        /// <summary>
        /// 上限。**它是一道棘轮，不是一个目标**：现在的实测值远在它之下，
        /// 写成上限是为了"哪天有人在这条路径上加了一次序列化"时先红一次。
        /// 调高它之前先回答一个问题：多出来的那部分乘以十万条连接是多少 GB。
        /// A ratchet rather than a target: raising it means paying the difference times the
        /// connection count in container memory.
        /// </summary>
        private const int BudgetBytesPerMessage = 4096;

        /// <summary>一条线上消息的原文，形状与 IM 的 conn.heartbeat 同级：短 target、小 body。</summary>
        private const string WireMessage = "{\"id\":\"1\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"hi\"}}";

        /// <summary>
        /// 把一条消息在库里的三段成本分开报出来：解析、分发、序列化响应。
        /// </summary>
        /// <remarks>
        /// 分开报是因为**合起来的那个数指不出该改哪里**。上游那次压测量到的是端到端每条约 5.4 KB
        /// （含 Kestrel、socket 与业务端点），而这三段是这个库自己的那部分——
        /// 下一个想把它压下去的人应该先看这三个数里哪个大，而不是从头再量一遍。
        /// Reported separately because the combined number does not say what to change.
        /// </remarks>
        [Fact]
        public async Task Where_the_per_message_allocation_goes()
        {
            using var host = await StartHostAsync();
            var options = host.Services.GetRequiredService<WebSocketRouteOption>();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            var context = NewContext(host);
            var request = MvcTestSupport.ParseRequest(WireMessage, options.DefaultRequestJsonSerializerOptions);

            for (var i = 0; i < 64; i++)
            {
                MvcTestSupport.ParseRequest(WireMessage, options.DefaultRequestJsonSerializerOptions);
                var warm = await Dispatch(options, context, lifetime, request);
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(warm, options.DefaultResponseJsonSerializerOptions);
            }

            const int Messages = 512;

            var parse = Measure(Messages, () => MvcTestSupport.ParseRequest(WireMessage, options.DefaultRequestJsonSerializerOptions));

            var before = GC.GetAllocatedBytesForCurrentThread();
            MvcResponseScheme response = null;
            for (var i = 0; i < Messages; i++)
            {
                response = await Dispatch(options, context, lifetime, request);
            }
            var dispatch = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Messages;

            var serialize = Measure(Messages,
                () => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response, options.DefaultResponseJsonSerializerOptions));

            _output.WriteLine($"parse     {parse,10:N0} bytes/message");
            _output.WriteLine($"dispatch  {dispatch,10:N0} bytes/message");
            _output.WriteLine($"serialize {serialize,10:N0} bytes/message");
            _output.WriteLine($"total     {parse + dispatch + serialize,10:N0} bytes/message");

            // 只断言"三段都测到了东西"。**这条用例的价值是那四行数字，不是一个阈值**——
            // 阈值在上面那条用例里，这里再放一个只会变成两处要一起改的常量。
            Assert.All(new[] { parse, dispatch, serialize }, value => Assert.True(value > 0));
        }

        /// <summary>
        /// 带超时的发送，在发送**已经同步完成**时不能有每次分配。
        /// </summary>
        /// <remarks>
        /// 网关每条消息回一帧，而每一帧都过这条路。原来的写法是
        /// <c>Task.WhenAny(sendTask, Task.Delay(timeout, linkedCts.Token))</c>——即使 sendTask
        /// 已经完成，它仍然要建一条 delay（定时器）、一个 WhenAny 组合任务，以及一个用来在赢了之后
        /// 拆掉定时器的链接 CTS，实测 **528 字节/次发送**。
        /// <c>Task.WaitAsync</c> 对已完成的任务直接短路，实测 0 字节。
        ///
        /// 阈值给 512 字节是**留给发送本身**（帧头、状态机）的，不是留给等待机制的：
        /// 一旦有人把等待改回 WhenAny+Delay，这条就会跨过去。
        /// The budget is for the send itself, not for the waiting mechanism: putting WhenAny+Delay
        /// back pushes it over.
        /// </remarks>
        [Fact]
        public async Task Sending_with_a_timeout_does_not_allocate_per_send_for_the_wait()
        {
            // **发送路径的配置是进程级静态字段**（MvcForward 在通道入口把 options 镜像进来），
            // 别的用例改过它们之后留在那里。不重设的话这条用例单跑 56 字节、跟全套一起跑 934 字节——
            // 而"单跑能过"不算过。这里显式钉住它测的那套配置：不物化上限、不切帧、不走流式降级。
            // The send path is configured through process-wide statics that other tests leave behind:
            // measured 56 bytes alone and 934 in the full suite before this reset.
            using var _ = new SendConfiguration(
                materializeBytes: 4L * 1024 * 1024,
                frameBytes: 256 * 1024 - 16,
                allowChunked: true,
                governorBytes: 0);

            var socket = new TestWebSocket();
            var payload = Encoding.UTF8.GetBytes("{\"id\":\"1\",\"status\":0}");

            for (var i = 0; i < 64; i++)
            {
                await Cyaim.WebSocketServer.Infrastructure.WebSocketManager.SendLocalAsync(
                    payload.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None,
                    timeout: TimeSpan.FromSeconds(5), sockets: socket);
            }

            const int Sends = 512;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Sends; i++)
            {
                await Cyaim.WebSocketServer.Infrastructure.WebSocketManager.SendLocalAsync(
                    payload.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None,
                    timeout: TimeSpan.FromSeconds(5), sockets: socket);
            }
            var perSend = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Sends;

            _output.WriteLine($"allocated {perSend:N0} bytes per timed send");
            Assert.InRange(perSend, 0, 512);
        }

        /// <summary>
        /// 换掉等待机制之后，超时那条分支还必须照旧：发送脱离、调用方不被挂住。
        /// </summary>
        /// <remarks>
        /// 这一条和上面那条是一对。只测"不分配"会让人可以把整段删掉换成 <c>await sendTask</c>——
        /// 那样确实不分配，而且会把调用方永远挂在一个不肯完成的发送上。
        /// Paired with the allocation test: measuring only "does not allocate" would be satisfied by
        /// deleting the timeout entirely, which parks the caller on a send that never completes.
        /// </remarks>
        [Fact]
        public async Task A_send_that_outlives_its_timeout_is_detached_rather_than_awaited()
        {
            var socket = new TestWebSocket { SendDelay = TimeSpan.FromSeconds(30) };
            var payload = Encoding.UTF8.GetBytes("{\"id\":\"1\"}");

            var started = DateTime.UtcNow;
            await Cyaim.WebSocketServer.Infrastructure.WebSocketManager.SendLocalAsync(
                payload.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None,
                timeout: TimeSpan.FromMilliseconds(200), sockets: socket);
            var waited = DateTime.UtcNow - started;

            Assert.True(waited < TimeSpan.FromSeconds(5),
                $"the caller waited {waited.TotalSeconds:N1}s for a send it should have detached from");
        }

        /// <summary>
        /// 进入时设定发送路径的静态配置，退出时还原成进入前的值——**还原是重点**：
        /// 这些字段是进程级的，用例之间会互相污染，而污染的表现是别的用例莫名其妙地慢或者贵。
        /// Sets the process-wide send configuration and restores whatever was there before.
        /// </summary>
        private sealed class SendConfiguration : IDisposable
        {
            private readonly long _materialize = Cyaim.WebSocketServer.Infrastructure.WebSocketManager.MaxSendMaterializeBytes;
            private readonly int _frame = Cyaim.WebSocketServer.Infrastructure.WebSocketManager.MaxSendFrameBytes;
            private readonly bool _chunked = Cyaim.WebSocketServer.Infrastructure.WebSocketManager.AllowChunkedSendAboveMaterializeLimit;
            private readonly long _governor = Cyaim.WebSocketServer.Infrastructure.WebSocketSendMemoryGovernor.MaxBytes;

            public SendConfiguration(long materializeBytes, int frameBytes, bool allowChunked, long governorBytes)
            {
                Cyaim.WebSocketServer.Infrastructure.WebSocketManager.MaxSendMaterializeBytes = materializeBytes;
                Cyaim.WebSocketServer.Infrastructure.WebSocketManager.MaxSendFrameBytes = frameBytes;
                Cyaim.WebSocketServer.Infrastructure.WebSocketManager.AllowChunkedSendAboveMaterializeLimit = allowChunked;
                Cyaim.WebSocketServer.Infrastructure.WebSocketSendMemoryGovernor.MaxBytes = governorBytes;
            }

            public void Dispose()
            {
                Cyaim.WebSocketServer.Infrastructure.WebSocketManager.MaxSendMaterializeBytes = _materialize;
                Cyaim.WebSocketServer.Infrastructure.WebSocketManager.MaxSendFrameBytes = _frame;
                Cyaim.WebSocketServer.Infrastructure.WebSocketManager.AllowChunkedSendAboveMaterializeLimit = _chunked;
                Cyaim.WebSocketServer.Infrastructure.WebSocketSendMemoryGovernor.MaxBytes = _governor;
            }
        }

        private static double Measure(int iterations, Action action)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++)
            {
                action();
            }
            return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;
        }

        private static async Task<MvcResponseScheme> Dispatch(
            WebSocketRouteOption options,
            HttpContext context,
            IHostApplicationLifetime lifetime,
            (MvcRequestScheme Scheme, System.Text.Json.Nodes.JsonObject Body) request)
        {
            var task = MvcChannelHandler.MvcDistributeAsync(
                options, context, webSocket: null, request.Scheme, request.Body,
                logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<WebSocketRouteMiddleware>.Instance,
                appLifetime: lifetime);
            Assert.True(task.IsCompleted, "dispatch went asynchronous; the per-thread allocation reading no longer covers it");
            return await task;
        }

        private static HttpContext NewContext(IHost host)
        {
            var context = new DefaultHttpContext { RequestServices = host.Services };
            context.Connection.Id = "alloc-test";
            context.Request.Path = "/ws";
            return context;
        }

        [Fact]
        public async Task Dispatching_one_message_stays_inside_the_allocation_budget()
        {
            using var host = await StartHostAsync();
            var options = host.Services.GetRequiredService<WebSocketRouteOption>();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            var context = new DefaultHttpContext { RequestServices = host.Services };
            context.Connection.Id = "alloc-test";
            context.Request.Path = "/ws";

            var request = MvcTestSupport.ParseRequest(
                "{\"id\":\"1\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"hi\"}}",
                options.DefaultRequestJsonSerializerOptions);

            // 预热：端点解析、方法调用器、序列化元数据全部是一次性成本，
            // 混进测量里会把结果抬高一个数量级。
            for (var i = 0; i < 64; i++)
            {
                await DispatchOnceAsync(options, context, lifetime, request);
            }

            const int Messages = 512;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Messages; i++)
            {
                await DispatchOnceAsync(options, context, lifetime, request);
            }
            var perMessage = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Messages;

            _output.WriteLine($"allocated {perMessage:N0} bytes per dispatched message (budget {BudgetBytesPerMessage:N0})");
            Assert.InRange(perMessage, 1, BudgetBytesPerMessage);
        }

        /// <summary>
        /// 一次分发，并且**断言它是同步完成的**——见类型注释：不同步完成的话，
        /// GetAllocatedBytesForCurrentThread 会漏掉跑在别的线程上的那部分，量出来的数偏小而看不出来。
        /// </summary>
        private static async Task DispatchOnceAsync(
            WebSocketRouteOption options,
            HttpContext context,
            IHostApplicationLifetime lifetime,
            (MvcRequestScheme Scheme, System.Text.Json.Nodes.JsonObject Body) request)
        {
            var task = MvcChannelHandler.MvcDistributeAsync(
                options, context, webSocket: null, request.Scheme, request.Body,
                logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<WebSocketRouteMiddleware>.Instance,
                appLifetime: lifetime);

            Assert.True(task.IsCompleted, "dispatch went asynchronous; the per-thread allocation reading no longer covers it");
            var response = await task;
            Assert.True(response.Status == 0, $"dispatch failed: status={response.Status} msg={response.Msg}");
        }

        private static async Task<IHost> StartHostAsync()
        {
            var option = new WebSocketRouteOption
            {
                WebSocketChannels = new System.Collections.Generic.Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/ws"] = new MvcChannelHandler().ConnectionEntry
                },
                WatchAssemblyContext = MvcTestSupport.BuildContext(typeof(MvcTestSupport.WsTestController))
            };

            var host = new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(option);
                        services.AddSingleton<MvcTestSupport.IGreetService, MvcTestSupport.GreetService>();
                    })
                    .Configure(app =>
                    {
                        app.UseWebSockets();
                        app.UseWebSocketServer();
                    }))
                .Build();

            await host.StartAsync();
            WebSocketRouteOption.ApplicationServices = host.Services;
            return host;
        }
    }
}
