using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using System.Reflection;
using System.Text;

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
        /// Type in assemblys
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
        /// Constructor in assembly type
        /// </summary>
        public Dictionary<Type, ConstructorInfo[]> AssemblyConstructors { get; set; }

        /// <summary>
        /// Constructor parameter list
        /// K class type,V class constructor parameter list
        /// </summary>
        public Dictionary<Type, ConstructorParameter[]> CoustructorParameters { get; set; }

        /// <summary>
        /// Constructor most parameter in class
        /// K Class type,V Constructor parameter
        /// </summary>
        public Dictionary<Type, ConstructorParameter> MaxCoustructorParameters { get; set; }

        /// <summary>
        /// Method parameter list in class public method
        /// K MethodInfo,V ParameterInfo
        /// </summary>
        public Dictionary<MethodInfo, ParameterInfo[]> MethodParameters { get; set; }
    }
}
