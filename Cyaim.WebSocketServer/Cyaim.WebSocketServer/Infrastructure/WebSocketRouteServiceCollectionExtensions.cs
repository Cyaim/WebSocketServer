using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Cyaim.WebSocketServer.Infrastructure
{
    /// <summary>
    /// WebSocket Route service extensions
    /// </summary>
    public static class WebSocketRouteServiceCollectionExtensions
    {
        /// <summary>
        /// Configure WebSocketRoute Middleware
        /// </summary>
        /// <param name="services"></param>
        /// <param name="setupAction"></param>
        public static void ConfigureWebSocketRoute(this IServiceCollection services, Action<WebSocketRouteOption> setupAction)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }
            if (setupAction == null)
            {
                throw new ArgumentNullException(nameof(setupAction));
            }
            var wsrOptions = new WebSocketRouteOption();
            setupAction(wsrOptions);
            if (wsrOptions.ApplicationServiceCollection == null)
            {
                throw new ArgumentNullException(nameof(wsrOptions.ApplicationServiceCollection), "WebSocketRouteOption.ApplicationServiceCollection parameter is required and cannot be null.");
            }
            if (wsrOptions.WebSocketChannels == null || wsrOptions.WebSocketChannels.Count < 1)
            {
                Console.WriteLine("WebSocket -> 没有定义WebSocket数据处理通道");
            }
            Assembly assembly;
            // 指定标记WebSocket特性的程序集路径，默认为本程序集
            if (string.IsNullOrEmpty(wsrOptions?.WatchAssemblyPath))
            {
                assembly = Assembly.GetEntryAssembly();
                wsrOptions.WatchAssemblyPath = assembly.Location;
            }
            else
            {
                assembly = Assembly.LoadFile(wsrOptions.WatchAssemblyPath);
            }
            if (wsrOptions.WatchAssemblyContext == null)
            {
                wsrOptions.WatchAssemblyContext = new WatchAssemblyContext();
            }
            
            #region 计算WebSocketEndPoint

            var points = new List<WebSocketEndPoint>();
            // 在程序集中查询带有指定命名空间前缀的类
            var assemblyName = string.IsNullOrEmpty(wsrOptions.WatchAssemblyNamespacePrefix) ? assembly.FullName.Split()[0]?.Trim(',') + ".Controllers" : wsrOptions.WatchAssemblyNamespacePrefix;
            var types = assembly.GetTypes().Where(x => !x.IsNestedPrivate && x.FullName.StartsWith(assemblyName)).ToList();
            wsrOptions.WatchAssemblyContext.WatchAssemblyPath = wsrOptions.WatchAssemblyPath;
            wsrOptions.WatchAssemblyContext.WatchAssemblyTypes = types;
            foreach (var item in types)
            {
                // 从类提取标记了的方法
                var accessParam = GetClassAccessParam(item);
                foreach (var paramItem in accessParam)
                {
                    if (paramItem == null)
                    {
                        continue;
                    }
                    if (string.IsNullOrEmpty(paramItem.Methods.FirstOrDefault()))
                    {
                        paramItem.Methods = new[] { paramItem.Action };
                    }

                    // 方法的类名替换Controller
                    var endPointPath = $"{paramItem.Controller.Replace("Controller", string.Empty)}.{paramItem.Methods.FirstOrDefault()}";
                    // 终结点全部小写
                    paramItem.MethodPath = endPointPath.ToLower();
                    paramItem.Class = item;
                    Console.WriteLine($"WebSocket加载成功 -> {endPointPath}");
                }
                points.AddRange(accessParam);
            }
            wsrOptions.WatchAssemblyContext.WatchEndPoint = points.ToArray();
            wsrOptions.WatchAssemblyContext.WatchMethods =
                new ConcurrentDictionary<string, MethodInfo>(wsrOptions.WatchAssemblyContext.WatchEndPoint.ToDictionary(x => x.MethodPath, x => x.MethodInfo));

            #endregion

            #region 计算构造函数与构造函数中的参数

            var assConstr = new Dictionary<Type, ConstructorInfo[]>();
            var assConstrParm = new Dictionary<Type, ConstructorParameter[]>();
            foreach (var item in wsrOptions.WatchAssemblyContext.WatchAssemblyTypes)
            {
                var constructorInfos = item.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                assConstrParm.Add(item, constructorInfos.Select(constrItem => new ConstructorParameter
                {
                    ConstructorInfo = constrItem,
                    ParameterInfos = constrItem.GetParameters()
                }).ToArray());
                assConstr.Add(item, constructorInfos);
            }
            wsrOptions.WatchAssemblyContext.AssemblyConstructors = assConstr;
            wsrOptions.WatchAssemblyContext.ConstructorParameters = assConstrParm;

            #endregion

            #region 计算构造函数里参数最多的

            var maxAssConstrParm = new Dictionary<Type, ConstructorParameter>();
            foreach (var item in assConstrParm)
            {
                var pg = item.Value.GroupBy(x => x.ParameterInfos.Length);
                var maxKey = pg.Select(x => x.Key).Max();
                var temp = pg.FirstOrDefault(x => x.Key == maxKey).FirstOrDefault();
                maxAssConstrParm.Add(item.Key, temp);
            }
            wsrOptions.WatchAssemblyContext.MaxConstructorParameters = maxAssConstrParm;

            #endregion

            #region 计算类公开方法的参数

            var methodPamams = new Dictionary<MethodInfo, ParameterInfo[]>();
            foreach (var item in points.Select(x => x.MethodInfo))
            {
                var parameterInfo = item.GetParameters();
                methodPamams.Add(item, parameterInfo);
            }
            wsrOptions.WatchAssemblyContext.MethodParameters = methodPamams;

            #endregion

            services.AddSingleton(x => wsrOptions);

            //services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            foreach (var item in wsrOptions.WebSocketChannels.Keys)
            {
                Console.WriteLine($"Define websocket channel on: {item}");
            }

            // 获取当前线程的默认文化
            var defaultCultureInfo = CultureInfo.CurrentCulture;
        }

        /// <summary>
        /// Get assembly controller "WebSocket" EndPoint
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public static WebSocketEndPoint[] GetClassAccessParam<T>()
        {
            var type = typeof(T);
            return GetClassAccessParam(type);
        }

        /// <summary>
        /// Get assembly controller "WebSocket" EndPoint
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static WebSocketEndPoint[] GetClassAccessParam(Type type)
        {
            var methodLevel = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                  .Select(x => x.GetCustomAttributes<WebSocketAttribute>().Select(a =>
                                      new WebSocketEndPoint
                                      {
                                          Action = x.Name,
                                          Controller = x.ReflectedType?.Name,
                                          Methods = new[] { a.Method },
                                          MethodInfo = x
                                      })).SelectMany((x, y) => x);
            var points =
                methodLevel.GroupBy(x => x.Controller + x.Action).Select(x =>
                {
                    var first = x.FirstOrDefault();
                    first.Methods = x.Select(m => m.Methods).SelectMany((y, z) => y).Distinct().ToArray();
                    return first;
                });
            return points.ToArray();
        }
    }
}