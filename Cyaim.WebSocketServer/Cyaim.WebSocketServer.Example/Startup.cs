using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Cyaim.WebSocketServer.Example.Common.Redis;
using Cyaim.WebSocketServer.Infrastructure;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Handlers;
using Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cyaim.WebSocketServer.Example
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            //×¢ÈëRedis
            var redisConn = Configuration["RedisConfigs:Hosts:0"];
            services.AddSingleton(x => new AuthRedisHelper(redisConn));
            services.AddSingleton(x => new ChatRedisHelper(redisConn));
            services.AddSingleton(x => new FriendRedisHelper(redisConn));


            services.AddControllers();

            services.ConfigureWebSocketRoute(x =>
            {
                var mvcHandler = new MvcChannelHandler();
                mvcHandler.AddRequestMiddleware(RequestPipelineStage.Connected, new PipelineItem()
                {
                    Item = new((c, o) => Task.CompletedTask)
                });

                mvcHandler.AddRequestMiddleware(new PipelineItem()
                {
                    Stage = RequestPipelineStage.Disconnected,
                    Item = new((c, o) => Task.CompletedTask)
                });

                mvcHandler.AddRequestMiddleware(
                    RequestPipelineStage.BeforeForwardingData,
                    new RequestForwardPipeline((context, webSocket, receiveResult, data, request, requestBody) =>
                    {
                        return Task.CompletedTask;
                    }));

                mvcHandler.RequestPipeline.AddRequestMiddleware(new PipelineItem()
                {
                    Stage = RequestPipelineStage.BeforeReceivingData,
                    Item = new RequestReceivePipeline((context, webSocket, receiveResult, data) => Task.CompletedTask)
                });


                mvcHandler.RequestPipeline.AddRequestMiddleware(RequestPipelineStage.ReceivingData, 
                    new RequestReceivePipeline((context, webSocket, receiveResult, data) => Task.CompletedTask));


                //Define channels
                x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
                {
                    { "/im",mvcHandler.ConnectionEntry}
                };
                x.ApplicationServiceCollection = services;
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            //app.UseHttpsRedirection();

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });


            //---------------------START---------------------
            var webSocketOptions = new WebSocketOptions()
            {
                KeepAliveInterval = TimeSpan.FromSeconds(15),
                ReceiveBufferSize = 4 * 1024
            };

            app.UseWebSockets(webSocketOptions);
            app.UseWebSocketServer();
            //---------------------END---------------------
        }



    }
}
