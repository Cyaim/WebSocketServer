# Endpoint 注入器源代码生成器

## 功能说明

此源代码生成器会自动为带有 `[WebSocket]` 特性的 endpoint 类生成优化的注入器，用于加速 HttpContext 和 WebSocket 的注入过程。

## 工作原理

1. **编译时扫描**：生成器扫描所有带有 `[WebSocket]` 特性的类
2. **生成注入器**：为每个符合条件的类生成一个 `{ClassName}Injector` 类
3. **运行时选择**：`EndpointInjectorFactory` 会自动选择使用生成的注入器或反射注入器

## 使用方式

### 1. 引用源代码生成器

在主项目的 `.csproj` 文件中添加：

```xml
<ItemGroup>
  <Analyzer Include="path/to/Cyaim.WebSocketServer.SourceGenerator.dll" />
</ItemGroup>
```

或者作为项目引用：

```xml
<ItemGroup>
  <ProjectReference Include="path/to/Cyaim.WebSocketServer.SourceGenerator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
```

### 2. 定义 Endpoint 类

```csharp
public class MyController
{
    // 默认属性名：WebSocketHttpContext
    public HttpContext WebSocketHttpContext { get; set; }
    
    // 默认属性名：WebSocketClient
    public WebSocket WebSocketClient { get; set; }

    [WebSocket]
    public async Task<object> MyMethod(string param)
    {
        // 可以直接使用 WebSocketHttpContext 和 WebSocketClient
        return new { message = "Hello" };
    }
}
```

### 3. 生成的代码

生成器会自动生成 `MyControllerInjector` 类：

```csharp
public class MyControllerInjector : IEndpointInjector
{
    public void Inject(object instance, HttpContext httpContext, WebSocket webSocket)
    {
        if (instance is MyController target)
        {
            target.WebSocketHttpContext = httpContext;
            target.WebSocketClient = webSocket;
        }
    }
}
```

## 兼容性

- ✅ **编译时已知的类型**：使用源代码生成的注入器（高性能）
- ✅ **动态添加的类型**：自动回退到反射注入器（兼容性）

## 性能优势

- **源代码生成**：直接属性赋值，无反射开销
- **反射后备**：动态类型自动使用反射，保持兼容性
- **缓存机制**：注入器实例被缓存，避免重复创建

## 注意事项

1. 属性必须是可写的（有 setter）
2. 属性类型必须匹配（HttpContext 和 WebSocket）
3. 默认属性名为 `WebSocketHttpContext` 和 `WebSocketClient`，可在 `WebSocketRouteOption` 中配置

