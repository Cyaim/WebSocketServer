using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers;
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
    /// Full round-trip tests: TestServer + WebSocketRouteMiddleware + MvcChannelHandler
    /// receive loop + MvcDistributeAsync + WebSocketManager response send.
    /// Exercises UseWebSocketServer(), which assigns WebSocketRouteOption.ApplicationServices,
    /// so the class runs in the StaticState collection and restores statics.
    /// </summary>
    [Collection("StaticState")]
    public class WebSocketServerEndToEndTests : IDisposable
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

        private readonly IServiceProvider _previousServices;

        public WebSocketServerEndToEndTests()
        {
            _previousServices = WebSocketRouteOption.ApplicationServices;
            MvcTestSupport.ResetCachedScopeFactory();
        }

        public void Dispose()
        {
            WebSocketRouteOption.ApplicationServices = _previousServices;
            MvcTestSupport.ResetCachedScopeFactory();
        }

        private static WebSocketRouteOption CreateOption(Action<WebSocketRouteOption> configure = null, MvcChannelHandler handler = null)
        {
            var option = new WebSocketRouteOption
            {
                WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/ws"] = (handler ?? new MvcChannelHandler()).ConnectionEntry
                },
                WatchAssemblyContext = MvcTestSupport.BuildContext(typeof(MvcTestSupport.WsTestController))
            };
            configure?.Invoke(option);
            return option;
        }

        private static async Task<IHost> StartHostAsync(WebSocketRouteOption option)
        {
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
                        // TestServer leaves HttpContext.Connection.Id null; MvcChannelHandler
                        // requires a non-null connection id (Clients.TryAdd / bandwidth tracker
                        // would throw ArgumentNullException otherwise), so assign one per request.
                        app.Use(async (context, next) =>
                        {
                            context.Connection.Id ??= Guid.NewGuid().ToString("N");
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

        private static async Task<string> SendAndReceiveAsync(WebSocket socket, string requestJson)
        {
            using var cts = new CancellationTokenSource(TestTimeout);
            await socket.SendAsync(Encoding.UTF8.GetBytes(requestJson), WebSocketMessageType.Text, true, cts.Token);
            return await ReceiveFullMessageAsync(socket, cts.Token);
        }

        private static async Task<string> ReceiveFullMessageAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("Connection closed while waiting for a response.");
                }
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                }
            }
        }

        [Fact]
        public async Task Echo_RoundTrip_ReturnsStatus0AndBody()
        {
            using var host = await StartHostAsync(CreateOption());
            var socket = await ConnectAsync(host);

            string response = await SendAndReceiveAsync(socket, "{\"id\":\"42\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"hi\"}}");

            using var doc = JsonDocument.Parse(response);
            Assert.Equal(0, doc.RootElement.GetProperty("Status").GetInt32());
            Assert.Equal("42", doc.RootElement.GetProperty("Id").GetString());
            Assert.Equal("wstest.echo", doc.RootElement.GetProperty("Target").GetString());
            Assert.Equal("echo:hi", doc.RootElement.GetProperty("Body").GetString());

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();
        }

        [Fact]
        public async Task AsyncEndpoint_RoundTrip_ReturnsUnwrappedTaskResult()
        {
            using var host = await StartHostAsync(CreateOption());
            var socket = await ConnectAsync(host);

            string response = await SendAndReceiveAsync(socket, "{\"id\":\"7\",\"target\":\"wstest.echoasync\",\"body\":{\"text\":\"async\"}}");

            using var doc = JsonDocument.Parse(response);
            Assert.Equal(0, doc.RootElement.GetProperty("Status").GetInt32());
            Assert.Equal("async:async", doc.RootElement.GetProperty("Body").GetString());

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();
        }

        [Fact]
        public async Task UnknownTarget_RoundTrip_ReturnsStatus2()
        {
            using var host = await StartHostAsync(CreateOption());
            var socket = await ConnectAsync(host);

            string response = await SendAndReceiveAsync(socket, "{\"id\":\"1\",\"target\":\"no.suchendpoint\",\"body\":{}}");

            using var doc = JsonDocument.Parse(response);
            Assert.Equal(2, doc.RootElement.GetProperty("Status").GetInt32());

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();
        }

        [Fact]
        public async Task ThrowingEndpoint_RoundTrip_ReturnsStatus1()
        {
            using var host = await StartHostAsync(CreateOption());
            var socket = await ConnectAsync(host);

            string response = await SendAndReceiveAsync(socket, "{\"id\":\"1\",\"target\":\"wstest.throw\"}");

            using var doc = JsonDocument.Parse(response);
            Assert.Equal(1, doc.RootElement.GetProperty("Status").GetInt32());
            // Sync endpoint exceptions are wrapped in TargetInvocationException, so the
            // message references the target rather than the original exception text.
            Assert.Contains("wstest.throw", doc.RootElement.GetProperty("Msg").GetString());

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();
        }

        [Fact]
        public async Task RequireRequestId_MissingId_ReturnsStatus1Error()
        {
            using var host = await StartHostAsync(CreateOption(o => o.RequireRequestId = true));
            var socket = await ConnectAsync(host);

            string response = await SendAndReceiveAsync(socket, "{\"target\":\"wstest.echo\",\"body\":{\"text\":\"hi\"}}");

            using var doc = JsonDocument.Parse(response);
            Assert.Equal(1, doc.RootElement.GetProperty("Status").GetInt32());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("Msg").GetString()));

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();
        }

        [Fact]
        public async Task RequireRequestIdDisabled_MissingId_StillDispatches()
        {
            using var host = await StartHostAsync(CreateOption(o => o.RequireRequestId = false));
            var socket = await ConnectAsync(host);

            string response = await SendAndReceiveAsync(socket, "{\"target\":\"wstest.echo\",\"body\":{\"text\":\"ok\"}}");

            using var doc = JsonDocument.Parse(response);
            Assert.Equal(0, doc.RootElement.GetProperty("Status").GetInt32());
            Assert.Equal("echo:ok", doc.RootElement.GetProperty("Body").GetString());

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();
        }

        [Fact]
        public async Task NonWebSocketRequest_OnChannelPath_Returns400()
        {
            using var host = await StartHostAsync(CreateOption());
            var client = host.GetTestServer().CreateClient();

            var response = await client.GetAsync("/ws");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await host.StopAsync();
        }

        [Fact]
        public async Task RequestOnOtherPath_FallsThroughMiddleware_Returns404()
        {
            using var host = await StartHostAsync(CreateOption());
            var client = host.GetTestServer().CreateClient();

            var response = await client.GetAsync("/not-a-channel");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await host.StopAsync();
        }

        [Fact]
        public async Task MaxConnectionLimitZero_RejectsHandshake()
        {
            using var host = await StartHostAsync(CreateOption(o => o.MaxConnectionLimit = 0));

            await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(host));
            await host.StopAsync();
        }

        [Fact]
        public async Task BeforeConnectionEvent_False_RejectsHandshake()
        {
            using var host = await StartHostAsync(CreateOption(o =>
                o.BeforeConnectionEvent += (context, opt, channel, logger) => Task.FromResult(false)));

            await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(host));
            await host.StopAsync();
        }

        [Fact]
        public async Task MultipleRequestsOnOneConnection_AllServed()
        {
            using var host = await StartHostAsync(CreateOption(o => o.EnableForwardTaskSyncProcessingMode = true));
            var socket = await ConnectAsync(host);

            string first = await SendAndReceiveAsync(socket, "{\"id\":\"1\",\"target\":\"wstest.add\",\"body\":{\"a\":1,\"b\":2}}");
            string second = await SendAndReceiveAsync(socket, "{\"id\":\"2\",\"target\":\"wstest.add\",\"body\":{\"a\":10,\"b\":20}}");

            using (var doc = JsonDocument.Parse(first))
            {
                Assert.Equal(3, doc.RootElement.GetProperty("Body").GetInt32());
            }
            using (var doc = JsonDocument.Parse(second))
            {
                Assert.Equal(30, doc.RootElement.GetProperty("Body").GetInt32());
            }

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();
        }

        [Fact]
        public async Task Middleware_WrapsRequest_DuringRoundTrip()
        {
            var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
            string observedTarget = null;

            using var host = await StartHostAsync(CreateOption(o =>
            {
                o.EnableForwardTaskSyncProcessingMode = true;
                o.Use(async (ctx, next) =>
                {
                    events.Enqueue("before");
                    observedTarget = ctx.Request?.Target;   // middleware sees the parsed request
                    await next(ctx);                        // run the endpoint
                    events.Enqueue("after");                // ...then wrap the response
                });
            }));
            var socket = await ConnectAsync(host);

            string response = await SendAndReceiveAsync(socket, "{\"id\":\"1\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"p\"}}");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();

            using var doc = JsonDocument.Parse(response);
            Assert.Equal(0, doc.RootElement.GetProperty("Status").GetInt32());
            // The endpoint still ran (correct response) and the middleware wrapped it before + after.
            Assert.Equal("wstest.echo", observedTarget);
            Assert.Equal(new[] { "before", "after" }, events.ToArray());
        }

        [Fact]
        public async Task Middleware_ShortCircuit_ReplacesResponse_DuringRoundTrip()
        {
            using var host = await StartHostAsync(CreateOption(o =>
            {
                o.EnableForwardTaskSyncProcessingMode = true;
                o.Use((ctx, next) =>
                {
                    // Do not call next -> the endpoint never runs; respond directly.
                    ctx.Response = new MvcResponseScheme { Status = 7, Id = ctx.Request?.Id, Target = ctx.Request?.Target, Body = "blocked" };
                    return Task.CompletedTask;
                });
            }));
            var socket = await ConnectAsync(host);

            string response = await SendAndReceiveAsync(socket, "{\"id\":\"9\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"p\"}}");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();

            using var doc = JsonDocument.Parse(response);
            Assert.Equal(7, doc.RootElement.GetProperty("Status").GetInt32());
            Assert.Equal("blocked", doc.RootElement.GetProperty("Body").GetString());
        }

        [Fact]
        public async Task BandwidthLimitPolicyEnabled_RequestStillServed()
        {
            using var host = await StartHostAsync(CreateOption(o => o.BandwidthLimitPolicy = new BandwidthLimitPolicy
            {
                Enabled = true,
                GlobalChannelBandwidthLimit = { ["/ws"] = 10 * 1024 * 1024 },
                ChannelMaxBandwidthLimit = { ["/ws"] = 10 * 1024 * 1024 },
                EndPointMaxBandwidthLimit = { ["wstest.echo"] = 10 * 1024 * 1024 }
            }));
            var socket = await ConnectAsync(host);

            string response = await SendAndReceiveAsync(socket, "{\"id\":\"1\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"bw\"}}");

            using var doc = JsonDocument.Parse(response);
            Assert.Equal(0, doc.RootElement.GetProperty("Status").GetInt32());
            Assert.Equal("echo:bw", doc.RootElement.GetProperty("Body").GetString());

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();
        }

        [Fact]
        public async Task ParallelForwardLimits_RequestStillServed()
        {
            var endPointLimits = new System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>();
            endPointLimits["wstest.echo"] = new SemaphoreSlim(1, 1);
            using var host = await StartHostAsync(CreateOption(o =>
            {
                o.MaxConnectionParallelForwardLimit = 2;
                o.MaxEndPointParallelForwardLimit = endPointLimits;
                o.EnableForwardTaskSyncProcessingMode = true;
            }));
            var socket = await ConnectAsync(host);

            string first = await SendAndReceiveAsync(socket, "{\"id\":\"1\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"a\"}}");
            string second = await SendAndReceiveAsync(socket, "{\"id\":\"2\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"b\"}}");

            using (var doc = JsonDocument.Parse(first))
            {
                Assert.Equal("echo:a", doc.RootElement.GetProperty("Body").GetString());
            }
            using (var doc = JsonDocument.Parse(second))
            {
                Assert.Equal("echo:b", doc.RootElement.GetProperty("Body").GetString());
            }

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();
        }

        [Fact]
        public async Task FragmentedRequest_IsReassembledAndServed()
        {
            using var host = await StartHostAsync(CreateOption());
            var socket = await ConnectAsync(host);
            byte[] request = Encoding.UTF8.GetBytes("{\"id\":\"9\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"frag\"}}");
            using var cts = new CancellationTokenSource(TestTimeout);

            int half = request.Length / 2;
            await socket.SendAsync(new ArraySegment<byte>(request, 0, half), WebSocketMessageType.Text, endOfMessage: false, cts.Token);
            await socket.SendAsync(new ArraySegment<byte>(request, half, request.Length - half), WebSocketMessageType.Text, endOfMessage: true, cts.Token);
            string response = await ReceiveFullMessageAsync(socket, cts.Token);

            using var doc = JsonDocument.Parse(response);
            Assert.Equal(0, doc.RootElement.GetProperty("Status").GetInt32());
            Assert.Equal("echo:frag", doc.RootElement.GetProperty("Body").GetString());

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();
        }

        [Fact]
        public async Task BinaryRequest_IsServedWithBinaryResponse()
        {
            using var host = await StartHostAsync(CreateOption());
            var socket = await ConnectAsync(host);
            byte[] request = Encoding.UTF8.GetBytes("{\"id\":\"3\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"bin\"}}");
            using var cts = new CancellationTokenSource(TestTimeout);

            await socket.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Binary, true, cts.Token);
            string response = await ReceiveFullMessageAsync(socket, cts.Token);

            using var doc = JsonDocument.Parse(response);
            Assert.Equal("echo:bin", doc.RootElement.GetProperty("Body").GetString());

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();
        }

        [Fact]
        public async Task RequestOverReceiveDataLimit_IsDropped_ConnectionStaysUsable()
        {
            // Limit sits between the small recovery request (~37 B) and the oversized one (~245 B):
            // the oversized message is rejected (its remaining frames drained), the small one is served.
            using var host = await StartHostAsync(CreateOption(o => o.MaxRequestReceiveDataLimit = 64));
            var socket = await ConnectAsync(host);
            using var cts = new CancellationTokenSource(TestTimeout);

            // Oversized fragmented message: it trips the size limit and is discarded without a response;
            // the server drains its remaining frames so the connection is not left mid-message.
            byte[] big = Encoding.UTF8.GetBytes("{\"id\":\"1\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"" + new string('x', 200) + "\"}}");
            await socket.SendAsync(new ArraySegment<byte>(big, 0, 100), WebSocketMessageType.Text, endOfMessage: false, cts.Token);
            await socket.SendAsync(new ArraySegment<byte>(big, 100, big.Length - 100), WebSocketMessageType.Text, endOfMessage: true, cts.Token);

            // The connection must remain usable for the next (small, single-frame) request
            string response = await SendAndReceiveAsync(socket, "{\"id\":\"2\",\"target\":\"wstest.noparams\"}");

            using var doc = JsonDocument.Parse(response);
            Assert.Equal("2", doc.RootElement.GetProperty("Id").GetString());
            Assert.Equal("noparams", doc.RootElement.GetProperty("Body").GetString());

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();
        }

        [Fact]
        public async Task GrosslyOversizedMessage_IsClosedWith1009_NotDrainedForever()
        {
            // A message far larger than both the limit and the drain cap must NOT be drained in full
            // (wasted bandwidth / potential hang). The server sends 1009 (Message Too Big) and closes.
            using var host = await StartHostAsync(CreateOption(o => o.MaxRequestReceiveDataLimit = 100));
            var socket = await ConnectAsync(host);
            using var cts = new CancellationTokenSource(TestTimeout);

            // ~300 KB, well past the 64 KB drain cap; sent as many small fragments.
            byte[] payload = Encoding.UTF8.GetBytes(new string('x', 300 * 1024));
            for (int off = 0; off < payload.Length; off += 8192)
            {
                int len = Math.Min(8192, payload.Length - off);
                bool last = off + len >= payload.Length;
                try { await socket.SendAsync(new ArraySegment<byte>(payload, off, len), WebSocketMessageType.Text, endOfMessage: last, cts.Token); }
                catch { break; } // server may abort mid-send once the drain cap is hit
            }

            // The server must have closed the connection (rather than hanging or staying open).
            var buf = new byte[1024];
            var completed = await Task.WhenAny(socket.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token), Task.Delay(TestTimeout));
            Assert.NotEqual(WebSocketState.Open, socket.State);

            try { await host.StopAsync(); } catch { }
        }

        [Fact]
        public async Task PerEndpointLimit_OverridesGlobal_ForBufferedEndpoint()
        {
            // Per-endpoint cap: global cap is tight (2 KiB), but wstest.echobig is [WebSocket(MaxBytes = 8 MiB)].
            using var host = await StartHostAsync(CreateOption(o => o.MaxRequestReceiveDataLimit = 2048));
            var socket = await ConnectAsync(host);

            // ~40 KiB to the overridden endpoint → accepted (under its 8 MiB cap), even though > global 2 KiB.
            string big = "{\"id\":\"1\",\"target\":\"wstest.echobig\",\"body\":{\"text\":\"" + new string('x', 40 * 1024) + "\"}}";
            string resp = await SendAndReceiveAsync(socket, big);
            using (var doc = JsonDocument.Parse(resp))
            {
                Assert.Equal("1", doc.RootElement.GetProperty("Id").GetString());
                Assert.Equal("echobig:40960", doc.RootElement.GetProperty("Body").GetString());
            }

            // Same size to a NON-overridden endpoint (wstest.echo) → rejected by the global 2 KiB cap (no response),
            // and the connection stays usable for a subsequent small request.
            using (var cts = new CancellationTokenSource(TestTimeout))
            {
                string big2 = "{\"id\":\"2\",\"target\":\"wstest.echo\",\"body\":{\"text\":\"" + new string('y', 40 * 1024) + "\"}}";
                await socket.SendAsync(Encoding.UTF8.GetBytes(big2), WebSocketMessageType.Text, true, cts.Token);
            }
            string small = await SendAndReceiveAsync(socket, "{\"id\":\"3\",\"target\":\"wstest.noparams\"}");
            using (var doc = JsonDocument.Parse(small))
            {
                Assert.Equal("3", doc.RootElement.GetProperty("Id").GetString());
            }

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();
        }

        // Build a streaming-upload binary message:
        // [4-byte magic "\0WSU"][4-byte BE header length][UTF8 JSON header][payload].
        private static byte[] BuildUploadMessage(string headerJson, byte[] payload)
        {
            byte[] magic = { 0x00, (byte)'W', (byte)'S', (byte)'U' };
            byte[] header = Encoding.UTF8.GetBytes(headerJson);
            var msg = new byte[magic.Length + 4 + header.Length + payload.Length];
            Array.Copy(magic, 0, msg, 0, magic.Length);
            int p = magic.Length;
            msg[p + 0] = (byte)(header.Length >> 24);
            msg[p + 1] = (byte)(header.Length >> 16);
            msg[p + 2] = (byte)(header.Length >> 8);
            msg[p + 3] = (byte)header.Length;
            Array.Copy(header, 0, msg, p + 4, header.Length);
            Array.Copy(payload, 0, msg, p + 4 + header.Length, payload.Length);
            return msg;
        }

        [Fact]
        public async Task StreamingEndpoint_ReceivesUpload_BypassingBufferedGlobalLimit()
        {
            // Global buffered cap is tight (4 KiB); a 256 KiB streaming upload must still succeed because the
            // streaming path never buffers the whole payload.
            using var host = await StartHostAsync(CreateOption(o => o.MaxRequestReceiveDataLimit = 4096));
            var socket = await ConnectAsync(host);
            using var cts = new CancellationTokenSource(TestTimeout);

            byte[] payload = new byte[256 * 1024];
            for (int i = 0; i < payload.Length; i++) { payload[i] = (byte)(i & 0xFF); }
            byte[] msg = BuildUploadMessage("{\"id\":\"u1\",\"target\":\"wstest.upload\"}", payload);

            for (int off = 0; off < msg.Length; off += 8192)
            {
                int len = Math.Min(8192, msg.Length - off);
                await socket.SendAsync(new ArraySegment<byte>(msg, off, len), WebSocketMessageType.Binary, off + len >= msg.Length, cts.Token);
            }

            string resp = await ReceiveFullMessageAsync(socket, cts.Token);
            using (var doc = JsonDocument.Parse(resp))
            {
                Assert.Equal("u1", doc.RootElement.GetProperty("Id").GetString());
                Assert.Equal("upload:262144", doc.RootElement.GetProperty("Body").GetString());
            }

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await host.StopAsync();
        }

        [Fact]
        public async Task StreamingEndpoint_OverMaxBytes_IsClosed()
        {
            // wstest.upload has MaxBytes = 1 MiB; a 2 MiB upload must be rejected (connection closed), not accepted.
            using var host = await StartHostAsync(CreateOption());
            var socket = await ConnectAsync(host);
            using var cts = new CancellationTokenSource(TestTimeout);

            byte[] payload = new byte[2 * 1024 * 1024];
            byte[] msg = BuildUploadMessage("{\"id\":\"big\",\"target\":\"wstest.upload\"}", payload);
            for (int off = 0; off < msg.Length; off += 8192)
            {
                int len = Math.Min(8192, msg.Length - off);
                try { await socket.SendAsync(new ArraySegment<byte>(msg, off, len), WebSocketMessageType.Binary, off + len >= msg.Length, cts.Token); }
                catch { break; } // server may abort mid-send once the cap is hit
            }

            var buf = new byte[1024];
            await Task.WhenAny(socket.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token), Task.Delay(TestTimeout));
            Assert.NotEqual(WebSocketState.Open, socket.State);

            try { await host.StopAsync(); } catch { }
        }

        [Fact]
        public async Task DisconnectedEvent_FiresWhenClientCloses()
        {
            var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var host = await StartHostAsync(CreateOption(o =>
                o.DisconnectedEvent += (context, opt, channel, logger) =>
                {
                    disconnected.TrySetResult(channel);
                    return Task.CompletedTask;
                }));
            var socket = await ConnectAsync(host);

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

            var completed = await Task.WhenAny(disconnected.Task, Task.Delay(TestTimeout));
            Assert.Same(disconnected.Task, completed);
            Assert.Equal("/ws", await disconnected.Task);
            await host.StopAsync();
        }
    }
}
