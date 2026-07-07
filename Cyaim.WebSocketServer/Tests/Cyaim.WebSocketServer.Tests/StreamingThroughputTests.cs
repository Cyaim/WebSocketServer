using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.MessagePack;
using Cyaim.WebSocketServer.Middlewares;
using MessagePack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Abstractions;

namespace Cyaim.WebSocketServer.Tests
{
    // Client<->server throughput of the streaming-upload path, for both channels (MVC/JSON and MessagePack).
    // Uploads a sizeable payload through the streaming protocol, asserts the server received every byte, and
    // reports MiB/s via test output. (Transport is the in-memory TestServer, so this measures the client-encode
    // + framing + server pipe/dispatch cost, isolated from network.)
    // 流式上传的客户端↔服务端吞吐测速（MVC/JSON 与 MessagePack 双通道）：上传大负载，校验字节数一致，并输出 MiB/s。
    public class StreamingThroughputTests
    {
        private readonly ITestOutputHelper _output;
        public StreamingThroughputTests(ITestOutputHelper output) => _output = output;

        private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);
        private const long TotalBytes = 32L * 1024 * 1024; // 32 MiB per upload

        private static async Task<IHost> StartHostAsync(string path, WebSocketRouteOption.WebSocketChannelHandler handler)
        {
            var option = new WebSocketRouteOption
            {
                WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler> { [path] = handler },
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
                        app.Use(async (context, next) => { context.Connection.Id ??= Guid.NewGuid().ToString("N"); await next(); });
                        app.UseWebSockets();
                        app.UseWebSocketServer();
                    }))
                .Build();
            await host.StartAsync();
            return host;
        }

        private static byte[] BuildHeaderFrame(string id, string target)
        {
            byte[] magic = { 0x00, (byte)'W', (byte)'S', (byte)'U' };
            byte[] header = Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\",\"target\":\"{target}\"}}");
            var frame = new byte[8 + header.Length];
            Array.Copy(magic, 0, frame, 0, 4);
            frame[4] = (byte)(header.Length >> 24);
            frame[5] = (byte)(header.Length >> 16);
            frame[6] = (byte)(header.Length >> 8);
            frame[7] = (byte)header.Length;
            Array.Copy(header, 0, frame, 8, header.Length);
            return frame;
        }

        // Uploads TotalBytes to `target` in frameSize chunks and returns (bytesReportedByServer, elapsed).
        private static async Task<(long reported, TimeSpan elapsed)> UploadAsync(
            WebSocket socket, string target, int frameSize, bool messagePackResponse)
        {
            using var cts = new CancellationTokenSource(TestTimeout);
            var headerFrame = BuildHeaderFrame("spd", target);
            var chunk = new byte[frameSize];

            var sw = Stopwatch.StartNew();
            // frame 1: magic + header (no payload); server detects the stream here
            await socket.SendAsync(new ArraySegment<byte>(headerFrame), WebSocketMessageType.Binary, false, cts.Token);
            long sent = 0;
            while (sent < TotalBytes)
            {
                int n = (int)Math.Min(frameSize, TotalBytes - sent);
                bool last = sent + n >= TotalBytes;
                await socket.SendAsync(new ArraySegment<byte>(chunk, 0, n), WebSocketMessageType.Binary, last, cts.Token);
                sent += n;
            }

            var buf = new byte[64 * 1024];
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await socket.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            sw.Stop();

            string body;
            if (messagePackResponse)
            {
                body = MessagePackSerializer.Deserialize<MessagePackResponseScheme>(ms.ToArray()).Body?.ToString();
            }
            else
            {
                using var doc = JsonDocument.Parse(ms.ToArray());
                body = doc.RootElement.GetProperty("Body").GetString();
            }
            long reported = long.Parse(body!.Substring("upload:".Length));
            return (reported, sw.Elapsed);
        }

        private static async Task<WebSocket> ConnectAsync(IHost host, string path)
        {
            var server = host.GetTestServer();
            using var cts = new CancellationTokenSource(TestTimeout);
            return await server.CreateWebSocketClient().ConnectAsync(new Uri(server.BaseAddress, path), cts.Token);
        }

        private void Report(string label, long total, TimeSpan elapsed)
        {
            double mib = total / (1024.0 * 1024.0);
            double mibps = mib / Math.Max(elapsed.TotalSeconds, 1e-6);
            _output.WriteLine($"{label}: {mib:F0} MiB in {elapsed.TotalMilliseconds:F0} ms => {mibps:F0} MiB/s");
        }

        [Fact]
        public async Task Mvc_StreamingUpload_Throughput()
        {
            using var host = await StartHostAsync("/ws", new MvcChannelHandler().ConnectionEntry);
            var socket = await ConnectAsync(host, "/ws");

            var (reported, elapsed) = await UploadAsync(socket, "wstest.uploadfast", 64 * 1024, messagePackResponse: false);

            Assert.Equal(TotalBytes, reported); // server received every byte
            Report("[MVC/JSON  64KiB frames]", TotalBytes, elapsed);

            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
            try { await host.StopAsync(); } catch { }
        }

        [Fact]
        public async Task MessagePack_StreamingUpload_Throughput()
        {
            using var host = await StartHostAsync("/mp", new MessagePackChannelHandler().ConnectionEntry);
            var socket = await ConnectAsync(host, "/mp");

            var (reported, elapsed) = await UploadAsync(socket, "wstest.uploadfast", 64 * 1024, messagePackResponse: true);

            Assert.Equal(TotalBytes, reported);
            Report("[MessagePack 64KiB frames]", TotalBytes, elapsed);

            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
            try { await host.StopAsync(); } catch { }
        }

        [Theory]
        [InlineData(16 * 1024)]
        [InlineData(64 * 1024)]
        [InlineData(256 * 1024)]
        public async Task Mvc_StreamingUpload_Throughput_ByFrameSize(int frameSize)
        {
            using var host = await StartHostAsync("/ws", new MvcChannelHandler().ConnectionEntry);
            var socket = await ConnectAsync(host, "/ws");

            var (reported, elapsed) = await UploadAsync(socket, "wstest.uploadfast", frameSize, messagePackResponse: false);

            Assert.Equal(TotalBytes, reported);
            Report($"[MVC/JSON  {frameSize / 1024}KiB frames]", TotalBytes, elapsed);

            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
            try { await host.StopAsync(); } catch { }
        }
    }
}
