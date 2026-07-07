# 流式上传与接收内存控制

面向"一堆 endpoint 里总有个别要传文件"的真实场景，Cyaim.WebSocketServer 提供两层能力：

1. **接收内存控制**（缓冲式端点）——按连接 / 按端点 / 全局三级封顶接收字节，默认即安全，防 OOM/DoS。
2. **流式上传端点**（`[WebSocket(Stream = true)]`）——大负载不整体缓冲，逐帧喂给端点的 `Stream`，**服务端内存恒定、与文件大小无关**，适合文件上传。

> TL;DR：普通端点默认单条消息 ≤ **4 MiB**（可调）；需要传大文件时，把那个端点标成 `Stream = true`，用客户端的 `uploadStream` 上传即可。

---

## 一、接收内存控制（缓冲式端点）

普通（非流式）端点会把整条消息缓冲进内存再分发。为防止单条超大消息或大量并发大消息把进程打爆，提供三级上限：

| 选项 | 作用域 | 默认 | 说明 |
|---|---|---|---|
| `MaxRequestReceiveDataLimit` | 全局 / 每条消息 | **4 MiB** | 单条消息最多缓冲的字节。`null` = 不限（自担 OOM 风险）。 |
| `[WebSocket(MaxBytes = N)]` | 单个端点 | 0（用全局） | 覆盖该端点的单条消息上限（缓冲式端点）。 |
| `MaxTotalReceiveBufferBytes` | 全局（所有连接累计） | `null`（禁用） | 所有连接"在途多帧接收缓冲"字节总预算，纵深防御协同突发。 |

```csharp
builder.Services.AddWebSocketServer(x =>
{
    x.MaxRequestReceiveDataLimit = 4L * 1024 * 1024;      // 默认 4 MiB；设 null 表示不限
    x.MaxTotalReceiveBufferBytes = 512L * 1024 * 1024;    // 可选：全局在途接收缓冲总预算
});
```

**端点级覆盖**（个别端点要收较大 JSON/消息）：

```csharp
[WebSocket("chat.send")]                              // 默认 4 MiB
public MvcResponseScheme Send(ChatMsg m) => ...;

[WebSocket("bulk.import", MaxBytes = 32 * 1024 * 1024)] // 这个端点放宽到 32 MiB（仍整条缓冲）
public MvcResponseScheme Import(BulkPayload p) => ...;
```

**超限行为**：超过生效上限的消息会被**有界排空**丢弃——读掉剩余帧（不写内存）以保持帧边界、连接继续可用；若消息过大/无界（无休止），则发送 `1009 (Message Too Big)` 并中止连接，避免被慢速无界消息读挂。

> ⚠️ **行为变更提示（2.0）**：`MaxRequestReceiveDataLimit` 默认从"不限"改为 **4 MiB**。若你的现有业务通过普通端点收发 **大于 4 MiB** 的单条消息，请显式调大该值（或设 `null`），或改用下面的流式端点。

---

## 二、流式上传端点

把端点标记为 `Stream = true`，它就不再整条缓冲，而是把到达的字节作为 `System.IO.Stream` **边收边喂**给你：

```csharp
[WebSocket("file.upload", Stream = true, MaxBytes = 2L * 1024 * 1024 * 1024)] // 2 GiB 流式上限
public async Task<object> Upload(UploadMeta meta, Stream body, CancellationToken ct)
{
    await using var fs = File.Create(Path.Combine(dir, meta.FileName));
    await body.CopyToAsync(fs, ct);          // 边收边写盘，永不整体入内存
    return new { bytes = fs.Length };
}
```

**形参按类型自动绑定**（顺序无关）：

| 形参类型 | 绑定为 |
|---|---|
| `System.IO.Stream`（必需） | 上传负载体（逐帧喂入） |
| `CancellationToken`（可选） | 连接生命周期取消令牌 |
| 其它类/接口（可选） | 从上传头部的 `meta` 字段反序列化 |

启动时校验：标了 `Stream = true` 却没有 `Stream` 形参 → **启动即抛异常**（fail-fast）。

**特性**：

- **内存恒定**：负载只在 Pipe 滑动窗口内暂存，与文件大小无关。
- **端到端背压**：端点写盘慢 → Pipe 阻塞 → 停止读帧 → TCP 反压到客户端。
- **`MaxBytes` 封顶**：流式端点的 `MaxBytes` 是"单次上传最多流过的字节数"（计数器封顶，不占内存）；0 = 不限。超限发 `1009` 并中止。
- **鉴权前置**：头部解析后、开喂数据前即可鉴权/配额检查，不通过直接拒绝，不浪费带宽。

---

## 三、线格式（协议）

流式上传是一条 **二进制** WebSocket 消息，帧内布局：

```
[4 字节魔数 "\0WSU"] [4 字节大端 头部长度 N] [N 字节 UTF-8 JSON 头部] [原始负载字节 ...]
```

- 头部 JSON：`{"id":"<请求id>","target":"file.upload","meta":{...}}`。
- 魔数 `\0WSU`（`0x00 'W' 'S' 'U'`）用于把流式上传与同连接上的普通消息区分开——JSON 与 MessagePack 请求都不以 `0x00` 开头，故不冲突。
- 响应仍按 `id` 关联，编码随通道（`/ws` 为 JSON，`/mp` 为 MessagePack）。

MVC（`/ws`）与 MessagePack（`/mp`）两个通道均支持。客户端 SDK 已封装此协议，无需手工拼帧。

---

## 四、客户端 `uploadStream`

6 个客户端均提供上传帮助方法，响应关联方式与普通请求一致。

**JavaScript / TypeScript**（分帧发送，客户端内存恒定）
```typescript
const res = await client.uploadStream("file.upload", fs.createReadStream("big.bin"), { fileName: "big.bin" });
// source 支持 Uint8Array / Node Readable / (异步)可迭代字节块
```

**Python**（异步分帧，客户端内存恒定）
```python
res = await client.upload_stream("file.upload", open("big.bin", "rb"), meta={"fileName": "big.bin"})
# source 支持 bytes / 文件对象(.read) / (异步)可迭代
```

**C#**（分帧发送，客户端内存恒定）
```csharp
await using var fs = File.OpenRead("big.bin");
var res = await client.UploadStreamAsync<UploadResult>("file.upload", fs, new { fileName = "big.bin" });
```

**Java**（`sendFragmentedFrame` 分帧，客户端内存恒定）
```java
try (var in = new FileInputStream("big.bin")) {
    var res = client.<UploadResult>uploadStream("file.upload", in, Map.of("fileName","big.bin"), 65536, UploadResult.class).get();
}
```

**Dart / Rust**（受各自 WebSocket 库限制为"单条消息"，客户端侧会缓冲负载；**服务端仍是流式、内存恒定**）
```dart
final res = await client.uploadStream<Map>('file.upload', bytes, meta: {'fileName': 'big.bin'});
```
```rust
let res: UploadResult = client.upload_stream("file.upload", payload_bytes, Some(meta_json)).await?;
```

> 说明：JS / Python / C# / Java 走真正的帧级分片，客户端内存不随文件增大；Dart / Rust 的 WebSocket 库只支持"一次一条完整消息"，客户端侧需缓冲负载，但服务端接收始终是流式的。

---

## 五、性能基准

仓库提供独立性能基准项目 `Tests/Cyaim.WebSocketServer.Benchmarks`（**非单元测试**，手动运行）：

```bash
dotnet run -c Release --project Cyaim.WebSocketServer/Tests/Cyaim.WebSocketServer.Benchmarks
```

真实 Kestrel（回环）本机参考结果（数值随硬件波动，形态是重点）：

| 场景 | 结果 |
|---|---|
| 单客户端 · MVC/JSON（16/64/256 KiB 帧） | 91 / 98 / 112 MiB/s |
| 单客户端 · MessagePack（64 KiB 帧） | ~96 MiB/s |
| 并发 N=16 · 各 32 MiB（共 512 MiB） | 聚合 ~474 MiB/s，**托管堆峰值仅 +18 MiB** |

**关键结论**：16 客户端并发共上传 512 MiB，服务端托管堆只涨 ~18 MiB（若走缓冲会飙升约 512 MiB）——流式内存受**并发数**约束，而非负载大小。

---

## 相关文档

- [核心库文档](./CORE.md) · [配置指南](./CONFIGURATION.md) · [客户端 SDK](./CLIENTS.md) · [API 参考](./API_REFERENCE.md)
