using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Infrastructure.Injectors
{
    /// <summary>
    /// 方法调用器接口，用于调用 endpoint 方法（支持源代码生成和反射两种方式）
    /// </summary>
    public interface IMethodInvoker
    {
        /// <summary>
        /// 调用方法
        /// </summary>
        /// <param name="instance">Endpoint 实例</param>
        /// <param name="args">方法参数</param>
        /// <returns>方法返回值（如果是 Task，则返回 Task.Result）</returns>
        object Invoke(object instance, object[] args);
    }
}

