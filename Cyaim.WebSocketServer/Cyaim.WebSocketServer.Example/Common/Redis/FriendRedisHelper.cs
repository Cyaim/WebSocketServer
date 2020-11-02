using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.WebSocketServer.Example.Common.Redis
{
    public class FriendRedisHelper : RedisHelper
    {
        public FriendRedisHelper(string connStr) : base(connStr, "Friend", 1)
        {

        }
    }
}
