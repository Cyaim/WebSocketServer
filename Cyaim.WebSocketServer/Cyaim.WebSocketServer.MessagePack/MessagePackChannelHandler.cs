using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.AccessControl;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Infrastructure.Injectors;
using Cyaim.WebSocketServer.Infrastructure.Metrics;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MessagePack;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.MessagePack
{
    /// <summary>
    /// Provide MessagePack binary protocol forwarding handler
    /// </summary>
    public class MessagePackChannelHandler : IWebSocketHandler
    {
        private ILogger<WebSocketRouteMiddleware> logger;
        private WebSocketRouteOption webSocketOption;
        private BandwidthLimitManager bandwidthLimitManager;
        private WebSocketMetricsCollector _metricsCollector;

        /// <summary>
        /// Get instance
        /// </summary>
        /// <param name="receiveBufferSize"></param>
        /// <param name="sendBufferSize"></param>
        public MessagePackChannelHandler(int receiveBufferSize = 4 * 1024, int sendBufferSize = 4 * 1024)
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
            Describe = "Provide MessagePack binary protocol forwarding handler",
            CanHandleBinary = true,
            CanHandleText = false
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
        /// Connected clients by messagepack channel
        /// </summary>
        public static ConcurrentDictionary<string, WebSocket> Clients { get; set; } = new ConcurrentDictionary<string, WebSocket>();

        /// <summary>
        /// Associated with the connection, limit the total number of forwarding requests being processed by the connection.
        /// WebSocketRouteOption.MaxParallelForwardLimit
        /// </summary>
        public SemaphoreSlim ParallelForwardLimitSlim = null;

        /// <summary>
        /// Max capacity retained by the per-connection receive stream after a message; larger spikes are released.
        /// 处理后每连接接收流最多保留的容量；更大的尖峰会被释放。
        /// </summary>
        private const int MaxRetainedReceiveCapacity = 64 * 1024;

        /// <summary>
        /// MessagePack Channel entry
        /// </summary>
        /// <param name="context"></param>
        /// <param name="logger"></param>
        /// <param name="webSocketOptions"></param>
        /// <returns></returns>
        public async Task ConnectionEntry(HttpContext context, ILogger<WebSocketRouteMiddleware> logger, WebSocketRouteOption webSocketOptions)
        {
            this.logger = logger;
            webSocketOption = webSocketOptions;

            // 注意：InjectorFactory 和 MethodInvokerFactory 的初始化在 MvcDistributeAsync 中处理
            // 该方法会优先使用 webSocketOptions 中的工厂（如果已初始化），否则创建新实例
            // 由于这些属性是 internal，扩展项目无法直接访问，但 MvcDistributeAsync 内部会处理

            // 获取指标收集器
            if (WebSocketRouteOption.ApplicationServices != null)
            {
                _metricsCollector = WebSocketRouteOption.ApplicationServices.GetService<WebSocketMetricsCollector>();
            }

            // 初始化带宽限速管理器
            var policy = webSocketOptions.BandwidthLimitPolicy;
            if (policy == null && WebSocketRouteOption.ApplicationServices != null)
            {
                try
                {
                    var options = WebSocketRouteOption.ApplicationServices.GetService<IOptions<Infrastructure.Configures.BandwidthLimitPolicy>>();
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

            // 配置并行转发上限
            if (ParallelForwardLimitSlim == null && webSocketOptions.MaxConnectionParallelForwardLimit != null)
            {
                ParallelForwardLimitSlim = new SemaphoreSlim(0, (int)webSocketOptions.MaxConnectionParallelForwardLimit);
            }

            WebSocketCloseStatus? webSocketCloseStatus = null;
            try
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    // Event instructions whether connection
                    var ifThisContinue = await MessagePackChannel_OnBeforeConnection(context, webSocketOptions, context.Request.Path, logger);
                    if (!ifThisContinue)
                    {
                        return;
                    }
                    var ifContinue = await webSocketOptions.OnBeforeConnection(context, webSocketOptions, context.Request.Path, logger);
                    if (!ifContinue)
                    {
                        return;
                    }

                    // 配置最大连接数
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
                            logger.LogDebug(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_ConnectionAlreadyExists));
                            return;
                        }

                        // 记录连接建立指标
                        var currentNodeId = Infrastructure.Cluster.GlobalClusterCenter.ClusterContext?.NodeId;
                        _metricsCollector?.RecordConnectionEstablished(currentNodeId, context.Request.Path);

                        // Register connection with cluster manager if cluster is enabled
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
                                logger.LogDebug($"Registered connection {context.Connection.Id} with cluster manager");
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, $"Failed to register connection {context.Connection.Id} with cluster manager");
                            }
                        }

                        IHostApplicationLifetime appLifetime = WebSocketRouteOption.ApplicationServices.GetService<IHostApplicationLifetime>();
                        if (appLifetime == null)
                        {
                            throw new InvalidOperationException("IHostApplicationLifetime service is not available");
                        }

                        await MessagePackForward(context, webSocket, webSocketOptions, appLifetime);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_DisconnectedInternalExceptions + ex.Message + Environment.NewLine + ex.StackTrace));
                    }
                    finally
                    {
                        if (webSocket.CloseStatus == null && webSocket.State == WebSocketState.Open)
                        {
                            await webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, string.Empty, CancellationToken.None).ConfigureAwait(false);
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

                await MessagePackChannel_OnDisconnected(context, webSocketCloseStatus, webSocketOptions, logger);
            }
        }

        /// <summary>
        /// MessagePack channel before connection
        /// </summary>
        public virtual async Task<bool> MessagePackChannel_OnBeforeConnection(HttpContext context, WebSocketRouteOption webSocketOptions, string channel, ILogger<WebSocketRouteMiddleware> logger)
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
                            var accessPolicy = WebSocketRouteOption.ApplicationServices.GetService<AccessControlPolicy>();
                            if (accessPolicy != null)
                            {
                                switch (accessPolicy.DeniedAction)
                                {
                                    case AccessDeniedAction.ReturnForbidden:
                                        context.Response.StatusCode = 403;
                                        await context.Response.WriteAsync(accessPolicy.DenialMessage ?? "Access denied");
                                        logger.LogWarning($"Access denied for IP {ipAddress} from {context.Request.Path}: {accessPolicy.DenialMessage}");
                                        break;
                                    case AccessDeniedAction.ReturnUnauthorized:
                                        context.Response.StatusCode = 401;
                                        await context.Response.WriteAsync(accessPolicy.DenialMessage ?? "Unauthorized");
                                        logger.LogWarning($"Access denied for IP {ipAddress} from {context.Request.Path}: {accessPolicy.DenialMessage}");
                                        break;
                                    case AccessDeniedAction.CloseConnection:
                                    default:
                                        logger.LogWarning($"Access denied for IP {ipAddress} from {context.Request.Path}: {accessPolicy.DenialMessage}");
                                        break;
                                }
                            }
                            else
                            {
                                logger.LogWarning($"Access denied for IP {ipAddress} from {context.Request.Path}");
                            }

                            return false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error checking access control");
                }
            }

            return await Task.FromResult(true);
        }

        /// <summary>
        /// MessagePack channel DisconnectionedEvent entry
        /// </summary>
        public virtual async Task MessagePackChannel_OnDisconnected(HttpContext context, WebSocketRouteOption webSocketOptions, string channel, ILogger<WebSocketRouteMiddleware> logger)
        {
            await Task.CompletedTask;
        }

        /// <summary>
        /// MessagePack channel DisconnectionedEvent entry
        /// </summary>
        private async Task MessagePackChannel_OnDisconnected(HttpContext context, WebSocketCloseStatus? webSocketCloseStatus, WebSocketRouteOption webSocketOptions, ILogger<WebSocketRouteMiddleware> logger)
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

            logger.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.OnDisconnected_Disconnected + msg + Environment.NewLine + $"Status:{webSocketCloseStatus.ToString() ?? "NoHandshakeSucceeded"}"));

            try
            {
                await MessagePackChannel_OnDisconnected(context, webSocketOptions, context.Request.Path, logger);

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
                    var clusterManager = Infrastructure.Cluster.GlobalClusterCenter.ClusterManager;
                    if (clusterManager != null)
                    {
                        try
                        {
                            await clusterManager.UnregisterConnectionAsync(context.Connection.Id);
                            logger.LogDebug($"Unregistered connection {context.Connection.Id} from cluster manager");
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, $"Failed to unregister connection {context.Connection.Id} from cluster manager");
                        }
                    }
                }

                ParallelForwardLimitSlim?.Dispose();
                ParallelForwardLimitSlim = null;
            }
        }


        /// <summary>
        /// Forward by WebSocket transfer type using MessagePack
        /// </summary>
        private async Task MessagePackForward(HttpContext context, WebSocket webSocket, WebSocketRouteOption webSocketOptions, IHostApplicationLifetime appLifetime)
        {
            try
            {
                string wsCloseDesc = string.Empty;
                // 应用全局接收内存预算（进程级、幂等）。
                Cyaim.WebSocketServer.Infrastructure.WebSocketReceiveMemoryGovernor.MaxBytes = webSocketOption.MaxTotalReceiveBufferBytes ?? 0;
                // 初始容量 0：单帧走快路径不写此流，绝大多数连接不分配接收缓冲；多帧首次写入才增长。
                // Zero initial capacity: single-frame fast path never writes it, so most connections allocate none.
                using MemoryStream wsReceiveReader = new MemoryStream();
                do
                {
                    long requestTime = DateTime.Now.Ticks;
                    WebSocketReceiveResult result = null;
                    SemaphoreSlim endPointSlim = null;
                    // 单帧快路径：整条消息一次 ReceiveAsync 收全时借用的租用缓冲区（外层 finally 归还）。null=多帧重组。
                    // Single-frame fast path: rented buffer borrowed when the whole message arrived in one
                    // ReceiveAsync (returned in the outer finally). Null => multi-frame reassembly path.
                    byte[] singleFrameBuffer = null;
                    int singleFrameCount = 0;
                    // 本条消息在全局接收内存预算中已预留的字节（多帧累计），在本迭代 finally 中释放。
                    long reservedReceiveBytes = 0;
                    // 方案A：解析出 target 后把生效上限从全局默认切到端点 MaxBytes（0=沿用全局）。
                    long effectiveReceiveLimit = webSocketOption.MaxRequestReceiveDataLimit ?? 0;
                    bool endpointPolicyResolved = false;
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
                                break;
                            }
                            else
                            {
                                await Task.Delay(300).ConfigureAwait(false);
                                continue;
                            }
                        }

                        #region 接收数据
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReceiveBinaryBufferSize);
                        while ((result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None)).Count > 0)
                        {
                            try
                            {
                                // 请求大小限制：Length > (long?)null 恒为 false，未配置时该检查被静默禁用。
                                // 用模式匹配显式判定，并按累计字节(已写入+本帧)判断，超限即拒绝该多帧消息。
                                // Enforce only when a limit is present (null == explicitly unlimited); check the
                                // accumulated length so an over-limit message is rejected before further growth.
                                long accumulatedLen = wsReceiveReader.Length + (result?.Count ?? 0);

                                // 方案A：从头部解析 target（MessagePack 需首帧收齐），命中端点策略则切换生效上限。
                                // 方案A: resolve target (MessagePack needs the first frame complete) and switch the cap.
                                if (!endpointPolicyResolved && webSocketOption.WatchAssemblyContext != null)
                                {
                                    ReadOnlySpan<byte> headerSpan = wsReceiveReader.Length > 0
                                        ? wsReceiveReader.GetBuffer().AsSpan(0, (int)wsReceiveReader.Length)
                                        : buffer.AsSpan(0, result?.Count ?? 0);
                                    string tgt = null;
                                    try { tgt = FindTargetFromMessagePack(headerSpan); } catch { /* header not complete yet */ }
                                    if (tgt != null)
                                    {
                                        if (webSocketOption.WatchAssemblyContext.TryGetEndpointPolicy(tgt, out var pol) && pol.MaxBytes > 0)
                                        {
                                            effectiveReceiveLimit = pol.MaxBytes;
                                        }
                                        endpointPolicyResolved = true;
                                    }
                                }

                                if (effectiveReceiveLimit > 0 && accumulatedLen > effectiveReceiveLimit)
                                {
                                    logger.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_RequestSizeMaximumLimit));
                                    // 有界排空该超限消息剩余帧；过大/无界则 1009+Abort。 / Bounded-drain; 1009+Abort if grossly oversized.
                                    await Cyaim.WebSocketServer.Infrastructure.WebSocketReceiveMemoryGovernor.DrainOversizedAsync(webSocket, buffer, result);
                                    goto CONTINUE_RECEIVE;
                                }

                                // 应用带宽限速策略
                                if (bandwidthLimitManager != null && result.Count > 0)
                                {
                                    string endPoint = null;
                                    if (wsReceiveReader.Length > 0)
                                    {
                                        try
                                        {
                                            endPoint = FindTargetFromMessagePack(wsReceiveReader.GetBuffer());
                                        }
                                        catch
                                        {
                                            // 如果无法解析，忽略端点信息
                                        }
                                    }

                                    await bandwidthLimitManager.WaitForBandwidthAsync(
                                        context.Request.Path,
                                        context.Connection.Id,
                                        endPoint,
                                        result.Count,
                                        context.Connection.RemoteIpAddress?.ToString(),
                                        CancellationToken.None);
                                }

                                // 记录消息接收指标
                                var currentNodeId = Infrastructure.Cluster.GlobalClusterCenter.ClusterContext?.NodeId;
                                _metricsCollector?.RecordMessageReceived(result.Count, currentNodeId, context.Request.Path);

                                // 记录统计信息
                                Infrastructure.Cluster.GlobalClusterCenter.StatisticsRecorder?.RecordBytesReceived(context.Connection.Id, result.Count);

                                // 单帧快路径：首帧即 EndOfMessage（wsReceiveReader 尚为空）时借用租用缓冲区解析，跳过整条负载拷贝。
                                // Single-frame fast path: borrow the rented buffer and skip the full-payload copy.
                                if ((result.EndOfMessage || result.CloseStatus.HasValue) && wsReceiveReader.Length == 0)
                                {
                                    singleFrameBuffer = buffer;
                                    singleFrameCount = result.Count;
                                    break;
                                }

                                // 多帧：先向全局接收内存预算预留本帧字节；超预算则拒绝该消息（背压）。
                                if (!Cyaim.WebSocketServer.Infrastructure.WebSocketReceiveMemoryGovernor.TryReserve(result.Count))
                                {
                                    logger.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_RequestSizeMaximumLimit));
                                    // 有界排空被拒消息剩余帧。 / Bounded-drain the rejected message's remaining frames.
                                    await Cyaim.WebSocketServer.Infrastructure.WebSocketReceiveMemoryGovernor.DrainOversizedAsync(webSocket, buffer, result);
                                    goto CONTINUE_RECEIVE;
                                }
                                reservedReceiveBytes += result.Count;

                                // 多帧：写入 MemoryStream 重组
                                await wsReceiveReader.WriteAsync(buffer.AsMemory(0, result.Count));

                                if (result.EndOfMessage || result.CloseStatus.HasValue)
                                {
                                    break;
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
                            }
                            finally
                            {
                                // 单帧快路径已转移缓冲区所有权（外层 finally 归还），此处不归还
                                if (!ReferenceEquals(buffer, singleFrameBuffer))
                                {
                                    ArrayPool<byte>.Shared.Return(buffer);
                                }
                            }
                        }

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        // 单帧快路径未写入 MemoryStream（Length==0），不要把 Capacity 收缩为 0（否则下条多帧消息要重新扩容）。
                        // Single-frame path didn't write the stream; don't shrink its Capacity to 0.
                        if (singleFrameBuffer == null && wsReceiveReader.Capacity > wsReceiveReader.Length)
                        {
                            wsReceiveReader.Capacity = (int)wsReceiveReader.Length;
                        }
                        #endregion

                        if (result == null)
                        {
                            continue;
                        }

                        // 有效数据：单帧快路径取自租用缓冲区（零拷贝，未经 MemoryStream）；多帧取 MemoryStream 缓冲。
                        // Valid data: single-frame reads from the rented buffer (zero-copy); multi-frame from the stream.
                        byte[] msgArray = singleFrameBuffer ?? wsReceiveReader.GetBuffer();
                        int msgLen = singleFrameBuffer != null ? singleFrameCount : (int)wsReceiveReader.Length;

                        // 在接收完数据后，应用端点级别的限速策略
                        string endpoint = null;
                        if (bandwidthLimitManager != null && msgLen > 0)
                        {
                            try
                            {
                                endpoint = FindTargetFromMessagePack(msgArray.AsSpan(0, msgLen));
                                if (!string.IsNullOrEmpty(endpoint))
                                {
                                    await bandwidthLimitManager.WaitForBandwidthAsync(
                                        context.Request.Path,
                                        context.Connection.Id,
                                        endpoint,
                                        msgLen,
                                        context.Connection.RemoteIpAddress?.ToString(),
                                        CancellationToken.None);
                                }
                            }
                            catch
                            {
                                // 如果无法解析端点，忽略
                            }
                        }

                        // EndPoint level restrictions
                        if (webSocketOption.MaxEndPointParallelForwardLimit != null)
                        {
                            if (string.IsNullOrEmpty(endpoint))
                            {
                                endpoint = FindTargetFromMessagePack(msgArray.AsSpan(0, msgLen));
                            }
                            if (endpoint != null && webSocketOption.MaxEndPointParallelForwardLimit.TryGetValue(endpoint, out endPointSlim) && endPointSlim != null)
                            {
                                await endPointSlim.WaitAsync().ConfigureAwait(false);
                            }
                        }

                        // 处理请求的数据 - 使用 MessagePack 反序列化
                        MessagePackRequestScheme requestScheme = null;
                        JsonObject requestBody = null;

                        try
                        {
                            // MessagePack 从偏移 0 读取一个对象，忽略尾部空闲；单帧缓冲区的 slack 与 GetBuffer 的 slack 处理一致。
                            // MessagePack reads one object from offset 0, ignoring trailing slack (same as GetBuffer()).
                            requestScheme = MessagePackSerializer.Deserialize<MessagePackRequestScheme>(msgArray);
                            
                            // 将 MessagePack Body 转换为 JsonObject 以兼容现有的 MvcDistributeAsync
                            if (requestScheme?.Body != null)
                            {
                                // 将对象序列化为 JSON 字符串，然后解析为 JsonObject
                                var jsonString = System.Text.Json.JsonSerializer.Serialize(requestScheme.Body);
                                var jsonNode = System.Text.Json.Nodes.JsonNode.Parse(jsonString);
                                requestBody = jsonNode?.AsObject();
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to deserialize MessagePack request");
                            continue;
                        }

                        // 检查请求是否包含Id属性
                        if (webSocketOption.RequireRequestId && (requestScheme == null || string.IsNullOrWhiteSpace(requestScheme.Id)))
                        {
                            // 创建错误响应
                            var errorResponse = new MessagePackResponseScheme()
                            {
                                Status = 1,
                                RequestTime = requestTime,
                                CompleteTime = DateTime.Now.Ticks,
                                Target = requestScheme?.Target,
                                Id = requestScheme?.Id,
                                Msg = string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcForwardSendData_RequestIdRequired)
                            };

                            // 发送错误响应 - 使用 MessagePack
                            var responseBytes = MessagePackSerializer.Serialize(errorResponse);
                            await webSocket.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Binary, true, CancellationToken.None);

                            logger.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcForwardSendData_RequestIdRequired));

                            // 记录消息发送指标
                            var currentNodeId = Infrastructure.Cluster.GlobalClusterCenter.ClusterContext?.NodeId;
                            _metricsCollector?.RecordMessageSent(responseBytes.Length, currentNodeId, context.Request.Path);

                            Infrastructure.Cluster.GlobalClusterCenter.StatisticsRecorder?.RecordBytesSent(context.Connection.Id, responseBytes.Length);

                            continue;
                        }

                        // 转换为 MvcRequestScheme 以兼容现有的分发逻辑
                        var mvcRequestScheme = new MvcRequestScheme
                        {
                            Id = requestScheme.Id,
                            Target = requestScheme.Target,
                            Body = requestScheme.Body
                        };

                        // 构建每消息上下文并经编译好的中间件链处理（终结点=端点分发，链返回后以 MessagePack 序列化并发送）。
                        // 仅在注册了中间件时才复制原始字节；异步模式下接收缓冲区会被复用。
                        var messageContext = new WebSocketMessageContext
                        {
                            HttpContext = context,
                            WebSocket = webSocket,
                            Options = webSocketOption,
                            MessageType = result.MessageType,
                            RequestTimeTicks = requestTime,
                            ReceivedData = webSocketOption.MiddlewareCount > 0 ? msgArray.AsMemory(0, msgLen).ToArray() : default,
                            Request = mvcRequestScheme,
                            RequestBody = requestBody,
                        };

                        Task processTask = ProcessMessageAsync(GetCompiledPipeline(webSocketOption, appLifetime), messageContext);
                        if (webSocketOption.EnableForwardTaskSyncProcessingMode)
                        {
                            await processTask;
                        }
                        else
                        {
                            _ = processTask.ContinueWith(static (t, state) =>
                            {
                                ((ILogger)state).LogInformation(t.Exception, I18nText.ConnectionEntry_DisconnectedInternalExceptions);
                            }, logger, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                        }

                    CONTINUE_RECEIVE:;
                    }
                    catch (Exception ex)
                    {
                        logger.LogInformation(ex, ex.Message);
                    }
                    finally
                    {
                        // 归还单帧快路径借用的租用缓冲区（同步反序列化已完成，异步转发只引用 ctx）。
                        // Return the single-frame fast-path buffer (sync deserialize done; async forward uses only ctx).
                        if (singleFrameBuffer != null)
                        {
                            ArrayPool<byte>.Shared.Return(singleFrameBuffer);
                            singleFrameBuffer = null;
                        }

                        wsCloseDesc = result?.CloseStatusDescription;

                        wsReceiveReader.Flush();
                        wsReceiveReader.SetLength(0);
                        wsReceiveReader.Seek(0, SeekOrigin.Begin);
                        wsReceiveReader.Position = 0;
                        // 收缩大尖峰，避免一次大多帧消息后长期占用峰值内存（Length 已为 0，收缩安全；单帧连接 Capacity 恒为 0）。
                        // Release large spikes so one big message doesn't retain peak memory (Length is 0, so safe).
                        if (wsReceiveReader.Capacity > MaxRetainedReceiveCapacity)
                        {
                            wsReceiveReader.Capacity = 0;
                        }
                        // 释放本条消息在全局接收内存预算中预留的字节。
                        if (reservedReceiveBytes > 0)
                        {
                            Cyaim.WebSocketServer.Infrastructure.WebSocketReceiveMemoryGovernor.Release(reservedReceiveBytes);
                            reservedReceiveBytes = 0;
                        }

                        if (ParallelForwardLimitSlim != null)
                        {
                            ParallelForwardLimitSlim.Release();
                        }
                        if (endPointSlim != null)
                        {
                            endPointSlim.Release();
                        }
                    }

                } while (!appLifetime.ApplicationStopping.IsCancellationRequested);

                // 连接断开
                if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseSent)
                {
                    await webSocket.CloseAsync(webSocket.CloseStatus == null ?
                        webSocket.State == WebSocketState.Aborted ?
                        WebSocketCloseStatus.InternalServerError : WebSocketCloseStatus.NormalClosure
                        : webSocket.CloseStatus.Value, wsCloseDesc, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                logger.LogTrace(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_AbortedReceivingData + ex.Message + Environment.NewLine + ex.StackTrace));
            }
        }

        /// <summary>
        /// Compiled per-connection middleware pipeline (built once, reused for every message).
        /// 编译好的中间件管道（一次构建，每消息复用）。
        /// </summary>
        private WebSocketRequestDelegate _compiledPipeline;

        /// <summary>
        /// Build (once) the middleware pipeline whose terminal dispatches to the endpoint (reusing the
        /// shared MvcDistributeAsync) and stores the result on the context.
        /// 构建（仅一次）中间件管道：终结点复用共享的 MvcDistributeAsync 分发到端点并把结果存到上下文。
        /// </summary>
        private WebSocketRequestDelegate GetCompiledPipeline(WebSocketRouteOption options, IHostApplicationLifetime appLifetime)
        {
            var pipeline = _compiledPipeline;
            if (pipeline == null)
            {
                var lifetime = appLifetime;
                var log = logger;
                pipeline = _compiledPipeline = options.BuildPipeline(async ctx =>
                {
                    ctx.Response = await MvcChannelHandler.MvcDistributeAsync(ctx.Options, ctx.HttpContext, ctx.WebSocket, ctx.Request, ctx.RequestBody, log, lifetime);
                });
            }
            return pipeline;
        }

        /// <summary>
        /// Run one message through the middleware pipeline, then convert the MVC response to a
        /// MessagePack response, serialize and send it (unless a middleware suppressed it).
        /// 让一条消息经过中间件管道，然后把 MVC 响应转换为 MessagePack 响应并序列化发送（除非中间件已抑制）。
        /// </summary>
        private async Task ProcessMessageAsync(WebSocketRequestDelegate pipeline, WebSocketMessageContext ctx)
        {
            try
            {
                await pipeline(ctx).ConfigureAwait(false);

                if (ctx.SuppressResponse || ctx.Response is not MvcResponseScheme mvcResponse)
                {
                    return;
                }

                var messagePackResponse = new MessagePackResponseScheme
                {
                    Status = mvcResponse.Status,
                    Msg = mvcResponse.Msg,
                    RequestTime = mvcResponse.RequestTime,
                    CompleteTime = mvcResponse.CompleteTime,
                    Id = mvcResponse.Id,
                    Target = mvcResponse.Target,
                    Body = mvcResponse.Body
                };

                var responseBytes = MessagePackSerializer.Serialize(messagePackResponse);
                await ctx.WebSocket.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Binary, true, CancellationToken.None);

                var currentNodeId = Infrastructure.Cluster.GlobalClusterCenter.ClusterContext?.NodeId;
                _metricsCollector?.RecordMessageSent(responseBytes.Length, currentNodeId, ctx.HttpContext.Request.Path);
                Infrastructure.Cluster.GlobalClusterCenter.StatisticsRecorder?.RecordBytesSent(ctx.HttpContext.Connection.Id, responseBytes.Length);
            }
            catch (Exception ex)
            {
                var errorResponse = new MessagePackResponseScheme
                {
                    Status = 1,
                    RequestTime = ctx.RequestTimeTicks,
                    CompleteTime = DateTime.Now.Ticks,
                    Target = ctx.Request?.Target,
                    Id = ctx.Request?.Id,
                    Msg = string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, ctx.HttpContext.Connection.RemoteIpAddress, ctx.HttpContext.Connection.RemotePort, ctx.HttpContext.Connection.Id, I18nText.MvcForwardSendData_RequestParsingError + ex.Message + Environment.NewLine + ex.StackTrace)
                };
                logger.LogInformation(errorResponse.Msg);

                try
                {
                    var responseBytes = MessagePackSerializer.Serialize(errorResponse);
                    await ctx.WebSocket.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Binary, true, CancellationToken.None);
                }
                catch
                {
                    // 忽略发送错误
                }
            }
        }

        /// <summary>
        /// Find target from MessagePack fragment
        /// </summary>
        /// <param name="messagePackFragment"></param>
        /// <returns></returns>
        private string FindTargetFromMessagePack(ReadOnlySpan<byte> messagePackFragment)
        {
            try
            {
                // 将 ReadOnlySpan 转换为 byte[] 以便反序列化
                byte[] buffer = messagePackFragment.ToArray();
                var request = MessagePackSerializer.Deserialize<MessagePackRequestScheme>(buffer);
                return request?.Target;
            }
            catch
            {
                // 如果反序列化失败，返回 null
                return null;
            }
        }

    }
}

