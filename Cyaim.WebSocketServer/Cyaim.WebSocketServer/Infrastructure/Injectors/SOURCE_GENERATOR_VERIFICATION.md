# 源生成器功能验证和性能对比

## 修复内容

### 1. 修复 EndpointInjectorFactory 查找生成的注入器类型的逻辑

**问题**：
- 原代码使用 `endpointType.FullName` 来查找注入器类型（`{FullName}Injector`）
- 但源生成器生成的类名格式是 `{ClassName}Injector`（在同一个命名空间中）

**修复**：
- 更新了 `TryCreateGeneratedInjector` 方法，使用多种策略查找生成的注入器：
  1. 首先尝试使用 `{Namespace}.{ClassName}Injector` 格式
  2. 如果找不到，尝试使用完整名称格式（处理嵌套类型）
  3. 最后，在同一个命名空间下搜索所有类型，查找匹配的注入器

**文件**：`EndpointInjectorFactory.cs`

### 2. 添加缺失的 using 语句

添加了 `using System.Linq;` 以支持 `FirstOrDefault` 方法。

## 测试项目

测试代码已移至独立的测试项目：`Tests/Cyaim.WebSocketServer.Tests.Injector`

### 项目结构

- `Program.cs` - 测试程序入口
- `EndpointInjectorFactoryTests.cs` - 测试代码
- `README.md` - 测试项目说明文档

### 测试方法

1. **VerifySourceGeneratorFunctionality()**
   - 验证源生成注入器是否能正确注入 HttpContext 和 WebSocket
   - 检查注入器类型是否为源生成的类型

2. **PerformanceComparison(int iterations)**
   - 对比源生成注入器和反射注入器的性能
   - 默认迭代 1,000,000 次
   - 输出性能指标和提升比例

3. **RunAllTests()**
   - 运行所有测试

## 如何运行测试

### 方法 1：使用 dotnet CLI

```bash
cd Tests/Cyaim.WebSocketServer.Tests.Injector
dotnet run
```

### 方法 2：使用 Visual Studio

1. 右键点击 `Cyaim.WebSocketServer.Tests.Injector` 项目
2. 选择"设为启动项目"
3. 按 F5 运行

## 预期结果

### 功能验证

如果源生成器正常工作，应该看到：
- ✅ 注入器类型名称包含 "Injector"
- ✅ HttpContext 注入成功
- ✅ WebSocket 注入成功
- ✅ 源生成注入器功能验证通过

### 性能对比

源生成注入器应该比反射注入器快 2-10 倍（取决于具体场景）。

典型结果：
- 源生成注入器：~50-200 万 ops/sec
- 反射注入器：~10-50 万 ops/sec
- 性能提升：2-10x

## 注意事项

1. **源生成器必须正确引用**
   - 确保项目引用了 `Cyaim.WebSocketServer.SourceGenerator`
   - 源生成器在编译时工作，运行时无法动态生成

2. **属性名称**
   - 源生成器目前只支持默认属性名：`WebSocketHttpContext` 和 `WebSocketClient`
   - 如果使用自定义属性名，将自动回退到反射注入器

3. **编译后检查**
   - 编译后，可以在 `obj` 目录下查看生成的 `.g.cs` 文件
   - 例如：`obj/Debug/net8.0/TestEndpointInjector.g.cs`

## 故障排除

### 如果测试显示使用反射注入器

1. 检查项目是否引用了源生成器项目：
   ```xml
   <ItemGroup>
     <ProjectReference Include="path/to/Cyaim.WebSocketServer.SourceGenerator.csproj" 
                       OutputItemType="Analyzer" 
                       ReferenceOutputAssembly="false" />
   </ItemGroup>
   ```

2. 检查 Endpoint 类是否有 `[WebSocket]` 特性

3. 检查属性名称是否为默认名称（`WebSocketHttpContext` 和 `WebSocketClient`）

4. 重新编译项目以确保源生成器运行

### 如果性能提升不明显

- 源生成注入器的主要优势在于避免反射调用
- 在大量调用场景下，性能提升会更明显
- 单次调用可能看不出明显差异

