using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Dashboard.Middlewares
{
    /// <summary>
    /// Dashboard middleware for serving the dashboard UI
    /// 用于提供仪表板 UI 的 Dashboard 中间件
    /// </summary>
    public class DashboardMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<DashboardMiddleware> _logger;
        private readonly string _dashboardPath;

        /// <summary>
        /// Constructor / 构造函数
        /// </summary>
        /// <param name="next">Next middleware / 下一个中间件</param>
        /// <param name="logger">Logger instance / 日志实例</param>
        /// <param name="dashboardPath">Dashboard path / Dashboard 路径</param>
        public DashboardMiddleware(
            RequestDelegate next,
            ILogger<DashboardMiddleware> logger,
            string dashboardPath = "/dashboard")
        {
            _next = next;
            _logger = logger;
            _dashboardPath = dashboardPath.TrimEnd('/');
        }

        /// <summary>
        /// Invoke middleware / 调用中间件
        /// </summary>
        /// <param name="context">HTTP context / HTTP 上下文</param>
        /// <returns>Task / 任务</returns>
        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value;

            // Serve dashboard HTML / 提供 Dashboard HTML
            if (path == _dashboardPath || path == $"{_dashboardPath}/")
            {
                await ServeDashboardHtml(context);
                return;
            }

            // Serve static files / 提供静态文件
            if (path.StartsWith($"{_dashboardPath}/", StringComparison.OrdinalIgnoreCase))
            {
                var filePath = path.Substring(_dashboardPath.Length + 1);
                await ServeStaticFile(context, filePath);
                return;
            }

            await _next(context);
        }

        /// <summary>
        /// Serve dashboard HTML / 提供 Dashboard HTML
        /// </summary>
        /// <param name="context">HTTP context / HTTP 上下文</param>
        private async Task ServeDashboardHtml(HttpContext context)
        {
            try
            {
                var html = GetDashboardHtml();
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(html);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error serving dashboard HTML");
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("Error loading dashboard");
            }
        }

        /// <summary>
        /// Serve static file / 提供静态文件
        /// </summary>
        /// <param name="context">HTTP context / HTTP 上下文</param>
        /// <param name="filePath">File path / 文件路径</param>
        private async Task ServeStaticFile(HttpContext context, string filePath)
        {
            try
            {
                // Get file from wwwroot / 从 wwwroot 获取文件
                var wwwrootPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "wwwroot",
                    filePath);

                if (!File.Exists(wwwrootPath))
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                // Set content type / 设置内容类型
                var extension = Path.GetExtension(filePath).ToLowerInvariant();
                context.Response.ContentType = GetContentType(extension);

                // Serve file / 提供文件
                var fileBytes = await File.ReadAllBytesAsync(wwwrootPath);
                await context.Response.Body.WriteAsync(fileBytes, 0, fileBytes.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error serving static file: {filePath}");
                context.Response.StatusCode = 500;
            }
        }

        /// <summary>
        /// Get content type by extension / 根据扩展名获取内容类型
        /// </summary>
        /// <param name="extension">File extension / 文件扩展名</param>
        /// <returns>Content type / 内容类型</returns>
        private string GetContentType(string extension)
        {
            return extension switch
            {
                ".js" => "application/javascript",
                ".css" => "text/css",
                ".json" => "application/json",
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".svg" => "image/svg+xml",
                ".ico" => "image/x-icon",
                ".html" => "text/html",
                ".map" => "application/json",
                _ => "application/octet-stream"
            };
        }

        /// <summary>
        /// Get dashboard HTML / 获取 Dashboard HTML
        /// </summary>
        /// <returns>HTML content / HTML 内容</returns>
        private string GetDashboardHtml()
        {
            try
            {
                // Try to load from wwwroot/public/index.html / 尝试从 wwwroot/public/index.html 加载
                var htmlPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "wwwroot",
                    "public",
                    "index.html");

                if (System.IO.File.Exists(htmlPath))
                {
                    return System.IO.File.ReadAllText(htmlPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load dashboard HTML from file, using default");
            }

            // Fallback to default HTML / 回退到默认 HTML
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>WebSocketServer Dashboard</title>
    <link rel=""stylesheet"" href=""/dashboard/app.css"">
</head>
<body>
    <div id=""app""></div>
    <script src=""/dashboard/app.js""></script>
</body>
</html>";
        }
    }
}

