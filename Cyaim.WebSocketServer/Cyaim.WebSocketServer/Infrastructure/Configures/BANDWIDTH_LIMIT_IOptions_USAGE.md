# 使用 IOptions 模式配置带宽限速策略

## 概述

现在带宽限速策略支持使用 ASP.NET Core 标准的 `IOptions<T>` 模式进行配置，这是更推荐的方式。

## 优势

1. **类型安全**：编译时检查配置类型
2. **自动绑定**：使用 `IConfiguration.Bind()` 自动绑定配置
3. **支持配置变更通知**：可以使用 `IOptionsMonitor<T>` 监听配置变更
4. **符合 ASP.NET Core 设计规范**：与框架其他部分保持一致
5. **简化代码**：无需手动解析配置节点

## 使用方式

### 方式一：通过 appsettings.json 自动绑定（推荐）

在 `appsettings.json` 中配置：

```json
{
  "BandwidthLimitPolicy": {
    "Enabled": true,
    "GlobalChannelBandwidthLimit": {
      "/ws": 10485760
    },
    "ChannelMaxBandwidthLimit": {
      "/ws": 5242880
    }
  }
}
```

然后在使用时传入 `IConfiguration`：

```csharp
builder.Services.ConfigureWebSocketRoute(builder.Configuration, x =>
{
    // 系统会自动从 IConfiguration 绑定到 IOptions<BandwidthLimitPolicy>
    // 如果 x.BandwidthLimitPolicy 为 null，会在运行时从 IOptions 加载
});
```

或者手动配置 IOptions：

```csharp
// 先配置 IOptions
builder.Services.Configure<BandwidthLimitPolicy>(
    builder.Configuration.GetSection("BandwidthLimitPolicy"));

// 然后配置 WebSocketRoute
builder.Services.ConfigureWebSocketRoute(x =>
{
    // 系统会在运行时从 IOptions 自动加载配置
});
```

### 方式二：使用 services.Configure<T> 配置

```csharp
// 先配置 IOptions
builder.Services.Configure<BandwidthLimitPolicy>(options =>
{
    options.Enabled = true;
    options.GlobalChannelBandwidthLimit = new Dictionary<string, long>
    {
        { "/ws", 10 * 1024 * 1024 }
    };
    options.ChannelMaxBandwidthLimit = new Dictionary<string, long>
    {
        { "/ws", 5 * 1024 * 1024 }
    };
});

// 然后配置 WebSocketRoute
builder.Services.ConfigureWebSocketRoute(x =>
{
    // 系统会在运行时从 IOptions<BandwidthLimitPolicy> 自动加载配置
    // 如果 x.BandwidthLimitPolicy 为 null，会自动使用 IOptions 中的配置
});
```

### 方式三：使用 IConfiguration.Bind() 手动绑定

```csharp
// 先配置 IOptions
builder.Services.Configure<BandwidthLimitPolicy>(
    builder.Configuration.GetSection("BandwidthLimitPolicy"));

// 然后配置 WebSocketRoute
builder.Services.ConfigureWebSocketRoute(x =>
{
    // 系统会在运行时从 IOptions<BandwidthLimitPolicy> 自动加载配置
});
```

### 方式四：在 ConfigureWebSocketRoute 中直接设置（优先级最高）

```csharp
builder.Services.ConfigureWebSocketRoute(x =>
{
    var mvcHandler = new MvcChannelHandler();
    
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", mvcHandler.ConnectionEntry }
    };
    
    // 直接设置限速策略（优先级最高）
    x.BandwidthLimitPolicy = new BandwidthLimitPolicy
    {
        Enabled = true,
        GlobalChannelBandwidthLimit = new Dictionary<string, long>
        {
            { "/ws", 10 * 1024 * 1024 }
        }
    };
    
    x.ApplicationServiceCollection = builder.Services;
});
```

## 配置优先级

1. **最高优先级**：在 `ConfigureWebSocketRoute` 的 `setupAction` 中直接设置的 `BandwidthLimitPolicy`
2. **次优先级**：从 `IOptions<BandwidthLimitPolicy>` 自动加载的配置
3. **最低优先级**：默认值（`Enabled = false`）

## 使用 IOptionsMonitor 监听配置变更

如果需要支持配置热更新，可以使用 `IOptionsMonitor<T>`：

```csharp
// 注册服务
builder.Services.Configure<BandwidthLimitPolicy>(builder.Configuration.GetSection("BandwidthLimitPolicy"));

// 在需要的地方注入 IOptionsMonitor
public class SomeService
{
    private readonly IOptionsMonitor<BandwidthLimitPolicy> _optionsMonitor;
    
    public SomeService(IOptionsMonitor<BandwidthLimitPolicy> optionsMonitor)
    {
        _optionsMonitor = optionsMonitor;
        
        // 监听配置变更
        _optionsMonitor.OnChange(policy =>
        {
            // 配置变更时的处理逻辑
            // 注意：需要手动更新 WebSocketRouteOption 中的 BandwidthLimitPolicy
        });
    }
    
    public BandwidthLimitPolicy GetCurrentPolicy()
    {
        return _optionsMonitor.CurrentValue;
    }
}
```

## 配置验证（可选）

可以添加配置验证：

```csharp
builder.Services.AddOptions<BandwidthLimitPolicy>()
    .Bind(builder.Configuration.GetSection("BandwidthLimitPolicy"))
    .ValidateDataAnnotations(); // 如果 BandwidthLimitPolicy 使用了数据注解验证
```

## 向后兼容

原有的扩展方法 `LoadFromConfiguration()` 和 `LoadFromJsonFile()` 仍然可用，用于向后兼容：

```csharp
var policy = new BandwidthLimitPolicy();
policy.LoadFromConfiguration(configuration);
// 或
policy.LoadFromJsonFile("bandwidth-limit-policy.json");
```

## 总结

推荐使用 `IOptions<T>` 模式，因为：
- 更符合 ASP.NET Core 的设计规范
- 代码更简洁
- 支持配置验证和变更通知
- 更好的依赖注入支持

