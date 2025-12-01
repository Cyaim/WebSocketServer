using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Concurrent;
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
                // 查找生成的注入器类型：{TypeName}Injector
                var injectorTypeName = $"{endpointType.FullName}Injector";
                var injectorType = endpointType.Assembly.GetType(injectorTypeName);
                
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

