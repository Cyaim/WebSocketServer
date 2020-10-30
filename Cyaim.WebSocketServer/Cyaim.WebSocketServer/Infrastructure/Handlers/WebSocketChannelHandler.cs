using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
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

namespace Cyaim.WebSocketServer.Infrastructure.Handlers
{
    public class WebSocketChannelHandler
    {
        /// <summary>
        /// Connected clients
        /// </summary>
        public static ConcurrentDictionary<string, WebSocketClient> Clients { get; set; } = new ConcurrentDictionary<string, WebSocketClient>();

        private HttpContext context;
        private ILogger<WebSocketRouteMiddleware> logger;
        private WebSocketRouteOption webSocketOption;
        private WebSocket webSocket;

        /// <summary>
        /// Mvc Channel entry
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocketManager"></param>
        /// <param name="logger"></param>
        /// <param name="webSocketOptions"></param>
        /// <returns></returns>
        public async Task MvcChannelHandler(HttpContext context, WebSocketManager webSocketManager, ILogger<WebSocketRouteMiddleware> logger, WebSocketRouteOption webSocketOptions)
        {
            this.context = context;
            this.logger = logger;
            this.webSocketOption = webSocketOptions;

            try
            {
                if (webSocketManager.IsWebSocketRequest)
                {
                    // Event instructions whether connection
                    bool ifContinue = await webSocketOptions.OnBeforeConnection(context, webSocketOptions, context.Request.Path, logger);
                    if (!ifContinue)
                    {
                        return;
                    }

                    using (WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync())
                    {
                        this.webSocket = webSocket;

                        logger.LogInformation($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> 连接已建立({context.Connection.Id})");
                        bool succ = Clients.TryAdd(context.Connection.Id, new WebSocketClient() { WebSocket = webSocket, Channel = context.Request.Path });
                        if (succ)
                        {
                            await Forward(context, webSocket);
                        }
                        else
                        {
                            throw new InvalidOperationException("客户端登录失败");
                        }


                    }
                }
                else
                {
                    logger.LogWarning($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> 拒绝连接，缺少请求头({context.Connection.Id})");
                    context.Response.StatusCode = 400;
                }
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                await OnDisConnected(context, this.webSocket, webSocketOptions, logger);
            }

        }


        /// <summary>
        /// Forward by WebSocket transfer type
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocket"></param>
        /// <returns></returns>
        private async Task Forward(HttpContext context, WebSocket webSocket)
        {
            var buffer = new byte[1024 * 4];
            WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            switch (result.MessageType)
            {
                case WebSocketMessageType.Binary:
                    await BinaryForward(context, webSocket, result, buffer);
                    break;
                case WebSocketMessageType.Text:
                    await TextForward(result, buffer);
                    break;
            }

            //链接断开
            await webSocket.CloseAsync(webSocket.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
        }

        /// <summary>
        /// Type by Text transfer
        /// </summary>
        /// <param name="result"></param>
        /// <param name="buffer"></param>
        /// <returns></returns>
        private async Task TextForward(WebSocketReceiveResult result, byte[] buffer)
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
                    await TextForwardSendData(result, json, requestTime);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, ex.Message);
                }
                finally
                {
                    //json = string.Empty;
                    json = json.Clear();
                }
            }

            //等待客户端发送数据，第二次接受数据
            while (!result.CloseStatus.HasValue)
            {
                try
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    requestTime = DateTime.Now.Ticks;

                    json = json.Append(Encoding.UTF8.GetString(buffer[..result.Count]));

                    if (!result.EndOfMessage || result.CloseStatus.HasValue)
                    {
                        continue;
                    }

                    await TextForwardSendData(result, json, requestTime);

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


        }

        private async Task TextForwardSendData(WebSocketReceiveResult result, StringBuilder json, long requsetTicks)
        {
            try
            {
                MvcRequestScheme request = JsonConvert.DeserializeObject<MvcRequestScheme>(json.ToString());

                //按节点请求转发
                object invokeResult = await DistributeAsync(webSocketOption, context, webSocket, request, logger);
                string serialJson = null;
                if (string.IsNullOrEmpty(request.Id))
                {
                    //如果客户端请求不包含Id，响应内容则移除Id
                    JObject jo = JObject.FromObject(invokeResult ?? string.Empty);
                    jo.Remove("Id");

                    serialJson = JsonConvert.SerializeObject(jo);
                }
                else
                {
                    serialJson = JsonConvert.SerializeObject(invokeResult);
                }


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
                await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(mvcResponse))), result.MessageType, result.EndOfMessage, CancellationToken.None);
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
                await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(mvcResponse))), result.MessageType, result.EndOfMessage, CancellationToken.None);
            }
            catch (Exception)
            {

                throw;
            }


        }

        private async Task BinaryForward(HttpContext context, WebSocket webSocket, WebSocketReceiveResult result, byte[] buffer)
        {

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
        public static async Task<MvcResponseScheme> DistributeAsync(WebSocketRouteOption webSocketOptions, HttpContext context, WebSocket webSocket, MvcRequestScheme request, ILogger<WebSocketRouteMiddleware> logger)
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

                    for (int i = 0; i < constructorParameter.ParameterInfos.Length; i++)
                    {
                        ParameterInfo item = constructorParameter.ParameterInfos[i];

                        instanceParmas[i] = WebSocketRouteOption.ApplicationServices.GetService(item.ParameterType);
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

                    #region 注入调用方法参数
                    MvcResponseScheme mvcResponse = new MvcResponseScheme() { Status = 0 };
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
                                            parmVal = jToken.ToObject(methodParmType);
                                        }
                                        else
                                        {
                                            continue;
                                        }
                                    }
                                    else
                                    {
                                        //自定义类，反序列化
                                        var classParmVal = JsonConvert.DeserializeObject(requestBody.ToString(), methodParmType);

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
                    #endregion

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
        private async Task OnDisConnected(HttpContext context, WebSocket webSocket, WebSocketRouteOption webSocketOptions, ILogger<WebSocketRouteMiddleware> logger)
        {
            string msg = string.Empty;
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

            logger.LogInformation($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> 连接已断开({context.Connection.Id})\r\nStatus:{webSocket.CloseStatus}\r\n{msg}");

            try
            {
                await webSocketOptions.OnDisConnectioned(context, webSocketOptions, context.Request.Path, logger);
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

    }
}
