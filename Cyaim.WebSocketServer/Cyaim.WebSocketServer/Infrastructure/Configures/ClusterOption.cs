using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.WebSocketServer.Infrastructure.Configures
{
    /// <summary>
    /// Cluster configure
    /// </summary>
    public class ClusterOption
    {
        /// <summary>
        /// Cluster channel name
        /// </summary>
        public string ChannelName { get; set; } = "/cluster";

        /// <summary>
        /// Node connection link
        /// </summary>
        public string[] Nodes { get; set; }

        /// <summary>
        /// Service level
        /// </summary>
        public ServiceLevel NodeLevel { get; set; }

        /// <summary>
        /// Instruct the current node enable services.
        /// If set false current node will forward request to other node.
        /// Only NodeLevel are valid for Master.
        /// </summary>
        public bool IsEnableCurrentService { get; set; }

    }


    /// <summary>
    /// Service node
    /// </summary>
    public enum ServiceLevel
    {
        /// <summary>
        /// Master node
        /// </summary>
        Master,
        /// <summary>
        /// Slave node
        /// </summary>
        Slave,
    }
}
