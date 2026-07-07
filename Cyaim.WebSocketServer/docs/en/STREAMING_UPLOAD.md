# Streaming Upload & Receive Memory Control

For the real-world case where "among many endpoints, a few need to transfer files", Cyaim.WebSocketServer
offers two layers:

1. **Receive memory control** (buffered endpoints) — cap received bytes per-connection / per-endpoint /
   globally. Safe by default, guards against OOM/DoS.
2. **Streaming upload endpoints** (`[WebSocket(Stream = true)]`) — a large payload is **not** buffered in
   full; it is fed to the endpoint as a `Stream`, so **server memory stays constant regardless of file
   size** — ideal for file upload.

> TL;DR: ordinary endpoints default to a single message ≤ **4 MiB** (configurable). To transfer large files,
> mark that endpoint `Stream = true` and upload with the client's `uploadStream` helper.

---

## 1. Receive memory control (buffered endpoints)

Ordinary (non-streaming) endpoints buffer the whole message before dispatch. Three caps prevent a single
huge message — or many concurrent large ones — from exhausting memory:

| Option | Scope | Default | Meaning |
|---|---|---|---|
| `MaxRequestReceiveDataLimit` | global / per message | **4 MiB** | Max bytes buffered for one message. `null` = unlimited (you own the OOM risk). |
| `[WebSocket(MaxBytes = N)]` | single endpoint | 0 (use global) | Per-endpoint override of the single-message cap (buffered endpoints). |
| `MaxTotalReceiveBufferBytes` | global (all connections) | `null` (disabled) | Process-wide budget for in-flight multi-frame receive buffers; defence-in-depth against coordinated bursts. |

```csharp
builder.Services.AddWebSocketServer(x =>
{
    x.MaxRequestReceiveDataLimit = 4L * 1024 * 1024;      // default 4 MiB; null = unlimited
    x.MaxTotalReceiveBufferBytes = 512L * 1024 * 1024;    // optional global in-flight budget
});
```

**Per-endpoint override:**

```csharp
[WebSocket("chat.send")]                                // default 4 MiB
public MvcResponseScheme Send(ChatMsg m) => ...;

[WebSocket("bulk.import", MaxBytes = 32 * 1024 * 1024)] // this endpoint allows up to 32 MiB (still buffered)
public MvcResponseScheme Import(BulkPayload p) => ...;
```

**Over-limit behaviour**: an over-limit message is *bounded-drained* (its remaining frames are read but not
stored, to preserve framing and keep the connection usable); if the message is grossly oversized/unbounded,
the server sends `1009 (Message Too Big)` and aborts, so a slow unbounded message can't hang the connection.

> ⚠️ **Behaviour change (2.0)**: `MaxRequestReceiveDataLimit` now defaults to **4 MiB** (was unlimited). If
> your app sends single messages **larger than 4 MiB** through ordinary endpoints, raise this value (or set
> it to `null`), or switch to a streaming endpoint below.

---

## 2. Streaming upload endpoints

Mark an endpoint `Stream = true` and it stops buffering — incoming bytes are fed to you as a
`System.IO.Stream` as they arrive:

```csharp
[WebSocket("file.upload", Stream = true, MaxBytes = 2L * 1024 * 1024 * 1024)] // 2 GiB streaming cap
public async Task<object> Upload(UploadMeta meta, Stream body, CancellationToken ct)
{
    await using var fs = File.Create(Path.Combine(dir, meta.FileName));
    await body.CopyToAsync(fs, ct);          // write to disk as it streams; never buffered in full
    return new { bytes = fs.Length };
}
```

**Parameters are bound by type** (order-independent):

| Parameter type | Bound to |
|---|---|
| `System.IO.Stream` (required) | the upload payload body (fed frame-by-frame) |
| `CancellationToken` (optional) | a token tied to the connection lifetime |
| any other class/interface (optional) | deserialized from the upload header's `meta` |

Startup validation: `Stream = true` without a `Stream` parameter → **throws at startup** (fail-fast).

**Properties:**

- **Constant memory** — the payload only lives in the Pipe's sliding window, independent of file size.
- **End-to-end backpressure** — slow disk write → Pipe blocks → frame reads pause → TCP backpressure to the client.
- **`MaxBytes` cap** — for a streaming endpoint, `MaxBytes` bounds the total streamed bytes (a running
  counter, not a buffer); 0 = unlimited. Over-cap sends `1009` and aborts.
- **Auth up front** — you can authenticate/quota-check after the header is parsed, before feeding data.

---

## 3. Wire format (protocol)

A streaming upload is one **binary** WebSocket message, framed as:

```
[4-byte magic "\0WSU"] [4-byte big-endian header length N] [N-byte UTF-8 JSON header] [raw payload bytes ...]
```

- Header JSON: `{"id":"<request id>","target":"file.upload","meta":{...}}`.
- The magic `\0WSU` (`0x00 'W' 'S' 'U'`) distinguishes a streaming upload from ordinary messages on the same
  connection — neither JSON nor a MessagePack request starts with `0x00`, so it cannot collide.
- The response is correlated by `id`; its encoding follows the channel (`/ws` = JSON, `/mp` = MessagePack).

Both the MVC (`/ws`) and MessagePack (`/mp`) channels support it. The client SDKs wrap this protocol — you
never hand-build frames.

---

## 4. Client `uploadStream`

All six clients ship an upload helper; the response is correlated like an ordinary request.

**JavaScript / TypeScript** (true frame-level streaming, constant client memory)
```typescript
const res = await client.uploadStream("file.upload", fs.createReadStream("big.bin"), { fileName: "big.bin" });
// source: Uint8Array / Node Readable / (async) iterable of byte chunks
```

**Python** (async fragmented send, constant client memory)
```python
res = await client.upload_stream("file.upload", open("big.bin", "rb"), meta={"fileName": "big.bin"})
# source: bytes / file-like object (.read) / (async) iterable
```

**C#** (fragmented send, constant client memory)
```csharp
await using var fs = File.OpenRead("big.bin");
var res = await client.UploadStreamAsync<UploadResult>("file.upload", fs, new { fileName = "big.bin" });
```

**Java** (`sendFragmentedFrame`, constant client memory)
```java
try (var in = new FileInputStream("big.bin")) {
    var res = client.<UploadResult>uploadStream("file.upload", in, Map.of("fileName","big.bin"), 65536, UploadResult.class).get();
}
```

**Dart / Rust** (their WebSocket libraries send one complete message, so the client buffers the payload;
the **server still streams it** with constant memory)
```dart
final res = await client.uploadStream<Map>('file.upload', bytes, meta: {'fileName': 'big.bin'});
```
```rust
let res: UploadResult = client.upload_stream("file.upload", payload_bytes, Some(meta_json)).await?;
```

> Note: JS / Python / C# / Java use real frame-level fragmentation (client memory doesn't grow with file
> size). Dart / Rust libraries only support "one complete message at a time", so the client buffers the
> payload — but the server receive path is always streaming.

---

## 5. Performance benchmark

A standalone benchmark project `Tests/Cyaim.WebSocketServer.Benchmarks` (**not a unit test**, run manually):

```bash
dotnet run -c Release --project Cyaim.WebSocketServer/Tests/Cyaim.WebSocketServer.Benchmarks
```

Sample results on one dev machine over loopback Kestrel (absolute numbers vary; the shape is the point):

| Scenario | Result |
|---|---|
| Single client · MVC/JSON (16/64/256 KiB frames) | 91 / 98 / 112 MiB/s |
| Single client · MessagePack (64 KiB frames) | ~96 MiB/s |
| Concurrent N=16 · 32 MiB each (512 MiB total) | aggregate ~474 MiB/s, **peak managed-heap Δ only +18 MiB** |

**Takeaway**: 16 clients upload 512 MiB total, yet the server's managed heap grows only ~18 MiB (a buffered
path would spike by ~512 MiB) — streaming memory is bounded by *concurrency*, not payload size.

---

## Related docs

- [Core](./CORE.md) · [Quick Start](./QUICK_START.md) · [Clients](./CLIENTS.md)
