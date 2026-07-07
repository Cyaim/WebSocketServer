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
                var wwwrootDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot"));
                var wwwrootPath = Path.GetFullPath(Path.Combine(wwwrootDir, filePath));

                // Path-traversal guard: the resolved path must stay inside wwwroot.
                // 路径穿越防护：解析后的路径必须仍在 wwwroot 内。
                if (!wwwrootPath.StartsWith(wwwrootDir, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                if (!File.Exists(wwwrootPath))
                {
                    // SPA fallback: deep links like /dashboard/overview have no file on disk —
                    // serve the app shell (index.html) and let the client router take over.
                    // SPA 回退：/dashboard/overview 这类深链在磁盘上没有文件，返回应用壳由前端路由接管。
                    if (!Path.HasExtension(filePath))
                    {
                        await ServeDashboardHtml(context);
                        return;
                    }
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
                ".woff" => "font/woff",
                ".woff2" => "font/woff2",
                ".ttf" => "font/ttf",
                ".txt" => "text/plain",
                ".webmanifest" => "application/manifest+json",
                _ => "application/octet-stream"
            };
        }

        /// <summary>
        /// Get dashboard HTML / 获取 Dashboard HTML
        /// </summary>
        /// <returns>HTML content / HTML 内容</returns>
        private string GetDashboardHtml()
        {
            // Prefer the built SPA shell (wwwroot/index.html, produced by websocketserver-dashboard),
            // then a custom wwwroot/public/index.html, and finally the self-contained embedded console
            // so the dashboard still works when no static build is deployed (e.g. bare NuGet usage).
            // 优先使用构建产物 wwwroot/index.html（websocketserver-dashboard 构建输出），
            // 其次自定义 wwwroot/public/index.html，最后回退到内嵌控制台（未部署静态产物时仍可用）。
            try
            {
                var spaShell = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "index.html");
                if (System.IO.File.Exists(spaShell))
                {
                    return System.IO.File.ReadAllText(spaShell);
                }

                var htmlPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "public", "index.html");
                if (System.IO.File.Exists(htmlPath))
                {
                    return System.IO.File.ReadAllText(htmlPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load custom dashboard HTML, using the embedded console");
            }

            // Embedded management console. {BASE} is the API route prefix served by the controllers.
            return DashboardUi.Html.Replace("{BASE}", "/ws_server/api");
        }
    }
}

