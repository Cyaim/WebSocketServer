﻿using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler
{
    /// <summary>
    /// Provide MVC forwarding handler
    /// </summary>
    public class MvcChannelHandler : IWebSocketHandler
    {
        /// <summary>
        /// Connected clients by mvc channel
        /// </summary>
        public static ConcurrentDictionary<string, WebSocket> Clients { get; set; } = new ConcurrentDictionary<string, WebSocket>();


        private ILogger<WebSocketRouteMiddleware> logger;
        private WebSocketRouteOption webSocketOption;

        /// <summary>
        /// Get instance
        /// </summary>
        /// <param name="receiveBufferSize"></param>
        public MvcChannelHandler(int receiveBufferSize = 4 * 1024)
        {
            ReceiveTextBufferSize = ReceiveBinaryBufferSize = receiveBufferSize;
        }


        /// <summary>
        /// Metadata used when parsing the handler
        /// </summary>
        public WebSocketHandlerMetadata Metadata { get; } = new WebSocketHandlerMetadata()
        {
            Describe = "Provide MVC forwarding handler",
            CanHandleBinary = true,
            CanHandleText = true,
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
        /// Mvc Channel entry
        /// </summary>
        /// <param name="context"></param>
        /// <param name="logger"></param>
        /// <param name="webSocketOptions"></param>
        /// <returns></returns>
        public async Task ConnectionEntry(HttpContext context, ILogger<WebSocketRouteMiddleware> logger, WebSocketRouteOption webSocketOptions)
        {
            this.logger = logger;
            this.webSocketOption = webSocketOptions;

            WebSocketCloseStatus? webSocketCloseStatus = null;
            try
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    // Event instructions whether connection
                    bool ifThisContinue = await MvcChannel_OnBeforeConnection(context, webSocketOptions, context.Request.Path, logger);
                    if (!ifThisContinue)
                    {
                        return;
                    }
                    bool ifContinue = await webSocketOptions.OnBeforeConnection(context, webSocketOptions, context.Request.Path, logger);
                    if (!ifContinue)
                    {
                        return;
                    }

                    // 接受连接
                    using WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    try
                    {
                        logger.LogInformation(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_Connected));
                        bool succ = Clients.TryAdd(context.Connection.Id, webSocket);
                        if (succ)
                        {
                            await MvcForward(context, webSocket);
                        }
                        else
                        {
                            // 如果配置了允许多连接
                            logger.LogDebug(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_ConnectionAlreadyExists));
                        }

                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_DisconnectedInternalExceptions + ex.Message + Environment.NewLine + ex.StackTrace));
                    }
                    finally
                    {
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
                logger.LogTrace(ex, ex.Message);
            }
            finally
            {
                await MvcChannel_OnDisconnected(context, webSocketCloseStatus, webSocketOptions, logger);
            }

        }

        #region Old

        ///// <summary>
        ///// Forward by WebSocket transfer type
        ///// </summary>
        ///// <param name="context"></param>
        ///// <param name="webSocket"></param>
        ///// <returns></returns>
        //private async Task MvcForward(HttpContext context, WebSocket webSocket)
        //{
        //    var buffer = new byte[ReceiveTextBufferSize];
        //    ArraySegment<byte> bufferSeg = new ArraySegment<byte>(buffer);

        //    try
        //    {
        //        WebSocketReceiveResult result = await webSocket.ReceiveAsync(bufferSeg, CancellationToken.None);
        //        //switch (result.MessageType)
        //        //{
        //        //    case WebSocketMessageType.Binary:
        //        //        await MvcBinaryForward(context, webSocket, result, buffer);
        //        //        break;
        //        //    case WebSocketMessageType.Text:
        //        //        await MvcTextForward(webSocket, context, result, buffer);
        //        //        break;
        //        //}
        //        await MvcForward(webSocket, context, result, bufferSeg);

        //        //链接断开
        //        await webSocket.CloseAsync(webSocket.CloseStatus == null ?
        //            webSocket.State == WebSocketState.Aborted ?
        //            WebSocketCloseStatus.InternalServerError : WebSocketCloseStatus.NormalClosure
        //            : webSocket.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
        //    }
        //    catch (OperationCanceledException ex)
        //    {
        //        logger.LogInformation($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> 终止接收数据({context.Connection.Id})\r\nStatus:{(webSocket.CloseStatus.HasValue ? webSocket.CloseStatus.ToString() : "ServerClose")}\r\n{ex.Message}");
        //    }
        //}

        ///// <summary>
        ///// Handle request content
        ///// </summary>
        ///// <param name="result"></param>
        ///// <param name="buffer"></param>
        ///// <param name="webSocket"></param>
        ///// <param name="context"></param>
        ///// <returns></returns>
        //private async Task MvcForward(WebSocket webSocket, HttpContext context, WebSocketReceiveResult result, ArraySegment<byte> buffer)
        //{

        //    long requestTime = DateTime.Now.Ticks;
        //    StringBuilder json = new StringBuilder();

        //    //处理第一次返回的数据
        //    json = json.Append(Encoding.UTF8.GetString(buffer[..result.Count]));

        //    //第一次接受已经接受完数据了
        //    if (result.EndOfMessage)
        //    {
        //        try
        //        {
        //            await MvcForwardSendData(webSocket, context, result, json, requestTime);
        //        }
        //        catch (Exception ex)
        //        {
        //            logger.LogError(ex, ex.Message);
        //        }
        //        finally
        //        {
        //            json = json.Clear();
        //        }
        //    }

        //    //等待客户端发送数据，第二次接受数据
        //    while (!result.CloseStatus.HasValue)
        //    {
        //        try
        //        {
        //            if (!(webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseSent))
        //            {
        //                break;
        //            }

        //            result = await webSocket.ReceiveAsync(buffer, CancellationToken.None);
        //            requestTime = DateTime.Now.Ticks;

        //            json = json.Append(Encoding.UTF8.GetString(buffer[..result.Count]));

        //            if (!result.EndOfMessage || result.CloseStatus.HasValue)
        //            {
        //                continue;
        //            }

        //            await MvcForwardSendData(webSocket, context, result, json, requestTime);

        //            json = json.Clear();
        //        }
        //        catch (OperationCanceledException ex)
        //        {
        //            logger.LogInformation($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> 终止接收数据({context.Connection.Id})\r\nStatus:{(webSocket.CloseStatus.HasValue ? webSocket.CloseStatus.ToString() : "ServerClose")}\r\n{ex.Message}");
        //        }
        //        catch (Exception ex)
        //        {
        //            logger.LogError(ex, ex.Message);
        //        }

        //    }


        //}
        #endregion

        /// <summary>
        /// Forward by WebSocket transfer type
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocket"></param>
        /// <returns></returns>
        private async Task MvcForward(HttpContext context, WebSocket webSocket)
        {
            try
            {
                CancellationTokenSource connCts = new CancellationTokenSource();
                string wsCloseDesc = string.Empty;
                using MemoryStream wsReceiveReader = new MemoryStream(ReceiveTextBufferSize);
                do
                {
                    long requestTime = DateTime.Now.Ticks;
                    WebSocketReceiveResult result = null;
                    try
                    {
                        if (!(webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseSent))
                        {
                            if (webSocket.State == WebSocketState.Aborted || webSocket.State == WebSocketState.CloseReceived || webSocket.State == WebSocketState.Closed)
                            {
                                connCts.Cancel();
                                goto CONTINUE;
                            }
                            else
                            {
                                await Task.Delay(1000).ConfigureAwait(false);
                                goto CONTINUE;
                            }

                        }

                        #region 接收数据
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReceiveTextBufferSize);
                        while ((result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None)).Count > 0)
                        {
                            try
                            {
                                // 请求大小限制
                                if (wsReceiveReader.Length > webSocketOption.MaxRequestReceiveDataLimit)
                                {
                                    logger.LogTrace(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_RequestSizeMaximumLimit));

                                    goto CONTINUE;
                                }

                                //await wsReceiveReader.WriteAsync(buffer, 0, result.Count);
                                await wsReceiveReader.WriteAsync(buffer.AsMemory(0, result.Count));
                                // 已经接受完数据了
                                if (result.EndOfMessage || result.CloseStatus.HasValue)
                                {
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogTrace(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.ConnectionEntry_ReceivingClientDataException + ex.Message + Environment.NewLine + ex.StackTrace));
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
                            }
                        }
                        #endregion

                        if (result == null)
                        {
                            continue;
                        }

                        // 缩小Capacity避免Getbuffer出现0x00
                        if (wsReceiveReader.Capacity > wsReceiveReader.Length)
                        {
                            wsReceiveReader.Capacity = (int)wsReceiveReader.Length;
                        }

                        // 处理请求的数据
                        //json = json.Append(Encoding.UTF8.GetString(wsReceiveReader.GetBuffer()));
                        //JsonDocument document = JsonDocument.Parse(wsReceiveReader.GetBuffer());
                        MvcRequestScheme requestScheme;
                        JsonObject requestBody;
                        using (JsonDocument doc = JsonDocument.Parse(wsReceiveReader.GetBuffer()))
                        {
                            JsonElement root = doc.RootElement;
                            bool hasBody = root.TryGetProperty("Body", out JsonElement body);
                            if (!hasBody)
                            {
                                hasBody = root.TryGetProperty("body", out body);
                            }

                            requestScheme = doc.Deserialize<MvcRequestScheme>(webSocketOption.DefaultRequestJsonSerialiazerOptions);
                            requestBody = body.ValueKind == JsonValueKind.Undefined ? null : (JsonNode.Parse(body.GetRawText())?.AsObject());
                        }

                        //requestScheme = JsonSerializer.Deserialize<MvcRequestScheme>(wsReceiveReader.GetBuffer(), webSocketOption.DefaultRequestJsonSerialiazerOptions);
                        await MvcForwardSendData(webSocket, context, result, requestScheme, requestBody, requestTime);

                    CONTINUE:;
                    }
                    catch (Exception ex)
                    {
                        logger.LogTrace(ex, ex.Message);
                    }
                    finally
                    {
                        wsCloseDesc = result?.CloseStatusDescription;

                        wsReceiveReader.Flush();
                        wsReceiveReader.SetLength(0);
                        wsReceiveReader.Seek(0, SeekOrigin.Begin);
                        wsReceiveReader.Position = 0;
                    }

                } while (!connCts.IsCancellationRequested);


                //switch (result.MessageType)
                //{
                //    case WebSocketMessageType.Binary:
                //        await MvcBinaryForward(context, webSocket, result, buffer);
                //        break;
                //    case WebSocketMessageType.Text:
                //        await MvcTextForward(webSocket, context, result, buffer);
                //        break;
                //}

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
        private async Task MvcForwardSendData(WebSocket webSocket, HttpContext context, WebSocketReceiveResult result, MvcRequestScheme request, JsonObject requestBody, long requsetTicks)
        {
            try
            {
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                //按节点请求转发
                object invokeResult = await MvcDistributeAsync(webSocketOption, context, webSocket, request, requestBody, logger);

                // 发送结果给客户端
                string serialJson = JsonSerializer.Serialize(invokeResult, webSocketOption.DefaultResponseJsonSerialiazerOptions);
                await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(serialJson)), result.MessageType, result.EndOfMessage, CancellationToken.None);
            }
            catch (JsonException ex)
            {
                MvcResponseSchemeException mvcRespEx = new MvcResponseSchemeException(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcForwardSendData_RequestParsingError + ex.Message + Environment.NewLine + ex.StackTrace))
                {
                    Status = 1,
                    RequestTime = requsetTicks,
                    ComplateTime = DateTime.Now.Ticks,
                };
                logger.LogTrace(mvcRespEx, mvcRespEx.Message);
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
        /// <param name="request"></param>
        /// <param name="requsetTicks"></param>
        /// <returns></returns>
        private async Task MvcForwardSendData(WebSocket webSocket, HttpContext context, WebSocketReceiveResult result, MvcRequestScheme request, long requsetTicks)
        {
            try
            {
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                //按节点请求转发
                JsonObject requestBody = null;
                string jsonString = JsonSerializer.Serialize(request.Body, webSocketOption.DefaultRequestJsonSerialiazerOptions);
                JsonNode requestJsonNode = JsonNode.Parse(jsonString);
                if (requestJsonNode != null)
                {
                    requestBody = requestJsonNode.AsObject();
                }
                object invokeResult = await MvcDistributeAsync(webSocketOption, context, webSocket, request, requestBody, logger);

                // 发送结果给客户端
                string serialJson = JsonSerializer.Serialize(invokeResult, webSocketOption.DefaultResponseJsonSerialiazerOptions);
                await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(serialJson)), result.MessageType, result.EndOfMessage, CancellationToken.None);
            }
            catch (JsonException ex)
            {
                MvcResponseSchemeException mvcRespEx = new MvcResponseSchemeException(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcForwardSendData_RequestParsingError + ex.Message + Environment.NewLine + ex.StackTrace))
                {
                    Status = 1,
                    RequestTime = requsetTicks,
                    ComplateTime = DateTime.Now.Ticks,
                };
                logger.LogTrace(mvcRespEx, mvcRespEx.Message);
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
        private async Task MvcForwardSendData(WebSocket webSocket, HttpContext context, WebSocketReceiveResult result, StringBuilder json, long requsetTicks)
        {
            try
            {
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                MvcRequestScheme request = JsonSerializer.Deserialize<MvcRequestScheme>(json.ToString(), webSocketOption.DefaultRequestJsonSerialiazerOptions);
                if (request == null)
                {
                    logger.LogTrace(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcForwardSendData_RequestBodyFormatError + json));
                    return;
                }

                await MvcForwardSendData(webSocket, context, result, request, requsetTicks).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                MvcResponseSchemeException mvcRespEx = new MvcResponseSchemeException(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, I18nText.MvcForwardSendData_RequestParsingError + ex.Message + Environment.NewLine + ex.StackTrace))
                {
                    Status = 1,
                    RequestTime = requsetTicks,
                    ComplateTime = DateTime.Now.Ticks,
                };
                logger.LogTrace(mvcRespEx, mvcRespEx.Message);
            }
            catch (Exception)
            {

                throw;
            }


        }


        #region Newtonsoft.Json

        ///// <summary>
        ///// Forward request to endpoint method
        ///// </summary>
        ///// <param name="webSocketOptions"></param>
        ///// <param name="context"></param>
        ///// <param name="webSocket"></param>
        ///// <param name="request"></param>
        ///// <param name="logger"></param>
        ///// <returns></returns>
        //public static async Task<MvcResponseScheme> MvcDistributeAsync(WebSocketRouteOption webSocketOptions, HttpContext context, WebSocket webSocket, MvcRequestScheme request, ILogger<WebSocketRouteMiddleware> logger)
        //{
        //    long requestTime = DateTime.Now.Ticks;
        //    string requestPath = request.Target.ToLower();
        //    JObject requestBody = request.Body as JObject;
        //    try
        //    {
        //        // 从键值对中获取对应的执行函数 
        //        webSocketOptions.WatchAssemblyContext.WatchMethods.TryGetValue(requestPath, out MethodInfo method);

        //        if (method == null)
        //        {
        //            goto NotFound;
        //        }
        //        Type clss = webSocketOptions.WatchAssemblyContext.WatchEndPoint.FirstOrDefault(x => x.MethodPath == requestPath)?.Class;
        //        if (clss == null)
        //        {
        //            //找不到访问目标

        //            goto NotFound;
        //        }

        //        #region 注入Socket的HttpContext和WebSocket客户端
        //        PropertyInfo contextInfo = clss.GetProperty(webSocketOptions.InjectionHttpContextPropertyName);
        //        PropertyInfo socketInfo = clss.GetProperty(webSocketOptions.InjectionWebSocketClientPropertyName);

        //        webSocketOptions.WatchAssemblyContext.MaxCoustructorParameters.TryGetValue(clss, out ConstructorParameter constructorParameter);

        //        object[] instanceParmas = new object[constructorParameter.ParameterInfos.Length];
        //        // Scope 
        //        var serviceScopeFactory = WebSocketRouteOption.ApplicationServices.GetService<IServiceScopeFactory>();
        //        var serviceScope = serviceScopeFactory.CreateScope();
        //        var scopeIocProvider = serviceScope.ServiceProvider;
        //        for (int i = 0; i < constructorParameter.ParameterInfos.Length; i++)
        //        {
        //            ParameterInfo item = constructorParameter.ParameterInfos[i];

        //            if (webSocketOptions.ApplicationServiceCollection == null)
        //            {
        //                logger.LogWarning("Cannot inject target constructor parameter because DI container WebSocketRouteOption.ApplicationServiceCollection is null.");
        //                break;
        //            }

        //            ServiceDescriptor nonSingleton = webSocketOptions.ApplicationServiceCollection.FirstOrDefault(x => x.ServiceType == item.ParameterType);
        //            if (nonSingleton == null || nonSingleton.Lifetime == ServiceLifetime.Singleton)
        //            {
        //                instanceParmas[i] = WebSocketRouteOption.ApplicationServices.GetService(item.ParameterType);
        //            }
        //            else
        //            {
        //                instanceParmas[i] = scopeIocProvider.GetService(item.ParameterType);
        //            }
        //        }

        //        object inst = Activator.CreateInstance(clss, instanceParmas);

        //        if (contextInfo != null && contextInfo.CanWrite)
        //        {
        //            contextInfo.SetValue(inst, context);
        //        }
        //        if (socketInfo != null && socketInfo.CanWrite)
        //        {
        //            socketInfo.SetValue(inst, webSocket);
        //        }
        //        #endregion

        //        MvcResponseScheme mvcResponse = new MvcResponseScheme() { Status = 0, RequestTime = requestTime };
        //        #region 注入调用方法参数
        //        webSocketOptions.WatchAssemblyContext.MethodParameters.TryGetValue(method, out ParameterInfo[] methodParam);
        //        object invokeResult = default;
        //        if (requestBody == null)
        //        {
        //            object[] args = new object[0];
        //            // 有参方法
        //            if (methodParam.Length > 0)
        //            {
        //                args = new object[methodParam.LongLength];

        //                // 为每个参数设置其类型的默认值
        //                for (int i = 0; i < methodParam.Length; i++)
        //                {
        //                    ParameterInfo item = methodParam[i];
        //                    if (item.HasDefaultValue)
        //                    {
        //                        args[i] = item.DefaultValue;
        //                        continue;
        //                    }
        //                    // 如果参数类型是值类型，则使用类型的零值
        //                    if (item.ParameterType.IsValueType)
        //                    {
        //                        args[i] = Activator.CreateInstance(item.ParameterType);
        //                    }
        //                    else
        //                    {
        //                        // 如果参数类型是引用类型，则使用 null
        //                        args[i] = null;
        //                    }
        //                }
        //            }
        //            else if (methodParam.Length < 0)
        //            {
        //                throw new InvalidOperationException("请求的目标终结点参数异常");
        //            }
        //            invokeResult = method.Invoke(inst, args);
        //        }
        //        else
        //        {
        //            // 异步调用该方法 

        //            Task<object> invoke = new Task<object>(() =>
        //            {
        //                object[] methodParm = new object[methodParam.Length];
        //                for (int i = 0; i < methodParam.Length; i++)
        //                {
        //                    ParameterInfo item = methodParam[i];
        //                    Type methodParmType = item.ParameterType;

        //                    //检测方法中的参数是否是C#定义类型
        //                    bool isBaseType = methodParmType.IsBasicType();
        //                    object parmVal = null;
        //                    try
        //                    {
        //                        if (isBaseType)
        //                        {
        //                            //C#定义数据类型，按参数名取json value
        //                            bool hasVal = requestBody.TryGetValue(item.Name, out JToken jToken);
        //                            if (hasVal)
        //                            {
        //                                try
        //                                {
        //                                    parmVal = jToken.ToObject(methodParmType);
        //                                }
        //                                catch (Exception ex)
        //                                {
        //                                    // containue format error.
        //                                    logger.LogWarning($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> {requestPath} 请求的方法参数数据格式化异常\r\n{ex.Message}\r\n{ex.StackTrace}");
        //                                }
        //                            }
        //                            else
        //                            {
        //                                continue;
        //                            }
        //                        }
        //                        else
        //                        {
        //                            //自定义类，反序列化
        //                            bool hasItemValue = requestBody.TryGetValue(item.Name, out JToken jToken);
        //                            object classParmVal = null;
        //                            if (hasItemValue)
        //                            {
        //                                try
        //                                {
        //                                    classParmVal = jToken.ToObject(methodParmType);
        //                                }
        //                                catch (ArgumentException)
        //                                {
        //                                    // Try use param name get value failure.
        //                                    //throw;
        //                                }
        //                            }

        //                            if (classParmVal == null)
        //                            {
        //                                classParmVal = JsonConvert.DeserializeObject(requestBody.ToString(), methodParmType);
        //                            }
        //                            parmVal = classParmVal;
        //                        }
        //                    }
        //                    catch (JsonReaderException ex)
        //                    {
        //                        //反序列化失败
        //                        //parmVal = null;
        //                        logger.LogError($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> {requestPath} 请求反序列异常\r\n{ex.Message}\r\n{ex.StackTrace}");
        //                    }
        //                    catch (FormatException ex)
        //                    {
        //                        //jToken.ToObject 抛出 类型转换失败
        //                        logger.LogError($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> {requestPath} 请求的方法参数数据格式化异常\r\n{ex.Message}\r\n{ex.StackTrace}");
        //                    }
        //                    methodParm[i] = parmVal;
        //                }

        //                return method.Invoke(inst, methodParm);
        //            });
        //            invoke.Start();

        //            invokeResult = await invoke;
        //        }

        //        //async api support
        //        if (invokeResult is Task)
        //        {
        //            dynamic invokeResultTask = invokeResult;
        //            await invokeResultTask;

        //            invokeResult = invokeResultTask.Result;
        //        }
        //        #endregion

        //        // dispose ioc scope
        //        serviceScope = null;
        //        serviceScope?.Dispose();

        //        mvcResponse.Id = request.Id;
        //        mvcResponse.FromTarget = request.Target;
        //        mvcResponse.Body = invokeResult;
        //        mvcResponse.ComplateTime = DateTime.Now.Ticks;

        //        return mvcResponse;

        //    }
        //    catch (Exception ex)
        //    {
        //        return new MvcResponseScheme() { Id = request.Id, Status = 1, Msg = $@"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> Target:{requestPath}\r\n{ex.Message}\r\n{ex.StackTrace}", RequestTime = requestTime, ComplateTime = DateTime.Now.Ticks };
        //    }

        //NotFound: return new MvcResponseScheme() { Id = request.Id, Status = 2, Msg = $@"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> Target:{requestPath} not found", RequestTime = requestTime, ComplateTime = DateTime.Now.Ticks };
        //}
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
        public static async Task<MvcResponseScheme> MvcDistributeAsync(WebSocketRouteOption webSocketOptions, HttpContext context, WebSocket webSocket, MvcRequestScheme request, JsonObject requestBody, ILogger<WebSocketRouteMiddleware> logger)
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

                webSocketOptions.WatchAssemblyContext.MaxCoustructorParameters.TryGetValue(clss, out ConstructorParameter constructorParameter);

                object[] instanceParmas = new object[constructorParameter.ParameterInfos.Length];
                // 从Scope从DI容器提取目标类构造函数所需的对象 
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

                object invokeResult = default;
                if (requestBody == null || requestBody.Count <= 0)
                {
                    object[] args = Array.Empty<object>();
                    // 有参方法
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
                    else if (methodParam.Length < 0)
                    {
                        throw new InvalidOperationException("请求的目标终结点参数异常");
                    }
                    invokeResult = method.Invoke(inst, args);
                }
                else
                {
                    // 异步调用目标方法 
                    Task<object> invoke = new Task<object>(() =>
                    {
                        object[] methodParm = new object[methodParam.Length];
                        for (int i = 0; i < methodParam.Length; i++)
                        {
                            ParameterInfo item = methodParam[i];

                            // 检测方法中的参数是否是C#定义的基本类型
                            object parmVal = null;
                            try
                            {
                                // 按参数名提取JsonNode
                                bool hasVal = requestBody.TryGetPropertyValue(item.Name, out JsonNode JProp);
                                if (hasVal)
                                {
                                    parmVal = item.ParameterType.ConvertTo(requestBody[item.Name]);
                                }
                                else
                                {
                                    continue;
                                }
                            }
                            //catch (JsonException ex)
                            //{
                            //    // 反序列化失败
                            //    logger.LogTrace($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> {requestPath} An exception occurred while operating the request data JSON\r\n{ex.Message}\r\n{ex.StackTrace}");
                            //}
                            catch (FormatException ex)
                            {
                                // ConvertTo 抛出 类型转换失败
                                logger.LogTrace(string.Format(I18nText.WS_INTERACTIVE_TEXT_TEMPALTE, context.Connection.RemoteIpAddress, context.Connection.RemotePort, context.Connection.Id, $"{requestPath}.{item.Name}" + I18nText.MvcForwardSendData_RequestBodyParameterFormatError + ex.Message + Environment.NewLine + ex.StackTrace));
                            }
                            methodParm[i] = parmVal;
                        }

                        return method.Invoke(inst, methodParm);
                    });
                    invoke.Start();

                    invokeResult = await invoke;
                }

                // Async api support
                if (invokeResult is Task)
                {
                    dynamic invokeResultTask = invokeResult;
                    await invokeResultTask;

                    invokeResult = invokeResultTask.Result;
                }
                #endregion

                // Dispose ioc scope
                serviceScope?.Dispose();
                serviceScope = null;

                mvcResponse.Id = request.Id;
                mvcResponse.Target = request.Target;
                mvcResponse.Body = invokeResult;
                mvcResponse.ComplateTime = DateTime.Now.Ticks;

                return mvcResponse;

            }
            catch (Exception ex)
            {
                var resp = new MvcResponseScheme() { Id = request.Id, Status = 1, RequestTime = requestTime, ComplateTime = DateTime.Now.Ticks };
                if (webSocketOptions.IsDevelopment)
                {
                    resp.Msg = $@"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> Target:{requestPath}\r\n{ex.Message}\r\n{ex.StackTrace}";
                }
                return resp;
            }

        NotFound: return new MvcResponseScheme() { Id = request.Id, Status = 2, Msg = $@"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> Target:{requestPath} not found", RequestTime = requestTime, ComplateTime = DateTime.Now.Ticks };
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
                await MvcChannel_OnDisconnectioned(context, webSocketOptions, context.Request.Path, logger);

                await webSocketOptions.OnDisConnectioned(context, webSocketOptions, context.Request.Path, logger);
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
        public virtual async Task MvcChannel_OnDisconnectioned(HttpContext context, WebSocketRouteOption webSocketOptions, string channel, ILogger<WebSocketRouteMiddleware> logger)
        {
            await Task.CompletedTask;
        }


    }
}
