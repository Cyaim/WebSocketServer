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
