using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Cyaim.WebSocketServer.Infrastructure.Configures
{
    public class WebSocketEndPoint
    {
        /// <summary>
        /// 控制器名称
        /// </summary>
        public string Controller { get; set; }

        /// <summary>
        /// 控制器方法
        /// </summary>
        public string Action { get; set; }

        /// <summary>
        /// 访问路径
        /// </summary>
        public string MethodPath { get; set; }

        /// <summary>
        /// 访问方法
        /// 特性标记的Name
        /// </summary>
        public string[] Methods { get; set; }

        /// <summary>
        /// 方法
        /// </summary>
        public MethodInfo MethodInfo { get; set; }

        /// <summary>
        /// 类
        /// </summary>
        public Type Class { get; set; }

    }
}
