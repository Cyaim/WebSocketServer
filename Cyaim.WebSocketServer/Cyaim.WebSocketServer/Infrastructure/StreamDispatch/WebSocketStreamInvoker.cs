using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Injectors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure.StreamDispatch
{
    /// <summary>
    /// Neutral result of invoking a streaming endpoint; each channel wraps it in its own response scheme.
    /// 调用流式端点的中立结果；各通道将其包装为自己的响应体。
    /// </summary>
    public readonly struct StreamInvokeResult
    {
        /// <summary>0 = ok, 1 = endpoint threw, 2 = endpoint not found.</summary>
        public int Status { get; }
        public object Body { get; }
        public string Msg { get; }
        public StreamInvokeResult(int status, object body, string msg)
        {
            Status = status;
            Body = body;
            Msg = msg;
        }
    }

    /// <summary>
    /// Shared dispatch for streaming endpoints (used by both the MVC and MessagePack channels): resolve the
    /// endpoint method, create the controller instance, inject HttpContext/WebSocket, bind parameters by type
    /// (Stream = the payload body, CancellationToken = ct, any other class/interface = deserialized from the
    /// header "meta"), then invoke it. The payload is never buffered — the endpoint reads <paramref name="body"/>
    /// as it is fed by the caller.
    /// 流式端点的共享分发：解析端点方法、建实例、注入、按类型绑定形参(Stream=负载体、CancellationToken=ct、
    /// 其余类/接口=从头部 meta 反序列化)并调用。负载不缓冲，端点边读 body 边处理。
    /// </summary>
    public static class WebSocketStreamInvoker
    {
        public static async Task<StreamInvokeResult> InvokeAsync(
            WebSocketRouteOption options, HttpContext context, WebSocket webSocket,
            string target, JsonNode meta, Stream body, CancellationToken cancellationToken, ILogger logger)
        {
            IServiceScope serviceScope = null;
            try
            {
                if (options.WatchAssemblyContext == null)
                {
                    return new StreamInvokeResult(2, null, null);
                }
                options.WatchAssemblyContext.WatchMethods.TryGetValue(target, out MethodInfo method);
                if (method == null)
                {
                    return new StreamInvokeResult(2, null, null);
                }
                Type targetClass = options.WatchAssemblyContext.GetEndpointClass(target);
                if (targetClass == null)
                {
                    return new StreamInvokeResult(2, null, null);
                }

                options.WatchAssemblyContext.MaxConstructorParameters.TryGetValue(targetClass, out ConstructorParameter constructorParameter);
                int ctorParamCount = constructorParameter.ParameterInfos?.Length ?? 0;
                object[] instanceParams = ctorParamCount == 0 ? Array.Empty<object>() : new object[ctorParamCount];
                var serviceScopeFactory = WebSocketRouteOption.ApplicationServices.GetService<IServiceScopeFactory>();
                serviceScope = serviceScopeFactory.CreateScope();
                var scopeIocProvider = serviceScope.ServiceProvider;
                for (int i = 0; i < ctorParamCount; i++)
                {
                    instanceParams[i] = scopeIocProvider.GetService(constructorParameter.ParameterInfos[i].ParameterType);
                }
                object inst = Activator.CreateInstance(targetClass, instanceParams);
                var injectorFactory = options.InjectorFactory ?? new EndpointInjectorFactory(options);
                injectorFactory.GetOrCreateInjector(targetClass).Inject(inst, context, webSocket);

                options.WatchAssemblyContext.MethodParameters.TryGetValue(method, out ParameterInfo[] methodParam);
                methodParam ??= method.GetParameters();
                object[] args = methodParam.Length == 0 ? Array.Empty<object>() : new object[methodParam.Length];
                for (int i = 0; i < methodParam.Length; i++)
                {
                    Type pt = methodParam[i].ParameterType;
                    if (typeof(Stream).IsAssignableFrom(pt)) { args[i] = body; }
                    else if (pt == typeof(CancellationToken)) { args[i] = cancellationToken; }
                    else if ((pt.IsClass || pt.IsInterface) && pt != typeof(string))
                    {
                        try { args[i] = meta?.Deserialize(pt, options.DefaultRequestJsonSerializerOptions); }
                        catch { args[i] = null; }
                    }
                    else if (methodParam[i].HasDefaultValue) { args[i] = methodParam[i].DefaultValue; }
                    else { args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null; }
                }

                object invokeResult;
                object raw = method.Invoke(inst, args);
                if (raw is Task task)
                {
                    await task.ConfigureAwait(false);
                    if (method.ReturnType == typeof(Task)) { invokeResult = null; }
                    else
                    {
                        Func<Task, object> taskResultGetter = null;
                        options.WatchAssemblyContext.MethodTaskResultGetters?.TryGetValue(method, out taskResultGetter);
                        invokeResult = taskResultGetter != null ? taskResultGetter(task) : null;
                    }
                }
                else { invokeResult = raw; }

                return new StreamInvokeResult(0, invokeResult, null);
            }
            catch (Exception ex)
            {
                if (ex is TargetInvocationException tiEx && tiEx.InnerException != null) { ex = tiEx.InnerException; }
                logger?.LogInformation(ex, ex.Message);
                return new StreamInvokeResult(1, null, ex.Message);
            }
            finally
            {
                serviceScope?.Dispose();
            }
        }
    }
}
