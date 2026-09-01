using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Injectors;
using Cyaim.WebSocketServer.Infrastructure.Metrics;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler
{
    /// <summary>
    /// Provide MVC forwarding handler
    /// </summary>
    public class MvcChannelHandler : IWebSocketHandler
    {
        private ILogger<WebSocketRouteMiddleware> logger;
        private WebSocketRouteOption webSocketOption;
        private BandwidthLimitManager bandwidthLimitManager;
        private WebSocketMetricsCollector _metricsCollector;
        private EndpointInjectorFactory _injectorFactory;
        private MethodInvokerFactory _methodInvokerFactory;

        /// <summary>
        /// Get instance
        /// </summary>
        /// <param name="receiveBufferSize"></param>
        /// <param name="sendBufferSize"></param>
        public MvcChannelHandler(int receiveBufferSize = 4 * 1024, int sendBufferSize = 4 * 1024)
        {
            ReceiveTextBufferSize = ReceiveBinaryBufferSize = receiveBufferSize;
            SendTextBufferSize = SendBinaryBufferSize = sendBufferSize;
        }


        #region Base

        /// <summary>
        /// Metadata used when parsing the handler
        /// </summary>
        public WebSocketHandlerMetadata Metadata { get; } = new WebSocketHandlerMetadata
        {
            Describe = "Provide MVC forwarding handler",
            CanHandleBinary = true,
            CanHandleText = true
        };

        /// <summary>
        /// Receive message buffer
        /// </summary>
        public int ReceiveTextBufferSize { get; set; }
        /// <summary>
        /// Receive message buffer
        /// </summary>
        public int ReceiveBinaryBufferSize { get; set; }
        /// <summary>
        /// Send message buffer
        /// </summary>
        public int SendTextBufferSize { get; set; }
        /// <summary>
        /// Send message buffer
        /// </summary>
        public int SendBinaryBufferSize { get; set; }

        /// <summary>
        /// SubProtocol
        /// </summary>
        public string SubProtocol { get; }
        #endregion

        /// <summary>
        /// Time out when sending response data
        /// </summary>
        public TimeSpan ResponseSendTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Connected clients by mvc channel
        /// </summary>
        public static ConcurrentDictionary<string, WebSocket> Clients { get; set; } = new ConcurrentDictionary<string, WebSocket>();


        /// <summary>
        /// Process-wide, despite the name — kept only so existing code compiles.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This field said "associated with the connection" and was not: <c>AddMvcChannel</c> builds
        /// <b>one handler per channel</b> (<c>new MvcChannelHandler(...).ConnectionEntry</c>) and hands the
        /// same delegate to every connection, so every connection on the channel shared this one
        /// semaphore. Setting <c>MaxConnectionParallelForwardLimit</c> to a small number in the belief
        /// that it was per-connection therefore serialised the whole process — the opposite of what the
        /// name promised, and worse the more connections the server carried.
        /// </para>
        /// <para>
        /// The per-connection gate is now a local in <c>MvcForward</c>. Nothing reads this field any
        /// more; it stays for one release so a downstream that assigns it still compiles.
        /// </para>
        /// <para>
        /// 名字说「与连接关联」，而它不是：AddMvcChannel 每个通道只建**一个** handler，
        /// 把同一个委托交给每一条连接，于是整条通道上所有连接共用这一个信号量。
        /// 有人以为它是每连接的、把上限设成一个小数字，实际效果是把整个进程串行化——
        /// 与名字承诺的正好相反，而且服务器扛的连接越多越糟。
        /// 真正的每连接闸门现在是 MvcForward 里的局部变量。这个字段已无人读取，
        /// 保留一个版本，只为让下游赋值它的代码还能编译。
        /// </para>
        /// </remarks>
        [Obsolete("This was process-wide, not per-connection. The gate is now a per-connection local; this field is no longer read.")]
        public SemaphoreSlim ParallelForwardLimitSlim = null;

        /// <summary>
        /// In-flight requests allowed per connection when <c>MaxConnectionParallelForwardLimit</c> is unset.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Unset used to mean "no gate at all", and the gate that existed released at <i>dispatch</i>
        /// rather than at <i>completion</i>, so neither setting it nor leaving it bounded the number of
        /// requests actually in flight. A client that pipelines without waiting for responses therefore
        /// held one DI scope, one controller instance and one parsed body per request, with nothing
        /// counting them: in-flight ≈ attacker bandwidth × backend latency, and it is a positive
        /// feedback loop, because the memory and thread-pool pressure slow the backend further.
        /// </para>
        /// <para>
        /// 16 is far above what a real client pipelines and far below what an attacker needs. The
        /// aggregate is bounded too: this cap times <c>MaxConnectionLimit</c>.
        /// 不设它曾经等于「完全没有闸门」，而存在的那个闸门是在**派发**处释放而不是**完成**处，
        /// 所以设不设都没有约束住真正在途的请求数。16 远高于真实客户端的流水线深度，
        /// 远低于攻击者需要的量；总量也有界：这个上限乘以 MaxConnectionLimit。
        /// </para>
        /// </remarks>
        internal const int DefaultConnectionInflightLimit = 16;

        /// <summary>
        /// After processing a message, the per-connection receive stream keeps at most this capacity;
        /// larger spikes are released so a one-off big multi-frame message doesn't retain peak memory
        /// for the connection's lifetime. Small multi-frame connections keep their modest buffer (no churn).
        /// 处理消息后，每连接接收流最多保留此容量；更大的尖峰会被释放，避免一次性大消息长期占用峰值内存。
        /// </summary>
        private const int MaxRetainedReceiveCapacity = 64 * 1024;

        /// <summary>
        /// Total bytes the header probe may scan for one message, however many frames it arrives in.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The probe resolves <c>target</c> out of the accumulated buffer, and it re-runs on every
        /// receive iteration until it succeeds. On the success path that is one scan. On the failure
        /// path — a message that simply never contains <c>target</c> — it is one scan of the whole
        /// accumulated buffer per iteration, which is O(n²) in the message size and is reachable by
        /// any client that has completed the handshake.
        /// </para>
        /// <para>
        /// With a 4 KiB receive buffer and a 1 MiB message cap, unbounded costs 256 iterations and
        /// ~134 MB scanned for 1 MiB delivered: a 128× amplification, and 512× if the client sends
        /// 1 KiB chunks. This budget makes the total independent of both the message size and the
        /// number of frames it is split into.
        /// </para>
        /// <para>
        /// A prefix cap alone would not do it — "only scan the first 64 KiB" still allows 65,536
        /// one-byte frames each triggering a 64 KiB scan. The bound has to be cumulative.
        /// </para>
        /// <para>
        /// 一条消息的头部探测总共可以扫描的字节数，与它分成几帧无关。
        /// 探测在每次接收迭代上重跑，直到解析出 target。成功路径上只扫一次；
        /// **失败路径**——消息里压根没有 target——是每次迭代全量重扫一遍已累积缓冲区，
        /// 开销是消息大小的平方级，而且任何完成握手的客户端都能触发。
        /// 按 4 KiB 接收缓冲 + 1 MiB 消息上限：不设界是 256 次迭代、为 1 MiB 的流量扫描约 134 MB，
        /// 放大 128 倍；客户端改用 1 KiB 分块则是 512 倍。
        /// 只设「前缀上限」不够：65536 个一字节帧，每个都触发一次 64 KiB 扫描。界必须是累计的。
        /// </para>
        /// </remarks>
        internal const int HeaderProbeBudgetBytes = 256 * 1024;

        /// <summary>
        /// Whether the header probe may run again for this message.
        /// </summary>
        /// <remarks>
        /// Split out so the bound can be asserted without driving a socket: the property that matters
        /// is that the total scanned bytes stay under <see cref="HeaderProbeBudgetBytes"/> no matter
        /// how the client chunks the message, and that is a statement about this predicate and the
        /// caller's subtraction, not about the receive loop.
        /// 拆出来，是为了不架 socket 也能断言那条界：真正要紧的性质是「无论客户端怎么分块，
        /// 累计扫描字节数不超过预算」，而那是关于这个判断和调用方那次扣减的陈述，与接收循环无关。
        /// </remarks>
        internal static bool ShouldProbeHeader(bool alreadyResolved, long remainingBudget) =>
            !alreadyResolved && remainingBudget > 0;

        /// <summary>
        /// Cached scope factory (singleton) to avoid a service lookup per request.
        /// 缓存的 ScopeFactory（单例），避免每次请求做一次服务查找。
        /// </summary>
        private static IServiceScopeFactory _cachedScopeFactory;


        #region Pipeline
        #endregion

        /// <summary>
        /// Mvc Channel entry
        /// </summary>
        /// <param name="context"></param>
        /// <param name="logger"></param>
        /// <param name="webSocketOptions"></param>
        /// <returns></returns>
        public async Task ConnectionEntry(HttpContext context, ILogger<WebSocketRouteMiddleware> logger, WebSocketRouteOption webSocketOptions)
        {
            this.logger = logger;
            webSocketOption = webSocketOptions;

            // 某些宿主（如 TestServer）不分配连接 ID，为空时补一个，避免后续以 null 作字典键崩溃
            // Some hosts (e.g. TestServer) don't assign a connection id; generate one so later
            // dictionary operations never receive a null key
            if (string.IsNullOrEmpty(context.Connection.Id))
            {
                context.Connection.Id = Guid.NewGuid().ToString("N");
            }

            // 初始化注入器工厂（如果尚未初始化）
            if (webSocketOptions.InjectorFactory == null)
            {
                webSocketOptions.InjectorFactory = new EndpointInjectorFactory(webSocketOptions);
            }
            _injectorFactory = webSocketOptions.InjectorFactory;

            // 初始化方法调用器工厂（如果尚未初始化）
            if (webSocketOptions.MethodInvokerFactory == null)
            {
                webSocketOptions.MethodInvokerFactory = new MethodInvokerFactory();
            }
            _methodInvokerFactory = webSocketOptions.MethodInvokerFactory;

            // 获取指标收集器
            if (WebSocketRouteOption.ApplicationServices != null)
            {
                _metricsCollector = WebSocketRouteOption.ApplicationServices.GetService<WebSocketMetricsCollector>();
            }

            // 初始化带宽限速管理器
            // 如果 BandwidthLimitPolicy 未设置，尝试从 IOptions 加载
            var policy = webSocketOptions.BandwidthLimitPolicy;
            if (policy == null && WebSocketRouteOption.ApplicationServices != null)
            {
                try
                {
                    var options = WebSocketRouteOption.ApplicationServices.GetService<Microsoft.Extensions.Options.IOptions<Infrastructure.Configures.BandwidthLimitPolicy>>();
                    if (options != null && options.Value != null)
                    {
                        policy = options.Value;
                    }
                }
                catch
                {
                    // 忽略错误，继续使用 null
                }
            }

            if (policy != null)
            {
                var loggerFactory = WebSocketRouteOption.ApplicationServices?.GetService<ILoggerFactory>();
                var bandwidthLogger = loggerFactory?.CreateLogger<BandwidthLimitManager>();
                var qpsPriorityManager = WebSocketRouteOption.ApplicationServices?.GetService<QpsPriorityManager>();
                bandwidthLimitManager = new BandwidthLimitManager(bandwidthLogger, policy, qpsPriorityManager);
            }

            // The in-flight gate used to be built here, on the handler — which is shared by every
            // connection on the channel. It is now built per connection inside MvcForward.
            // 在途闸门原本建在这里、挂在 handler 上，而 handler 是整条通道共用的。
            // 现在它建在 MvcForward 里，每条连接一个。

            WebSocketCloseStatus? webSocketCloseStatus = null;
            try
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    // Event instructions whether connection
                    var ifThisContinue = await MvcChannel_OnBeforeConnection(context, webSocketOptions, context.Request.Path, logger);
                    if (!ifThisContinue)
                    {
                        return;
                    }
                    var ifContinue = await webSocketOptions.OnBeforeConnection(context, webSocketOptions, context.Request.Path, logger);
                    if (!ifContinue)
                    {
                        return;
                    }

                    // 配置最大连接数（Count 为 O(锁桶数)，避免 LongCount 对百万级连接做 O(n) 快照枚举）
                    // Use Count instead of LongCount: O(lock buckets) vs O(n) snapshot enumeration at 1M+ connections
                    if ((ulong)Clients.Count >= webSocketOptions.MaxConnectionLimit)
                    {
                        // **必须带上状态码和一行日志。** 这里以前是裸 return：管道走完没人写过状态，
                        // 于是响应是 `200 OK`，没有升级、没有理由、没有日志。客户端看到的是
                        // 「请求成功了，但不是 WebSocket」——和网关坏掉长得一模一样，
                        // 而真相是这个节点满了、换一个节点立刻就能连上。
                        // 实测：两个网关各 10 万条封顶时，压力机看到十二万次 `upgrade refused: HTTP/1.1 200 OK`，
                        // 而服务端日志里一个字都没有——判断"是我满了还是它坏了"完全无从下手。
                        // 503 + Retry-After 才是负载均衡器和客户端重连逻辑真正能用的答案。
                        //
                        // A bare return left the pipeline to finish with nobody writing a status, so a node at
                        // capacity answered 200 OK: no upgrade, no reason, no log line. To the client that is
                        // indistinguishable from a broken gateway, when in fact another node would have taken
                        // it immediately. Measured at 120k such refusals with nothing on the server side.
                        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                        // 用索引器而不是 Headers.RetryAfter：那个强类型属性是 net6+ 才有的，
                        // 而本库还编 netstandard2.1——写成强类型时 net10 的测试工程照样过，
                        // 只有把库按全部 TFM 编一遍才会红。
                        // The typed accessor is net6+, and this library still targets netstandard2.1.
                        context.Response.Headers["Retry-After"] = "1";
                        logger.LogWarning(
                            "WebSocket connection from {RemoteIp}:{RemotePort} refused: this node holds "
                            + "{Held} connections and MaxConnectionLimit is {Limit}",
                            context.Connection.RemoteIpAddress,
                            context.Connection.RemotePort,
                            Clients.Count,
                            webSocketOptions.MaxConnectionLimit);
                        return;
                    }

                    // 接受连接
                    using WebSocket webSocket = string.IsNullOrEmpty(SubProtocol) ? await context.WebSockets.AcceptWebSocketAsync() : await context.WebSockets.AcceptWebSocketAsync(SubProtocol);
                    try
                    {
                        logger.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_Connected));
                        bool succ = Clients.TryAdd(context.Connection.Id, webSocket);
                        if (!succ && !webSocketOptions.AllowSameConnectionIdAccess)
                        {
                            // 如果配置了允许多连接
                            logger.LogDebug(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_ConnectionAlreadyExists));

                            return;
                        }

                        // 记录连接建立指标
                        var currentNodeId = Infrastructure.Cluster.GlobalClusterCenter.ClusterContext?.NodeId;
                        _metricsCollector?.RecordConnectionEstablished(currentNodeId, context.Request.Path);

                        // Register connection with cluster manager if cluster is enabled
                        // 如果启用了集群，向集群管理器注册连接
                        var clusterManager = Infrastructure.Cluster.GlobalClusterCenter.ClusterManager;
                        if (clusterManager != null)
                        {
                            try
                            {
                                var remoteIpAddress = context.Connection.RemoteIpAddress?.ToString();
                                var remotePort = context.Connection.RemotePort;
                                await clusterManager.RegisterConnectionAsync(
                                    context.Connection.Id,
                                    context.Request.Path,
                                    remoteIpAddress,
                                    remotePort);
                                logger.LogDebug(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_ClusterManagerRegistered));
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_ClusterManagerRegisterFailed));
                            }
                        }

                        IHostApplicationLifetime appLifetime = WebSocketRouteOption.ApplicationServices.GetRequiredService<IHostApplicationLifetime>();

                        await MvcForward(context, webSocket, webSocketOptions, appLifetime);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_DisconnectedInternalExceptions + ex.Message + Environment.NewLine + ex.StackTrace));
                    }
                    finally
                    {
                        if (webSocket.CloseStatus == null && webSocket.State == WebSocketState.Open)
                        {
                            //await webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, string.Empty, CancellationToken.None).ConfigureAwait(false);
                            webSocket.Abort();
                        }
                        webSocketCloseStatus = webSocket.CloseStatus;
                    }
                }
                else
                {
                    logger.LogDebug(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_ConnectionDenied));
                    context.Response.StatusCode = 400;
                }
            }
            catch (Exception ex)
            {
                logger.LogInformation(ex, ex.Message + Environment.NewLine + ex.StackTrace);
            }
            finally
            {
                // 清理带宽限速跟踪器
                if (bandwidthLimitManager != null)
                {
                    bandwidthLimitManager.RemoveConnection(context.Connection.Id);
                }

                // 记录连接关闭指标
                var currentNodeId = Infrastructure.Cluster.GlobalClusterCenter.ClusterContext?.NodeId;
                var closeStatusStr = webSocketCloseStatus?.ToString();
                _metricsCollector?.RecordConnectionClosed(currentNodeId, context.Request.Path, closeStatusStr);

                await MvcChannel_OnDisconnected(context, webSocketCloseStatus, webSocketOptions, logger);
            }
        }

        /// <summary>
        /// Forward by WebSocket transfer type
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocket"></param>
        /// <returns></returns>
        private async Task MvcForward(HttpContext context, WebSocket webSocket, WebSocketRouteOption webSocketOptions, IHostApplicationLifetime appLifetime)
        {
            try
            {
                string wsCloseDesc = string.Empty;
                // 应用全局接收内存预算（进程级、幂等）。
                // Apply the process-wide receive-memory budget (idempotent).
                WebSocketReceiveMemoryGovernor.MaxBytes = webSocketOptions.MaxTotalReceiveBufferBytes ?? 0;
                // 发送侧上限与接收侧同样由通道入口镜像进静态字段（WebSocketManager 是静态类，拿不到 options）。
                // The send-side limits are mirrored in at the channel entry just like the receive-side ones
                // (WebSocketManager is static and cannot see options).
                WebSocketManager.MaxSendMaterializeBytes = webSocketOptions.MaxSendMaterializeBytes ?? 0;
                WebSocketManager.MaxSendFrameBytes = webSocketOptions.MaxSendFrameBytes;
                WebSocketManager.AllowChunkedSendAboveMaterializeLimit = webSocketOptions.AllowChunkedSendAboveMaterializeLimit;
                WebSocketSendMemoryGovernor.MaxBytes = webSocketOptions.MaxTotalSendMaterializeBytes ?? 0;
                // 初始容量 0：单帧消息走快路径、根本不写这个流，因此绝大多数连接不会分配接收缓冲。
                // 多帧消息首次写入时才按需增长；处理后在 finally 里收缩大尖峰（见下）。
                // Zero initial capacity: single-frame messages take the fast path and never write this
                // stream, so the vast majority of connections allocate no receive buffer. Multi-frame
                // messages grow it on first write; large spikes are shrunk in the finally below.
                using MemoryStream wsReceiveReader = new MemoryStream();

                // Per connection, and deliberately not disposed: a request still in flight when the
                // connection ends will Release() this from its continuation, and disposing it here
                // would turn a normal disconnect into an ObjectDisposedException on the thread pool.
                // SemaphoreSlim only needs disposal once AvailableWaitHandle has been touched, and
                // nothing here touches it.
                // 每连接一个，且**刻意不释放**：连接结束时仍在途的请求会在它的续体里 Release 它，
                // 在这里 Dispose 会把一次正常断连变成线程池上的 ObjectDisposedException。
                // SemaphoreSlim 只有在碰过 AvailableWaitHandle 之后才需要 Dispose，这里没有碰它。
                SemaphoreSlim connectionInflight = new SemaphoreSlim(
                    (int)(webSocketOption.MaxConnectionParallelForwardLimit ?? DefaultConnectionInflightLimit),
                    (int)(webSocketOption.MaxConnectionParallelForwardLimit ?? DefaultConnectionInflightLimit));

                bool connectionClosed = false;
                do
                {
                    long requestTime = DateTime.UtcNow.Ticks;
                    WebSocketReceiveResult result = null;
                    SemaphoreSlim endPointSlim = null;

                    // Set once the gates below have been handed to the dispatched task, which then owns
                    // releasing them. Until then this iteration's finally owns them — the `goto
                    // CONTINUE_RECEIVE` paths never dispatch and must not leak a permit.
                    // 一旦下面那两个闸门被交给派发出去的任务，就由那个任务负责释放；在此之前归本轮的 finally。
                    // goto CONTINUE_RECEIVE 的那几条路径根本不派发，绝不能把票漏掉。
                    bool gatesOwnedByTask = false;
                    bool receivedClose = false;
                    // 单帧快路径：整条消息一次 ReceiveAsync 收全时借用的租用缓冲区（所有权从接收循环转移到本迭代，
                    // 在同步解析完成后于外层 finally 归还）。为 null 表示走多帧 MemoryStream 重组路径。
                    // Single-frame fast path: the rented buffer borrowed when the whole message arrived in one
                    // ReceiveAsync (ownership moves out of the receive loop, returned in the outer finally after
                    // the synchronous parse). Null => multi-frame MemoryStream reassembly path.
                    byte[] singleFrameBuffer = null;
                    int singleFrameCount = 0;
                    // 本条消息在全局接收内存预算中已预留的字节（多帧累计），在本迭代 finally 中释放。
                    // Bytes this message reserved from the global receive-memory budget (multi-frame), released in finally.
                    long reservedReceiveBytes = 0;
                    // 端点级上限：一旦从头部解析出 target，就把生效上限从全局默认切到该端点的 MaxBytes（0=沿用全局）。
                    // Per-endpoint cap: once the target is parsed from the header, switch the effective cap from the
                    // global default to this endpoint's MaxBytes (0 = keep global).
                    long effectiveReceiveLimit = webSocketOption.MaxRequestReceiveDataLimit ?? 0;

                    // Budget for the header probe below, per message. Without it the probe is O(n²)
                    // in the message size — see HeaderProbeBudgetBytes for the arithmetic.
                    // 头部探测的每消息扫描预算。没有它，探测的开销是消息大小的平方级。
                    long headerScanBudget = HeaderProbeBudgetBytes;
                    bool endpointPolicyResolved = false;
                    // 从头部解析出的 target，解析一次后供端点大小策略、逐帧带宽限速和端点并发限流共用。
                    // 头部只在第一帧里，而第一帧时数据还在 buffer 中（尚未写入 wsReceiveReader）。
                    // The target parsed from the header, resolved once and shared by the endpoint size policy, the
                    // per-frame bandwidth throttle and the per-endpoint concurrency limit. The header lives in the
                    // first frame, and at that point the bytes are still in `buffer`, not yet in wsReceiveReader.
                    string resolvedTarget = null;
                    // target 解析出来之前已经收下、因而只能按"无端点"计费的字节数。
                    // 消息收完后用兜底解析出的端点一次性补记进端点桶，否则把 target 放在 payload 末尾
                    // 就能让端点级限额收不到任何字节。
                    // Bytes already received while the target was still unknown, and therefore charged
                    // without an endpoint. They are settled into the endpoint bucket once the message is
                    // complete; without that, putting `target` at the end of the payload keeps the
                    // per-endpoint limit from ever seeing them.
                    long endpointUnattributedBytes = 0;
                    try
                    {
                        // The connection-level gate is taken just before dispatch, not here. Taken here it
                        // covered "block waiting for the client's next message", which a single receive
                        // loop can never contend with — one connection reads one message at a time, so the
                        // gate was never actually held by two iterations at once.
                        // 连接级闸门改在派发前获取，不在这里。放在这里覆盖的是「阻塞等客户端的下一条消息」，
                        // 而单条连接的接收循环本来就是串行的一条，两轮迭代永远不会同时持票——它拦不住任何东西。

                        if (!(webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseSent))
                        {
                            if (webSocket.State == WebSocketState.Aborted || webSocket.State == WebSocketState.CloseReceived || webSocket.State == WebSocketState.Closed)
                            {
                                // 连接已关闭，设置标志并退出
                                connectionClosed = true;
                                break;
                            }
                            else
                            {
                                await Task.Delay(300).ConfigureAwait(false);
                                continue;
                            }

                        }

                        #region 接收数据
                        // 接收数据的缓冲区
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReceiveTextBufferSize);
                        bool messageComplete = false;

                        try
                        {
                            while (!messageComplete && !receivedClose)
                            {
                                // 接收数据帧
                                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                                // 如果接收到Close消息，保存状态并退出接收循环
                                if (result.MessageType == WebSocketMessageType.Close)
                                {
                                    receivedClose = true;
                                    connectionClosed = true;
                                    wsCloseDesc = result.CloseStatusDescription;
                                    // 响应Close帧（如果连接状态允许）
                                    if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                                    {
                                        try
                                        {
                                            await webSocket.CloseAsync(
                                                result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                                                result.CloseStatusDescription ?? string.Empty,
                                                CancellationToken.None);
                                        }
                                        catch (Exception ex)
                                        {
                                            logger.LogDebug(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_CloseResponseFailed + Environment.NewLine + ex.Message));
                                        }
                                    }
                                    break;
                                }

                                // 如果Count为0，检查是否消息已完成
                                // 正常情况下，Count应该大于0，但如果EndOfMessage为true，说明消息接收完成
                                if (result.Count == 0)
                                {
                                    if (result.EndOfMessage)
                                    {
                                        messageComplete = true;
                                        break;
                                    }
                                    // Count为0但EndOfMessage为false的情况不应该发生，但为了安全继续等待
                                    continue;
                                }

                                // 流式上传：二进制消息在本通道也用于普通 JSON 请求，故用魔数前缀区分流式上传。
                                // JSON 永不以 0x00 开头，因此 "\0WSU" 前缀不会与普通请求冲突。首帧命中魔数→走流式。
                                // Streaming upload: binary is also a valid transport for normal JSON requests here, so a
                                // magic prefix distinguishes a streaming upload. JSON never starts with 0x00, so "\0WSU" can't collide.
                                if (result.MessageType == WebSocketMessageType.Binary && wsReceiveReader.Length == 0 && singleFrameBuffer == null
                                    && Infrastructure.StreamDispatch.StreamUploadProtocol.StartsWithMagic(buffer, result.Count))
                                {
                                    await MvcStreamForward(webSocket, context, buffer, result, webSocketOption, logger, appLifetime.ApplicationStopping);
                                    goto CONTINUE_RECEIVE;
                                }

                                // 请求大小限制：注意 wsReceiveReader.Length > (long?)null 在 C# 中恒为 false，
                                // 之前 limit 未配置(null)时该检查被静默禁用→单条(多帧)消息可无限增长直至 OOM。
                                // 这里用模式匹配显式判定：limit 有值才限制；null 表示显式"不限"(需自担 OOM 风险)。
                                // The old `Length > (long?)null` was always false, silently disabling the cap when
                                // unset. Enforce only when a limit is present; null means explicitly unlimited.
                                // 累计接收字节 = 已写入流的多帧数据 + 本帧(尚未写入)，避免超限后仍先写入再判断。
                                long accumulatedLen = wsReceiveReader.Length + (result?.Count ?? 0);

                                // 端点级上限：从头部解析 target，命中端点策略则切换生效上限。header 通常在首帧内，
                                // 首帧时数据在 buffer、后续帧在 wsReceiveReader；解析不到(头部未到齐)就先用全局默认兜底。
                                // Per-endpoint cap: resolve target from the header; if an endpoint policy matches, switch the
                                // effective cap. On the first frame the bytes are in `buffer`; later they're in wsReceiveReader.
                                // 头部只解析一次，端点大小策略与带宽限速共用结果。此前两处各自解析，
                                // 且限速那一处会在每一帧重扫整个已累积缓冲区——100 帧的消息就是 100 次全量扫描。
                                // Resolve the header once and share it: the size policy and the bandwidth throttle both
                                // need the target. They used to parse separately, and the throttle re-scanned the whole
                                // accumulated buffer on every frame — 100 frames meant 100 full scans.
                                if (ShouldProbeHeader(endpointPolicyResolved, headerScanBudget)
                                    && (webSocketOption.WatchAssemblyContext != null || bandwidthLimitManager != null))
                                {
                                    ReadOnlySpan<byte> headerSpan = wsReceiveReader.Length > 0
                                        ? wsReceiveReader.GetBuffer().AsSpan(0, (int)wsReceiveReader.Length)
                                        : buffer.AsSpan(0, result?.Count ?? 0);

                                    // Charged before the scan, not after: a scan that throws must still cost its
                                    // budget, or malformed JSON becomes a way to scan for free.
                                    // 先扣再扫：抛异常的那次扫描也必须计费，否则畸形 JSON 就成了免费扫描的入口。
                                    headerScanBudget -= headerSpan.Length;

                                    string tgt = null;
                                    try { tgt = FindJsonPropertyValue(headerSpan); } catch { /* header not complete yet */ }
                                    if (tgt != null)
                                    {
                                        resolvedTarget = tgt;
                                        if (webSocketOption.WatchAssemblyContext != null
                                            && webSocketOption.WatchAssemblyContext.TryGetEndpointPolicy(tgt, out var pol) && pol.MaxBytes > 0)
                                        {
                                            effectiveReceiveLimit = pol.MaxBytes;
                                        }
                                        endpointPolicyResolved = true;
                                    }
                                    else if (headerScanBudget <= 0)
                                    {
                                        // Give up probing for this message. The global MaxRequestReceiveDataLimit
                                        // still applies — giving up costs a per-endpoint cap that would have been
                                        // *larger*, never a cap that would have been smaller, so the conservative
                                        // outcome is the one that survives.
                                        // 放弃对这条消息的探测。全局上限依然生效：放弃只会丢掉一个**更宽**的端点上限，
                                        // 不会丢掉更严的那个，所以留下来的是保守的那一侧。
                                        endpointPolicyResolved = true;
                                    }
                                }

                                if (effectiveReceiveLimit > 0 && accumulatedLen > effectiveReceiveLimit)
                                {
                                    logger.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_RequestSizeMaximumLimit));

                                    // 有界排空该超限消息的剩余帧；过大/无界则发送 1009 并 Abort（连接随后在外层状态检查处退出）。
                                    // Bounded-drain the rest of this oversized message; if grossly oversized/unbounded it
                                    // sends 1009 and aborts (the loop then exits at its top-of-loop state check).
                                    await WebSocketReceiveMemoryGovernor.DrainOversizedAsync(webSocket, buffer, result);
                                    goto CONTINUE_RECEIVE;
                                }

                                // 逐帧限速。端点取自上面解析出的 target：本帧字节此刻还在 buffer 里、尚未写入
                                // wsReceiveReader，所以每条消息的第一帧 wsReceiveReader.Length 都是 0（单帧消息全程为 0）。
                                // 此前这里只读 wsReceiveReader，于是唯一带着头部的那一帧永远解析不出端点，
                                // 端点限速只能等消息收完才补一刀——大上传在接收过程中完全不受端点限额约束。
                                // 解析不出端点的帧照常计入通道桶和连接桶，字节数另记进 endpointUnattributedBytes，
                                // 待消息收完后补进端点桶（见下方）。
                                // Per-frame throttling. The endpoint comes from the target resolved above: this frame's
                                // bytes are still in `buffer`, not yet written to wsReceiveReader, so its Length is 0 on
                                // every message's first frame (and throughout a single-frame message). Reading only
                                // wsReceiveReader here meant the one frame carrying the header never resolved an endpoint,
                                // so endpoint throttling could only be applied after the whole message had landed — a large
                                // upload was never paced against its endpoint limit while arriving.
                                // Frames with no endpoint yet are still charged to the channel and connection buckets;
                                // their bytes are tallied into endpointUnattributedBytes and settled once the message ends.
                                if (bandwidthLimitManager != null && result.Count > 0)
                                {
                                    if (resolvedTarget == null)
                                    {
                                        endpointUnattributedBytes += result.Count;
                                    }

                                    await bandwidthLimitManager.WaitForBandwidthAsync(
                                        context.Request.Path,
                                        context.Connection.Id,
                                        resolvedTarget,
                                        result.Count,
                                        context.Connection.RemoteIpAddress?.ToString(),
                                        CancellationToken.None);
                                }

                                // 记录消息接收指标
                                var currentNodeId = Infrastructure.Cluster.GlobalClusterCenter.ClusterContext?.NodeId;
                                _metricsCollector?.RecordMessageReceived(result.Count, currentNodeId, context.Request.Path);

                                // 记录统计信息（如果统计记录器可用）
                                Infrastructure.Cluster.GlobalClusterCenter.StatisticsRecorder?.RecordBytesReceived(context.Connection.Id, result.Count);

                                // 单帧快路径：本条消息第一帧即 EndOfMessage（wsReceiveReader 尚为空），说明整条消息
                                // 已在租用缓冲区中收全。直接借用该缓冲区做后续解析，跳过写入 MemoryStream 的整条负载拷贝。
                                // 通过 singleFrameBuffer 转移所有权，接收循环的 finally 不再归还，改由外层 finally 归还。
                                // Single-frame fast path: the message completed on its first frame (wsReceiveReader still
                                // empty), so the whole payload is already in the rented buffer. Borrow it for parsing and
                                // skip the full-payload copy into the MemoryStream. Ownership transfers via singleFrameBuffer.
                                if ((result.EndOfMessage || result.CloseStatus.HasValue) && wsReceiveReader.Length == 0)
                                {
                                    singleFrameBuffer = buffer;
                                    singleFrameCount = result.Count;
                                    messageComplete = true;
                                    break;
                                }

                                // 多帧：先向全局接收内存预算预留本帧字节；超预算则拒绝该消息（背压）。
                                // Multi-frame: reserve this frame against the global receive budget; reject on over-budget.
                                if (!WebSocketReceiveMemoryGovernor.TryReserve(result.Count))
                                {
                                    logger.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_RequestSizeMaximumLimit));
                                    // 有界排空被拒消息的剩余帧。 / Bounded-drain the rejected message's remaining frames.
                                    await WebSocketReceiveMemoryGovernor.DrainOversizedAsync(webSocket, buffer, result);
                                    goto CONTINUE_RECEIVE;
                                }
                                reservedReceiveBytes += result.Count;

                                // 多帧：写入 MemoryStream 重组
                                // Multi-frame: reassemble into the MemoryStream
                                await wsReceiveReader.WriteAsync(buffer.AsMemory(0, result.Count));

                                // 检查消息是否接收完成
                                // 只有当EndOfMessage为true时，才认为消息接收完成
                                if (result.EndOfMessage || result.CloseStatus.HasValue)
                                {
                                    messageComplete = true;
                                    break;
                                }

                                // 如果EndOfMessage为false，说明还有更多帧需要接收，继续循环
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(
                                string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE,
                                        context.Connection.RemoteIpAddress,
                                        context.Connection.RemotePort,
                                        context.Connection.Id,
                                        I18nText.ConnectionEntry_ReceivingClientDataException + Environment.NewLine + ex.Message + Environment.NewLine + ex.StackTrace
                                    )
                                );
                            // 发生异常时，如果已经接收到部分数据且EndOfMessage为true，认为消息接收完成
                            if (result != null && result.EndOfMessage)
                            {
                                messageComplete = true;
                            }
                        }
                        finally
                        {
                            // 归还buffer；单帧快路径已把所有权转移给本迭代（外层 finally 归还），此处不归还
                            // Return the buffer, unless the single-frame fast path took ownership (outer finally returns it)
                            if (!ReferenceEquals(buffer, singleFrameBuffer))
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
                            }
                        }

                        // 如果接收到Close消息，直接退出当前循环，不再处理数据
                        if (receivedClose)
                        {
                            // 设置连接关闭标志，退出外层循环
                            connectionClosed = true;
                            break;
                        }

                        #endregion

                        // 如果result为null或接收到Close消息，跳过后续处理
                        if (result == null || receivedClose)
                        {
                            continue;
                        }

                        // 有效数据视图：单帧快路径直接取自租用缓冲区（零拷贝，未经过 MemoryStream）；
                        // 多帧路径取 MemoryStream 已写入长度的视图。
                        // Valid-data view: the single-frame fast path reads straight from the rented buffer
                        // (zero-copy, never touched the MemoryStream); multi-frame reads the written slice.
                        int receivedLength = singleFrameBuffer != null ? singleFrameCount : (int)wsReceiveReader.Length;
                        ReadOnlyMemory<byte> receivedData = singleFrameBuffer != null
                            ? singleFrameBuffer.AsMemory(0, singleFrameCount)
                            : wsReceiveReader.GetBuffer().AsMemory(0, receivedLength);

                        // 通道桶与连接桶已在循环内逐帧计过了，这里**不能**再调 WaitForBandwidthAsync：
                        // 它对这两个桶是无条件计费的，再调一次就是把同一批字节计两遍——修复前正是如此，
                        // 结果是配置的通道级/连接级限额实际只有一半生效。
                        // 这里只做一件事：把 target 解析出来之前那些帧的字节补进端点桶。target 在 JSON 里的
                        // 位置由客户端决定，可能落在最后一帧；不补的话，把 target 放到 payload 末尾就能让
                        // 端点级限额一个字节都收不到。
                        // The channel and connection buckets were already charged per frame, so WaitForBandwidthAsync
                        // must NOT be called again here: it charges those two unconditionally, and a second call counted
                        // the same bytes twice — which is what left the configured channel/connection limits at half
                        // their intended value before this fix.
                        // The only thing done here is settling the bytes that arrived before the target was known into
                        // the endpoint bucket. Where `target` sits in the JSON is the client's choice and may be the last
                        // frame; without this, putting it at the end of the payload keeps the per-endpoint limit from
                        // seeing a single byte.
                        string endpoint = resolvedTarget;

                        if (bandwidthLimitManager != null && endpointUnattributedBytes > 0)
                        {
                            // 兜底解析：整条消息都在手上了，此时一定能拿到 target（如果它确实存在）。
                            // Fallback parse: the whole message is in hand, so the target resolves now if it exists.
                            if (string.IsNullOrEmpty(endpoint))
                            {
                                try { endpoint = FindJsonPropertyValue(receivedData.Span); } catch { /* not JSON */ }
                            }

                            if (!string.IsNullOrEmpty(endpoint))
                            {
                                bandwidthLimitManager.RecordEndPointBytes(endpoint, endpointUnattributedBytes);
                            }
                        }

                        // EndPoint level restrictions
                        if (webSocketOption.MaxEndPointParallelForwardLimit != null)
                        {
                            if (string.IsNullOrEmpty(endpoint))
                            {
                                endpoint = FindJsonPropertyValue(receivedData.Span);
                            }
                            if (endpoint != null && webSocketOption.MaxEndPointParallelForwardLimit.TryGetValue(endpoint, out endPointSlim) && endPointSlim != null)
                            {
                                await endPointSlim.WaitAsync().ConfigureAwait(false);
                            }
                        }

                        // 请求处理管道 分阶段 接收数据前后 转发前后等

                        // 处理请求的数据
                        MvcRequestScheme requestScheme = null;
                        JsonObject requestBody = null;

                        using (JsonDocument doc = JsonDocument.Parse(receivedData))
                        {
                            JsonElement root = doc.RootElement;
                            JsonElement body = default;
                            bool hasBody = false;
                            foreach (string name in MvcRequestScheme.BODY_NAMES)
                            {
                                hasBody = root.TryGetProperty(name, out body);
                                if (hasBody) break;
                            }

                            requestScheme = doc.Deserialize<MvcRequestScheme>(webSocketOption.DefaultRequestJsonSerializerOptions);
                            // Clone 使节点脱离文档的池化缓冲区；JsonObject.Create 避免旧版 GetRawText 的字符串分配和第三次解析
                            // Clone detaches from the document's pooled buffer; JsonObject.Create avoids the old GetRawText string alloc + third parse
                            requestBody = body.ValueKind != JsonValueKind.Object ? null : JsonObject.Create(body.Clone());
                        }

                        // 检查请求是否包含Id属性
                        if (webSocketOption.RequireRequestId && (requestScheme == null || string.IsNullOrWhiteSpace(requestScheme.Id)))
                        {
                            // 创建错误响应
                            MvcResponseScheme errorResponse = new MvcResponseScheme()
                            {
                                Status = 1,
                                RequestTime = requestTime,
                                CompleteTime = DateTime.UtcNow.Ticks,
                                Target = requestScheme?.Target,
                                Id = requestScheme?.Id,
                                Msg = string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcForwardSendData_RequestIdRequired)
                            };

                            // 发送错误响应（经由每 socket 发送锁，避免与并发响应交叠）
                            // Send error response through the per-socket send gate to avoid interleaving with concurrent responses
                            var responseBytes = JsonSerializer.SerializeToUtf8Bytes(errorResponse, webSocketOption.DefaultResponseJsonSerializerOptions);
                            await WebSocketManager.SendLocalAsync(responseBytes.AsMemory(), result.MessageType, true, CancellationToken.None, timeout: ResponseSendTimeout, sockets: webSocket).ConfigureAwait(false);

                            logger.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcForwardSendData_RequestIdRequired));

                            // 记录消息发送指标
                            var currentNodeId = Infrastructure.Cluster.GlobalClusterCenter.ClusterContext?.NodeId;
                            _metricsCollector?.RecordMessageSent(responseBytes.Length, currentNodeId, context.Request.Path);

                            // 记录统计信息（如果统计记录器可用）
                            Infrastructure.Cluster.GlobalClusterCenter.StatisticsRecorder?.RecordBytesSent(context.Connection.Id, responseBytes.Length);

                            continue;
                        }

                        // 构建每消息上下文并经编译好的中间件链处理（终结点=端点分发，链返回后发送响应）。
                        // 仅在注册了中间件时才复制原始字节；异步处理模式下接收缓冲区会被复用，复制以保证中间件读取安全。
                        // Build the per-message context and run it through the compiled middleware chain
                        // (terminal = endpoint dispatch; response sent after the chain). Copy the raw bytes
                        // only when middleware is registered, since the receive buffer is reused in async mode.
                        var messageContext = new WebSocketMessageContext
                        {
                            HttpContext = context,
                            WebSocket = webSocket,
                            Options = webSocketOption,
                            MessageType = result.MessageType,
                            RequestTimeTicks = requestTime,
                            ReceivedData = webSocketOption.MiddlewareCount > 0 ? receivedData.ToArray() : default,
                            Request = requestScheme,
                            RequestBody = requestBody,
                        };

                        // Taken here and released when the request *finishes* — not when it is dispatched.
                        // Releasing at dispatch (which is what the iteration's finally used to do) leaves
                        // the number of requests actually in flight unbounded: each one holds a DI scope, a
                        // controller instance and a parsed body, and nothing counts them.
                        // 在这里取票，在请求**完成**时还票——而不是在派发时。
                        // 在派发处还票（此前迭代 finally 干的事）等于对真正在途的请求数不设界：
                        // 每一条在途都持有一个 DI Scope、一个控制器实例和一份解析好的请求体，而没有任何东西在数它们。
                        await connectionInflight.WaitAsync().ConfigureAwait(false);

                        Task processTask;
                        try
                        {
                            processTask = ProcessMessageAsync(GetCompiledPipeline(webSocketOption, appLifetime), messageContext);
                        }
                        catch
                        {
                            // Never dispatched, so nobody downstream will give the permits back.
                            // 没派发出去，下游不会有人还票。
                            connectionInflight.Release();
                            throw;
                        }

                        // From here the task owns both gates; this iteration's finally must not touch them.
                        // 从这里起两个闸门归那个任务；本轮的 finally 不能再碰它们。
                        gatesOwnedByTask = true;

                        // 是否串行
                        if (webSocketOption.EnableForwardTaskSyncProcessingMode)
                        {
                            try
                            {
                                await processTask;
                            }
                            finally
                            {
                                connectionInflight.Release();
                                endPointSlim?.Release();
                            }
                        }
                        else
                        {
                            // Runs on every outcome, not just faults: this continuation is what returns the
                            // permits, so an OnlyOnFaulted one would return them only when the request threw.
                            // Static delegate + tuple state keeps the no-closure-allocation property the
                            // original had.
                            // 这个续体在**任何**结局上都要跑，不只是出错时：还票靠的就是它，
                            // 而 OnlyOnFaulted 的续体只会在请求抛异常时还票。
                            // 静态委托 + 元组 state，保持原来「不分配闭包」的性质。
                            _ = processTask.ContinueWith(static (t, state) =>
                            {
                                (ILogger log, SemaphoreSlim inflight, SemaphoreSlim endpoint) s =
                                    ((ILogger, SemaphoreSlim, SemaphoreSlim))state;

                                if (t.IsFaulted)
                                {
                                    s.log.LogInformation(t.Exception, I18nText.ConnectionEntry_DisconnectedInternalExceptions);
                                }

                                s.inflight.Release();
                                s.endpoint?.Release();
                            //
                            // The ILogger cast is not cosmetic. `logger` is ILogger<WebSocketRouteMiddleware>,
                            // so `(logger, ...)` boxes a ValueTuple<ILogger<WebSocketRouteMiddleware>, ...>,
                            // and unboxing a value tuple demands the *exact* type — the cast inside the
                            // continuation would throw InvalidCastException, which a continuation swallows.
                            // The permits would then never come back and the connection would wedge at the
                            // cap: a security fix that silently turns into a deadlock. Caught by
                            // InflightRequestLimitTests.Finished_requests_give_their_permit_back.
                            // 这个 ILogger 转型不是修饰。logger 的静态类型是 ILogger<WebSocketRouteMiddleware>，
                            // 于是 (logger, ...) 装箱的是 ValueTuple<ILogger<WebSocketRouteMiddleware>, ...>，
                            // 而值元组拆箱要求类型**完全一致**——续体里那次强转会抛 InvalidCastException，
                            // 而续体会把它吞掉。票于是一张都还不回来，连接卡死在上限上：
                            // 一个悄悄变成死锁的安全修复。由 Finished_requests_give_their_permit_back 抓到。
                            }, ((ILogger)logger, connectionInflight, endPointSlim), TaskContinuationOptions.ExecuteSynchronously);
                        }

                    CONTINUE_RECEIVE:;
                    }
                    catch (Exception ex)
                    {
                        // Two problems in one line, and both are paid on every failed message.
                        //
                        // The buffer was materialised into a UTF-16 string that no sink would ever
                        // render: `ex.Message` was the message *template*, and a template with no
                        // placeholder discards its arguments. LogInformation is an extension method,
                        // so the argument is evaluated before IsEnabled is consulted — raising the log
                        // level did not switch the allocation off. Any message past the receive buffer
                        // size takes this path (4 KiB here), so a stream of oversized non-JSON is a 2×
                        // allocation amplifier that a caller controls.
                        //
                        // And `ex.Message` as a template is a hazard by itself: an exception whose text
                        // contains `{` is parsed as a placeholder and the logging call throws — inside
                        // the outermost catch of the receive loop, which is the worst place for it.
                        //
                        // 一行里两个问题，而且每条失败消息都要付一次。
                        // 缓冲区被物化成一份没有任何 sink 会渲染的 UTF-16 字符串：ex.Message 是消息**模板**，
                        // 而没有占位符的模板会丢弃它的参数。LogInformation 是扩展方法，
                        // 实参在 IsEnabled 之前就求值——调高日志级别关不掉这次分配。
                        // 任何超过接收缓冲大小（这里 4 KiB）的消息都走这条路径，
                        // 于是持续发送超大的非 JSON 就是一个调用方可控的 2× 分配放大器。
                        // 而 ex.Message 当模板本身也是个雷：异常文本里出现 `{` 会被当占位符解析、
                        // 日志调用自身抛异常——发生在接收循环的最外层 catch 里，是最糟的位置。
                        logger.LogInformation(ex, "Message dispatch failed; {Bytes} bytes were buffered", wsReceiveReader.Length);
                    }
                    finally
                    {
                        // 归还单帧快路径借用的租用缓冲区（此时同步解析已完成：requestScheme/requestBody 已独立解析出，
                        // 中间件的 ReceivedData 已按需复制，异步转发只引用 ctx，不再引用本缓冲区）。
                        // Return the single-frame fast-path buffer. By now the synchronous parse is done
                        // (requestScheme/requestBody are independent, middleware ReceivedData is copied if needed,
                        // and the async forward only references ctx), so the buffer is no longer read.
                        if (singleFrameBuffer != null)
                        {
                            ArrayPool<byte>.Shared.Return(singleFrameBuffer);
                            singleFrameBuffer = null;
                        }

                        // 保存Close状态信息（如果还没有保存）
                        if (result != null && !string.IsNullOrEmpty(result.CloseStatusDescription) && string.IsNullOrEmpty(wsCloseDesc))
                        {
                            wsCloseDesc = result.CloseStatusDescription;
                        }

                        // 重置接收缓冲区
                        wsReceiveReader.Flush();
                        wsReceiveReader.SetLength(0);
                        wsReceiveReader.Seek(0, SeekOrigin.Begin);
                        wsReceiveReader.Position = 0;
                        // 收缩大尖峰：一次大多帧消息后不再永久占用峰值内存。此时同步解析已完成、异步转发只引用 ctx，
                        // 且 Length 已为 0（SetLength(0) 之后收缩 Capacity 不会抛），因此安全。单帧连接 Capacity 恒为 0，此处 no-op。
                        // Shrink large spikes so one big multi-frame message doesn't retain peak memory for the
                        // connection's lifetime. Safe here: reads of receivedData are done, Length is already 0.
                        if (wsReceiveReader.Capacity > MaxRetainedReceiveCapacity)
                        {
                            wsReceiveReader.Capacity = 0;
                        }
                        // 释放本条消息在全局接收内存预算中预留的字节。
                        // Release this message's reservation from the global receive-memory budget.
                        if (reservedReceiveBytes > 0)
                        {
                            WebSocketReceiveMemoryGovernor.Release(reservedReceiveBytes);
                            reservedReceiveBytes = 0;
                        }

                        // 释放信号量——只有在闸门还没交给任务时才归本轮所有。
                        // Release the gates only while this iteration still owns them.
                        if (!gatesOwnedByTask && endPointSlim != null)
                        {
                            endPointSlim.Release();
                        }
                    }

                } while (!appLifetime.ApplicationStopping.IsCancellationRequested && !connectionClosed);

                // 连接断开处理
                // 如果连接仍然打开，需要关闭它
                if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                {
                    try
                    {
                        // 如果已经收到了Close消息，使用接收到的Close状态
                        // 否则使用默认的关闭状态
                        WebSocketCloseStatus closeStatus = webSocket.CloseStatus ??
                            (webSocket.State == WebSocketState.Aborted ?
                                WebSocketCloseStatus.InternalServerError :
                                WebSocketCloseStatus.NormalClosure);

                        string closeDescription = wsCloseDesc ?? string.Empty;

                        await webSocket.CloseAsync(closeStatus, closeDescription, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_CloseConnectionError + Environment.NewLine + ex.Message));
                    }
                }
                // 如果已经发送了Close消息，等待对方关闭
                else if (webSocket.State == WebSocketState.CloseSent)
                {
                    // 连接正在关闭中，不需要额外操作
                }
            }
            catch (Exception ex)
            {
                logger.LogTrace(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_AbortedReceivingData + ex.Message + Environment.NewLine + ex.StackTrace));
            }
        }


        /// <summary>
        /// MvcChannel forward data
        /// </summary>
        /// <param name="result"></param>
        /// <param name="webSocket"></param>
        /// <param name="context"></param>
        /// <param name="request"></param>
        /// <param name="requestBody"></param>
        /// <param name="requsetTicks"></param>
        /// <returns></returns>
        /// <summary>
        /// Compiled per-connection middleware pipeline (built once, reused for every message).
        /// 编译好的中间件管道（一次构建，每消息复用）。
        /// </summary>
        private WebSocketRequestDelegate _compiledPipeline;

        /// <summary>
        /// Build (once) the middleware pipeline whose terminal dispatches to the endpoint and stores
        /// the result on the context. IHostApplicationLifetime is an app singleton, so capturing the
        /// first connection's instance in the terminal is safe across all connections.
        /// 构建（仅一次）中间件管道：终结点分发到端点并把结果存到上下文。
        /// IHostApplicationLifetime 是应用级单例，终结点捕获首个连接的实例对所有连接都安全。
        /// </summary>
        private WebSocketRequestDelegate GetCompiledPipeline(WebSocketRouteOption options, IHostApplicationLifetime appLifetime)
        {
            var pipeline = _compiledPipeline;
            if (pipeline == null)
            {
                var lifetime = appLifetime;
                var log = logger;
                // Benign race: concurrent first-callers build identical pipelines.
                pipeline = _compiledPipeline = options.BuildPipeline(async ctx =>
                {
                    ctx.Response = await MvcDistributeAsync(ctx.Options, ctx.HttpContext, ctx.WebSocket, ctx.Request, ctx.RequestBody, log, lifetime);
                });
            }
            return pipeline;
        }

        /// <summary>
        /// Run one message through the middleware pipeline, then serialize and send the response
        /// (unless a middleware suppressed it). The endpoint dispatch is the pipeline's terminal.
        /// 让一条消息经过中间件管道，然后序列化并发送响应（除非中间件已抑制）。端点分发是管道的终结点。
        /// </summary>
        private async Task ProcessMessageAsync(WebSocketRequestDelegate pipeline, WebSocketMessageContext ctx)
        {
            try
            {
                await pipeline(ctx).ConfigureAwait(false);

                if (ctx.SuppressResponse || ctx.Response == null)
                {
                    return;
                }

                // 序列化响应（仅一次），直接序列化为 UTF-8 字节，同一份数据用于发送与指标统计
                // Serialize the response exactly once; reuse the bytes for send + metrics.
                var responseBytes = JsonSerializer.SerializeToUtf8Bytes(ctx.Response, ctx.Options.DefaultResponseJsonSerializerOptions);

                await WebSocketManager.SendLocalAsync(responseBytes.AsMemory(), ctx.MessageType, responseBytes.Length <= SendTextBufferSize, CancellationToken.None, timeout: ResponseSendTimeout, sendBufferSize: (uint)SendTextBufferSize, sockets: ctx.WebSocket).ConfigureAwait(false);

                var currentNodeId = Infrastructure.Cluster.GlobalClusterCenter.ClusterContext?.NodeId;
                _metricsCollector?.RecordMessageSent(responseBytes.Length, currentNodeId, ctx.HttpContext.Request.Path);
                Infrastructure.Cluster.GlobalClusterCenter.StatisticsRecorder?.RecordBytesSent(ctx.HttpContext.Connection.Id, responseBytes.Length);
            }
            catch (JsonException ex)
            {
                MvcResponseSchemeException mvcRespEx = new MvcResponseSchemeException(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, ctx.HttpContext.Connection.RemoteIpAddress, ctx.HttpContext.Connection.RemotePort, ctx.HttpContext.Connection.Id, I18nText.MvcForwardSendData_RequestParsingError + ex.Message + Environment.NewLine + ex.StackTrace))
                {
                    Status = 1,
                    RequestTime = ctx.RequestTimeTicks,
                    CompleteTime = DateTime.UtcNow.Ticks,
                    Target = ctx.Request?.Target,
                };
                logger.LogInformation(mvcRespEx, mvcRespEx.Message);
            }
        }

        #region Forward Other

        /// <summary>
        /// MvcChannel forward data
        /// </summary>
        /// <param name="result"></param>
        /// <param name="webSocket"></param>
        /// <param name="context"></param>
        /// <param name="request"></param>
        /// <param name="requsetTicks"></param>
        /// <returns></returns>
        private async Task MvcForwardSendData(WebSocket webSocket, HttpContext context, WebSocketReceiveResult result, MvcRequestScheme request, long requsetTicks, IHostApplicationLifetime appLifetime)
        {
            try
            {
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                //按节点请求转发
                JsonObject requestBody = null;
                string jsonString = JsonSerializer.Serialize(request.Body, webSocketOption.DefaultRequestJsonSerializerOptions);
                JsonNode requestJsonNode = JsonNode.Parse(jsonString);
                if (requestJsonNode != null)
                {
                    requestBody = requestJsonNode.AsObject();
                }
                object invokeResult = await MvcDistributeAsync(webSocketOption, context, webSocket, request, requestBody, logger, appLifetime);

                // 发送结果给客户端
                //string serialJson = JsonSerializer.Serialize(invokeResult, webSocketOption.DefaultResponseJsonSerializerOptions);
                //await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(serialJson)), result.MessageType, result.EndOfMessage, CancellationToken.None);

                await invokeResult.SendLocalAsync(webSocketOption.DefaultResponseJsonSerializerOptions, result.MessageType, timeout: ResponseSendTimeout, encoding: Encoding.UTF8, sendBufferSize: SendTextBufferSize, socket: webSocket).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                MvcResponseSchemeException mvcRespEx = new MvcResponseSchemeException(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcForwardSendData_RequestParsingError + ex.Message + Environment.NewLine + ex.StackTrace))
                {
                    Status = 1,
                    RequestTime = requsetTicks,
                    CompleteTime = DateTime.UtcNow.Ticks,
                };
                logger.LogInformation(mvcRespEx, mvcRespEx.Message);
            }
            catch (Exception)
            {

                throw;
            }


        }

        /// <summary>
        /// MvcChannel forward data
        /// </summary>
        /// <param name="result"></param>
        /// <param name="webSocket"></param>
        /// <param name="context"></param>
        /// <param name="json"></param>
        /// <param name="requsetTicks"></param>
        /// <returns></returns>
        private async Task MvcForwardSendData(WebSocket webSocket, HttpContext context, WebSocketReceiveResult result, StringBuilder json, long requsetTicks, IHostApplicationLifetime appLifetime)
        {
            try
            {
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                MvcRequestScheme request = JsonSerializer.Deserialize<MvcRequestScheme>(json.ToString(), webSocketOption.DefaultRequestJsonSerializerOptions);
                if (request == null)
                {
                    logger.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcForwardSendData_RequestBodyFormatError + json));
                    return;
                }

                await MvcForwardSendData(webSocket, context, result, request, requsetTicks, appLifetime).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                MvcResponseSchemeException mvcRespEx = new MvcResponseSchemeException(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcForwardSendData_RequestParsingError + ex.Message + Environment.NewLine + ex.StackTrace))
                {
                    Status = 1,
                    RequestTime = requsetTicks,
                    CompleteTime = DateTime.UtcNow.Ticks,
                };
                logger.LogInformation(mvcRespEx, mvcRespEx.Message);
            }
            catch (Exception)
            {

                throw;
            }


        }
        #endregion

        /// <summary>
        /// Forward request to endpoint method
        /// </summary>
        /// <param name="webSocketOptions"></param>
        /// <param name="context"></param>
        /// <param name="webSocket"></param>
        /// <param name="request"></param>
        /// <param name="requestBody"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public static async Task<MvcResponseScheme> MvcDistributeAsync(WebSocketRouteOption webSocketOptions, HttpContext context, WebSocket webSocket, MvcRequestScheme request, JsonObject requestBody, ILogger<WebSocketRouteMiddleware> logger, IHostApplicationLifetime appLifetime)
        {
            long requestTime = DateTime.UtcNow.Ticks;
            // 终结点表为忽略大小写字典，无需每请求 ToLower 分配
            // Endpoint tables use case-insensitive comparers, no per-request ToLower allocation needed
            string requestPath = request.Target;
            IServiceScope serviceScope = null;
            if (string.IsNullOrEmpty(requestPath))
            {
                goto NotFound;
            }
            try
            {
                // 从键值对中获取对应的执行函数
                webSocketOptions.WatchAssemblyContext.WatchMethods.TryGetValue(requestPath, out MethodInfo method);

                if (method == null)
                {
                    goto NotFound;
                }
                // O(1) 字典查找，替代对 WatchEndPoint 的每请求线性扫描
                // O(1) dictionary lookup instead of a per-request linear scan over WatchEndPoint
                Type targetClass = webSocketOptions.WatchAssemblyContext.GetEndpointClass(requestPath);
                if (targetClass == null)
                {
                    //找不到访问目标
                    goto NotFound;
                }

                #region 注入Socket的HttpContext和WebSocket客户端
                webSocketOptions.WatchAssemblyContext.MaxConstructorParameters.TryGetValue(targetClass, out ConstructorParameter constructorParameter);

                int ctorParamCount = constructorParameter.ParameterInfos?.Length ?? 0;
                object[] instanceParmas = ctorParamCount == 0 ? Array.Empty<object>() : new object[ctorParamCount];
                // 从Scope DI容器提取目标类构造函数所需的对象。
                // Scope 容器可正确解析所有生命周期（单例来自根容器），
                // 无需再对 IServiceCollection 做每参数 O(n) 的 ServiceDescriptor 扫描。
                // Resolve constructor dependencies from the scoped provider. It handles every
                // lifetime correctly (singletons come from the root), eliminating the old
                // per-parameter O(n) scan of the IServiceCollection.
                var serviceScopeFactory = _cachedScopeFactory ??= WebSocketRouteOption.ApplicationServices.GetService<IServiceScopeFactory>();
                serviceScope = serviceScopeFactory.CreateScope();
                var scopeIocProvider = serviceScope.ServiceProvider;
                for (int i = 0; i < ctorParamCount; i++)
                {
                    instanceParmas[i] = scopeIocProvider.GetService(constructorParameter.ParameterInfos[i].ParameterType);
                }

                // 用**上面刚查出来的那个** ConstructorInfo 直接构造，而不是把类型和参数交给
                // Activator 让它再挑一次重载。两个理由，一个省一个对：
                //
                // 省：`Activator.CreateInstance(Type, object[])` 每次都要跑一遍绑定器（挑重载、
                // 校验参数），实测每条消息 **344 字节**；同一个 ctor 直接 Invoke 是 **40 字节**
                // （同一线程分配量口径，见 Tests/MessageAllocationTests）。分发路径上每条消息 300 字节，
                // 乘以十万条连接的消息量就是实打实的堆压力——而这一段的目标就是把它压下去。
                //
                // 对：instanceParmas 是**按 constructorParameter.ParameterInfos 逐个解析出来的**，
                // 也就是说它只对那一个 ctor 成立。交给 Activator 之后由绑定器重新挑重载，
                // 挑中另一个同参数个数的重载并不违反它的契约——而那时参数的含义已经错位了。
                //
                // Construct through the ConstructorInfo just looked up rather than letting Activator
                // re-select an overload: cheaper (344 B/message versus 40 B, measured) and stricter,
                // because instanceParmas was resolved against exactly that constructor's parameters.
                var ctor = constructorParameter.ConstructorInfo;
                object inst = ctor != null
                    ? ctor.Invoke(instanceParmas)
                    : Activator.CreateInstance(targetClass, instanceParmas);

                // 使用注入器工厂注入 HttpContext 和 WebSocket（支持源代码生成和反射两种方式）
                // **兜底也要缓存，而不是每条消息新建一个工厂。**
                // 工厂本身很便宜，贵的是它里面那份缓存：GetOrCreateInjector 第一次会为这个类型
                // 构建（并缓存）注入器，而每条消息换一个新工厂就等于每条消息重建一次。
                // 实测（Tests/MessageAllocationTests）：走兜底时一条消息分配 **113,156 字节**，
                // 而工厂被复用时是三位数——**同一段代码，两个数量级**。
                // 正常路径由 ConnectionEntry 在建连时把 InjectorFactory 填好，所以这条兜底只在
                // 有人绕过 ConnectionEntry 直接调 MvcDistributeAsync 时才会走到——
                // 而那时它安静地把每条消息变贵一百倍，没有任何信号。
                //
                // The fallback has to be cached too. The factory itself is cheap; what is expensive is
                // the per-type cache inside it, which a fresh factory per message rebuilds every time.
                // Measured at 113,156 bytes per message on the fallback path versus three digits when
                // the factory is reused — same code, two orders of magnitude, and no signal at all.
                var injectorFactory = webSocketOptions.InjectorFactory
                    ?? (webSocketOptions.InjectorFactory = new EndpointInjectorFactory(webSocketOptions));
                var injector = injectorFactory.GetOrCreateInjector(targetClass);
                injector.Inject(inst, context, webSocket);
                #endregion

                MvcResponseScheme mvcResponse = new MvcResponseScheme() { Status = 0, RequestTime = requestTime };
                #region 注入调用方法参数
                webSocketOptions.WatchAssemblyContext.MethodParameters.TryGetValue(method, out ParameterInfo[] methodParam);

                object[] args = Array.Empty<object>();
                object invokeResult = default;

                // A CancellationToken is supplied by the connection, never by the caller, so it is
                // not something the request body can bind. Counting it as a bindable parameter is
                // what made Handler(TRequest req, CancellationToken ct) fall out of whole-body
                // binding: the dispatcher took the by-name path, found no "req" property in the
                // body, and passed null — so every such endpoint failed on every call. The
                // streaming dispatcher (WebSocketStreamInvoker) already binds CancellationToken
                // from the connection; this brings the MVC path in line with it.
                // CancellationToken 由连接提供而非调用方传入，不参与请求体绑定。此前它被算作可绑定形参，
                // 导致 Handler(TRequest req, CancellationToken ct) 退出"整体绑定"、req 恒为 null。
                // 流式分发器早已按类型注入连接令牌，这里与之对齐。
                int bindableParamCount = 0;
                int firstBindableParam = -1;
                for (int i = 0; i < methodParam.Length; i++)
                {
                    if (methodParam[i].ParameterType == typeof(CancellationToken))
                    {
                        continue;
                    }

                    bindableParamCount++;
                    if (firstBindableParam < 0)
                    {
                        firstBindableParam = i;
                    }
                }

                if (requestBody == null || requestBody.Count <= 0)
                {
                    // 如果目标是有参方法，设置默认值
                    if (methodParam.Length > 0)
                    {
                        args = new object[methodParam.LongLength];

                        // 为每个参数设置其类型的默认值
                        for (int i = 0; i < methodParam.Length; i++)
                        {
                            ParameterInfo item = methodParam[i];
                            if (item.ParameterType == typeof(CancellationToken))
                            {
                                args[i] = context.RequestAborted;
                                continue;
                            }
                            if (item.HasDefaultValue)
                            {
                                args[i] = item.DefaultValue;
                                continue;
                            }
                            // 如果参数类型是值类型，则使用类型的零值
                            if (item.ParameterType.IsValueType)
                            {
                                args[i] = Activator.CreateInstance(item.ParameterType);
                            }
                            else
                            {
                                // 如果参数类型是引用类型，则使用 null
                                args[i] = null;
                            }
                        }
                    }
                }
                else
                {
                    IDictionary<string, JsonNode> requestBodyDict = requestBody;
                    // 有参方法
                    //object[] args = new object[methodParam.Length];
                    args = new object[methodParam.LongLength];
                    // 如果目标方法只有1个可绑定参数并且是对象或者接口（CancellationToken 不计入）
                    // Whole-body binding when exactly one parameter can come from the body.
                    if (bindableParamCount == 1
                        && (methodParam[firstBindableParam].ParameterType.IsClass
                            || methodParam[firstBindableParam].ParameterType.IsInterface))
                    {
                        // Any CancellationToken alongside it still gets the connection's token.
                        for (int i = 0; i < methodParam.Length; i++)
                        {
                            if (methodParam[i].ParameterType == typeof(CancellationToken))
                            {
                                args[i] = context.RequestAborted;
                            }
                        }

                        int targetBindIndex = firstBindableParam;
                        ParameterInfo targetBindParam = methodParam[targetBindIndex];
                        // 先是直接按形参参数名提取，从Json提取不到则进行参数展开
                        bool hasVal = requestBody.TryGetPropertyValue(targetBindParam.Name, out JsonNode jProp);
                        if (!hasVal)
                        {
                            // 忽略大小写再提取一次（与多参数路径保持一致）
                            // Case-insensitive retry, consistent with the multi-parameter path
                            jProp = requestBodyDict.FirstOrDefault(x => x.Key.Equals(targetBindParam.Name, StringComparison.OrdinalIgnoreCase)).Value;
                            hasVal = jProp != null;
                        }
                        if (hasVal)
                        {
                            args[targetBindIndex] = targetBindParam.ParameterType.ConvertTo(jProp);
                        }
                        else
                        {
                            PropertyInfo[] targetProp = targetBindParam.ParameterType.GetProperties();

                            object targetPropInst = Activator.CreateInstance(targetBindParam.ParameterType);
                            foreach (var propInfo in targetProp)
                            {
                                // 按参数名提取JsonNode
                                hasVal = requestBody.TryGetPropertyValue(propInfo.Name, out jProp);
                                if (hasVal)
                                {
                                    propInfo.SetValue(targetPropInst, propInfo.PropertyType.ConvertTo(jProp));
                                }
                                else
                                {
                                    // 忽略大小写再提取一次
                                    jProp = requestBodyDict.FirstOrDefault(x => x.Key.Equals(propInfo.Name, StringComparison.OrdinalIgnoreCase)).Value;

                                    if (jProp == null) continue;

                                    propInfo.SetValue(targetPropInst, propInfo.PropertyType.ConvertTo(jProp));
                                }
                            }
                            args[targetBindIndex] = targetPropInst;
                        }

                    }
                    else
                    {
                        for (int i = 0; i < methodParam.Length; i++)
                        {
                            ParameterInfo item = methodParam[i];

                            // Supplied by the connection, not by the body — and looking for a JSON
                            // property named "ct" would only ever find nothing.
                            // 由连接提供，而不是从请求体里按名字找。
                            if (item.ParameterType == typeof(CancellationToken))
                            {
                                args[i] = context.RequestAborted;
                                continue;
                            }

                            // 检测方法中的参数是否是C#定义的基本类型
                            object parmVal = null;
                            try
                            {
                                // 按参数名提取JsonNode
                                bool hasVal = requestBody.TryGetPropertyValue(item.Name, out JsonNode jProp);
                                if (hasVal)
                                {
                                    parmVal = item.ParameterType.ConvertTo(jProp);
                                }
                                else
                                {
                                    jProp = requestBodyDict.FirstOrDefault(x => x.Key.Equals(item.Name, StringComparison.OrdinalIgnoreCase)).Value;

                                    if (jProp == null) continue;

                                    parmVal = item.ParameterType.ConvertTo(jProp);
                                }
                            }
                            catch (FormatException ex)
                            {
                                // ConvertTo 抛出 类型转换失败
                                logger.LogTrace(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, string.Concat(requestPath, ".", item.Name, I18nText.MvcForwardSendData_RequestBodyParameterFormatError, ex.Message, Environment.NewLine, ex.StackTrace)));
                            }
                            args[i] = parmVal;
                        }
                    }

                    //invokeResult = method.Invoke(inst, methodParm);

                    #region 套娃
                    // 异步调用目标方法 
                    //Task<object> invoke = new Task<object>(() =>
                    //{
                    //    object[] methodParm = new object[methodParam.Length];
                    //    for (int i = 0; i < methodParam.Length; i++)
                    //    {
                    //        ParameterInfo item = methodParam[i];

                    //        // 检测方法中的参数是否是C#定义的基本类型
                    //        object parmVal = null;
                    //        try
                    //        {
                    //            // 按参数名提取JsonNode
                    //            bool hasVal = requestBody.TryGetPropertyValue(item.Name, out JsonNode JProp);
                    //            if (hasVal)
                    //            {
                    //                parmVal = item.ParameterType.ConvertTo(JProp);
                    //            }
                    //            else
                    //            {
                    //                continue;
                    //            }
                    //        }
                    //        //catch (JsonException ex)
                    //        //{
                    //        //    // 反序列化失败
                    //        //    logger.LogTrace($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> {requestPath} An exception occurred while operating the request data JSON\r\n{ex.Message}\r\n{ex.StackTrace}");
                    //        //}
                    //        catch (FormatException ex)
                    //        {
                    //            // ConvertTo 抛出 类型转换失败
                    //            logger.LogTrace(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, $"{requestPath}.{item.Name}" + I18nText.MvcForwardSendData_RequestBodyParameterFormatError + ex.Message + Environment.NewLine + ex.StackTrace));
                    //        }
                    //        methodParm[i] = parmVal;
                    //    }

                    //    return method.Invoke(inst, methodParm);
                    //    invokeResult = method.Invoke(inst, methodParm);
                    //});
                    //invoke.Start();

                    //invokeResult = await invoke;
                    #endregion
                }

                // 使用lifetime实现直接结束执行/等待执行完成后再结束
                appLifetime.ApplicationStopping.ThrowIfCancellationRequested();

                // 使用方法调用器工厂调用目标方法（支持源代码生成和反射两种方式）
                // 同上：贵的是工厂里那份按 MethodInfo 缓存的调用器，每条消息换新工厂就等于每条重建一次。
                // Same as the injector factory above: the per-method invoker cache is the expensive part.
                var methodInvokerFactory = webSocketOptions.MethodInvokerFactory
                    ?? (webSocketOptions.MethodInvokerFactory = new MethodInvokerFactory());
                var methodInvoker = methodInvokerFactory.GetOrCreateInvoker(method);
                invokeResult = methodInvoker.Invoke(inst, args);

                // Async api support
                if (invokeResult is Task task)
                {
                    // 这条等待要么等端点跑完，要么等进程开始停机。
                    //
                    // **不能直接把 ApplicationStopping 交给 Task.Delay。** Task.Delay(Infinite, token) 会在那个
                    // token 上注册一条回调，而 WhenAny 先完成**不会**把它解绑——ApplicationStopping 的 CTS 活到
                    // 进程结束，于是每处理一条消息就在它上面永久多留一个 CallbackNode + DelayPromise。
                    // 实测（gcdump，20,000 条连接跑四分钟）：CallbackNode 379,969 个、
                    // DelayPromiseWithCancellation 339,862 个，合计约 60 MB，而且**只随处理过的消息数增长，
                    // 连接关掉也不还**。这不是「每条连接贵一点」，是一条随消息量单调上涨的泄漏。
                    //
                    // Task.Delay(Infinite, token) registers a callback on that token, and WhenAny completing on
                    // the other task does not unregister it. ApplicationStopping's CTS lives as long as the
                    // process, so every message handled leaves one CallbackNode + DelayPromise on it forever —
                    // measured at 380k / 340k live nodes (~60 MB) after four minutes at 20k connections, and it
                    // grows with messages handled, not with connections held.
                    //
                    // 用一个链接 CTS 代替：等待一结束就取消它，注册随之解绑；Dispose 再把它自己从
                    // ApplicationStopping 上摘掉。停机语义不变——父 token 一取消，链接 token 同时取消。
                    using var stopWaiter = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.ApplicationStopping);
                    await Task.WhenAny(task, Task.Delay(Timeout.Infinite, stopWaiter.Token));
                    stopWaiter.Cancel();

                    if (task.IsCanceled || task.IsFaulted)
                    {
                        await task;
                    }

                    if (task.Exception != null)
                    {
                        throw task.Exception;
                    }

                    if (method.ReturnType == typeof(Task))
                    {
                        invokeResult = null;
                    }
                    else
                    {
                        Func<Task, object> taskResultGetter = null;
                        webSocketOptions.WatchAssemblyContext.MethodTaskResultGetters?.TryGetValue(method, out taskResultGetter);
                        invokeResult = taskResultGetter != null
                            ? taskResultGetter(task)
                            : null;
                    }
                }


                #endregion


                mvcResponse.Id = request.Id;
                mvcResponse.Target = request.Target;
                mvcResponse.Body = invokeResult;
                mvcResponse.CompleteTime = DateTime.UtcNow.Ticks;

                return mvcResponse;
            }
            catch (Exception ex)
            {
                MvcResponseScheme resp = new MvcResponseScheme() { Id = request.Id, Status = 1, Target = request.Target, RequestTime = requestTime, CompleteTime = DateTime.UtcNow.Ticks };

                if (ex is AggregateException aggEx && aggEx.InnerException != null)
                {
                    ex = aggEx.InnerException;
                }
                // 反射调用的同步异常被 TargetInvocationException 包裹，剥掉以暴露原始异常
                // Synchronous endpoint exceptions surface wrapped in TargetInvocationException
                // via reflection invoke — unwrap so callers see the original exception
                if (ex is TargetInvocationException tiEx && tiEx.InnerException != null)
                {
                    ex = tiEx.InnerException;
                }

                // The exception detail goes to the log, not to the socket.
                //
                // This used to put `ex.Message` and the full stack trace into resp.Msg, and resp is what
                // goes back to the caller: any client that could make an endpoint throw got assembly
                // names, file paths, internal type names and framework versions out of a server it had
                // only just connected to. A handler registered on ExceptionEvent could overwrite Msg —
                // im-cloud's does, and its comment says why — but a default that leaks unless the host
                // knows to override it is a default that leaks.
                //
                // Msg still names the target, which the client supplied and already knows, so a
                // developer reading a response can still tell *which* endpoint failed. Everything that
                // identifies the server stays on this side of the wire. Whoever wants the detail on the
                // wire can put it back in an ExceptionEvent handler — that hook receives `ex` itself.
                //
                // 异常细节进日志，不进 socket。
                // 这里原本把 ex.Message 和完整堆栈写进 resp.Msg，而 resp 正是回给调用方的东西：
                // 任何能让端点抛异常的客户端，都能从一台它刚连上的服务器拿到程序集名、文件路径、
                // 内部类型名和框架版本。注册在 ExceptionEvent 上的处理器可以覆盖 Msg——
                // im-cloud 的就这么做了，注释里写明了理由——但一个「宿主不知道要覆盖就会泄」的默认值，
                // 就是一个会泄的默认值。
                // Msg 仍然点名 target：那是客户端自己传上来的、它本来就知道，
                // 于是读响应的开发者仍能知道是**哪个**端点失败了。能标识服务器的东西全部留在这一侧。
                // 想让细节上线的人可以在 ExceptionEvent 处理器里放回去——那个钩子拿得到 ex 本身。
                resp.Msg = string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcDistributeAsync_Target + requestPath);
                logger.LogInformation(ex, resp.Msg);

                MvcResponseScheme customResp = await webSocketOptions.OnException(ex, request, resp, context, webSocketOptions, context.Request.Path, logger).ConfigureAwait(false);

                return customResp;
            }
            finally
            {
                // Dispose ioc scope
                serviceScope?.Dispose();
                serviceScope = null;
            }

        NotFound:
            logger.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcDistributeAsync_EndPointNotFound + requestPath));

            return new MvcResponseScheme() { Id = request.Id, Status = 2, Target = request.Target, RequestTime = requestTime, CompleteTime = DateTime.UtcNow.Ticks };
        }

        /// <summary>
        /// 识别到二进制流式上传后接管本条消息：共享接收器解析头部、建 Pipe、边收边喂端点（内存恒定），
        /// 本方法只负责把结果按本通道(JSON)编码回发。
        /// Streaming upload path — the shared receiver parses the header, sets up a Pipe and feeds the endpoint
        /// (constant memory); this method only encodes the result back in this channel's format (JSON).
        /// </summary>
        private async Task MvcStreamForward(WebSocket webSocket, HttpContext context, byte[] buffer, WebSocketReceiveResult firstResult, WebSocketRouteOption webSocketOptions, ILogger<WebSocketRouteMiddleware> logger, CancellationToken connectionToken)
        {
            long requestTime = DateTime.UtcNow.Ticks;
            var outcome = await Infrastructure.StreamDispatch.WebSocketStreamReceiver.ReceiveAndInvokeAsync(webSocket, context, buffer, firstResult, webSocketOptions, logger, connectionToken);
            if (!outcome.Handled || webSocket.State != WebSocketState.Open)
            {
                return;
            }
            var resp = new MvcResponseScheme { Id = outcome.Id, Target = outcome.Target, Status = outcome.Result.Status, Body = outcome.Result.Body, Msg = outcome.Result.Msg, RequestTime = requestTime, CompleteTime = DateTime.UtcNow.Ticks };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(resp, webSocketOptions.DefaultResponseJsonSerializerOptions);
            // 必须走 WebSocketManager：直接 SendAsync 会绕过 per-socket 发送门闩，插进别人正在发的多帧消息中间，
            // 造出一条带结束标志、内容却是别人前半截的消息——正是发送不变式要消灭的东西。
            // Must go through WebSocketManager: a raw SendAsync bypasses the per-socket send gate and can inject
            // itself into someone else's multi-frame message, producing one that carries the end flag while holding
            // another message's opening bytes — exactly what the send invariant exists to prevent.
            await WebSocketManager.SendLocalAsync(
                bytes.AsMemory(), WebSocketMessageType.Text, sendAtOnce: true,
                CancellationToken.None, timeout: null, sockets: webSocket);
        }

        /// <summary>
        /// Client close connection
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocketCloseStatus"></param>
        /// <param name="webSocketOptions"></param>
        /// <param name="logger"></param>
        private async Task MvcChannel_OnDisconnected(HttpContext context, WebSocketCloseStatus? webSocketCloseStatus, WebSocketRouteOption webSocketOptions, ILogger<WebSocketRouteMiddleware> logger)
        {
            // 打印关闭连接信息
            string msg = string.Empty;
            if (webSocketCloseStatus.HasValue)
            {
                switch (webSocketCloseStatus.Value)
                {
                    case WebSocketCloseStatus.Empty:
                        msg = I18nText.WebSocketCloseStatus_Empty;
                        break;
                    case WebSocketCloseStatus.EndpointUnavailable:
                        msg = I18nText.WebSocketCloseStatus_EndpointUnavailable;
                        break;
                    case WebSocketCloseStatus.InternalServerError:
                        msg = I18nText.WebSocketCloseStatus_InternalServerError;
                        break;
                    case WebSocketCloseStatus.InvalidMessageType:
                        msg = I18nText.WebSocketCloseStatus_InvalidMessageType;
                        break;
                    case WebSocketCloseStatus.InvalidPayloadData:
                        msg = I18nText.WebSocketCloseStatus_InvalidPayloadData;
                        break;
                    case WebSocketCloseStatus.MandatoryExtension:
                        msg = I18nText.WebSocketCloseStatus_MandatoryExtension;
                        break;
                    case WebSocketCloseStatus.MessageTooBig:
                        msg = I18nText.WebSocketCloseStatus_MessageTooBig;
                        break;
                    case WebSocketCloseStatus.NormalClosure:
                        msg = I18nText.WebSocketCloseStatus_NormalClosure;
                        break;
                    case WebSocketCloseStatus.PolicyViolation:
                        msg = I18nText.WebSocketCloseStatus_PolicyViolation;
                        break;
                    case WebSocketCloseStatus.ProtocolError:
                        msg = I18nText.WebSocketCloseStatus_ProtocolError;
                        break;
                    default:
                        break;
                }
            }
            else
            {
                msg = I18nText.WebSocketCloseStatus_ConnectionShutdown;
            }

            logger.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, string.Concat(I18nText.OnDisconnected_Disconnected, msg, Environment.NewLine, "Status:", webSocketCloseStatus?.ToString() ?? "NoHandshakeSucceeded")));

            try
            {
                await MvcChannel_OnDisconnected(context, webSocketOptions, context.Request.Path, logger);

                await webSocketOptions.OnDisconnected(context, webSocketOptions, context.Request.Path, logger);
            }
            catch (Exception ex)
            {
                logger.LogInformation(ex, ex.Message);
            }
            finally
            {
                bool wsExists = Clients.ContainsKey(context.Connection.Id);
                if (wsExists)
                {
                    Clients.TryRemove(context.Connection.Id, out var _);

                    // Unregister connection from cluster manager if cluster is enabled
                    // 如果启用了集群，从集群管理器注销连接
                    var clusterManager = Infrastructure.Cluster.GlobalClusterCenter.ClusterManager;
                    if (clusterManager != null)
                    {
                        try
                        {
                            await clusterManager.UnregisterConnectionAsync(context.Connection.Id);
                            logger.LogDebug(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_ClusterManagerUnregistered));
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_ClusterManagerUnregisterFailed));
                        }
                    }
                }

                // Still cleaned up, still wrong, and both on purpose. This handler is shared by every
                // connection on the channel, so disposing here on *one* connection's teardown pulled the
                // semaphore out from under all the others — which is the same confusion that made the
                // field process-wide in the first place. Nothing reads it now, so the only thing this
                // disposes is a value a downstream assigned; it goes away with the field next release.
                // 仍然清理、仍然是错的，两者都是刻意的：handler 由整条通道共用，
                // 在**一条**连接断开时 Dispose 它，等于把信号量从其余所有连接脚下抽走——
                // 正是同一个混淆当初把这个字段做成了进程级。现在没人读它，
                // 这里 Dispose 掉的只可能是下游赋进来的值；它会随字段在下个版本一起消失。
#pragma warning disable CS0618 // deliberately touching the obsolete field, to clean up what a caller may have assigned
                ParallelForwardLimitSlim?.Dispose();
                ParallelForwardLimitSlim = null;
#pragma warning restore CS0618
            }
        }

        /// <summary>
        /// Mvc channel before connection
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocketOptions"></param>
        /// <param name="channel"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public virtual async Task<bool> MvcChannel_OnBeforeConnection(HttpContext context, WebSocketRouteOption webSocketOptions, string channel, ILogger<WebSocketRouteMiddleware> logger)
        {
            // Check access control / 检查访问控制
            if (WebSocketRouteOption.ApplicationServices != null)
            {
                try
                {
                    var accessControlService = WebSocketRouteOption.ApplicationServices.GetService<AccessControlService>();
                    if (accessControlService != null)
                    {
                        var ipAddress = context.Connection.RemoteIpAddress?.ToString();
                        var isAllowed = await accessControlService.IsAllowedAsync(ipAddress);

                        if (!isAllowed)
                        {
                            var policy = WebSocketRouteOption.ApplicationServices.GetService<AccessControlPolicy>();
                            if (policy != null)
                            {
                                switch (policy.DeniedAction)
                                {
                                    case AccessDeniedAction.ReturnForbidden:
                                        context.Response.StatusCode = 403;
                                        await context.Response.WriteAsync(policy.DenialMessage ?? "Access denied");
                                        logger.LogWarning(string.Format(I18nText.ConnectionEntry_AccessDeniedWithMessage, ipAddress, context.Request.Path, policy.DenialMessage ?? string.Empty));
                                        break;
                                    case AccessDeniedAction.ReturnUnauthorized:
                                        context.Response.StatusCode = 401;
                                        await context.Response.WriteAsync(policy.DenialMessage ?? "Unauthorized");
                                        logger.LogWarning(string.Format(I18nText.ConnectionEntry_AccessDeniedWithMessage, ipAddress, context.Request.Path, policy.DenialMessage ?? string.Empty));
                                        break;
                                    case AccessDeniedAction.CloseConnection:
                                    default:
                                        logger.LogWarning(string.Format(I18nText.ConnectionEntry_AccessDeniedWithMessage, ipAddress, context.Request.Path, policy.DenialMessage ?? string.Empty));
                                        break;
                                }
                            }
                            else
                            {
                                logger.LogWarning(string.Format(I18nText.ConnectionEntry_AccessDenied, ipAddress, context.Request.Path));
                            }

                            return false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, I18nText.ConnectionEntry_AccessControlError);
                    // Allow connection on error to avoid blocking legitimate users / 出错时允许连接，避免阻止合法用户
                }
            }

            return await Task.FromResult(true);
        }

        /// <summary>
        /// Mvc channel DisconnectionedEvent entry
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocketOptions"></param>
        /// <param name="channel"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public virtual async Task MvcChannel_OnDisconnected(HttpContext context, WebSocketRouteOption webSocketOptions, string channel, ILogger<WebSocketRouteMiddleware> logger)
        {
            await Task.CompletedTask;
        }


        /// <summary>
        /// Find target from JSON fragment
        /// </summary>
        /// <param name="jsonFragment"></param>
        /// <returns></returns>
        public string FindJsonPropertyValue(ReadOnlySpan<byte> jsonFragment, string PropertyName = IMvcScheme.VAR_TATGET)
        {
            var jsonReader = new Utf8JsonReader(jsonFragment, isFinalBlock: false, state: default);

            try
            {
                while (jsonReader.Read())
                {
                    try
                    {
                        if (jsonReader.TokenType == JsonTokenType.PropertyName)
                        {
                            // 先做零分配的精确匹配；不匹配时仅在长度一致的情况下才分配字符串做忽略大小写比较
                            // Zero-alloc exact match first; only allocate for a case-insensitive
                            // comparison when the raw length matches the target name
                            bool matched = jsonReader.ValueTextEquals(PropertyName);
                            if (!matched && !jsonReader.HasValueSequence && jsonReader.ValueSpan.Length == PropertyName.Length)
                            {
                                matched = string.Equals(jsonReader.GetString(), PropertyName, StringComparison.OrdinalIgnoreCase);
                            }

                            if (matched)
                            {
                                jsonReader.Read();
                                if (jsonReader.TokenType == JsonTokenType.String)
                                {
                                    return jsonReader.GetString();
                                }
                            }
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }

            return null;
        }



    }
}