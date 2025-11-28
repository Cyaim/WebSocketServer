using System;
using System.Collections.Generic;
using System.Linq;
using Cyaim.WebSocketServer.Dashboard.Models;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Dashboard.Controllers
{
    /// <summary>
    /// WebSocket endpoint discovery controller
    /// WebSocket 端点发现控制器
    /// </summary>
    [ApiController]
    [Route("ws_server/api/endpoints")]
    public class EndpointController : ControllerBase
    {
        private readonly ILogger<EndpointController> _logger;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        public EndpointController(ILogger<EndpointController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Get all WebSocket endpoints / 获取所有 WebSocket 端点
        /// </summary>
        /// <returns>List of WebSocket endpoints / WebSocket 端点列表</returns>
        [HttpGet]
        public ActionResult<ApiResponse<List<WebSocketEndpointInfo>>> GetAll()
        {
            try
            {
                var serviceProvider = WebSocketRouteOption.ApplicationServices;
                if (serviceProvider == null)
                {
                    return Ok(new ApiResponse<List<WebSocketEndpointInfo>>
                    {
                        Success = false,
                        Error = "WebSocket server not initialized"
                    });
                }

                var option = serviceProvider.GetService(typeof(WebSocketRouteOption)) as WebSocketRouteOption;
                if (option?.WatchAssemblyContext?.WatchEndPoint == null)
                {
                    return Ok(new ApiResponse<List<WebSocketEndpointInfo>>
                    {
                        Success = false,
                        Error = "WebSocket endpoints not available"
                    });
                }

                var endpoints = option.WatchAssemblyContext.WatchEndPoint
                    .Select(ep => new WebSocketEndpointInfo
                    {
                        Controller = ep.Controller,
                        Action = ep.Action,
                        MethodPath = ep.MethodPath,
                        Methods = ep.Methods ?? Array.Empty<string>(),
                        FullName = $"{ep.Controller}.{ep.Action}",
                        Target = ep.MethodPath
                    })
                    .ToList();

                return Ok(new ApiResponse<List<WebSocketEndpointInfo>>
                {
                    Success = true,
                    Data = endpoints
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting WebSocket endpoints");
                return StatusCode(500, new ApiResponse<List<WebSocketEndpointInfo>>
                {
                    Success = false,
                    Error = ex.Message
                });
            }
        }
    }

    /// <summary>
    /// WebSocket endpoint information / WebSocket 端点信息
    /// </summary>
    public class WebSocketEndpointInfo
    {
        /// <summary>
        /// Controller name / 控制器名称
        /// </summary>
        public string Controller { get; set; } = string.Empty;

        /// <summary>
        /// Action name / 操作方法名称
        /// </summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>
        /// Method path (lowercase) / 方法路径（小写）
        /// </summary>
        public string MethodPath { get; set; } = string.Empty;

        /// <summary>
        /// HTTP methods / HTTP 方法
        /// </summary>
        public string[] Methods { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Full name (Controller.Action) / 完整名称（控制器.操作）
        /// </summary>
        public string FullName { get; set; } = string.Empty;

        /// <summary>
        /// Target for WebSocket request / WebSocket 请求目标
        /// </summary>
        public string Target { get; set; } = string.Empty;
    }
}

