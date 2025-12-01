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

        /// <summary>
        /// Request handler pipeline
        /// </summary>
        public ConcurrentDictionary<RequestPipelineStage, ConcurrentQueue<PipelineItem>> RequestPipeline { get; } = new ConcurrentDictionary<RequestPipelineStage, ConcurrentQueue<PipelineItem>>();
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

                    // 配置最大连接数
                    if ((ulong)Clients.LongCount() >= webSocketOptions.MaxConnectionLimit)
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
                                logger.LogDebug($"Registered connection {context.Connection.Id} with cluster manager");
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, $"Failed to register connection {context.Connection.Id} with cluster manager");
                            }
                        }

                        // 执行Connected管道
                        _ = await InvokePipeline(RequestPipelineStage.Connected, PipelineContext.CreateReceive(context, webSocket, null, null, webSocketOptions));

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

                await MvcChannel_OnDisconnected(context, webSocketCloseStatus, webSocketOptions, logger);

                // 执行管道 Disconnected
                _ = await InvokePipeline(RequestPipelineStage.Disconnected, PipelineContext.CreateBasic(context, webSocketOptions));
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
                do
                {
                    long requestTime = DateTime.Now.Ticks;
                    WebSocketReceiveResult result = null;
                    SemaphoreSlim endPointSlim = null;
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
                                // exit
                                break;
                            }
                            else
                            {
                                await Task.Delay(300).ConfigureAwait(false);
                                continue;
                            }

                        }

                        // 执行BeforeReceivingData管道
                        _ = await InvokePipeline(RequestPipelineStage.BeforeReceivingData, PipelineContext.CreateReceive(context, webSocket, null, null, webSocketOption));

                        #region 接收数据
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReceiveTextBufferSize);
                        while ((result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None)).Count > 0)
                        {
                            try
                            {
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
                                            endPoint = FindJsonPropertyValue(wsReceiveReader.GetBuffer());
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

                                //await wsReceiveReader.WriteAsync(buffer, 0, result.Count);
                                await wsReceiveReader.WriteAsync(buffer.AsMemory(0, result.Count));

                                // 记录消息接收指标
                                var currentNodeId = Infrastructure.Cluster.GlobalClusterCenter.ClusterContext?.NodeId;
                                _metricsCollector?.RecordMessageReceived(result.Count, currentNodeId, context.Request.Path);

                                // 记录统计信息（如果统计记录器可用）
                                Infrastructure.Cluster.GlobalClusterCenter.StatisticsRecorder?.RecordBytesReceived(context.Connection.Id, result.Count);

                                // 执行ReceivingData管道
                                _ = await InvokePipeline(RequestPipelineStage.ReceivingData, PipelineContext.CreateReceive(context, webSocket, result, buffer, webSocketOption));

                                // 已经接受完数据了
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
                                ArrayPool<byte>.Shared.Return(buffer);
                            }
                        }
                        // 如果接收到的消息是Close时，断开连接
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }
                        // 缩小Capacity避免Getbuffer出现0x00
                        if (wsReceiveReader.Capacity > wsReceiveReader.Length)
                        {
                            wsReceiveReader.Capacity = (int)wsReceiveReader.Length;
                        }
                        #endregion

                        // 执行AfterReceivingData管道
                        _ = await InvokePipeline(RequestPipelineStage.AfterReceivingData, PipelineContext.CreateReceive(context, webSocket, result, wsReceiveReader.GetBuffer(), webSocketOption));

                        if (result == null)
                        {
                            continue;
                        }

                        // 在接收完数据后，应用端点级别的限速策略
                        string endpoint = null;
                        if (bandwidthLimitManager != null && wsReceiveReader.Length > 0)
                        {
                            try
                            {
                                endpoint = FindJsonPropertyValue(wsReceiveReader.GetBuffer());
                                if (!string.IsNullOrEmpty(endpoint))
                                {
                                    // 对完整消息应用端点级别限速
                                    await bandwidthLimitManager.WaitForBandwidthAsync(
                                        context.Request.Path,
                                        context.Connection.Id,
                                        endpoint,
                                        (int)wsReceiveReader.Length,
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
                                endpoint = FindJsonPropertyValue(wsReceiveReader.GetBuffer());
                            }
                            if (webSocketOption.MaxEndPointParallelForwardLimit.TryGetValue(endpoint, out endPointSlim) && endPointSlim != null)
                            {
                                await endPointSlim.WaitAsync().ConfigureAwait(false);
                            }
                        }

                        // 请求处理管道 分阶段 接收数据前后 转发前后等

                        // 处理请求的数据
                        MvcRequestScheme requestScheme = null;
                        JsonObject requestBody = null;

                        //Console.WriteLine(Encoding.UTF8.GetString(wsReceiveReader.GetBuffer()));
                        using (JsonDocument doc = JsonDocument.Parse(wsReceiveReader.GetBuffer()))
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
                            requestBody = body.ValueKind == JsonValueKind.Undefined ? null : (JsonNode.Parse(body.GetRawText())?.AsObject());
                        }

                        // 执行管道 BeforeForwardingData
                        _ = await InvokePipeline(RequestPipelineStage.BeforeForwardingData, PipelineContext.CreateForward(context, webSocket, result, wsReceiveReader.GetBuffer(), requestScheme, requestBody, webSocketOption));

                        //requestScheme = JsonSerializer.Deserialize<MvcRequestScheme>(wsReceiveReader.GetBuffer(), webSocketOption.DefaultRequestJsonSerialiazerOptions);
                        // 改异步转发
                        Task forwardTask = MvcForwardSendData(webSocket, context, result, requestScheme, requestBody, requestTime, appLifetime);
                        // 是否串行
                        if (webSocketOption.EnableForwardTaskSyncProcessingMode)
                        {
                            await forwardTask;
                        }

                        // 执行管道 AfterForwardingData
                        _ = await InvokePipeline(RequestPipelineStage.AfterForwardingData, PipelineContext.CreateForward(context, webSocket, result, wsReceiveReader.GetBuffer(), requestScheme, requestBody, webSocketOption));

                    CONTINUE_RECEIVE:;
                    }
                    catch (Exception ex)
                    {
                        logger.LogInformation(ex, ex.Message, Encoding.UTF8.GetString(wsReceiveReader.GetBuffer()));
                    }
                    finally
                    {
                        wsCloseDesc = result?.CloseStatusDescription;

                        wsReceiveReader.Flush();
                        wsReceiveReader.SetLength(0);
                        wsReceiveReader.Seek(0, SeekOrigin.Begin);
                        wsReceiveReader.Position = 0;

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
        /// MvcChannel forward data
        /// </summary>
        /// <param name="result"></param>
        /// <param name="webSocket"></param>
        /// <param name="context"></param>
        /// <param name="request"></param>
        /// <param name="requestBody"></param>
        /// <param name="requsetTicks"></param>
        /// <returns></returns>
        private async Task MvcForwardSendData(WebSocket webSocket, HttpContext context, WebSocketReceiveResult result, MvcRequestScheme request, JsonObject requestBody, long requsetTicks, IHostApplicationLifetime appLifetime)
        {
            try
            {
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                //按节点请求转发
                object invokeResult = await MvcDistributeAsync(webSocketOption, context, webSocket, request, requestBody, logger, appLifetime);

                // 发送结果给客户端
                //string serialJson = JsonSerializer.Serialize(invokeResult, webSocketOption.DefaultResponseJsonSerializerOptions);
                //await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(serialJson)), result.MessageType, result.EndOfMessage, CancellationToken.None);

                // 序列化响应以获取大小
                string serialJson = JsonSerializer.Serialize(invokeResult, webSocketOption.DefaultResponseJsonSerializerOptions);
                var responseBytes = Encoding.UTF8.GetBytes(serialJson);

                await invokeResult.SendAsync(webSocketOption.DefaultResponseJsonSerializerOptions, result.MessageType, timeout: ResponseSendTimeout, encoding: Encoding.UTF8, sendBufferSize: SendTextBufferSize, socket: webSocket).ConfigureAwait(false);

                // 记录消息发送指标
                var currentNodeId = Infrastructure.Cluster.GlobalClusterCenter.ClusterContext?.NodeId;
                _metricsCollector?.RecordMessageSent(responseBytes.Length, currentNodeId, context.Request.Path);

                // 记录统计信息（如果统计记录器可用）
                Infrastructure.Cluster.GlobalClusterCenter.StatisticsRecorder?.RecordBytesSent(context.Connection.Id, responseBytes.Length);
            }
            catch (JsonException ex)
            {
                MvcResponseSchemeException mvcRespEx = new MvcResponseSchemeException(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcForwardSendData_RequestParsingError + ex.Message + Environment.NewLine + ex.StackTrace))
                {
                    Status = 1,
                    RequestTime = requsetTicks,
                    CompleteTime = DateTime.Now.Ticks,
                    Target = request.Target,
                };
                logger.LogInformation(mvcRespEx, mvcRespEx.Message);
            }
            catch (Exception)
            {

                throw;
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

                await invokeResult.SendAsync(webSocketOption.DefaultResponseJsonSerializerOptions, result.MessageType, timeout: ResponseSendTimeout, encoding: Encoding.UTF8, sendBufferSize: SendTextBufferSize, socket: webSocket).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                MvcResponseSchemeException mvcRespEx = new MvcResponseSchemeException(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcForwardSendData_RequestParsingError + ex.Message + Environment.NewLine + ex.StackTrace))
                {
                    Status = 1,
                    RequestTime = requsetTicks,
                    CompleteTime = DateTime.Now.Ticks,
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
                    CompleteTime = DateTime.Now.Ticks,
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
            long requestTime = DateTime.Now.Ticks;
            string requestPath = request.Target.ToLower();

            try
            {
                // 从键值对中获取对应的执行函数 
                webSocketOptions.WatchAssemblyContext.WatchMethods.TryGetValue(requestPath, out MethodInfo method);

                if (method == null)
                {
                    goto NotFound;
                }
                Type clss = webSocketOptions.WatchAssemblyContext.WatchEndPoint.FirstOrDefault(x => x.MethodPath == requestPath)?.Class;
                if (clss == null)
                {
                    //找不到访问目标
                    goto NotFound;
                }

                #region 注入Socket的HttpContext和WebSocket客户端
                webSocketOptions.WatchAssemblyContext.MaxConstructorParameters.TryGetValue(clss, out ConstructorParameter constructorParameter);

                object[] instanceParmas = new object[constructorParameter.ParameterInfos.Length];
                // 从Scope DI容器提取目标类构造函数所需的对象 
                var serviceScopeFactory = WebSocketRouteOption.ApplicationServices.GetService<IServiceScopeFactory>();
                var serviceScope = serviceScopeFactory.CreateScope();
                var scopeIocProvider = serviceScope.ServiceProvider;
                for (int i = 0; i < constructorParameter.ParameterInfos.Length; i++)
                {
                    ParameterInfo item = constructorParameter.ParameterInfos[i];

                    if (webSocketOptions.ApplicationServiceCollection == null)
                    {
                        logger.LogWarning(I18nText.MvcDistributeAsync_EmptyDI);
                        break;
                    }

                    ServiceDescriptor nonSingleton = webSocketOptions.ApplicationServiceCollection.FirstOrDefault(x => x.ServiceType == item.ParameterType);
                    if (nonSingleton == null || nonSingleton.Lifetime == ServiceLifetime.Singleton)
                    {
                        instanceParmas[i] = WebSocketRouteOption.ApplicationServices.GetService(item.ParameterType);
                    }
                    else
                    {
                        instanceParmas[i] = scopeIocProvider.GetService(item.ParameterType);
                    }
                }

                object inst = Activator.CreateInstance(clss, instanceParmas);

                // 使用注入器工厂注入 HttpContext 和 WebSocket（支持源代码生成和反射两种方式）
                var injectorFactory = webSocketOptions.InjectorFactory ?? new EndpointInjectorFactory(webSocketOptions);
                var injector = injectorFactory.GetOrCreateInjector(clss);
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
                                logger.LogTrace(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, $"{requestPath}.{item.Name}" + I18nText.MvcForwardSendData_RequestBodyParameterFormatError + ex.Message + Environment.NewLine + ex.StackTrace));
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
                if (invokeResult is Task)
                {
                    dynamic invokeResultTask = invokeResult;
                    //await invokeResultTask;
                    await Task.WhenAny(invokeResultTask, Task.Delay(Timeout.Infinite, appLifetime.ApplicationStopping));

                    if (invokeResultTask.Exception != null)
                    {
                        throw invokeResultTask.Exception;
                    }

                    try
                    {
                        invokeResult = invokeResultTask.Result;
                    }
                    catch (RuntimeBinderException)
                    {
                        invokeResult = null;
                    }
                }

                #endregion

                // Dispose ioc scope
                serviceScope?.Dispose();
                serviceScope = null;

                mvcResponse.Id = request.Id;
                mvcResponse.Target = request.Target;
                mvcResponse.Body = invokeResult;
                mvcResponse.CompleteTime = DateTime.Now.Ticks;

                return mvcResponse;
            }
            catch (Exception ex)
            {
                MvcResponseScheme resp = new MvcResponseScheme() { Id = request.Id, Status = 1, Target = request.Target, RequestTime = requestTime, CompleteTime = DateTime.Now.Ticks };

                ex = (ex.InnerException ?? ex);
                resp.Msg = string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcDistributeAsync_Target + requestPath + Environment.NewLine + ex.Message + Environment.NewLine + ex.StackTrace);
                logger.LogInformation(resp.Msg);

                MvcResponseScheme customResp = await webSocketOptions.OnException(ex, request, resp, context, webSocketOptions, context.Request.Path, logger).ConfigureAwait(false);

                //if (!webSocketOptions.IsDevelopment)
                //    resp.Msg = null;

                return customResp;
            }

        NotFound:
            logger.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcDistributeAsync_EndPointNotFound + requestPath));

            return new MvcResponseScheme() { Id = request.Id, Status = 2, Target = request.Target, RequestTime = requestTime, CompleteTime = DateTime.Now.Ticks };
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

            logger.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.OnDisconnected_Disconnected + msg + Environment.NewLine + $"Status:{webSocketCloseStatus.ToString() ?? "NoHandshakeSucceeded"}"));

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
                                        logger.LogWarning($"Access denied for IP {ipAddress} from {context.Request.Path}: {policy.DenialMessage}");
                                        break;
                                    case AccessDeniedAction.ReturnUnauthorized:
                                        context.Response.StatusCode = 401;
                                        await context.Response.WriteAsync(policy.DenialMessage ?? "Unauthorized");
                                        logger.LogWarning($"Access denied for IP {ipAddress} from {context.Request.Path}: {policy.DenialMessage}");
                                        break;
                                    case AccessDeniedAction.CloseConnection:
                                    default:
                                        logger.LogWarning($"Access denied for IP {ipAddress} from {context.Request.Path}: {policy.DenialMessage}");
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
            var jsonReader = new Utf8JsonReader(jsonFragment);

            try
            {
                while (jsonReader.Read())
                {
                    try
                    {
                        if (jsonReader.TokenType == JsonTokenType.PropertyName && jsonReader.GetString()?.ToLower() == PropertyName)
                        {
                            jsonReader.Read();
                            if (jsonReader.TokenType == JsonTokenType.String)
                            {
                                string targetValue = jsonReader.GetString();
                                return targetValue;
                            }
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }

            return null;
        }


        #region Invoke pipeline

        /// <summary>
        /// 统一的管道调用方法
        /// </summary>
        /// <param name="requestStage">处理阶段</param>
        /// <param name="context">管道上下文（从对象池获取，使用完毕后自动归还）</param>
        /// <returns></returns>
        /// <remarks>
        /// 注意：context 对象会在所有管道处理器执行完毕后自动归还到对象池。
        /// 管道处理器不应在异步操作中保存 context 的引用，因为方法返回后对象会被清理和重用。
        /// </remarks>
        private async Task<ConcurrentQueue<PipelineItem>> InvokePipeline(RequestPipelineStage requestStage, PipelineContext context)
        {
            try
            {
                if (!RequestPipeline.TryGetValue(requestStage, out ConcurrentQueue<PipelineItem> invokes) || invokes == null)
                {
                    return null;
                }

                var ordered = invokes.OrderBy(x => x.Order);
                foreach (PipelineItem item in ordered)
                {
                    try
                    {
                        if (item.Item != null)
                        {
                            await item.Item.InvokeAsync(context);
                        }
                    }
                    catch (Exception ex)
                    {
                        item.Exception = ex;
                        item.ExceptionItem = item;
                    }
                }

                return invokes;
            }
            finally
            {
                // 使用完毕后归还到对象池，确保即使发生异常也能正确归还
                context?.Return();
            }
        }


        #region Add request middleware to RequestPipeline

        /// <summary>
        /// Add request middleware to RequestPipeline
        /// </summary>
        /// <param name="requestPipelineStage">Pipeline processing stage</param>
        /// <param name="handler">Processing program.The Stage in the handler will be overwritten by the requestPipeStage parameter.</param>
        /// <returns></returns>
        public ConcurrentQueue<PipelineItem> AddRequestMiddleware(RequestPipelineStage requestPipelineStage, PipelineItem handler)
        {
            if (!RequestPipeline.TryGetValue(requestPipelineStage, out ConcurrentQueue<PipelineItem> value))
            {
                value = new ConcurrentQueue<PipelineItem>();
                RequestPipeline.TryAdd(requestPipelineStage, value);
            }

            handler.Stage = requestPipelineStage;
            value.Enqueue(handler);

            return value;
        }

        /// <summary>
        /// Add request middleware to RequestPipeline
        /// </summary>
        /// <param name="handler">processing program</param>
        /// <returns></returns>
        public ConcurrentDictionary<RequestPipelineStage, ConcurrentQueue<PipelineItem>> AddRequestMiddleware(PipelineItem handler)
        {
            if (!RequestPipeline.TryGetValue(handler.Stage, out ConcurrentQueue<PipelineItem> value))
            {
                value = new ConcurrentQueue<PipelineItem>();
                RequestPipeline.TryAdd(handler.Stage, value);
            }

            value.Enqueue(handler);

            return RequestPipeline;
        }

        /// <summary>
        /// Add request middleware to RequestPipeline
        /// </summary>
        /// <param name="stage">Pipeline processing stage</param>
        /// <param name="invoke"></param>
        /// <param name="order">If it is null, add 1 on the largest order in the current stage queue. If there is no data in the current queue, the order is 0.</param>
        /// <returns></returns>
        public ConcurrentDictionary<RequestPipelineStage, ConcurrentQueue<PipelineItem>> AddRequestMiddleware(RequestPipelineStage stage, RequestPipeline invoke, float? order = null)
        {
            if (!RequestPipeline.TryGetValue(stage, out ConcurrentQueue<PipelineItem> value))
            {
                value = new ConcurrentQueue<PipelineItem>();
                RequestPipeline.TryAdd(stage, value);
            }

            if (order == null)
            {
                order = value.IsEmpty ? 0 : value.Max(x => x.Order) + 1;
            }

            value.Enqueue(new PipelineItem()
            {
                Item = invoke,
                Order = order.Value,
                Stage = stage
            });

            return RequestPipeline;
        }

        #endregion

        #endregion

    }
}