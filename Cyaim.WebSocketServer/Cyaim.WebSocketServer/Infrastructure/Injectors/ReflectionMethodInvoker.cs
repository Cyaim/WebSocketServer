using System;
using System.Reflection;

namespace Cyaim.WebSocketServer.Infrastructure.Injectors
{
    /// <summary>
    /// 基于反射的方法调用器（用于动态添加的方法或未生成代码的方法）
    /// </summary>
    public class ReflectionMethodInvoker : IMethodInvoker
    {
        private readonly MethodInfo _method;

        /// <summary>
        /// 创建反射方法调用器
        /// </summary>
        public ReflectionMethodInvoker(MethodInfo method)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
        }

        /// <summary>
        /// 调用方法
        /// </summary>
        public object Invoke(object instance, object[] args)
        {
            return _method.Invoke(instance, args);
        }
    }
}

