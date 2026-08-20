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
        /// Associated with the connection, limit the total number of forwarding requests being processed by the connection.
        /// WebSocketRouteOption.MaxParallelForwardLimit
        /// </summary>
        public SemaphoreSlim ParallelForwardLimitSlim = null;

        /// <summary>
        /// After processing a message, the per-connection receive stream keeps at most this capacity;
        /// larger spikes are released so a one-off big multi-frame message doesn't retain peak memory
        /// for the connection's lifetime. Small multi-frame connections keep their modest buffer (no churn).
        /// 处理消息后，每连接接收流最多保留此容量；更大的尖峰会被释放，避免一次性大消息长期占用峰值内存。
        /// </summary>
        private const int MaxRetainedReceiveCapacity = 64 * 1024;

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

            // 配置并行转发上限（初始许可数必须等于上限，否则首个 WaitAsync 将永久阻塞）
            // Initial permit count must equal the limit, otherwise the first WaitAsync blocks forever
            if (ParallelForwardLimitSlim == null && webSocketOptions.MaxConnectionParallelForwardLimit != null)
            {
                ParallelForwardLimitSlim = new SemaphoreSlim((int)webSocketOptions.MaxConnectionParallelForwardLimit, (int)webSocketOptions.MaxConnectionParallelForwardLimit);
            }

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
                // 初始容量 0：单帧消息走快路径、根本不写这个流，因此绝大多数连接不会分配接收缓冲。
                // 多帧消息首次写入时才按需增长；处理后在 finally 里收缩大尖峰（见下）。
                // Zero initial capacity: single-frame messages take the fast path and never write this
                // stream, so the vast majority of connections allocate no receive buffer. Multi-frame
                // messages grow it on first write; large spikes are shrunk in the finally below.
                using MemoryStream wsReceiveReader = new MemoryStream();
                bool connectionClosed = false;
                do
                {
                    long requestTime = DateTime.UtcNow.Ticks;
                    WebSocketReceiveResult result = null;
                    SemaphoreSlim endPointSlim = null;
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
                        // Connection level restrictions
                        if (ParallelForwardLimitSlim != null)
                        {
                            await ParallelForwardLimitSlim.WaitAsync().ConfigureAwait(false);
                        }

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
                                if (!endpointPolicyResolved && (webSocketOption.WatchAssemblyContext != null || bandwidthLimitManager != null))
                                {
                                    ReadOnlySpan<byte> headerSpan = wsReceiveReader.Length > 0
                                        ? wsReceiveReader.GetBuffer().AsSpan(0, (int)wsReceiveReader.Length)
                                        : buffer.AsSpan(0, result?.Count ?? 0);
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

                        Task processTask = ProcessMessageAsync(GetCompiledPipeline(webSocketOption, appLifetime), messageContext);
                        // 是否串行
                        if (webSocketOption.EnableForwardTaskSyncProcessingMode)
                        {
                            await processTask;
                        }
                        else
                        {
                            // 处理 Task 异常，避免未观察到的异常（静态委托 + state 避免闭包分配）。
                            _ = processTask.ContinueWith(static (t, state) =>
                            {
                                ((ILogger)state).LogInformation(t.Exception, I18nText.ConnectionEntry_DisconnectedInternalExceptions);
                            }, logger, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                        }

                    CONTINUE_RECEIVE:;
                    }
                    catch (Exception ex)
                    {
                        logger.LogInformation(ex, ex.Message, Encoding.UTF8.GetString(wsReceiveReader.GetBuffer(), 0, (int)wsReceiveReader.Length));
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

                        // 释放信号量
                        if (ParallelForwardLimitSlim != null)
                        {
                            ParallelForwardLimitSlim.Release();
                        }
                        if (endPointSlim != null)
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

                object inst = Activator.CreateInstance(targetClass, instanceParmas);

                // 使用注入器工厂注入 HttpContext 和 WebSocket（支持源代码生成和反射两种方式）
                var injectorFactory = webSocketOptions.InjectorFactory ?? new EndpointInjectorFactory(webSocketOptions);
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
                var methodInvokerFactory = webSocketOptions.MethodInvokerFactory ?? new MethodInvokerFactory();
                var methodInvoker = methodInvokerFactory.GetOrCreateInvoker(method);
                invokeResult = methodInvoker.Invoke(inst, args);

                // Async api support
                if (invokeResult is Task task)
                {
                    await Task.WhenAny(task, Task.Delay(Timeout.Infinite, appLifetime.ApplicationStopping));

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

                resp.Msg = string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcDistributeAsync_Target + requestPath + Environment.NewLine + ex.Message + Environment.NewLine + ex.StackTrace);
                logger.LogInformation(resp.Msg);

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
            await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
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

                ParallelForwardLimitSlim?.Dispose();
                ParallelForwardLimitSlim = null;
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