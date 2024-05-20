using System.Reflection;

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