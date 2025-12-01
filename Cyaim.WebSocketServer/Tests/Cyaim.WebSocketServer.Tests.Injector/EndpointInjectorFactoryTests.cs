using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Infrastructure.Injectors;
using Microsoft.AspNetCore.Http;
using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Cyaim.WebSocketServer.Tests.Injector
{
    /// <summary>
    /// EndpointInjectorFactory 功能验证和性能测试
    /// </summary>
    public static class EndpointInjectorFactoryTests
    {
        /// <summary>
        /// 测试用的 Endpoint 类（用于验证源生成功能）
        /// </summary>
        public class TestEndpoint
        {
            public HttpContext? WebSocketHttpContext { get; set; }
            public WebSocket? WebSocketClient { get; set; }

            [Infrastructure.Attributes.WebSocket]
            public Task<string> TestMethod(string message)
            {
                return Task.FromResult($"Echo: {message}");
            }

            [Infrastructure.Attributes.WebSocket]
            public string Echo(string message)
            {
                return $"Echo: {message}";
            }

            [Infrastructure.Attributes.WebSocket]
            public int Add(int a, int b)
            {
                return a + b;
            }

            [Infrastructure.Attributes.WebSocket]
            public void VoidMethod(string message)
            {
                // 无返回值方法
            }

            [Infrastructure.Attributes.WebSocket]
            public async Task<int> AsyncAdd(int a, int b)
            {
                await Task.Delay(1);
                return a + b;
            }
        }

        /// <summary>
        /// 验证源生成注入器功能
        /// </summary>
        public static void VerifySourceGeneratorFunctionality()
        {
            Console.WriteLine("=== 验证源生成注入器功能 ===");

            var options = new WebSocketRouteOption();
            var factory = new EndpointInjectorFactory(options);
            var endpointType = typeof(TestEndpoint);

            // 获取注入器
            var injector = factory.GetOrCreateInjector(endpointType);

            // 检查注入器类型
            var injectorType = injector.GetType();
            var isGenerated = injectorType.Name == "TestEndpointInjector" && 
                             injectorType.Namespace == endpointType.Namespace;

            Console.WriteLine($"注入器类型: {injectorType.FullName}");
            Console.WriteLine($"是否为源生成注入器: {isGenerated}");
            Console.WriteLine($"注入器类型名称: {injectorType.Name}");

            // 创建测试实例
            var instance = new TestEndpoint();
            var mockContext = new DefaultHttpContext();
            var mockWebSocket = new MockWebSocket();

            // 执行注入
            injector.Inject(instance, mockContext, mockWebSocket);

            // 验证注入结果
            var httpContextInjected = instance.WebSocketHttpContext == mockContext;
            var webSocketInjected = instance.WebSocketClient == mockWebSocket;

            Console.WriteLine($"HttpContext 注入成功: {httpContextInjected}");
            Console.WriteLine($"WebSocket 注入成功: {webSocketInjected}");

            if (httpContextInjected && webSocketInjected)
            {
                Console.WriteLine("✅ 源生成注入器功能验证通过");
            }
            else
            {
                Console.WriteLine("❌ 源生成注入器功能验证失败");
            }

            Console.WriteLine();
        }

        /// <summary>
        /// 性能对比测试：源生成 vs 反射
        /// </summary>
        public static void PerformanceComparison(int iterations = 1_000_000)
        {
            Console.WriteLine("=== 性能对比测试：源生成 vs 反射 ===");
            Console.WriteLine($"迭代次数: {iterations:N0}");
            Console.WriteLine();

            var options = new WebSocketRouteOption();
            var factory = new EndpointInjectorFactory(options);
            var endpointType = typeof(TestEndpoint);

            // 获取注入器（可能是源生成的或反射的）
            var injector = factory.GetOrCreateInjector(endpointType);
            var isGenerated = injector.GetType().Name == "TestEndpointInjector";

            Console.WriteLine($"使用的注入器类型: {(isGenerated ? "源生成" : "反射")}");
            Console.WriteLine();

            // 准备测试数据
            var mockContext = new DefaultHttpContext();
            var mockWebSocket = new MockWebSocket();

            // 测试源生成/反射注入器性能
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var instance = new TestEndpoint();
                injector.Inject(instance, mockContext, mockWebSocket);
            }
            sw.Stop();

            var injectorTime = sw.ElapsedMilliseconds;
            var injectorOpsPerSecond = (iterations * 1000.0) / sw.ElapsedMilliseconds;

            Console.WriteLine($"注入器执行时间: {injectorTime} ms");
            Console.WriteLine($"每秒操作数: {injectorOpsPerSecond:N0} ops/sec");
            Console.WriteLine();

            // 测试反射注入器性能（强制使用反射）
            var reflectionInjector = new ReflectionEndpointInjector(
                endpointType,
                options.InjectionHttpContextPropertyName,
                options.InjectionWebSocketClientPropertyName);

            sw.Restart();
            for (int i = 0; i < iterations; i++)
            {
                var instance = new TestEndpoint();
                reflectionInjector.Inject(instance, mockContext, mockWebSocket);
            }
            sw.Stop();

            var reflectionTime = sw.ElapsedMilliseconds;
            var reflectionOpsPerSecond = (iterations * 1000.0) / sw.ElapsedMilliseconds;

            Console.WriteLine($"反射注入器执行时间: {reflectionTime} ms");
            Console.WriteLine($"每秒操作数: {reflectionOpsPerSecond:N0} ops/sec");
            Console.WriteLine();

            // 计算性能提升
            if (isGenerated)
            {
                var speedup = (double)reflectionTime / injectorTime;
                var improvement = ((reflectionTime - injectorTime) * 100.0) / reflectionTime;

                Console.WriteLine($"性能提升: {speedup:F2}x");
                Console.WriteLine($"性能改善: {improvement:F2}%");
                Console.WriteLine();

                if (speedup > 1.0)
                {
                    Console.WriteLine("✅ 源生成注入器性能优于反射注入器");
                }
                else
                {
                    Console.WriteLine("⚠️ 源生成注入器性能未达到预期（可能未启用源生成）");
                }
            }
            else
            {
                Console.WriteLine("⚠️ 未检测到源生成的注入器，使用反射注入器");
                Console.WriteLine("提示: 确保项目引用了 Cyaim.WebSocketServer.SourceGenerator");
            }

            Console.WriteLine();
        }

        /// <summary>
        /// 验证源生成方法调用器功能
        /// </summary>
        public static void VerifyMethodInvokerFunctionality()
        {
            Console.WriteLine("=== 验证源生成方法调用器功能 ===");

            var factory = new MethodInvokerFactory();
            var endpointType = typeof(TestEndpoint);
            var testInstance = new TestEndpoint();

            // 测试不同的方法
            var methods = new (string name, MethodInfo method, object[] args, object? expected)[]
            {
                ("Echo", endpointType.GetMethod("Echo")!, new object[] { "Hello" }, (object?)"Echo: Hello"),
                ("Add", endpointType.GetMethod("Add")!, new object[] { 5, 3 }, (object?)8),
                ("VoidMethod", endpointType.GetMethod("VoidMethod")!, new object[] { "test" }, null),
            };

            foreach (var (name, method, args, expected) in methods)
            {
                Console.WriteLine($"\n测试方法: {name}");

                // 获取调用器
                var invoker = factory.GetOrCreateInvoker(method);
                var invokerType = invoker.GetType();
                var isGenerated = invokerType.Name == $"TestEndpoint_{name}Invoker" &&
                                 invokerType.Namespace == endpointType.Namespace;

                Console.WriteLine($"  调用器类型: {invokerType.FullName}");
                Console.WriteLine($"  是否为源生成调用器: {isGenerated}");

                // 执行调用
                var result = invoker.Invoke(testInstance, args);

                // 验证结果
                if (expected == null)
                {
                    // void 方法
                    var success = result == null;
                    Console.WriteLine($"  调用成功: {success}");
                    if (success)
                    {
                        Console.WriteLine($"  ✅ {name} 方法调用验证通过");
                    }
                    else
                    {
                        Console.WriteLine($"  ❌ {name} 方法调用验证失败");
                    }
                }
                else if (result is Task task)
                {
                    // 异步方法需要等待
                    task.Wait();
                    var taskResult = task.GetType().GetProperty("Result")?.GetValue(task);
                    var success = taskResult?.Equals(expected) ?? false;
                    Console.WriteLine($"  调用结果: {taskResult}");
                    Console.WriteLine($"  预期结果: {expected}");
                    Console.WriteLine($"  调用成功: {success}");
                    if (success)
                    {
                        Console.WriteLine($"  ✅ {name} 方法调用验证通过");
                    }
                    else
                    {
                        Console.WriteLine($"  ❌ {name} 方法调用验证失败");
                    }
                }
                else
                {
                    var success = result?.Equals(expected) ?? false;
                    Console.WriteLine($"  调用结果: {result}");
                    Console.WriteLine($"  预期结果: {expected}");
                    Console.WriteLine($"  调用成功: {success}");
                    if (success)
                    {
                        Console.WriteLine($"  ✅ {name} 方法调用验证通过");
                    }
                    else
                    {
                        Console.WriteLine($"  ❌ {name} 方法调用验证失败");
                    }
                }
            }

            Console.WriteLine();
        }

        /// <summary>
        /// 方法调用器性能对比测试：源生成 vs 反射
        /// </summary>
        public static void MethodInvokerPerformanceComparison(int iterations = 1_000_000)
        {
            Console.WriteLine("=== 方法调用器性能对比测试：源生成 vs 反射 ===");
            Console.WriteLine($"迭代次数: {iterations:N0}");
            Console.WriteLine();

            var factory = new MethodInvokerFactory();
            var endpointType = typeof(TestEndpoint);
            var method = endpointType.GetMethod("Add")!;

            // 获取调用器（可能是源生成的或反射的）
            var invoker = factory.GetOrCreateInvoker(method);
            var isGenerated = invoker.GetType().Name == "TestEndpoint_AddInvoker";

            Console.WriteLine($"使用的调用器类型: {(isGenerated ? "源生成" : "反射")}");
            Console.WriteLine();

            // 准备测试数据
            var testInstance = new TestEndpoint();
            var args = new object[] { 5, 3 };

            // 测试源生成/反射调用器性能
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                invoker.Invoke(testInstance, args);
            }
            sw.Stop();

            var invokerTime = sw.ElapsedMilliseconds;
            var invokerOpsPerSecond = (iterations * 1000.0) / sw.ElapsedMilliseconds;

            Console.WriteLine($"调用器执行时间: {invokerTime} ms");
            Console.WriteLine($"每秒操作数: {invokerOpsPerSecond:N0} ops/sec");
            Console.WriteLine();

            // 测试反射调用器性能（强制使用反射）
            var reflectionInvoker = new ReflectionMethodInvoker(method);

            sw.Restart();
            for (int i = 0; i < iterations; i++)
            {
                reflectionInvoker.Invoke(testInstance, args);
            }
            sw.Stop();

            var reflectionTime = sw.ElapsedMilliseconds;
            var reflectionOpsPerSecond = (iterations * 1000.0) / sw.ElapsedMilliseconds;

            Console.WriteLine($"反射调用器执行时间: {reflectionTime} ms");
            Console.WriteLine($"每秒操作数: {reflectionOpsPerSecond:N0} ops/sec");
            Console.WriteLine();

            // 计算性能提升
            if (isGenerated)
            {
                var speedup = (double)reflectionTime / invokerTime;
                var improvement = ((reflectionTime - invokerTime) * 100.0) / reflectionTime;

                Console.WriteLine($"性能提升: {speedup:F2}x");
                Console.WriteLine($"性能改善: {improvement:F2}%");
                Console.WriteLine();

                if (speedup > 1.0)
                {
                    Console.WriteLine("✅ 源生成调用器性能优于反射调用器");
                }
                else
                {
                    Console.WriteLine("⚠️ 源生成调用器性能未达到预期（可能未启用源生成）");
                }
            }
            else
            {
                Console.WriteLine("⚠️ 未检测到源生成的调用器，使用反射调用器");
                Console.WriteLine("提示: 确保项目引用了 Cyaim.WebSocketServer.SourceGenerator");
            }

            Console.WriteLine();
        }

        /// <summary>
        /// 运行所有测试
        /// </summary>
        public static void RunAllTests()
        {
            VerifySourceGeneratorFunctionality();
            PerformanceComparison(1_000_000);
            VerifyMethodInvokerFunctionality();
            MethodInvokerPerformanceComparison(1_000_000);
        }

        /// <summary>
        /// 模拟 WebSocket 类（用于测试）
        /// </summary>
        private class MockWebSocket : WebSocket
        {
            public override WebSocketCloseStatus? CloseStatus => null;
            public override string? CloseStatusDescription => null;
            public override WebSocketState State => WebSocketState.Open;
            public override string? SubProtocol => null;

            public override void Abort() { }
            public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

            public override void Dispose()
            {
            }

            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotImplementedException();
            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}

