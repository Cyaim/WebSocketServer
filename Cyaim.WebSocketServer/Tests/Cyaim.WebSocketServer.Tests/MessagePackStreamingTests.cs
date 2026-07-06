using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.MessagePack;
using Cyaim.WebSocketServer.Middlewares;
using MessagePack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Tests
{
    // Streaming upload on the MessagePack channel: the JSON-framed upload arrives on /mp and the response is
    // MessagePack. Reuses the shared streaming receiver (already covered on the MVC channel); this verifies the
    // MessagePack-specific wrapper (magic detection + MessagePack response encoding).
    public class MessagePackStreamingTests
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

        private static async Task<IHost> StartMpHostAsync(Action<WebSocketRouteOption> configure = null)
        {
            var option = new WebSocketRouteOption
            {
                WebSocketChannels = new System.Collections.Generic.Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>
                {
                    ["/mp"] = new MessagePackChannelHandler().ConnectionEntry
                },
                WatchAssemblyContext = MvcTestSupport.BuildContext(typeof(MvcTestSupport.WsTestController))
            };
            configure?.Invoke(option);

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
        public async Task MessagePackChannel_StreamingUpload_ReturnsMessagePackResponse()
        {
            // Tight global buffered cap (4 KiB); a 256 KiB streaming upload must still succeed on /mp.
            using var host = await StartMpHostAsync(o => o.MaxRequestReceiveDataLimit = 4096);
            var server = host.GetTestServer();
            var client = server.CreateWebSocketClient();
            using var cts = new CancellationTokenSource(TestTimeout);
            var socket = await client.ConnectAsync(new Uri(server.BaseAddress, "/mp"), cts.Token);

            byte[] payload = new byte[256 * 1024];
            for (int i = 0; i < payload.Length; i++) { payload[i] = (byte)(i & 0xFF); }
            byte[] msg = BuildUploadMessage("{\"id\":\"mpu1\",\"target\":\"wstest.upload\"}", payload);
            // Send the whole upload as a single WebSocket message (the server still reads it in buffer-sized chunks).
            await socket.SendAsync(new ArraySegment<byte>(msg), WebSocketMessageType.Binary, true, cts.Token);

            // receive the (binary MessagePack) response
            var buf = new byte[64 * 1024];
            using var ms = new System.IO.MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await socket.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);

            var resp = MessagePackSerializer.Deserialize<MessagePackResponseScheme>(ms.ToArray());
            Assert.Equal(0, resp.Status);
            Assert.Equal("mpu1", resp.Id);
            Assert.Equal("upload:262144", resp.Body?.ToString());

            // Close is best-effort: the server may have already torn down its side after fulfilling the request.
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
            try { await host.StopAsync(); } catch { }
        }
    }
}
