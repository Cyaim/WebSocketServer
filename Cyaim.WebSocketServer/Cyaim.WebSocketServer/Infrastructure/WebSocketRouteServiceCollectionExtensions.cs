using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Authentication.ExtendedProtection;
using System.Text;

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

            WebSocketRouteOption wsrOptions = new WebSocketRouteOption();
            setupAction(wsrOptions);
            if (wsrOptions == null)
            {
                wsrOptions = new WebSocketRouteOption();
            }

            if (wsrOptions.ApplicationServiceCollection == null)
            {
                throw new ArgumentNullException("WebSocketRouteOption.ApplicationServiceCollection parameter is required and cannot be null.");
            }

            if (wsrOptions.WebSocketChannels == null || wsrOptions.WebSocketChannels.Count < 1)
            {
                Console.WriteLine("WebSocket -> 没有定义WebSocket数据处理通道");
            }


            Assembly assembly = null;
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

            List<WebSocketEndPoint> points = new List<WebSocketEndPoint>();
            string assemblyName = assembly.FullName.Split()[0]?.Trim(',') + ".Controllers";
            var types = assembly.GetTypes().Where(x => !x.IsNestedPrivate && x.FullName.StartsWith(assemblyName)).ToList();

            wsrOptions.WatchAssemblyContext.WatchAssemblyPath = wsrOptions.WatchAssemblyPath;
            wsrOptions.WatchAssemblyContext.WatchAssemblyTypes = types;


            foreach (Type item in types)
            {
                var accessParm = GetClassAccessParm(item);
                foreach (WebSocketEndPoint parmItem in accessParm)
                {
                    if (parmItem == null)
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(parmItem.Methods.FirstOrDefault()))
                    {
                        parmItem.Methods = new string[] { parmItem.Action };
                    }

                    //not supported 
                    parmItem.MethodPath = $"{parmItem.Controller.Replace("Controller", "")}.{parmItem.Methods.FirstOrDefault()}".ToLower();
                    parmItem.Class = item;
                    Console.WriteLine($"WebSocket加载成功 -> { parmItem.Controller.Replace("Controller", "")}.{parmItem.Methods.FirstOrDefault()}");
                }

                points.AddRange(accessParm);
            }

            wsrOptions.WatchAssemblyContext.WatchEndPoint = points.ToArray();


            wsrOptions.WatchAssemblyContext.WatchMethods =
                new ConcurrentDictionary<string, MethodInfo>(
                    wsrOptions.WatchAssemblyContext.WatchEndPoint.ToDictionary(x => x.MethodPath, x => x.MethodInfo));
            #endregion

            #region 计算构造函数与构造函数中的参数

            Dictionary<Type, ConstructorInfo[]> assConstr = new Dictionary<Type, ConstructorInfo[]>();
            Dictionary<Type, ConstructorParameter[]> assConstrParm = new Dictionary<Type, ConstructorParameter[]>();

            foreach (Type item in wsrOptions.WatchAssemblyContext.WatchAssemblyTypes)
            {
                ConstructorInfo[] constructorInfos = item.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                List<ConstructorParameter> constructorParameters = new List<ConstructorParameter>();
                foreach (var constrItem in constructorInfos)
                {
                    ConstructorParameter constructorParameter = new ConstructorParameter();

                    constructorParameter.ConstructorInfo = constrItem;
                    constructorParameter.ParameterInfos = constrItem.GetParameters();

                    constructorParameters.Add(constructorParameter);
                }

                assConstrParm.Add(item, constructorParameters.ToArray());

                assConstr.Add(item, constructorInfos);
            }
            wsrOptions.WatchAssemblyContext.AssemblyConstructors = assConstr;
            wsrOptions.WatchAssemblyContext.CoustructorParameters = assConstrParm;
            #endregion

            #region 计算构造函数里参数最多的

            Dictionary<Type, ConstructorParameter> maxAssConstrParm = new Dictionary<Type, ConstructorParameter>();
            foreach (var item in assConstrParm)
            {
                var pg = item.Value.GroupBy(x => x.ParameterInfos.Length);

                int maxKey = pg.Select(x => x.Key).Max();

                var temp = pg.FirstOrDefault(x => x.Key == maxKey).FirstOrDefault();
                maxAssConstrParm.Add(item.Key, temp);
            }
            wsrOptions.WatchAssemblyContext.MaxCoustructorParameters = maxAssConstrParm;
            #endregion

            #region 计算类公开方法的参数

            Dictionary<MethodInfo, ParameterInfo[]> methodPamams = new Dictionary<MethodInfo, ParameterInfo[]>();
            foreach (var item in points.Select(x => x.MethodInfo))
            {
                ParameterInfo[] parameterInfo = item.GetParameters();

                methodPamams.Add(item, parameterInfo);
            }
            wsrOptions.WatchAssemblyContext.MethodParameters = methodPamams;
            #endregion

            services.AddSingleton(x => wsrOptions);

            //services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        }


        /// <summary>
        /// Get assembly controller "WebSocket" EndPoint
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public static WebSocketEndPoint[] GetClassAccessParm<T>()
        {
            var type = typeof(T);
            return GetClassAccessParm(type);
        }

        /// <summary>
        /// Get assembly controller "WebSocket" EndPoint
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static WebSocketEndPoint[] GetClassAccessParm(Type type)
        {
            var methodLevel = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(x =>
                {
                    return x.GetCustomAttributes<WebSocketAttribute>().Select(a =>
                    {
                        WebSocketEndPoint point = new WebSocketEndPoint();
                        point.Action = x.Name;
                        point.Controller = x.ReflectedType.Name;
                        point.Methods = new string[] { a.Method };
                        point.MethodInfo = x;

                        return point;
                    }).ToArray();
                }).SelectMany((x, y) => x);
            IEnumerable<WebSocketEndPoint> points =
                methodLevel.GroupBy(x => x.Controller + x.Action).Select(x =>
               {
                   WebSocketEndPoint first = x.FirstOrDefault();

                   first.Methods = x.Select(m => m.Methods).SelectMany((y, z) => y).Distinct().ToArray();

                   return first;
               });

            return points.ToArray();
        }
    }
}
