# 源生成器功能验证和性能测试项目

此项目用于验证源生成注入器和方法调用器的功能和性能。

## 项目结构

- `Program.cs` - 测试程序入口
- `EndpointInjectorFactoryTests.cs` - 测试代码（包含注入器和方法调用器测试）
- `README.md` - 本文件

## 运行测试

### 方法 1: 使用 dotnet CLI

```bash
cd Tests/Cyaim.WebSocketServer.Tests.Injector
dotnet run
```

### 方法 2: 使用 Visual Studio

1. 右键点击项目
2. 选择"设为启动项目"
3. 按 F5 运行

## 测试内容

### 1. 注入器功能验证测试

验证源生成注入器是否能正确注入 HttpContext 和 WebSocket。

### 2. 注入器性能对比测试

对比源生成注入器和反射注入器的性能，默认迭代 1,000,000 次。

### 3. 方法调用器功能验证测试

验证源生成方法调用器是否能正确调用不同的方法类型：
- 同步方法（返回字符串）
- 同步方法（返回值类型）
- void 方法（无返回值）
- 异步方法（返回 Task）

### 4. 方法调用器性能对比测试

对比源生成方法调用器和反射方法调用器的性能，默认迭代 1,000,000 次。

## 预期结果

### 注入器功能验证

如果源生成器正常工作，应该看到：
- ✅ 注入器类型名称包含 "Injector"
- ✅ HttpContext 注入成功
- ✅ WebSocket 注入成功
- ✅ 源生成注入器功能验证通过

### 方法调用器功能验证

如果源生成器正常工作，应该看到：
- ✅ 调用器类型名称格式为 "{ClassName}_{MethodName}Invoker"
- ✅ 各种方法类型调用成功（同步、异步、void）
- ✅ 方法返回值正确
- ✅ 源生成方法调用器功能验证通过

### 性能对比

源生成注入器和调用器应该比反射方式快 2-10 倍（取决于具体场景）。

典型结果：
- 源生成注入器：~50-200 万 ops/sec
- 反射注入器：~10-50 万 ops/sec
- 性能提升：2-10x

- 源生成方法调用器：~100-500 万 ops/sec
- 反射方法调用器：~20-100 万 ops/sec
- 性能提升：3-10x

## 注意事项

1. **源生成器必须正确引用**
   - 项目已引用 `Cyaim.WebSocketServer.SourceGenerator` 作为 Analyzer
   - 源生成器在编译时工作，运行时无法动态生成

2. **属性名称**
   - 源生成器目前只支持默认属性名：`WebSocketHttpContext` 和 `WebSocketClient`
   - 如果使用自定义属性名，将自动回退到反射注入器

3. **方法要求**
   - 方法必须带有 `[WebSocket]` 特性才会生成调用器
   - 支持各种返回类型：void、普通类型、Task、Task<T>
   - 支持各种参数类型：值类型、引用类型、可空类型

4. **编译后检查**
   - 编译后，可以在 `obj` 目录下查看生成的 `.g.cs` 文件
   - 注入器：`obj/Debug/net8.0/TestEndpointInjector.g.cs`
   - 方法调用器：`obj/Debug/net8.0/TestEndpoint_EchoInvoker.g.cs`、`TestEndpoint_AddInvoker.g.cs` 等

## 故障排除

### 如果测试显示使用反射注入器或调用器

1. 检查项目是否引用了源生成器项目：
   ```xml
   <ProjectReference Include="..\..\Cyaim.WebSocketServer.SourceGenerator\Cyaim.WebSocketServer.SourceGenerator.csproj" 
                     OutputItemType="Analyzer" 
                     ReferenceOutputAssembly="false" />
   ```

2. 检查 TestEndpoint 类是否有 `[WebSocket]` 特性
   - 类中的方法必须带有 `[WebSocket]` 特性才会生成调用器

3. 检查属性名称是否为默认名称（`WebSocketHttpContext` 和 `WebSocketClient`）
   - 如果使用自定义属性名，注入器将自动回退到反射方式

4. 重新编译项目以确保源生成器运行
   - 源生成器在编译时工作，需要重新编译才能生成新的代码

5. 检查生成的代码文件
   - 在 `obj/Debug/net8.0/` 目录下应该能看到生成的 `.g.cs` 文件
   - 如果文件不存在，说明源生成器可能没有运行

### 如果性能提升不明显

- 源生成注入器和调用器的主要优势在于避免反射调用
- 在大量调用场景下，性能提升会更明显
- 单次调用可能看不出明显差异
- 方法调用器的性能提升可能因方法复杂度而异

