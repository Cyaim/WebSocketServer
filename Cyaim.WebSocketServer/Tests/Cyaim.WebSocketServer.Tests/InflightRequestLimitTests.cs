using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>
    /// Per-host probe: counts how many calls are inside the endpoint, and parks them until released.
    /// </summary>
    /// <remarks>
    /// Injected rather than static, and that is not tidiness. With static counters the previous test's
    /// connection kept incrementing them after the next test had reset them — the disposal of one host
    /// releases its parked requests, and those completions land during the following test. It showed up
    /// as 16 pipelined requests being counted as 20, which reads like the cap failing when it is the
    /// harness leaking.
    /// 用注入而不是静态，不是整洁强迫症：静态计数会让上一条测试的连接在下一条 Reset 之后继续加数——
    /// 销毁一个 host 会释放它停住的请求，那些完成落在下一条测试期间。
    /// 现象是 16 条流水线被数成 20，读起来像上限失效，其实是脚手架在漏。
    /// </remarks>
    public sealed class InflightProbe
    {
        private int _current;
        private int _peak;
        private int _entered;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Peak => Volatile.Read(ref _peak);

        public int Entered => Volatile.Read(ref _entered);

        public int Current => Volatile.Read(ref _current);

        public void ReleaseAll() => _release.TrySetResult();

        public async Task EnterAsync()
        {
            int now = Interlocked.Increment(ref _current);
            Interlocked.Increment(ref _entered);

            // Peak is a max, not a sample: the assertion is about the highest concurrency the gate ever
            // allowed, and sampling would miss the moment it was exceeded.
            // peak 取的是最大值而不是采样：断言问的是闸门**曾经**放进去多少，采样会错过越界的那一刻。
            int seen;
            while (now > (seen = Volatile.Read(ref _peak)))
            {
                Interlocked.CompareExchange(ref _peak, now, seen);
            }

            await _release.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref _current);
        }
    }

    /// <summary>The endpoint the tests pipeline into. Target resolves as "probe.park".</summary>
    public class ProbeController
    {
        private readonly InflightProbe _probe;

        public ProbeController(InflightProbe probe) => _probe = probe;

        public async Task<string> Park()
        {
            await _probe.EnterAsync().ConfigureAwait(false);
            return "parked";
        }
    }

    /// <summary>
    /// That a client cannot pipeline an unbounded number of requests into flight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate used to be released in the receive loop's <c>finally</c> — that is, the moment the
    /// request was <i>dispatched</i>, not when it <i>finished</i>. A client that pipelines without
    /// waiting for responses therefore had no bound at all: each in-flight request holds a DI scope,
    /// a controller instance and a parsed body, and nothing counted them. In-flight ≈ attacker
    /// bandwidth × backend latency, and it feeds back on itself, because the pressure slows the
    /// backend and slower backend means more in flight.
    /// 闸门此前在接收循环的 finally 里释放——也就是请求被**派发**的那一刻，而不是它**完成**的那一刻。
    /// 于是不等响应就流水线发送的客户端完全不受约束：每条在途都持有一个 DI Scope、
    /// 一个控制器实例和一份解析好的请求体，而没有任何东西在数它们。
    /// 在途数 ≈ 攻击者带宽 × 后端时延，而且是正反馈：压力让后端更慢，后端更慢就有更多在途。
    /// </para>
    /// <para>
    /// The gate was also a field on the handler, and <c>AddMvcChannel</c> builds one handler per
    /// channel — so it was shared by every connection despite its name. Both halves are asserted
    /// here: the cap binds, and it binds <i>per connection</i>.
    /// 那个闸门还是 handler 上的字段，而 AddMvcChannel 每通道只建一个 handler——
    /// 于是它名为「每连接」，实为所有连接共用。两半都在这里断言：上限生效，且是**每连接**生效。
    /// </para>
    /// </remarks>
    [Collection("StaticState")]
    public class InflightRequestLimitTests : IDisposable
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

        private readonly IServiceProvider _previousServices;
        private readonly InflightProbe _probe = new();

        public InflightRequestLimitTests()
        {
            _previousServices = WebSocketRouteOption.ApplicationServices;
            MvcTestSupport.ResetCachedScopeFactory();
        }

        public void Dispose()
        {
            _probe.ReleaseAll();
            WebSocketRouteOption.ApplicationServices = _previousServices;
            MvcTestSupport.ResetCachedScopeFactory();
        }

        private async Task<IHost> StartHostAsync(uint? limit)
        {
            var option = new WebSocketRouteOption
            {
                WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/ws"] = new MvcChannelHandler().ConnectionEntry
                },
                WatchAssemblyContext = MvcTestSupport.BuildContext(typeof(ProbeController)),
                MaxConnectionParallelForwardLimit = limit,
            };

            var host = new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(option);
                        services.AddSingleton(_probe);
                    })
                    .Configure(app =>
                    {
                        // TestServer leaves HttpContext.Connection.Id null and the handler requires one.
                        // TestServer 不填 Connection.Id，而 handler 需要它非空。
                        app.Use(async (ctx, next) =>
                        {
                            ctx.Connection.Id ??= Guid.NewGuid().ToString("N");
                            await next();
                        });
                        app.UseWebSockets();
                        app.UseWebSocketServer();
                    }))
                .Build();

            await host.StartAsync();
            return host;
        }

        private static async Task<WebSocket> ConnectAsync(IHost host)
        {
            var server = host.GetTestServer();
            var client = server.CreateWebSocketClient();
            using var cts = new CancellationTokenSource(TestTimeout);
            return await client.ConnectAsync(new Uri(server.BaseAddress, "/ws"), cts.Token);
        }

        private static Task PipelineAsync(WebSocket socket, int count, CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                for (int i = 0; i < count; i++)
                {
                    string frame = JsonSerializer.Serialize(new
                    {
                        Id = "p" + i,
                        // BuildContext keys endpoints as "{type name minus Controller}.{method}", lowercased.
                        // Getting this wrong does not fail loudly: the request resolves to nothing, zero
                        // handlers run, and an assertion like "peak <= limit" passes with peak = 0.
                        // 端点键是「类名去掉 Controller」+「.」+ 方法名，全小写。写错了不会响亮地失败：
                        // 请求解析不到任何东西、零个处理器执行，而「peak <= limit」这种断言在 peak = 0 时照样通过。
                        Target = "probe.park",
                        Body = new { },
                    });

                    await socket.SendAsync(
                        Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                }
            }, ct);
        }

        /// <summary>
        /// Drains responses in the background, so the connection is not held up by an unread socket.
        /// </summary>
        /// <remarks>
        /// A test that never reads is testing backpressure, not the permit. With the gate released on
        /// completion, and completion including "the response was sent", a client that does not read
        /// stops being served at exactly the cap — which is the correct behaviour and was measured
        /// here before this reader existed: 12 pipelined, 3 admitted, and nothing moved after the
        /// endpoint was let go. That is the bound doing its job, not a leak, and it is why the permit
        /// test needs a reader while the cap tests deliberately do not have one.
        /// 从不读 socket 的测试测的是背压，不是票。闸门在**完成**时释放，而完成包含「响应已发出」，
        /// 于是不读的客户端恰好卡在上限上——这是正确行为，而且在这个读取端存在之前就实测到了：
        /// 流水线 12 条、放进 3 条，放开端点之后一动不动。那是界在起作用，不是漏票。
        /// 这也是为什么「还票」那条测试需要读取端，而「上限」那两条刻意不需要。
        /// </remarks>
        private static Task DrainAsync(WebSocket socket, CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                var buffer = new byte[8 * 1024];
                try
                {
                    while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // The socket closing under the reader is the normal end of this loop.
                    // 读取端在 socket 关闭时结束，是正常终止。
                }
            }, ct);
        }

        /// <summary>
        /// Ends a test's connection so its leftovers cannot land on the next test's probe.
        /// </summary>
        /// <remarks>
        /// <c>WebSocketRouteOption.ApplicationServices</c> is static, so a receive loop that is still
        /// draining pipelined messages after its own host is gone resolves controllers out of whichever
        /// host is current — the <i>next</i> test's. It shows up as that test counting more requests
        /// than it sent (16 pipelined, 20 counted), which reads exactly like the cap failing. Releasing
        /// the parked calls and closing the socket is what stops one test from writing into another.
        /// WebSocketRouteOption.ApplicationServices 是静态的：一条在自己 host 消失之后还在排空
        /// 流水线消息的接收循环，会从**当前**那个 host 里解析控制器——也就是下一条测试的。
        /// 现象是那条测试数到的请求比它发出去的还多（发 16、数到 20），读起来就像上限失效。
        /// 放开停住的调用、关掉 socket，才能让一条测试不写进另一条。
        /// </remarks>
        private async Task EndConnectionsAsync(params WebSocket[] sockets)
        {
            _probe.ReleaseAll();

            foreach (var socket in sockets)
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token)
                            .ConfigureAwait(false);
                    }
                }
                catch
                {
                    // A socket the server already aborted is closed enough for this purpose.
                    // 服务端已经 Abort 掉的 socket，对这里的目的而言已经足够「关上了」。
                }
            }

            // Give the server's loops a moment to notice the close before the host goes away.
            // 给服务端的循环一点时间察觉关闭，然后 host 才消失。
            await Task.Delay(200).ConfigureAwait(false);
        }

        /// <summary>Waits for the probe to stop admitting new calls, or for the timeout.</summary>
        private async Task SettleAsync()
        {
            int stable = 0, last = -1;
            for (int i = 0; i < 200 && stable < 10; i++)
            {
                await Task.Delay(20).ConfigureAwait(false);
                int now = _probe.Entered;
                stable = now == last ? stable + 1 : 0;
                last = now;
            }
        }

        [Fact]
        public async Task A_client_cannot_pipeline_more_requests_into_flight_than_the_limit()
        {
            const int limit = 3;
            const int pipelined = 24;

            using var host = await StartHostAsync(limit);
            using var cts = new CancellationTokenSource(TestTimeout);
            var socket = await ConnectAsync(host);

            await PipelineAsync(socket, pipelined, cts.Token);
            await SettleAsync();

            // Anti-vacuity first: "peak <= limit" is trivially true when nothing ran at all, which is
            // exactly what a mistyped target produces. Assert the path was exercised before asserting
            // anything about the bound.
            // 先反真空：什么都没跑时「peak <= limit」恒真，而目标写错正好产出这个结果。
            // 先断言这条路径真的被走过，再去断言那条界。
            Assert.True(
                _probe.Entered > 0,
                "no request reached the endpoint at all — this test would pass for the wrong reason");

            Assert.True(
                _probe.Peak <= limit,
                $"pipelined {pipelined} requests behind a limit of {limit}; {_probe.Peak} were in flight at once");

            // And the gate is actually holding the rest back rather than the client being slow:
            // without the cap all 24 would have entered.
            // 而且确实是闸门在挡，不是客户端慢：不设界的话 24 条都会进来。
            Assert.True(
                _probe.Entered <= limit,
                $"{_probe.Entered} requests entered the endpoint; only {limit} should have");

            await EndConnectionsAsync(socket);
        }

        /// <summary>
        /// The permits come back. This is the risk the fix introduces, and the reason it is asserted
        /// separately: a gate that never releases turns a denial of service into a deadlock, which is
        /// not an improvement.
        /// 票要还得回来。这是这次修复引入的风险，所以单独断言：
        /// 一个永不释放的闸门把拒绝服务变成死锁，那不叫改进。
        /// </summary>
        [Fact]
        public async Task Finished_requests_give_their_permit_back()
        {
            const int limit = 3;
            const int pipelined = 12;

            using var host = await StartHostAsync(limit);
            using var cts = new CancellationTokenSource(TestTimeout);
            var socket = await ConnectAsync(host);

            _ = DrainAsync(socket, cts.Token);

            await PipelineAsync(socket, pipelined, cts.Token);
            await SettleAsync();

            int held = _probe.Entered;
            Assert.True(held <= limit, $"{held} entered before release; the cap was {limit}");

            _probe.ReleaseAll();

            // Every queued request must now drain. If a permit leaked, this stalls at `held`.
            // 排队的请求现在必须全部流干。漏了票的话，这里会停在 held 上不动。
            for (int i = 0; i < 400 && _probe.Entered < pipelined; i++)
            {
                await Task.Delay(25);
            }

            Assert.Equal(pipelined, _probe.Entered);

            await EndConnectionsAsync(socket);
        }

        /// <summary>
        /// Leaving the limit unset must not mean "no limit". It used to.
        /// 不设上限不能等于「没有上限」。它曾经就是。
        /// </summary>
        [Fact]
        public async Task An_unset_limit_still_bounds_in_flight_requests()
        {
            int pipelined = MvcChannelHandler.DefaultConnectionInflightLimit * 4;

            using var host = await StartHostAsync(null);
            using var cts = new CancellationTokenSource(TestTimeout);
            var socket = await ConnectAsync(host);

            await PipelineAsync(socket, pipelined, cts.Token);
            await SettleAsync();

            Assert.True(
                _probe.Entered > 0,
                "no request reached the endpoint at all — this test would pass for the wrong reason");

            Assert.True(
                _probe.Peak <= MvcChannelHandler.DefaultConnectionInflightLimit,
                $"with no configured limit, {_probe.Peak} requests were in flight; "
                + $"the default is {MvcChannelHandler.DefaultConnectionInflightLimit}");

            await EndConnectionsAsync(socket);
        }

        /// <summary>
        /// The cap is per connection, not per process.
        /// </summary>
        /// <remarks>
        /// The old gate lived on the handler, and <c>AddMvcChannel</c> builds one handler per channel,
        /// so a cap of N was N for the whole server. On a gateway carrying a million connections that
        /// is not a smaller bound, it is a different failure: every connection queues behind every
        /// other one. This asserts the bound scales with connections instead.
        /// 旧闸门挂在 handler 上，而 AddMvcChannel 每通道只建一个 handler，于是上限 N 是**整台服务器** N。
        /// 在扛百万连接的网关上这不是「更严的界」，是另一种故障：每条连接都排在其余所有连接后面。
        /// 这条断言的是界随连接数放大。
        /// </remarks>
        [Fact]
        public async Task The_limit_is_per_connection_not_per_process()
        {
            const int limit = 2;

            using var host = await StartHostAsync(limit);
            using var cts = new CancellationTokenSource(TestTimeout);

            var a = await ConnectAsync(host);
            var b = await ConnectAsync(host);

            await PipelineAsync(a, 8, cts.Token);
            await PipelineAsync(b, 8, cts.Token);
            await SettleAsync();

            // Two connections, so two connections' worth of permits — a process-wide gate would have
            // admitted `limit` in total no matter how many connections were asking.
            // 两条连接就该有两条连接的票。进程级闸门无论多少连接在要，总共只放 limit 条。
            Assert.True(
                _probe.Entered > limit,
                $"two connections admitted only {_probe.Entered} requests, "
                + $"which is what a single process-wide gate of {limit} would do");

            Assert.True(
                _probe.Entered <= limit * 2,
                $"two connections admitted {_probe.Entered}; the per-connection cap of {limit} allows at most {limit * 2}");

            await EndConnectionsAsync(a, b);
        }
    }
}
