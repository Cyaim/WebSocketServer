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
                using MemoryStream wsReceiveReader = new MemoryStream(ReceiveTextBufferSize);
                bool connectionClosed = false;
                do
                {
                    long requestTime = DateTime.UtcNow.Ticks;
                    WebSocketReceiveResult result = null;
                    SemaphoreSlim endPointSlim = null;
                    bool receivedClose = false;
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

                                // 请求大小限制
                                if (wsReceiveReader.Length > webSocketOption.MaxRequestReceiveDataLimit)
                                {
                                    logger.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_RequestSizeMaximumLimit));

                                    goto CONTINUE_RECEIVE;
                                }

                                // 应用带宽限速策略
                                if (bandwidthLimitManager != null && result.Count > 0)
                                {
                                    string endPoint = null;
                                    // 尝试从已接收的数据中提取端点信息（如果数据足够）
                                    if (wsReceiveReader.Length > 0)
                                    {
                                        try
                                        {
                                            // 仅解析有效数据段，GetBuffer 的空闲区可能残留上一条消息的数据
                                            // Slice to valid length: GetBuffer slack may contain previous message bytes
                                            endPoint = FindJsonPropertyValue(wsReceiveReader.GetBuffer().AsSpan(0, (int)wsReceiveReader.Length));
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

                                // 将接收到的数据写入MemoryStream
                                await wsReceiveReader.WriteAsync(buffer.AsMemory(0, result.Count));

                                // 记录消息接收指标
                                var currentNodeId = Infrastructure.Cluster.GlobalClusterCenter.ClusterContext?.NodeId;
                                _metricsCollector?.RecordMessageReceived(result.Count, currentNodeId, context.Request.Path);

                                // 记录统计信息（如果统计记录器可用）
                                Infrastructure.Cluster.GlobalClusterCenter.StatisticsRecorder?.RecordBytesReceived(context.Connection.Id, result.Count);

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
                            // 归还buffer
                            ArrayPool<byte>.Shared.Return(buffer);
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

                        // 有效数据视图：仅取已写入长度，替代旧版为让 GetBuffer 干净而做的 Capacity 收缩
                        // （Capacity 收缩每条消息都会重新分配并拷贝整个缓冲区）
                        // Valid-data view sliced to written length, replacing the old Capacity-shrink hack
                        // (which reallocated and copied the whole buffer on every message)
                        int receivedLength = (int)wsReceiveReader.Length;
                        ReadOnlyMemory<byte> receivedData = wsReceiveReader.GetBuffer().AsMemory(0, receivedLength);

                        // 在接收完数据后，应用端点级别的限速策略
                        string endpoint = null;
                        if (bandwidthLimitManager != null && receivedLength > 0)
                        {
                            try
                            {
                                endpoint = FindJsonPropertyValue(receivedData.Span);
                                if (!string.IsNullOrEmpty(endpoint))
                                {
                                    // 对完整消息应用端点级别限速
                                    await bandwidthLimitManager.WaitForBandwidthAsync(
                                        context.Request.Path,
                                        context.Connection.Id,
                                        endpoint,
                                        receivedLength,
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
                    // 如果目标方法只有1个参数并且是对象或者接口
                    if (methodParam.Length == 1 && (methodParam[0].ParameterType.IsClass || methodParam[0].ParameterType.IsInterface))
                    {
                        ParameterInfo targetBindParam = methodParam[0];
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
                            args[0] = targetBindParam.ParameterType.ConvertTo(jProp);
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
                            args[0] = targetPropInst;
                        }

                    }
                    else
                    {
                        for (int i = 0; i < methodParam.Length; i++)
                        {
                            ParameterInfo item = methodParam[i];

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