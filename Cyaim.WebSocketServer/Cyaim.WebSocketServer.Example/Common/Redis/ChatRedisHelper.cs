using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.WebSocketServer.Example.Common.Redis
{
    public class ChatRedisHelper : RedisHelper
    {
        public ChatRedisHelper(string connStr) : base(connStr, "Chat", 2)
        {

        }
    }
}
