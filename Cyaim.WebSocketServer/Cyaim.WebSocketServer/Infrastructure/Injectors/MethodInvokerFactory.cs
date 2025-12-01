using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace Cyaim.WebSocketServer.Infrastructure.Injectors
{
    /// <summary>
    /// 方法调用器工厂，支持源代码生成和反射两种方式
    /// </summary>
    public class MethodInvokerFactory
    {
        private readonly ConcurrentDictionary<MethodInfo, IMethodInvoker> _invokerCache = new ConcurrentDictionary<MethodInfo, IMethodInvoker>();

        /// <summary>
        /// 获取或创建方法调用器
        /// </summary>
        public IMethodInvoker GetOrCreateInvoker(MethodInfo method)
        {
            return _invokerCache.GetOrAdd(method, m =>
            {
                // 首先尝试使用源代码生成的调用器
                var generatedInvoker = TryCreateGeneratedInvoker(m);
                if (generatedInvoker != null)
                {
                    return generatedInvoker;
                }

                // 如果生成器不存在，使用反射调用器
                return new ReflectionMethodInvoker(m);
            });
        }

        /// <summary>
        /// 尝试创建源代码生成的方法调用器
        /// </summary>
        private IMethodInvoker TryCreateGeneratedInvoker(MethodInfo method)
        {
            try
            {
                // 源生成器生成的调用器类名格式：{ClassName}_{MethodName}Invoker（在同一个命名空间中）
                // 例如：MyController.Echo -> MyController_EchoInvoker
                var declaringType = method.DeclaringType;
                if (declaringType == null)
                    return null;

                var className = declaringType.Name;
                var methodName = method.Name;
                var invokerTypeName = $"{declaringType.Namespace}.{className}_{methodName}Invoker";

                // 首先尝试在同一程序集中查找
                var invokerType = declaringType.Assembly.GetType(invokerTypeName);

                // 如果找不到，尝试使用完整名称查找（处理嵌套类型）
                if (invokerType == null)
                {
                    var fullName = declaringType.FullName;
                    if (fullName != null)
                    {
                        var fullInvokerName = $"{fullName}_{methodName}Invoker";
                        invokerType = declaringType.Assembly.GetType(fullInvokerName);
                    }
                }

                // 如果还是找不到，尝试在同一个命名空间下查找所有类型
                if (invokerType == null)
                {
                    var allTypes = declaringType.Assembly.GetTypes();
                    invokerType = allTypes.FirstOrDefault(t =>
                        t.Namespace == declaringType.Namespace &&
                        t.Name == $"{className}_{methodName}Invoker" &&
                        typeof(IMethodInvoker).IsAssignableFrom(t));
                }

                if (invokerType != null && typeof(IMethodInvoker).IsAssignableFrom(invokerType))
                {
                    // 使用无参构造函数创建调用器实例
                    var constructor = invokerType.GetConstructor(Type.EmptyTypes);
                    if (constructor != null)
                    {
                        return (IMethodInvoker)constructor.Invoke(null);
                    }
                }
            }
            catch
            {
                // 如果生成器不存在或创建失败，返回 null，将使用反射调用器
            }

            return null;
        }

        /// <summary>
        /// 清除缓存（用于动态添加/删除方法后刷新）
        /// </summary>
        public void ClearCache()
        {
            _invokerCache.Clear();
        }

        /// <summary>
        /// 移除特定方法的缓存
        /// </summary>
        public void RemoveCache(MethodInfo method)
        {
            _invokerCache.TryRemove(method, out _);
        }
    }
}

