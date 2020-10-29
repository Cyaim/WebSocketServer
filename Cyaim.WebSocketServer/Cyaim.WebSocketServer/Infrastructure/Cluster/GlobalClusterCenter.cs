﻿using Cyaim.WebSocketServer.Infrastructure.Configures;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;

namespace Cyaim.WebSocketServer.Infrastructure.Cluster
{
    /// <summary>
    /// Global cluster status center
    /// </summary>
    public static class GlobalClusterCenter
    {
        /// <summary>
        /// Cluster context
        /// </summary>
        public static ClusterOption ClusterContext { get; set; }

        /// <summary>
        /// All nodes 
        /// </summary>
        public static List<WebSocket> Nodes { get; set; }
    }
}
