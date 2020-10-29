using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using System.Reflection;
using System.Text;

namespace Cyaim.WebSocketServer.Infrastructure.Configures
{
    public class WatchAssemblyContext
    {
        /// <summary>
        /// 监听程序集路径
        /// </summary>
        public string WatchAssemblyPath { get; set; }

        /// <summary>
        /// 监听程序集
        /// </summary>
        public List<Type> WatchAssemblyTypes { get; set; }

        /// <summary>
        /// 监听的访问节点
        /// </summary>
        public WebSocketEndPoint[] WatchEndPoint { get; set; }

        /// <summary>
        /// 监听的访问节点与对应函数
        /// </summary>
        public ConcurrentDictionary<string, MethodInfo> WatchMethods { get; set; } = new ConcurrentDictionary<string, MethodInfo>();

        /// <summary>
        /// 程序集的构造函数
        /// </summary>
        public Dictionary<Type, ConstructorInfo[]> AssemblyConstructors { get; set; }

        /// <summary>
        /// 程序集构造函数的参数列表
        /// </summary>
        public Dictionary<Type, ConstructorParameter[]> CoustructorParameters { get; set; }

        /// <summary>
        /// 程序集中构造函数参数最多的函数
        /// </summary>
        public Dictionary<Type, ConstructorParameter> MaxCoustructorParameters { get; set; }

        /// <summary>
        /// 程序集中公开函数的参数列表
        /// </summary>
        public Dictionary<MethodInfo, ParameterInfo[]> MethodParameters { get; set; }
    }
}
