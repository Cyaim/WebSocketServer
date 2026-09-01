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
using Xunit;

namespace Cyaim.WebSocketServer.Tests
{
    /// <summary>An endpoint that throws with a message a leak test can look for.</summary>
    public class ThrowingController
    {
        /// <summary>The exception message, chosen to be unmistakable if it ever reaches a client.</summary>
        public const string Secret = "connection string to db-prod-07 refused";

        public string Boom() => throw new InvalidOperationException(Secret);
    }

    /// <summary>
    /// That an endpoint throwing does not hand the caller a stack trace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default used to put <c>ex.Message</c> and the full stack trace into the response, and the
    /// response goes back over the socket. Any client that could make an endpoint throw — which is
    /// usually just a matter of sending the wrong type in a field — got assembly names, file paths,
    /// internal type names and framework versions out of a server it had only just connected to.
    /// 这个默认值原本把 ex.Message 和完整堆栈写进响应，而响应是经 socket 回给调用方的。
    /// 任何能让端点抛异常的客户端——通常只需要在某个字段里传错类型——都能从一台它刚连上的服务器
    /// 拿到程序集名、文件路径、内部类型名和框架版本。
    /// </para>
    /// <para>
    /// A host could already override it through <c>ExceptionEvent</c>, and at least one does. That is
    /// not a defence: a default that leaks unless the host knows to override it is a default that
    /// leaks, and a library published as a package is used by hosts that do not know.
    /// 宿主本来就能用 ExceptionEvent 盖掉它，而且确实有宿主这么做了。那不构成辩护：
    /// 一个「宿主不知道要覆盖就会泄」的默认值就是会泄的默认值，
    /// 而作为包发布的库，用它的宿主正是那些不知道的人。
    /// </para>
    /// <para>
    /// This asserts on the frame the client actually receives, not on the response object the server
    /// built. Asserting on the object would pass for a server that constructed a safe message and then
    /// sent a different one.
    /// 断言落在**客户端真正收到的那一帧**上，而不是服务端构造出来的响应对象。
    /// 对着对象断言，会让一个「构造了安全消息、却发出了另一条」的服务器照样通过。
    /// </para>
    /// </remarks>
    [Collection("StaticState")]
    public class ExceptionDetailOnWireTests : IDisposable
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

        private readonly IServiceProvider _previousServices;

        public ExceptionDetailOnWireTests()
        {
            _previousServices = WebSocketRouteOption.ApplicationServices;
            MvcTestSupport.ResetCachedScopeFactory();
        }

        public void Dispose()
        {
            WebSocketRouteOption.ApplicationServices = _previousServices;
            MvcTestSupport.ResetCachedScopeFactory();
        }

        private static async Task<IHost> StartHostAsync()
        {
            var option = new WebSocketRouteOption
            {
                WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/ws"] = new MvcChannelHandler().ConnectionEntry
                },
                WatchAssemblyContext = MvcTestSupport.BuildContext(typeof(ThrowingController)),
            };

            var host = new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(services => services.AddSingleton(option))
                    .Configure(app =>
                    {
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

        [Fact]
        public async Task An_endpoint_that_throws_does_not_put_its_stack_trace_on_the_wire()
        {
            using var host = await StartHostAsync();
            using var cts = new CancellationTokenSource(TestTimeout);

            var server = host.GetTestServer();
            var socket = await server.CreateWebSocketClient()
                .ConnectAsync(new Uri(server.BaseAddress, "/ws"), cts.Token);

            string frame = JsonSerializer.Serialize(new { Id = "boom-1", Target = "throwing.boom", Body = new { } });
            await socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, cts.Token);

            var buffer = new byte[64 * 1024];
            var received = await socket.ReceiveAsync(buffer, cts.Token);
            string reply = Encoding.UTF8.GetString(buffer, 0, received.Count);

            // Anti-vacuity: a reply that never arrived, or one for a different request, proves nothing.
            // 反真空：没收到回复、或收到的是别的请求的回复，都证明不了任何事。
            Assert.Contains("boom-1", reply);

            Assert.DoesNotContain(
                ThrowingController.Secret, reply, StringComparison.Ordinal);

            Assert.DoesNotContain(
                "InvalidOperationException", reply, StringComparison.Ordinal);

            // The give-away shape of a .NET stack trace, whatever the frames happen to be.
            // .NET 堆栈的标志性形状，不管具体帧是什么。
            Assert.DoesNotContain("   at ", reply, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(MvcChannelHandler), reply, StringComparison.Ordinal);

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
        }

        /// <summary>
        /// A host that wants the detail can still put it back.
        /// </summary>
        /// <remarks>
        /// Without this the fix would be a removal rather than a relocation, and someone would
        /// eventually restore the leak because there was no supported way to debug.
        /// 没有这一条，这次修复就成了「删掉」而不是「挪走」，
        /// 早晚会有人因为「没有受支持的调试办法」而把泄漏加回去。
        /// </remarks>
        [Fact]
        public async Task A_host_can_still_opt_into_the_detail_through_ExceptionEvent()
        {
            var option = new WebSocketRouteOption
            {
                WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/ws"] = new MvcChannelHandler().ConnectionEntry
                },
                WatchAssemblyContext = MvcTestSupport.BuildContext(typeof(ThrowingController)),
            };

            option.ExceptionEvent += static (ex, request, response, context, opts, path, logger) =>
            {
                response.Msg = ex.Message;
                return Task.FromResult(response);
            };

            using var host = new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(services => services.AddSingleton(option))
                    .Configure(app =>
                    {
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

            using var cts = new CancellationTokenSource(TestTimeout);
            var server = host.GetTestServer();
            var socket = await server.CreateWebSocketClient()
                .ConnectAsync(new Uri(server.BaseAddress, "/ws"), cts.Token);

            string frame = JsonSerializer.Serialize(new { Id = "boom-2", Target = "throwing.boom", Body = new { } });
            await socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, cts.Token);

            var buffer = new byte[64 * 1024];
            var received = await socket.ReceiveAsync(buffer, cts.Token);
            string reply = Encoding.UTF8.GetString(buffer, 0, received.Count);

            Assert.Contains(ThrowingController.Secret, reply, StringComparison.Ordinal);

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
        }
    }
}
