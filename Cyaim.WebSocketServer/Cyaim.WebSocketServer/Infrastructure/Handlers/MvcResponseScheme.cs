using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers
{
    public class MvcResponseScheme
    {

        public int Status { get; set; }

        public string Msg { get; set; }

        /// <summary>
        /// 请求时间
        /// </summary>
        public long ReauestTime { get; set; }

        /// <summary>
        /// 结束时间
        /// </summary>
        public long ComplateTime { get; set; }

        public object Body { get; set; }
    }
}
