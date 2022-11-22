using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Microsoft.CSharp;


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

            WebSocket webSocketCloseInst = null;

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

                    using (WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync())
                    {
                        webSocketCloseInst = webSocket;

                        logger.LogInformation($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> Connected({context.Connection.Id})");
                        bool succ = Clients.TryAdd(context.Connection.Id, webSocket);
                        if (succ)
                        {
                            await MvcForward(context, webSocket);
                        }
                        else
                        {
                            logger.LogError($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> Client connect failed({context.Connection.Id})");
                        }


                    }
                }
                else
                {
                    logger.LogTrace($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> Connection denied:request header error({context.Connection.Id})");
                    context.Response.StatusCode = 400;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, ex.Message);
                //throw;
            }
            finally
            {
                await MvcChannel_OnDisConnected(context, webSocketCloseInst, webSocketOptions, logger);
            }

        }

        /// <summary>
        /// Forward by WebSocket transfer type
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocket"></param>
        /// <returns></returns>
        private async Task MvcForward(HttpContext context, WebSocket webSocket)
        {
            var buffer = new byte[ReceiveTextBufferSize];
            ArraySegment<byte> bufferSeg = new ArraySegment<byte>(buffer);
            
            try
            {
                WebSocketReceiveResult result = await webSocket.ReceiveAsync(bufferSeg, CancellationToken.None);
                //switch (result.MessageType)
                //{
                //    case WebSocketMessageType.Binary:
                //        await MvcBinaryForward(context, webSocket, result, buffer);
                //        break;
                //    case WebSocketMessageType.Text:
                //        await MvcTextForward(webSocket, context, result, buffer);
                //        break;
                //}
                await MvcForward(webSocket, context, result, bufferSeg);

                //链接断开
                await webSocket.CloseAsync(webSocket.CloseStatus == null ?
                    webSocket.State == WebSocketState.Aborted ?
                    WebSocketCloseStatus.InternalServerError : WebSocketCloseStatus.NormalClosure
                    : webSocket.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
            }
            catch (OperationCanceledException ex)
            {
                logger.LogInformation($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> 终止接收数据({context.Connection.Id})\r\nStatus:{(webSocket.CloseStatus.HasValue ? webSocket.CloseStatus.ToString() : "ServerClose")}\r\n{ex.Message}");
            }
        }

        /// <summary>
        /// Handle request content
        /// </summary>
        /// <param name="result"></param>
        /// <param name="buffer"></param>
        /// <param name="webSocket"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        private async Task MvcForward(WebSocket webSocket, HttpContext context, WebSocketReceiveResult result, ArraySegment<byte> buffer)
        {

            long requestTime = DateTime.Now.Ticks;
            StringBuilder json = new StringBuilder();

            //处理第一次返回的数据
            json = json.Append(Encoding.UTF8.GetString(buffer[..result.Count]));

            //第一次接受已经接受完数据了
            if (result.EndOfMessage)
            {
                try
                {
                    await MvcForwardSendData(webSocket, context, result, json, requestTime);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, ex.Message);
                }
                finally
                {
                    json = json.Clear();
                }
            }

            //等待客户端发送数据，第二次接受数据
            while (!result.CloseStatus.HasValue)
            {
                try
                {
                    if (!(webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseSent))
                    {
                        break;
                    }
                    
                    result = await webSocket.ReceiveAsync(buffer, CancellationToken.None);
                    requestTime = DateTime.Now.Ticks;

                    json = json.Append(Encoding.UTF8.GetString(buffer[..result.Count]));

                    if (!result.EndOfMessage || result.CloseStatus.HasValue)
                    {
                        continue;
                    }

                    await MvcForwardSendData(webSocket, context, result, json, requestTime);

                    json = json.Clear();
                }
                catch (OperationCanceledException ex)
                {
                    logger.LogInformation($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> 终止接收数据({context.Connection.Id})\r\nStatus:{(webSocket.CloseStatus.HasValue ? webSocket.CloseStatus.ToString() : "ServerClose")}\r\n{ex.Message}");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, ex.Message);
                }

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
                MvcRequestScheme request = JsonConvert.DeserializeObject<MvcRequestScheme>(json.ToString());
                if (request == null)
                {
                    logger.LogTrace("Json格式错误，请求数据：" + json);
                    //throw new JsonSerializationException("Json格式错误，请求数据：" + json);
                    return;
                }

                //按节点请求转发
                object invokeResult = await MvcDistributeAsync(webSocketOption, context, webSocket, request, logger);

                string serialJson = null;
                JObject jo = JObject.FromObject(invokeResult ?? string.Empty);
                if (string.IsNullOrEmpty(request.Id))
                {
                    //如果客户端请求不包含Id，响应内容则移除Id
                    jo.Remove("Id");
                }

                // 如果没有抛出异常则移除Msg
                if (jo.TryGetValue("Msg", out JToken v))
                {
                    if (string.IsNullOrEmpty(v.ToString()))
                    {
                        jo.Remove("Msg");
                    }
                }

                serialJson = JsonConvert.SerializeObject(jo);

                await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(serialJson)), result.MessageType, result.EndOfMessage, CancellationToken.None);
            }
            catch (JsonSerializationException ex)
            {

                MvcResponseScheme mvcResponse = new MvcResponseScheme()
                {
                    Status = 1,
                    RequestTime = requsetTicks,
                    ComplateTime = DateTime.Now.Ticks,
                    Msg = $"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> \r\n {ex.Message}\r\n{ex.StackTrace}",
                };
                logger.LogDebug(JsonConvert.SerializeObject(mvcResponse));

                //if (result.MessageType != WebSocketMessageType.Close)
                //{
                //    await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(mvcResponse))), result.MessageType, result.EndOfMessage, CancellationToken.None);
                //}
            }
            catch (JsonReaderException ex)
            {
                MvcResponseScheme mvcResponse = new MvcResponseScheme()
                {
                    Status = 1,
                    RequestTime = requsetTicks,
                    ComplateTime = DateTime.Now.Ticks,
                    Msg = $"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> 请求解析错误\r\n {ex.Message}\r\n{ex.StackTrace}",
                };
                logger.LogDebug(JsonConvert.SerializeObject(mvcResponse));
                //if (result.MessageType != WebSocketMessageType.Close)
                //{
                //    await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(mvcResponse))), result.MessageType, result.EndOfMessage, CancellationToken.None);
                //}
            }
            catch (Exception)
            {

                throw;
            }


        }

        /// <summary>
        /// Forward request to endpoint method
        /// </summary>
        /// <param name="webSocketOptions"></param>
        /// <param name="context"></param>
        /// <param name="webSocket"></param>
        /// <param name="request"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public static async Task<MvcResponseScheme> MvcDistributeAsync(WebSocketRouteOption webSocketOptions, HttpContext context, WebSocket webSocket, MvcRequestScheme request, ILogger<WebSocketRouteMiddleware> logger)
        {
            long requestTime = DateTime.Now.Ticks;
            string requestPath = request.Target.ToLower();
            JObject requestBody = request.Body as JObject;

            try
            {
                // 从键值对中获取对应的执行函数 
                webSocketOptions.WatchAssemblyContext.WatchMethods.TryGetValue(requestPath, out MethodInfo method);

                if (method != null)
                {
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
                    // Scope 
                    var serviceScopeFactory = WebSocketRouteOption.ApplicationServices.GetService<IServiceScopeFactory>();
                    var serviceScope = serviceScopeFactory.CreateScope();
                    var scopeIocProvider = serviceScope.ServiceProvider;
                    for (int i = 0; i < constructorParameter.ParameterInfos.Length; i++)
                    {
                        ParameterInfo item = constructorParameter.ParameterInfos[i];

                        if (webSocketOptions.ApplicationServiceCollection == null)
                        {
                            logger.LogWarning("Cannot inject target constructor parameter because DI container WebSocketRouteOption.ApplicationServiceCollection is null.", "");
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
                    object invokeResult = default;
                    if (requestBody == null)
                    {
                        //无参方法
                        invokeResult = method.Invoke(inst, new object[0]);
                    }
                    else
                    {
                        // 异步调用该方法 
                        webSocketOptions.WatchAssemblyContext.MethodParameters.TryGetValue(method, out ParameterInfo[] methodParam);

                        Task<object> invoke = new Task<object>(() =>
                        {
                            object[] methodParm = new object[methodParam.Length];
                            for (int i = 0; i < methodParam.Length; i++)
                            {
                                ParameterInfo item = methodParam[i];
                                Type methodParmType = item.ParameterType;

                                //检测方法中的参数是否是C#定义类型
                                bool isBaseType = methodParmType.IsBasicType();
                                object parmVal = null;
                                try
                                {
                                    if (isBaseType)
                                    {
                                        //C#定义数据类型，按参数名取json value
                                        bool hasVal = requestBody.TryGetValue(item.Name, out JToken jToken);
                                        if (hasVal)
                                        {
                                            try
                                            {
                                                parmVal = jToken.ToObject(methodParmType);
                                            }
                                            catch (Exception ex)
                                            {
                                                // containue format error.
                                                logger.LogWarning($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> {requestPath} 请求的方法参数数据格式化异常\r\n{ex.Message}\r\n{ex.StackTrace}");
                                            }
                                        }
                                        else
                                        {
                                            continue;
                                        }
                                    }
                                    else
                                    {
                                        //自定义类，反序列化
                                        bool hasItemValue = requestBody.TryGetValue(item.Name, out JToken jToken);
                                        object classParmVal = null;
                                        if (hasItemValue)
                                        {
                                            try
                                            {
                                                classParmVal = jToken.ToObject(methodParmType);
                                            }
                                            catch (ArgumentException)
                                            {
                                                // Try use param name get value failure.
                                                //throw;
                                            }
                                        }

                                        if (classParmVal == null)
                                        {
                                            classParmVal = JsonConvert.DeserializeObject(requestBody.ToString(), methodParmType);
                                        }
                                        parmVal = classParmVal;
                                    }
                                }
                                catch (JsonReaderException ex)
                                {
                                    //反序列化失败
                                    //parmVal = null;
                                    logger.LogError($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> {requestPath} 请求反序列异常\r\n{ex.Message}\r\n{ex.StackTrace}");
                                }
                                catch (FormatException ex)
                                {
                                    //jToken.ToObject 抛出 类型转换失败
                                    logger.LogError($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> {requestPath} 请求的方法参数数据格式化异常\r\n{ex.Message}\r\n{ex.StackTrace}");
                                }
                                methodParm[i] = parmVal;
                            }

                            return method.Invoke(inst, methodParm);
                        });
                        invoke.Start();

                        invokeResult = await invoke;
                    }

                    //async api support
                    if (invokeResult is Task)
                    {
                        dynamic invokeResultTask = invokeResult;
                        await invokeResultTask;

                        invokeResult = invokeResultTask.Result;
                    }
                    #endregion

                    // dispose ioc scope
                    serviceScope = null;
                    serviceScope?.Dispose();

                    mvcResponse.Id = request.Id;
                    mvcResponse.Body = invokeResult;
                    mvcResponse.ComplateTime = DateTime.Now.Ticks;

                    return mvcResponse;
                }
            }
            catch (Exception ex)
            {
                return new MvcResponseScheme() { Id = request.Id, Status = 1, Msg = $@"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> Target:{requestPath}\r\n{ex.Message}\r\n{ex.StackTrace}", RequestTime = requestTime, ComplateTime = DateTime.Now.Ticks };
            }

        NotFound: return new MvcResponseScheme() { Id = request.Id, Status = 2, Msg = $@"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> Target:{requestPath} not found", RequestTime = requestTime, ComplateTime = DateTime.Now.Ticks };
        }

        /// <summary>
        /// Client close connection
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocket"></param>
        /// <param name="webSocketOptions"></param>
        /// <param name="logger"></param>
        private async Task MvcChannel_OnDisConnected(HttpContext context, WebSocket webSocket, WebSocketRouteOption webSocketOptions, ILogger<WebSocketRouteMiddleware> logger)
        {
            string msg = string.Empty;

            if ((webSocket?.CloseStatus.HasValue).HasValue)
            {
                switch (webSocket.CloseStatus.Value)
                {
                    case WebSocketCloseStatus.Empty:
                        msg = "No error specified.";
                        break;
                    case WebSocketCloseStatus.EndpointUnavailable:
                        msg = "Indicates an endpoint is being removed. Either the server or client will become unavailable.";
                        break;
                    case WebSocketCloseStatus.InternalServerError:
                        msg = "The connection will be closed by the server because of an error on the server.";
                        break;
                    case WebSocketCloseStatus.InvalidMessageType:
                        msg = "The client or server is terminating the connection because it cannot accept the data type it received.";
                        break;
                    case WebSocketCloseStatus.InvalidPayloadData:
                        msg = "The client or server is terminating the connection because it has received data inconsistent with the message type.";
                        break;
                    case WebSocketCloseStatus.MandatoryExtension:
                        msg = "The client is terminating the connection because it expected the server to negotiate an extension.";
                        break;
                    case WebSocketCloseStatus.MessageTooBig:
                        msg = "Reserved for future use.";
                        break;
                    case WebSocketCloseStatus.NormalClosure:
                        msg = "The connection has closed after the request was fulfilled.";
                        break;
                    case WebSocketCloseStatus.PolicyViolation:
                        msg = "The connection will be closed because an endpoint has received a messagethat violates its policy.";
                        break;
                    case WebSocketCloseStatus.ProtocolError:
                        msg = "The client or server is terminating the connection because of a protocol error.";
                        break;
                    default:
                        break;
                }
            }
            else
            {
                msg = "The connection of this client is shutting down.";
            }

            logger.LogInformation($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> 连接已断开({context.Connection.Id})\r\nStatus:{webSocket?.CloseStatus.ToString() ?? "NoHandshakeSucceeded"}\r\n{msg}");

            try
            {
                await MvcChannel_OnDisConnectioned(context, webSocketOptions, context.Request.Path, logger);

                await webSocketOptions.OnDisConnectioned(context, webSocketOptions, context.Request.Path, logger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, ex.Message);
            }
            finally
            {
                bool wsExists = Clients.ContainsKey(context.Connection.Id);
                if (wsExists)
                {
                    Clients.TryRemove(context.Connection.Id, out var ws);
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
        /// Mvc channel DisConnectionedEvent entry
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocketOptions"></param>
        /// <param name="channel"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        public virtual async Task MvcChannel_OnDisConnectioned(HttpContext context, WebSocketRouteOption webSocketOptions, string channel, ILogger<WebSocketRouteMiddleware> logger)
        {
            await Task.CompletedTask;
        }


    }
}
