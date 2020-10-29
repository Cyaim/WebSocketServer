using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers
{
    public class MvcRequestScheme
    {

        public string Target { get; set; }

        public object Body { get; set; }
    }
}
