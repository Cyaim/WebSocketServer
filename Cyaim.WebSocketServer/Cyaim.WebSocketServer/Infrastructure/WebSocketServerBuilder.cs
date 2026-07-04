using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Cyaim.WebSocketServer.Infrastructure
{
    /// <summary>
    /// A fluent builder returned by services.AddWebSocketServer(), used to register additional WebSocket channels.
    /// If AddWebSocketServer() auto-wired the default "/ws" MvcChannelHandler (because no channel was configured),
    /// the first explicit AddChannel/AddMvcChannel call replaces that auto default,
    /// so the channels you register are exactly the channels you get.
    ///
    /// services.AddWebSocketServer() 返回的流式构建器，用于注册额外的 WebSocket 通道。
    /// 如果 AddWebSocketServer() 因为未配置任何通道而自动挂载了默认的 "/ws" MvcChannelHandler，
    /// 则第一次显式调用 AddChannel/AddMvcChannel 会替换该自动默认通道，
    /// 即：你注册了哪些通道，最终就是哪些通道。
    /// </summary>
    public interface IWebSocketServerBuilder
    {
        /// <summary>
        /// The application's service collection.
        /// 应用程序的服务容器。
        /// </summary>
        IServiceCollection Services { get; }

        /// <summary>
        /// The <see cref="WebSocketRouteOption"/> configured by AddWebSocketServer.
        /// Can be used to attach events (e.g. BeforeConnectionEvent) or tweak options before the app starts.
        ///
        /// 由 AddWebSocketServer 配置完成的 <see cref="WebSocketRouteOption"/>。
        /// 可在应用启动前用于挂接事件（如 BeforeConnectionEvent）或调整配置。
        /// </summary>
        WebSocketRouteOption Options { get; }

        /// <summary>
        /// Register a WebSocket channel with a custom handler. Registering the same channel path again overwrites the previous handler.
        /// 注册一个使用自定义处理器的 WebSocket 通道。重复注册相同通道路径将覆盖之前的处理器。
        /// </summary>
        /// <param name="channel">Channel request path, e.g. "/chat" / 通道请求路径，例如 "/chat"</param>
        /// <param name="handler">Channel connection handler / 通道连接处理器</param>
        /// <returns>The builder, for chaining / 构建器本身，便于链式调用</returns>
        IWebSocketServerBuilder AddChannel(string channel, WebSocketRouteOption.WebSocketChannelHandler handler);

        /// <summary>
        /// Register a WebSocket channel handled by a new <see cref="MvcChannelHandler"/> (MVC-style endpoint forwarding).
        /// 注册一个由新的 <see cref="MvcChannelHandler"/> 处理的 WebSocket 通道（MVC 风格的终结点转发）。
        /// </summary>
        /// <param name="channel">Channel request path, default "/ws" / 通道请求路径，默认 "/ws"</param>
        /// <param name="receiveBufferSize">Receive buffer size in bytes, default 4096 / 接收缓冲区大小（字节），默认 4096</param>
        /// <param name="sendBufferSize">Send buffer size in bytes, default 4096 / 发送缓冲区大小（字节），默认 4096</param>
        /// <returns>The builder, for chaining / 构建器本身，便于链式调用</returns>
        IWebSocketServerBuilder AddMvcChannel(string channel = WebSocketRouteServiceCollectionExtensions.DefaultWebSocketChannel, int receiveBufferSize = 4 * 1024, int sendBufferSize = 4 * 1024);
    }

    /// <summary>
    /// Default implementation of <see cref="IWebSocketServerBuilder"/>.
    /// <see cref="IWebSocketServerBuilder"/> 的默认实现。
    /// </summary>
    internal sealed class WebSocketServerBuilder : IWebSocketServerBuilder
    {
        /// <summary>
        /// True when AddWebSocketServer auto-wired the default "/ws" channel because the user didn't configure any channel.
        /// The first explicit channel registration removes that auto default.
        ///
        /// 当用户未配置任何通道、AddWebSocketServer 自动挂载了默认 "/ws" 通道时为 true。
        /// 第一次显式注册通道时会移除该自动默认通道。
        /// </summary>
        private bool _hasAutoDefaultChannel;

        /// <summary>
        /// Create builder instance / 创建构建器实例
        /// </summary>
        /// <param name="services"></param>
        /// <param name="options"></param>
        /// <param name="hasAutoDefaultChannel"></param>
        public WebSocketServerBuilder(IServiceCollection services, WebSocketRouteOption options, bool hasAutoDefaultChannel)
        {
            Services = services ?? throw new ArgumentNullException(nameof(services));
            Options = options ?? throw new ArgumentNullException(nameof(options));
            _hasAutoDefaultChannel = hasAutoDefaultChannel;
        }

        /// <inheritdoc />
        public IServiceCollection Services { get; }

        /// <inheritdoc />
        public WebSocketRouteOption Options { get; }

        /// <inheritdoc />
        public IWebSocketServerBuilder AddChannel(string channel, WebSocketRouteOption.WebSocketChannelHandler handler)
        {
            if (string.IsNullOrEmpty(channel))
            {
                throw new ArgumentNullException(nameof(channel));
            }
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            // The user is now specifying channels explicitly, remove the auto-wired default "/ws"
            // 用户开始显式指定通道，移除自动挂载的默认 "/ws" 通道
            if (_hasAutoDefaultChannel)
            {
                Options.WebSocketChannels.Remove(WebSocketRouteServiceCollectionExtensions.DefaultWebSocketChannel);
                _hasAutoDefaultChannel = false;
            }

            Options.WebSocketChannels[channel] = handler;
            Console.WriteLine(I18nText.DefineWebSocketChannelOn + channel);

            return this;
        }

        /// <inheritdoc />
        public IWebSocketServerBuilder AddMvcChannel(string channel = WebSocketRouteServiceCollectionExtensions.DefaultWebSocketChannel, int receiveBufferSize = 4 * 1024, int sendBufferSize = 4 * 1024)
        {
            return AddChannel(channel, new MvcChannelHandler(receiveBufferSize, sendBufferSize).ConnectionEntry);
        }
    }
}
