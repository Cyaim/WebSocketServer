using Cyaim.WebSocketServer.MessagePack;
using MessagePack;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Cyaim.WebSocketServer.Benchmarks
{
    /// <summary>
    /// Drives real ClientWebSocket clients against the benchmark server over loopback and reports throughput
    /// and heap behaviour. NOT a unit test — a manually-run performance benchmark.
    /// </summary>
    internal static class BenchmarkRunner
    {
        private static readonly byte[] Magic = { 0x00, (byte)'W', (byte)'S', (byte)'U' };

        public static async Task RunAll(string wsBase)
        {
            const long size = 32L * 1024 * 1024; // 32 MiB per upload

            Console.WriteLine("=== Single-client streaming upload throughput (32 MiB) ===");
            foreach (int frame in new[] { 16 * 1024, 64 * 1024, 256 * 1024 })
            {
                var (rep, el) = await UploadOnceAsync(wsBase, "/ws", "bench.upload", size, frame);
                PrintThroughput($"MVC/JSON     {frame / 1024,4} KiB frames", size, el, rep == size);
            }
            {
                var (rep, el) = await UploadOnceAsync(wsBase, "/mp", "bench.upload", size, 64 * 1024, mpResponse: true);
                PrintThroughput("MessagePack    64 KiB frames", size, el, rep == size);
            }

            Console.WriteLine();
            Console.WriteLine("=== Concurrent N-client streaming upload — aggregate throughput + managed heap ===");
            Console.WriteLine("    (streaming => peak heap Δ stays bounded, does NOT scale with total bytes uploaded)");
            const long perClient = 32L * 1024 * 1024; // 32 MiB each
            foreach (int n in new[] { 1, 4, 8, 16 })
            {
                await RunConcurrentAsync(wsBase, n, perClient, 64 * 1024);
            }
        }

        // Uploads totalBytes to `target` via the streaming protocol and returns (bytesReportedByServer, elapsed).
        private static async Task<(long reported, TimeSpan elapsed)> UploadOnceAsync(
            string wsBase, string channel, string target, long totalBytes, int frameSize, bool mpResponse = false)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsBase + channel), CancellationToken.None);

            byte[] header = Encoding.UTF8.GetBytes($"{{\"id\":\"b\",\"target\":\"{target}\"}}");
            byte[] frame1 = new byte[8 + header.Length];
            Array.Copy(Magic, 0, frame1, 0, 4);
            frame1[4] = (byte)(header.Length >> 24);
            frame1[5] = (byte)(header.Length >> 16);
            frame1[6] = (byte)(header.Length >> 8);
            frame1[7] = (byte)header.Length;
            Array.Copy(header, 0, frame1, 8, header.Length);
            var chunk = new byte[frameSize];

            var sw = Stopwatch.StartNew();
            await ws.SendAsync(new ArraySegment<byte>(frame1), WebSocketMessageType.Binary, false, CancellationToken.None);
            long sent = 0;
            while (sent < totalBytes)
            {
                int n = (int)Math.Min(frameSize, totalBytes - sent);
                bool last = sent + n >= totalBytes;
                await ws.SendAsync(new ArraySegment<byte>(chunk, 0, n), WebSocketMessageType.Binary, last, CancellationToken.None);
                sent += n;
            }

            var buf = new byte[64 * 1024];
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            sw.Stop();

            string body = mpResponse
                ? MessagePackSerializer.Deserialize<MessagePackResponseScheme>(ms.ToArray()).Body?.ToString()
                : JsonDocument.Parse(ms.ToArray()).RootElement.GetProperty("Body").GetString();
            long reported = long.Parse(body!.Substring("upload:".Length));

            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
            return (reported, sw.Elapsed);
        }

        private static async Task RunConcurrentAsync(string wsBase, int n, long perClient, int frameSize)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long baseline = GC.GetTotalMemory(true);
            long peak = baseline;

            using var stop = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    long m = GC.GetTotalMemory(false);
                    if (m > peak) { peak = m; }
                    await Task.Delay(10);
                }
            });

            var sw = Stopwatch.StartNew();
            var tasks = new Task<(long reported, TimeSpan elapsed)>[n];
            for (int i = 0; i < n; i++)
            {
                tasks[i] = UploadOnceAsync(wsBase, "/ws", "bench.upload", perClient, frameSize);
            }
            var results = await Task.WhenAll(tasks);
            sw.Stop();
            stop.Cancel();
            try { await sampler; } catch { }

            bool ok = Array.TrueForAll(results, x => x.reported == perClient);
            long totalBytes = (long)n * perClient;
            double aggMiBps = totalBytes / (1024.0 * 1024.0) / sw.Elapsed.TotalSeconds;
            double peakDelta = (peak - baseline) / (1024.0 * 1024.0);
            Console.WriteLine(
                $"  N={n,2}  uploaded={totalBytes / 1024 / 1024,5} MiB  {sw.Elapsed.TotalMilliseconds,6:F0} ms" +
                $"  aggregate={aggMiBps,6:F0} MiB/s  heap base={baseline / 1024 / 1024,4} peak={peak / 1024 / 1024,4}" +
                $" Δ={peakDelta,5:F0} MiB  {(ok ? "OK" : "MISMATCH")}");
        }

        private static void PrintThroughput(string label, long total, TimeSpan elapsed, bool ok)
        {
            double mib = total / (1024.0 * 1024.0);
            double mibps = mib / Math.Max(elapsed.TotalSeconds, 1e-6);
            Console.WriteLine($"  {label}: {mib,4:F0} MiB in {elapsed.TotalMilliseconds,6:F0} ms => {mibps,6:F0} MiB/s  {(ok ? "OK" : "MISMATCH")}");
        }
    }
}
