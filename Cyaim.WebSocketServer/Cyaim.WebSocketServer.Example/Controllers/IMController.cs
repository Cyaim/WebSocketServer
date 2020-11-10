using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Example.Common.Redis;
using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Example.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class IMController : ControllerBase
    {

        private readonly ILogger<IMController> _logger;

        private readonly AuthRedisHelper _authRedis;
        private readonly ChatRedisHelper _chatRedis;
        private readonly FriendRedisHelper _friendRedis;

        public IMController(ILogger<IMController> logger, AuthRedisHelper authRedis, ChatRedisHelper chatRedis, FriendRedisHelper friendRedis)
        {
            _logger = logger;
            _authRedis = authRedis;
            _chatRedis = chatRedis;
            _friendRedis = friendRedis;
        }

        public HttpContext WebSocketHttpContext { get; set; }
        public WebSocket WebSocketClient { get; set; }

        /// <summary>
        /// Login
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="pwd"></param>
        /// <returns></returns>
        [WebSocket]
        [HttpPost("Login")]
        public async Task<string> Login(string uid, string pwd)
        {
            //登录
            //把ContextId与uid关联

            bool isSet = await _authRedis.StringSetAsync(uid, WebSocketHttpContext.Connection.Id);
            if (isSet)
            {
                return WebSocketHttpContext.Connection.Id;
            }

            return null;
        }

        #region Friend


        /// <summary>
        /// Request add friend
        /// </summary>
        /// <param name="uid"></param>
        /// <returns></returns>
        [WebSocket]
        [HttpPost("RequestAddFriend")]
        public async Task<string> RequestAddFriend(string uid)
        {
            //发送好友请求

            //获取好友在线ID
            string friendConnId = await _authRedis.StringGetAsync(uid);
            MvcChannelHandler.Clients.TryGetValue(friendConnId, out WebSocket requestFriendSocket);

            return null;
        }

        public async Task<string> RequestFriendOperation(bool isAccept)
        {


            return null;
        }

        /// <summary>
        /// Remove friend
        /// </summary>
        /// <param name="uid"></param>
        /// <returns></returns>
        [WebSocket]
        [HttpPost("RemoveFriends")]
        public async Task<string> RemoveFriends(List<string> uid)
        {
            //删除好友


            return null;
        }

        /// <summary>
        /// Get friends
        /// </summary>
        /// <returns></returns>
        [WebSocket]
        [HttpPost("GetFriends")]
        public async Task<List<string>> GetFriends()
        {
            //通过HttpContext就能找到对应的uname


            return null;
        }


        #endregion

        #region Chat

        /// <summary>
        /// Create chat room
        /// </summary>
        /// <param name="gname">room name</param>
        /// <param name="uid">friend id</param>
        /// <returns>chat room id</returns>
        [WebSocket]
        public Task<string> CreateChat(string gname, string uid)
        {

            return null;
        }

        /// <summary>
        /// Close chat room
        /// </summary>
        /// <param name="gid"></param>
        /// <returns></returns>
        [WebSocket]
        public Task<string> CloseChat(string gid)
        {
            //解散聊天室，只有创建者者可操作

            return null;
        }


        /// <summary>
        /// Exit chat room
        /// </summary>
        /// <param name="gid"></param>
        /// <returns></returns>
        [WebSocket]
        public Task<string> MyExitChat(string gid)
        {
            //退出聊天室，管理者移交给第2个加入的uid

            return null;
        }

        /// <summary>
        /// Get chats room
        /// </summary>
        /// <returns></returns>
        [WebSocket]
        public Task<List<string>> GetChats()
        {
            //获取本id下所有的聊天室

            return null;
        }

        /// <summary>
        /// Send chat message
        /// </summary>
        /// <param name="gid">chat room id</param>
        /// <param name="msg"></param>
        /// <returns></returns>
        [WebSocket]
        public Task<string> SendMsg(string gid, string msg)
        {
            //删除好友


            return null;
        }
        #endregion



    }
}
