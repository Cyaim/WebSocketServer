using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.WebSocketServer.Example.Common.Redis
{
    public class AuthRedisHelper : RedisHelper
    {
        public AuthRedisHelper(string connStr) : base(connStr, "Auth", 0)
        {

        }
    }
}
