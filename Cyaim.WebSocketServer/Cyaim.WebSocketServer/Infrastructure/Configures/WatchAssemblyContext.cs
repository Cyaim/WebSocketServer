using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure.Configures
{
    /// <summary>
    /// Watch assembly context
    /// </summary>
    public class WatchAssemblyContext
    {
        /// <summary>
        /// assembly path
        /// </summary>
        public string WatchAssemblyPath { get; set; }

        /// <summary>
        /// Type in assemblies
        /// </summary>
        public List<Type> WatchAssemblyTypes { get; set; }

        /// <summary>
        /// Assembly in WebSocketAttributes
        /// </summary>
        public WebSocketEndPoint[] WatchEndPoint { get; set; }

        /// <summary>
        /// K WebSocket "MethodPath",V "MethodInfo"
        /// </summary>
        public ConcurrentDictionary<string, MethodInfo> WatchMethods { get; set; } = new ConcurrentDictionary<string, MethodInfo>();

        /// <summary>
        /// Per-endpoint receive policy (streaming flag + size cap), keyed by MethodPath (target),
        /// case-insensitive. Only endpoints that set <see cref="Attributes.WebSocketAttribute.Stream"/>
        /// or <see cref="Attributes.WebSocketAttribute.MaxBytes"/> appear here; others use the global default.
        /// 端点级接收策略（流式标记 + 上限），按 target 忽略大小写索引；只有配置了 Stream/MaxBytes 的端点才在表里。
        /// </summary>
        public ConcurrentDictionary<string, EndpointReceivePolicy> EndpointPolicies { get; set; } = new ConcurrentDictionary<string, EndpointReceivePolicy>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Resolve a target's receive policy. Returns false when the endpoint has no per-endpoint override
        /// (caller should then use the global default and buffered dispatch). 解析端点策略；无覆盖时返回 false。
        /// </summary>
        public bool TryGetEndpointPolicy(string target, out EndpointReceivePolicy policy)
        {
            if (target != null && EndpointPolicies != null)
            {
                return EndpointPolicies.TryGetValue(target, out policy);
            }
            policy = default;
            return false;
        }

        /// <summary>
        /// Constructor in assembly type
        /// </summary>
        public Dictionary<Type, ConstructorInfo[]> AssemblyConstructors { get; set; }

        /// <summary>
        /// Constructor parameter list
        /// K class type,V class constructor parameter list
        /// </summary>
        public Dictionary<Type, ConstructorParameter[]> ConstructorParameters { get; set; }

        /// <summary>
        /// Constructor most parameter in class
        /// K Class type,V Constructor parameter
        /// </summary>
        public Dictionary<Type, ConstructorParameter> MaxConstructorParameters { get; set; }

        /// <summary>
        /// Method parameter list in class public method
        /// K MethodInfo,V ParameterInfo
        /// </summary>
        public Dictionary<MethodInfo, ParameterInfo[]> MethodParameters { get; set; }

        /// <summary>
        /// Task result getter cache in endpoint method
        /// K endpoint MethodInfo,V Task result getter
        /// </summary>
        public Dictionary<MethodInfo, Func<Task, object>> MethodTaskResultGetters { get; set; }

        /// <summary>
        /// Lazily built lookup from endpoint method path to declaring class,
        /// avoiding an O(n) scan of <see cref="WatchEndPoint"/> per request.
        /// 从终结点方法路径到所属类的惰性构建查找表，避免每次请求对 <see cref="WatchEndPoint"/> 做 O(n) 扫描。
        /// </summary>
        private volatile ConcurrentDictionary<string, Type> _endpointClassByPath;

        /// <summary>
        /// Get the class that declares the endpoint at <paramref name="methodPath"/> in O(1).
        /// 以 O(1) 获取声明 <paramref name="methodPath"/> 终结点的类。
        /// </summary>
        /// <param name="methodPath">Endpoint method path (lowercase) / 终结点方法路径（小写）</param>
        /// <returns>Declaring class or null / 所属类，找不到返回 null</returns>
        public Type GetEndpointClass(string methodPath)
        {
            if (methodPath == null)
            {
                return null;
            }

            var map = _endpointClassByPath;
            if (map == null)
            {
                map = new ConcurrentDictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
                var endpoints = WatchEndPoint;
                if (endpoints != null)
                {
                    foreach (var endpoint in endpoints)
                    {
                        if (endpoint?.MethodPath != null && endpoint.Class != null)
                        {
                            map.TryAdd(endpoint.MethodPath, endpoint.Class);
                        }
                    }
                }
                // Benign race: concurrent initializers build identical maps
                // 良性竞争：并发初始化会构建出相同的映射
                _endpointClassByPath = map;
            }

            return map.TryGetValue(methodPath, out var type) ? type : null;
        }
    }
}