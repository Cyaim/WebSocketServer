using Cyaim.WebSocketServer.Infrastructure.Attributes;

namespace Cyaim.WebSocketServer.Benchmarks.Controllers
{
    /// <summary>
    /// Endpoints exercised by the throughput benchmark. Discovered by the normal [WebSocket] scan.
    /// </summary>
    public class BenchController
    {
        /// <summary>
        /// Streaming upload with no size cap: reads and discards the payload (constant memory), returns the count.
        /// Target: <c>bench.upload</c>.
        /// </summary>
        [WebSocket(Stream = true)]
        public async Task<string> Upload(Stream body, CancellationToken ct)
        {
            long total = 0;
            var buf = new byte[64 * 1024];
            int n;
            while ((n = await body.ReadAsync(buf, 0, buf.Length, ct)) > 0)
            {
                total += n;
            }
            return "upload:" + total;
        }

        /// <summary>Plain request/response, for a baseline. Target: <c>bench.echo</c>.</summary>
        [WebSocket]
        public string Echo(string text) => text;
    }
}
