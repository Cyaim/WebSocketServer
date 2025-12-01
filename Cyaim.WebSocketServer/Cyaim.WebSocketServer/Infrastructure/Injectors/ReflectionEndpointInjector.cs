using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.AspNetCore.Http;
using System;
using System.Net.WebSockets;
using System.Reflection;

namespace Cyaim.WebSocketServer.Infrastructure.Injectors
{
    /// <summary>
    /// 基于反射的 Endpoint 注入器（用于动态添加的 endpoint 或未生成代码的类型）
    /// </summary>
    public class ReflectionEndpointInjector : IEndpointInjector
    {
        private readonly Type _endpointType;
        private readonly string _httpContextPropertyName;
        private readonly string _webSocketPropertyName;
        private PropertyInfo _contextProperty;
        private PropertyInfo _socketProperty;

        /// <summary>
        /// 创建反射注入器
        /// </summary>
        public ReflectionEndpointInjector(Type endpointType, string httpContextPropertyName, string webSocketPropertyName)
        {
            _endpointType = endpointType ?? throw new ArgumentNullException(nameof(endpointType));
            _httpContextPropertyName = httpContextPropertyName;
            _webSocketPropertyName = webSocketPropertyName;
            
            // 缓存属性信息
            _contextProperty = _endpointType.GetProperty(_httpContextPropertyName);
            _socketProperty = _endpointType.GetProperty(_webSocketPropertyName);
        }

        /// <summary>
        /// 注入 HttpContext 和 WebSocket
        /// </summary>
        public void Inject(object instance, HttpContext httpContext, WebSocket webSocket)
        {
            if (instance == null)
                return;

            if (_contextProperty != null && _contextProperty.CanWrite)
            {
                _contextProperty.SetValue(instance, httpContext);
            }

            if (_socketProperty != null && _socketProperty.CanWrite)
            {
                _socketProperty.SetValue(instance, webSocket);
            }
        }
    }
}

