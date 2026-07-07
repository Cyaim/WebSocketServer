# Cyaim.WebSocketServer.Benchmarks

Performance benchmark for the **streaming upload** path (`[WebSocket(Stream = true)]`). This is **not a unit
test** — it is a manually-run console harness. `dotnet test` does not execute it.

性能基准（非单元测试）：测量流式上传端点的吞吐与并发下的内存平稳度。手动运行，`dotnet test` 不会执行它。

## Run / 运行

```bash
dotnet run -c Release --project Cyaim.WebSocketServer/Tests/Cyaim.WebSocketServer.Benchmarks
```

It starts a real Kestrel server on `127.0.0.1:5199` (buffered request cap set to a tight **64 KiB**, so any
successful large upload proves the streaming path bypasses buffering), then drives real `ClientWebSocket`
clients over loopback.

## What it measures / 测什么

1. **Single-client throughput** — a 32 MiB streaming upload at 16 / 64 / 256 KiB frame sizes, on both the
   MVC (JSON) and MessagePack channels. Throughput rises with frame size (fewer awaits per byte).
2. **Concurrent N-client aggregate throughput + managed heap** — N clients (1/4/8/16) each stream 32 MiB
   at once. Reports aggregate MiB/s and the peak managed-heap delta during the run. Because the payload is
   never buffered, the heap delta stays bounded by *concurrency* (Pipe windows + per-connection buffers),
   **not** by total bytes uploaded.

## Sample results / 参考结果

Loopback Kestrel, one dev machine (absolute numbers vary by hardware; the *shape* is the point):

```
=== Single-client streaming upload throughput (32 MiB) ===
  MVC/JSON       16 KiB frames:   32 MiB in  ~350 ms =>  ~91 MiB/s  OK
  MVC/JSON       64 KiB frames:   32 MiB in  ~330 ms =>  ~98 MiB/s  OK
  MVC/JSON      256 KiB frames:   32 MiB in  ~290 ms => ~112 MiB/s  OK
  MessagePack    64 KiB frames:   32 MiB in  ~330 ms =>  ~96 MiB/s  OK

=== Concurrent N-client streaming upload — aggregate throughput + managed heap ===
  N= 1  uploaded=   32 MiB   ~130 ms  aggregate= ~244 MiB/s  heap Δ=  2 MiB  OK
  N= 4  uploaded=  128 MiB   ~225 ms  aggregate= ~570 MiB/s  heap Δ= 10 MiB  OK
  N= 8  uploaded=  256 MiB   ~350 ms  aggregate= ~730 MiB/s  heap Δ= 15 MiB  OK
  N=16  uploaded=  512 MiB  ~1080 ms  aggregate= ~474 MiB/s  heap Δ= 18 MiB  OK
```

Takeaway: **N=16 uploads 512 MiB total but the heap grows only ~18 MiB** — a buffered path would spike by
~512 MiB. Streaming memory scales with concurrency, not payload size.
