using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure
{
    /// <summary>
    /// WebSocket Route service extensions
    /// </summary>
    public static class WebSocketRouteServiceCollectionExtensions
    {
        /// <summary>
        /// Default WebSocket channel path used by <see cref="AddWebSocketServer(IServiceCollection)"/> when no channel is configured.
        /// <see cref="AddWebSocketServer(IServiceCollection)"/> 在未配置任何通道时使用的默认 WebSocket 通道路径。
        /// </summary>
        public const string DefaultWebSocketChannel = "/ws";

        #region AddWebSocketServer

        /// <summary>
        /// Add Cyaim WebSocketServer with default settings:
        /// an <see cref="MvcChannelHandler"/> is wired on channel "/ws" and endpoints are discovered
        /// from the entry assembly's "*.Controllers" namespace (methods marked with [WebSocket]).
        /// Then call app.UseWebSockets() and app.UseWebSocketServer() in the request pipeline.
        ///
        /// 使用默认配置添加 Cyaim WebSocketServer：
        /// 自动在 "/ws" 通道上挂载 <see cref="MvcChannelHandler"/>，并从入口程序集的 "*.Controllers" 命名空间发现标记了 [WebSocket] 的终结点。
        /// 之后请在请求管道中调用 app.UseWebSockets() 和 app.UseWebSocketServer()。
        /// </summary>
        /// <param name="services">The service collection / 服务容器</param>
        /// <returns>A builder that can be used to add more channels / 可用于继续添加通道的构建器</returns>
        public static IWebSocketServerBuilder AddWebSocketServer(this IServiceCollection services)
        {
            return AddWebSocketServer(services, null, null);
        }

        /// <summary>
        /// Add Cyaim WebSocketServer with custom configuration.
        /// Defaults are applied first (ApplicationServiceCollection = services, empty channel table),
        /// then <paramref name="configure"/> runs so it can override them.
        /// If no channel was configured, an <see cref="MvcChannelHandler"/> is wired on channel "/ws" automatically.
        ///
        /// 使用自定义配置添加 Cyaim WebSocketServer。
        /// 先应用默认值（ApplicationServiceCollection = services、空通道表），随后执行 <paramref name="configure"/>，因此用户配置可以覆盖默认值。
        /// 如果最终没有配置任何通道，将自动在 "/ws" 通道上挂载 <see cref="MvcChannelHandler"/>。
        /// </summary>
        /// <param name="services">The service collection / 服务容器</param>
        /// <param name="configure">Configuration action for <see cref="WebSocketRouteOption"/> / <see cref="WebSocketRouteOption"/> 的配置委托</param>
        /// <returns>A builder that can be used to add more channels / 可用于继续添加通道的构建器</returns>
        public static IWebSocketServerBuilder AddWebSocketServer(this IServiceCollection services, Action<WebSocketRouteOption> configure)
        {
            return AddWebSocketServer(services, null, configure);
        }

        /// <summary>
        /// Add Cyaim WebSocketServer with custom configuration and IConfiguration support
        /// (used for loading BandwidthLimitPolicy from configuration, same as ConfigureWebSocketRoute).
        ///
        /// 使用自定义配置添加 Cyaim WebSocketServer，并支持传入 IConfiguration
        /// （用于从配置文件加载 BandwidthLimitPolicy，与 ConfigureWebSocketRoute 行为一致）。
        /// </summary>
        /// <param name="services">The service collection / 服务容器</param>
        /// <param name="configuration">Configuration object for loading BandwidthLimitPolicy / 用于加载 BandwidthLimitPolicy 的配置对象</param>
        /// <param name="configure">Configuration action for <see cref="WebSocketRouteOption"/> / <see cref="WebSocketRouteOption"/> 的配置委托</param>
        /// <returns>A builder that can be used to add more channels / 可用于继续添加通道的构建器</returns>
        public static IWebSocketServerBuilder AddWebSocketServer(this IServiceCollection services, IConfiguration configuration, Action<WebSocketRouteOption> configure)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            var defaultChannelAdded = false;
            var option = ConfigureWebSocketRouteCore(services, configuration, x =>
            {
                // Apply defaults first / 先应用默认值
                x.ApplicationServiceCollection = services;
                if (x.WebSocketChannels == null)
                {
                    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>();
                }

                // User configuration runs last so it can override the defaults / 用户配置最后执行，可覆盖默认值
                configure?.Invoke(x);

                // Safety net: never leave required members null / 兜底：确保必需成员不为空
                if (x.ApplicationServiceCollection == null)
                {
                    x.ApplicationServiceCollection = services;
                }
                if (x.WebSocketChannels == null)
                {
                    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>();
                }

                // Default-wire an MvcChannelHandler on "/ws" if the user didn't specify any channel
                // 如果用户没有指定任何通道，默认在 "/ws" 上挂载 MvcChannelHandler
                if (x.WebSocketChannels.Count == 0)
                {
                    x.WebSocketChannels.Add(DefaultWebSocketChannel, new MvcChannelHandler().ConnectionEntry);
                    defaultChannelAdded = true;
                }
            });

            return new WebSocketServerBuilder(services, option, defaultChannelAdded);
        }

        #endregion

        /// <summary>
        /// Configure WebSocketRoute Middleware
        /// </summary>
        /// <param name="services"></param>
        /// <param name="setupAction"></param>
        public static void ConfigureWebSocketRoute(this IServiceCollection services, Action<WebSocketRouteOption> setupAction)
        {
            ConfigureWebSocketRoute(services, null, setupAction);
        }

        /// <summary>
        /// Configure WebSocketRoute Middleware with IConfiguration
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration">Configuration object for loading BandwidthLimitPolicy. If provided, will automatically configure IOptions&lt;BandwidthLimitPolicy&gt;.</param>
        /// <param name="setupAction"></param>
        public static void ConfigureWebSocketRoute(this IServiceCollection services, IConfiguration configuration, Action<WebSocketRouteOption> setupAction)
        {
            _ = ConfigureWebSocketRouteCore(services, configuration, setupAction);
        }

        /// <summary>
        /// Core logic shared by ConfigureWebSocketRoute and AddWebSocketServer.
        /// Builds the <see cref="WebSocketRouteOption"/>, discovers endpoints and registers the option singleton.
        ///
        /// ConfigureWebSocketRoute 与 AddWebSocketServer 共享的核心逻辑。
        /// 构建 <see cref="WebSocketRouteOption"/>、发现终结点并注册配置单例。
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <param name="setupAction"></param>
        /// <returns>The configured <see cref="WebSocketRouteOption"/> instance / 配置完成的 <see cref="WebSocketRouteOption"/> 实例</returns>
        internal static WebSocketRouteOption ConfigureWebSocketRouteCore(IServiceCollection services, IConfiguration configuration, Action<WebSocketRouteOption> setupAction)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }
            if (setupAction == null)
            {
                throw new ArgumentNullException(nameof(setupAction));
            }
            // 注意：不要 TryAddSingleton<IHostApplicationLifetime>() —— 把接口注册为自身实现会在
            // BuildServiceProvider 时抛异常。Generic Host 总会注册真正的 ApplicationLifetime。
            // Do NOT TryAddSingleton<IHostApplicationLifetime>() — registering the interface as its own
            // implementation throws at BuildServiceProvider. Generic Host always provides the real one.

            // 如果提供了 IConfiguration，自动配置 IOptions<BandwidthLimitPolicy>
            // 用户也可以手动调用 services.Configure<BandwidthLimitPolicy>(...) 来配置
            if (configuration != null)
            {
                var policySection = configuration.GetSection("BandwidthLimitPolicy");
                if (policySection.Exists())
                {
                    // 使用 Action<T> 重载，手动从配置加载，避免依赖 Microsoft.Extensions.Options.ConfigurationExtensions
                    services.Configure<BandwidthLimitPolicy>(options =>
                    {
                        var policy = new BandwidthLimitPolicy();
                        policy.LoadFromConfiguration(configuration, "BandwidthLimitPolicy");
                        // 复制属性值到 options
                        options.Enabled = policy.Enabled;
                        options.GlobalChannelBandwidthLimit = policy.GlobalChannelBandwidthLimit;
                        options.ChannelMinBandwidthGuarantee = policy.ChannelMinBandwidthGuarantee;
                        options.ChannelMaxBandwidthLimit = policy.ChannelMaxBandwidthLimit;
                        options.ChannelEnableAverageBandwidth = policy.ChannelEnableAverageBandwidth;
                        options.ChannelConnectionMinBandwidthGuarantee = policy.ChannelConnectionMinBandwidthGuarantee;
                        options.ChannelConnectionMaxBandwidthLimit = policy.ChannelConnectionMaxBandwidthLimit;
                        options.EndPointMaxBandwidthLimit = policy.EndPointMaxBandwidthLimit;
                        options.EndPointMinBandwidthGuarantee = policy.EndPointMinBandwidthGuarantee;
                    });
                }
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

            // 终结点发现结果为空时给出明确警告，避免静默失败
            // Warn loudly when endpoint discovery finds nothing, instead of failing silently
            if (points.Count == 0)
            {
                Console.WriteLine($"WebSocket -> Warning: no WebSocket endpoint was discovered. Scanned namespace prefix: \"{assemblyName}\" (assembly: {assembly.GetName().Name}). Make sure your controller classes are under this namespace and their methods are marked with [WebSocket], or set WebSocketRouteOption.WatchAssemblyNamespacePrefix to the correct namespace prefix.");
                Console.WriteLine($"WebSocket -> 警告：未发现任何 WebSocket 终结点。已扫描的命名空间前缀：\"{assemblyName}\"（程序集：{assembly.GetName().Name}）。请确认控制器类位于该命名空间下且方法标记了 [WebSocket] 特性，或将 WebSocketRouteOption.WatchAssemblyNamespacePrefix 设置为正确的命名空间前缀。");
            }

            wsrOptions.WatchAssemblyContext.WatchEndPoint = points.ToArray();
            // 忽略大小写查找，热路径无需再对每条请求的 Target 做 ToLower 分配
            // Case-insensitive lookup so the hot path no longer allocates a lowercased Target per request
            wsrOptions.WatchAssemblyContext.WatchMethods =
                new ConcurrentDictionary<string, MethodInfo>(wsrOptions.WatchAssemblyContext.WatchEndPoint.ToDictionary(x => x.MethodPath, x => x.MethodInfo), StringComparer.OrdinalIgnoreCase);

            // 端点级接收策略（方案A缓冲上限 / 方案B流式）——只登记有覆盖的端点；流式端点在此做签名校验（启动即失败）。
            // Per-endpoint receive policy (方案A buffered cap / 方案B streaming). Only endpoints with an override
            // are registered; streaming endpoints are signature-validated here (fail-fast at startup).
            var endpointPolicies = new ConcurrentDictionary<string, EndpointReceivePolicy>(StringComparer.OrdinalIgnoreCase);
            foreach (var ep in wsrOptions.WatchAssemblyContext.WatchEndPoint)
            {
                if (ep.IsStream)
                {
                    bool hasStreamParam = ep.MethodInfo.GetParameters().Any(p => typeof(System.IO.Stream).IsAssignableFrom(p.ParameterType));
                    if (!hasStreamParam)
                    {
                        throw new InvalidOperationException(
                            $"WebSocket streaming endpoint \"{ep.MethodPath}\" ({ep.MethodInfo.DeclaringType?.Name}.{ep.MethodInfo.Name}) is marked [WebSocket(Stream = true)] but has no System.IO.Stream parameter to receive the payload. Add a Stream parameter, e.g. Upload(UploadHeader head, Stream body, CancellationToken ct). / 流式端点缺少 System.IO.Stream 形参。");
                    }
                }
                if (ep.IsStream || ep.MaxBytes > 0)
                {
                    endpointPolicies[ep.MethodPath] = new EndpointReceivePolicy(ep.IsStream, ep.MaxBytes);
                }
            }
            wsrOptions.WatchAssemblyContext.EndpointPolicies = endpointPolicies;

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

            #region 计算终结点返回值Task<TResult>结果读取器缓存

            var methodTaskResultGetters = new Dictionary<MethodInfo, Func<Task, object>>();
            foreach (var item in points.Select(x => x.MethodInfo))
            {
                if (TryBuildTaskResultGetter(item.ReturnType, out var taskResultGetter))
                {
                    methodTaskResultGetters[item] = taskResultGetter;
                }
            }
            wsrOptions.WatchAssemblyContext.MethodTaskResultGetters = methodTaskResultGetters;

            #endregion

            services.AddSingleton(x => wsrOptions);

            //services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            foreach (var item in wsrOptions.WebSocketChannels.Keys)
            {
                Console.WriteLine(I18nText.DefineWebSocketChannelOn + item);
            }

            // 获取当前线程的默认文化
            var defaultCultureInfo = CultureInfo.CurrentCulture;

            return wsrOptions;
        }

        /// <summary>
        /// 编译表达式树以获取 Task<TResult> 的结果，避免每次通过反射访问 Result 属性的性能开销。
        /// </summary>
        /// <param name="returnType"></param>
        /// <param name="getter"></param>
        /// <returns></returns>
        private static bool TryBuildTaskResultGetter(Type returnType, out Func<Task, object> getter)
        {
            getter = null;
            if (returnType == null || !returnType.IsGenericType)
            {
                return false;
            }

            var genericTypeDef = returnType.GetGenericTypeDefinition();
            if (genericTypeDef != typeof(Task<>))
            {
                return false;
            }

            var resultType = returnType.GenericTypeArguments[0];
            if (resultType.Name == "VoidTaskResult" && resultType.Namespace == "System.Threading.Tasks")
            {
                return false;
            }

            var taskParam = Expression.Parameter(typeof(Task), "task");
            var castTask = Expression.Convert(taskParam, returnType);
            var resultProperty = Expression.Property(castTask, "Result");
            var castResult = Expression.Convert(resultProperty, typeof(object));

            getter = Expression.Lambda<Func<Task, object>>(castResult, taskParam).Compile();
            return true;
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
                                          MethodInfo = x,
                                          IsStream = a.Stream,
                                          MaxBytes = a.MaxBytes
                                      })).SelectMany((x, y) => x);
            var points =
                methodLevel.GroupBy(x => x.Controller + x.Action).Select(x =>
                {
                    var first = x.FirstOrDefault();
                    first.Methods = x.Select(m => m.Methods).SelectMany((y, z) => y).Distinct().ToArray();
                    // 一个方法的多个 [WebSocket] 共享同一签名，故流式标记须一致；上限取首个非 0 值。
                    // A method's multiple [WebSocket] attrs share one signature, so the streaming flag must
                    // agree; the size cap takes the first non-zero value across them.
                    first.IsStream = x.Any(m => m.IsStream);
                    first.MaxBytes = x.Select(m => m.MaxBytes).FirstOrDefault(v => v > 0);
                    return first;
                });
            return points.ToArray();
        }
    }
}