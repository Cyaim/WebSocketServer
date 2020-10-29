using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Cyaim.WebSocketServer.Infrastructure.Configures
{
    public struct ConstructorParameter
    {
        public ConstructorInfo ConstructorInfo { get; set; }

        public ParameterInfo[] ParameterInfos { get; set; }
    }
}
