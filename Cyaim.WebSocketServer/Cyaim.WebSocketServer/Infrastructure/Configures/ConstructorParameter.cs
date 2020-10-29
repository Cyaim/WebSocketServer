using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Cyaim.WebSocketServer.Infrastructure.Configures
{
    /// <summary>
    /// Constructor parameter
    /// </summary>
    public struct ConstructorParameter
    {
        /// <summary>
        /// Constructor info
        /// </summary>
        public ConstructorInfo ConstructorInfo { get; set; }

        /// <summary>
        /// Parameter in constructor
        /// </summary>
        public ParameterInfo[] ParameterInfos { get; set; }
    }
}
