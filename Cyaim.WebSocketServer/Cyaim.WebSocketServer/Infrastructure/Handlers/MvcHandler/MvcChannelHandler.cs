﻿using Cyaim.WebSocketServer.Infrastructure.Configures;
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

        const string PipeStr_RequestForwardPipeline = "RequestForwardPipeline";

        const string PipeStr_RequestReceivePipeline = "RequestReceivePipeline";

        const string PipeStr_RequestPipeline = "RequestPipeline";

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

                        // 执行BeforeReceivingData管道
                        _ = await InvokePipeline(RequestPipelineStage.Connected, context, webSocket, null, null);

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
                await MvcChannel_OnDisconnected(context, webSocketCloseStatus, webSocketOptions, logger);

                // 执行管道 Disconnected
                _ = await InvokePipeline(RequestPipelineStage.Disconnected, context, webSocketOptions);
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

                        // Feature 要做流量控制 🔨🔨🔨

                        // 执行BeforeReceivingData管道
                        _ = await InvokePipeline(RequestPipelineStage.BeforeReceivingData, context, webSocket, null, null);

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

                                //await wsReceiveReader.WriteAsync(buffer, 0, result.Count);
                                await wsReceiveReader.WriteAsync(buffer.AsMemory(0, result.Count));

                                // 执行ReceivingData管道
                                _ = await InvokePipeline(RequestPipelineStage.ReceivingData, context, webSocket, result, buffer);

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
                        _ = await InvokePipeline(RequestPipelineStage.ReceivingData, context, webSocket, result, wsReceiveReader.GetBuffer());

                        if (result == null)
                        {
                            continue;
                        }

                        // EndPoint level restrictions
                        if (webSocketOption.MaxEndPointParallelForwardLimit != null)
                        {
                            string endpoint = FindJsonPropertyValue(wsReceiveReader.GetBuffer());
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
                        _ = await InvokePipeline(RequestPipelineStage.BeforeForwardingData, context, webSocket, result, wsReceiveReader.GetBuffer(), requestScheme, requestBody);

                        //requestScheme = JsonSerializer.Deserialize<MvcRequestScheme>(wsReceiveReader.GetBuffer(), webSocketOption.DefaultRequestJsonSerialiazerOptions);
                        // 改异步转发
                        Task forwardTask = MvcForwardSendData(webSocket, context, result, requestScheme, requestBody, requestTime, appLifetime);
                        // 是否串行
                        if (webSocketOption.EnableForwardTaskSyncProcessingMode)
                        {
                            await forwardTask;
                        }

                        // 执行管道 AfterForwardingData
                        _ = await InvokePipeline(RequestPipelineStage.AfterForwardingData, context, webSocket, result, wsReceiveReader.GetBuffer(), requestScheme, requestBody);

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

                await invokeResult.SendAsync(webSocketOption.DefaultResponseJsonSerializerOptions, result.MessageType, timeout: ResponseSendTimeout, encoding: Encoding.UTF8, sendBufferSize: SendTextBufferSize, socket: webSocket).ConfigureAwait(false);
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
                PropertyInfo contextInfo = clss.GetProperty(webSocketOptions.InjectionHttpContextPropertyName);
                PropertyInfo socketInfo = clss.GetProperty(webSocketOptions.InjectionWebSocketClientPropertyName);

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

                // 注入目标类中的帮助属性HttpContext、WebSocket
                if (contextInfo != null && contextInfo.CanWrite)
                {
                    contextInfo.SetValue(inst, context);
                }
                if (socketInfo != null && socketInfo.CanWrite)
                {
                    socketInfo.SetValue(inst, webSocket);
                }
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

                // 调用目标方法 
                invokeResult = method.Invoke(inst, args);

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
        /// Call request forwarding pipeline
        /// </summary>
        /// <param name="requestStage"></param>
        /// <param name="context"></param>
        /// <param name="webSocket"></param>
        /// <param name="result"></param>
        /// <param name="buffer"></param>
        /// <param name="request"></param>
        /// <param name="requestBody"></param>
        /// <returns></returns>
        private async Task<ConcurrentQueue<PipelineItem>> InvokePipeline(RequestPipelineStage requestStage, HttpContext context, WebSocket webSocket, WebSocketReceiveResult result, byte[] buffer, MvcRequestScheme request, JsonObject requestBody)
        {
            ConcurrentQueue<PipelineItem> inovkes;
            RequestPipeline.TryGetValue(requestStage, out inovkes);
            var order = inovkes?.OrderBy(x => x.Order);
            if (order == null)
            {
                return await Task.FromResult<ConcurrentQueue<PipelineItem>>(null);
            }
            foreach (PipelineItem x in order)
            {
                try
                {
                    var p = (x.Item as RequestForwardPipeline) ?? throw new NotSupportedException(I18nText.AtThisStagePipelineNotSupport + PipeStr_RequestForwardPipeline);
                    await p?.Invoke?.Invoke(context, webSocket, result, buffer, request, requestBody);
                }
                catch (Exception ex)
                {
                    x.Exception = ex;
                    x.ExceptionItem = x;
                }
            }
            return inovkes;
        }

        /// <summary>
        /// Call the request receiving pipeline
        /// </summary>
        /// <param name="requestStage"></param>
        /// <param name="context"></param>
        /// <param name="webSocket"></param>
        /// <param name="result"></param>
        /// <param name="buffer"></param>
        /// <returns></returns>
        private async Task<ConcurrentQueue<PipelineItem>> InvokePipeline(RequestPipelineStage requestStage, HttpContext context, WebSocket webSocket, WebSocketReceiveResult result, byte[] buffer)
        {
            ConcurrentQueue<PipelineItem> inovkes;
            RequestPipeline.TryGetValue(requestStage, out inovkes);
            var order = inovkes?.OrderBy(x => x.Order);
            if (order == null)
            {
                return await Task.FromResult<ConcurrentQueue<PipelineItem>>(null);
            }
            foreach (PipelineItem x in order)
            {
                try
                {
                    var p = (x.Item as RequestReceivePipeline) ?? throw new NotSupportedException(I18nText.AtThisStagePipelineNotSupport + PipeStr_RequestReceivePipeline);
                    await p?.Invoke?.Invoke(context, webSocket, result, buffer);
                }
                catch (Exception ex)
                {
                    x.Exception = ex;
                    x.ExceptionItem = x;
                }
            }
            return inovkes;
        }

        /// <summary>
        /// Call the basic request pipeline
        /// </summary>
        /// <param name="requestStage"></param>
        /// <param name="context"></param>
        /// <param name="webSocketOptions"></param>
        /// <returns></returns>
        private async Task<ConcurrentQueue<PipelineItem>> InvokePipeline(RequestPipelineStage requestStage, HttpContext context, WebSocketRouteOption webSocketOptions)
        {
            ConcurrentQueue<PipelineItem> inovkes;
            RequestPipeline.TryGetValue(requestStage, out inovkes);
            var order = inovkes?.OrderBy(x => x.Order);
            if (order == null)
            {
                return await Task.FromResult<ConcurrentQueue<PipelineItem>>(null);
            }
            foreach (PipelineItem x in order)
            {
                try
                {
                    var p = x.Item ?? throw new NotSupportedException(I18nText.AtThisStagePipelineNotSupport + PipeStr_RequestPipeline);
                    await p?.Invoke?.Invoke(context, webSocketOptions);
                }
                catch (Exception ex)
                {
                    x.Exception = ex;
                    x.ExceptionItem = x;
                }
            }
            return inovkes;
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