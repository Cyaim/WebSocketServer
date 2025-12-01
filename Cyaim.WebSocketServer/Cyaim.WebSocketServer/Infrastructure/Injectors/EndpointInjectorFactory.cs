using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;

namespace Cyaim.WebSocketServer.Infrastructure.Injectors
{
    /// <summary>
    /// Endpoint 注入器工厂，支持源代码生成和反射两种方式
    /// </summary>
    public class EndpointInjectorFactory
    {
        private readonly ConcurrentDictionary<Type, IEndpointInjector> _injectorCache = new ConcurrentDictionary<Type, IEndpointInjector>();
        private readonly string _httpContextPropertyName;
        private readonly string _webSocketPropertyName;

        /// <summary>
        /// 创建注入器工厂
        /// </summary>
        public EndpointInjectorFactory(WebSocketRouteOption options)
        {
            _httpContextPropertyName = options?.InjectionHttpContextPropertyName ?? "WebSocketHttpContext";
            _webSocketPropertyName = options?.InjectionWebSocketClientPropertyName ?? "WebSocketClient";
        }

        /// <summary>
        /// 获取或创建注入器
        /// </summary>
        public IEndpointInjector GetOrCreateInjector(Type endpointType)
        {
            return _injectorCache.GetOrAdd(endpointType, type =>
            {
                // 首先尝试使用源代码生成的注入器
                var generatedInjector = TryCreateGeneratedInjector(type);
                if (generatedInjector != null)
                {
                    return generatedInjector;
                }

                // 如果生成器不存在，使用反射注入器
                return new ReflectionEndpointInjector(type, _httpContextPropertyName, _webSocketPropertyName);
            });
        }

        /// <summary>
        /// 尝试创建源代码生成的注入器
        /// </summary>
        private IEndpointInjector TryCreateGeneratedInjector(Type endpointType)
        {
            try
            {
                // 源生成器生成的注入器类名格式：{ClassName}Injector（在同一个命名空间中）
                // 例如：MyController -> MyControllerInjector
                var className = endpointType.Name;
                var injectorTypeName = $"{endpointType.Namespace}.{className}Injector";
                
                // 首先尝试在同一程序集中查找
                var injectorType = endpointType.Assembly.GetType(injectorTypeName);
                
                // 如果找不到，尝试在所有已加载的程序集中查找（处理嵌套类型等情况）
                if (injectorType == null)
                {
                    // 尝试使用完整名称查找（处理嵌套类型）
                    var fullName = endpointType.FullName;
                    if (fullName != null)
                    {
                        var fullInjectorName = $"{fullName}Injector";
                        injectorType = endpointType.Assembly.GetType(fullInjectorName);
                    }
                }
                
                // 如果还是找不到，尝试在同一个命名空间下查找所有类型
                if (injectorType == null)
                {
                    var allTypes = endpointType.Assembly.GetTypes();
                    injectorType = allTypes.FirstOrDefault(t => 
                        t.Namespace == endpointType.Namespace &&
                        t.Name == $"{className}Injector" &&
                        typeof(IEndpointInjector).IsAssignableFrom(t));
                }
                
                if (injectorType != null && typeof(IEndpointInjector).IsAssignableFrom(injectorType))
                {
                    // 使用无参构造函数创建注入器实例
                    var constructor = injectorType.GetConstructor(Type.EmptyTypes);
                    if (constructor != null)
                    {
                        return (IEndpointInjector)constructor.Invoke(null);
                    }
                }
            }
            catch
            {
                // 如果生成器不存在或创建失败，返回 null，将使用反射注入器
            }

            return null;
        }

        /// <summary>
        /// 清除缓存（用于动态添加/删除 endpoint 后刷新）
        /// </summary>
        public void ClearCache()
        {
            _injectorCache.Clear();
        }

        /// <summary>
        /// 移除特定类型的缓存
        /// </summary>
        public void RemoveCache(Type endpointType)
        {
            _injectorCache.TryRemove(endpointType, out _);
        }
    }
}

