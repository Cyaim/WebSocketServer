using Cyaim.WebSocketServer.Tests.Injector;

Console.WriteLine("========================================");
Console.WriteLine("源生成器功能验证和性能测试套件");
Console.WriteLine("========================================");
Console.WriteLine();

try
{
    EndpointInjectorFactoryTests.RunAllTests();
    Console.WriteLine("========================================");
    Console.WriteLine("所有测试完成");
    Console.WriteLine("========================================");
}
catch (Exception ex)
{
    Console.WriteLine($"测试执行出错: {ex.Message}");
    Console.WriteLine($"堆栈跟踪: {ex.StackTrace}");
    Environment.Exit(1);
}

