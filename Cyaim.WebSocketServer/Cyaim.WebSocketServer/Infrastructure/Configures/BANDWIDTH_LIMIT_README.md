# 接收请求体限速策略使用说明

## 概述

接收请求体限速策略用于防止某些连接占用服务器绝大部分带宽资源，确保其他连接能够正常工作。策略支持多层级配置，可动态通过代码配置，也可从配置文件加载，并可随时开关。

## 策略层级

### 1. 通道级别（单个连接）

- **最低带宽保障** (`ChannelMinBandwidthGuarantee`): 确保单个连接能够获得的最低带宽（字节/秒）
- **最高带宽限制** (`ChannelMaxBandwidthLimit`): 限制单个连接的最大带宽（字节/秒）

### 2. 通道级别（多个连接）

- **平均分配带宽策略** (`ChannelEnableAverageBandwidth`): 是否启用平均分配带宽，当启用时，通道总带宽会平均分配给所有连接
- **链接最低带宽保障** (`ChannelConnectionMinBandwidthGuarantee`): 每个连接的最低带宽保障（字节/秒）
- **链接最高带宽限制** (`ChannelConnectionMaxBandwidthLimit`): 每个连接的最高带宽限制（字节/秒）

### 3. WebSocket端点级别

- **端点最高限速** (`EndPointMaxBandwidthLimit`): 每个端点的最高限速（字节/秒）
- **端点最低带宽保障** (`EndPointMinBandwidthGuarantee`): 每个端点的最低带宽保障（字节/秒）

### 4. 全局服务级别

- **全局通道限速** (`GlobalChannelBandwidthLimit`): 配置每个通道的总限速（字节/秒）

## 配置方式

### 方式一：通过代码配置

```csharp
builder.Services.ConfigureWebSocketRoute(x =>
{
    var mvcHandler = new MvcChannelHandler();
    
    x.WebSocketChannels = new Dictionary<string, WebSocketRouteOption.WebSocketChannelHandler>()
    {
        { "/ws", mvcHandler.ConnectionEntry }
    };
    
    // 配置限速策略
    x.BandwidthLimitPolicy = new BandwidthLimitPolicy
    {
        Enabled = true,
        GlobalChannelBandwidthLimit = new Dictionary<string, long>
        {
            { "/ws", 10 * 1024 * 1024 } // 10MB/s
        },
        ChannelMinBandwidthGuarantee = new Dictionary<string, long>
        {
            { "/ws", 1024 * 1024 } // 1MB/s
        },
        ChannelMaxBandwidthLimit = new Dictionary<string, long>
        {
            { "/ws", 5 * 1024 * 1024 } // 5MB/s
        },
        ChannelEnableAverageBandwidth = new Dictionary<string, bool>
        {
            { "/ws", true }
        },
        ChannelConnectionMinBandwidthGuarantee = new Dictionary<string, long>
        {
            { "/ws", 512 * 1024 } // 512KB/s
        },
        ChannelConnectionMaxBandwidthLimit = new Dictionary<string, long>
        {
            { "/ws", 2 * 1024 * 1024 } // 2MB/s
        },
        EndPointMaxBandwidthLimit = new Dictionary<string, long>
        {
            { "controller.action", 1024 * 1024 } // 1MB/s
        },
        EndPointMinBandwidthGuarantee = new Dictionary<string, long>
        {
            { "controller.action", 256 * 1024 } // 256KB/s
        }
    };
    
    x.ApplicationServiceCollection = builder.Services;
});
```

### 方式二：从配置文件加载

#### 使用 appsettings.json

在 `appsettings.json` 中添加配置：

```json
{
  "BandwidthLimitPolicy": {
    "Enabled": true,
    "GlobalChannelBandwidthLimit": {
      "/ws": 10485760
    },
    "ChannelMinBandwidthGuarantee": {
      "/ws": 1048576
    },
    "ChannelMaxBandwidthLimit": {
      "/ws": 5242880
    },
    "ChannelEnableAverageBandwidth": {
      "/ws": true
    },
    "ChannelConnectionMinBandwidthGuarantee": {
      "/ws": 524288
    },
    "ChannelConnectionMaxBandwidthLimit": {
      "/ws": 2097152
    },
    "EndPointMaxBandwidthLimit": {
      "controller.action": 1048576
    },
    "EndPointMinBandwidthGuarantee": {
      "controller.action": 262144
    }
  }
}
```

系统会自动从 `IConfiguration` 加载配置。

#### 从JSON文件加载

```csharp
var policy = new BandwidthLimitPolicy();
policy.LoadFromJsonFile("bandwidth-limit-policy.json");
webSocketOptions.BandwidthLimitPolicy = policy;
```

#### 从IConfiguration加载

```csharp
var policy = new BandwidthLimitPolicy();
policy.LoadFromConfiguration(configuration, "BandwidthLimitPolicy");
webSocketOptions.BandwidthLimitPolicy = policy;
```

## 动态更新策略

可以通过 `BandwidthLimitManager` 动态更新策略：

```csharp
// 获取限速管理器（需要在MvcChannelHandler中暴露）
var newPolicy = new BandwidthLimitPolicy
{
    Enabled = true,
    // ... 配置
};
bandwidthLimitManager.UpdatePolicy(newPolicy);
```

## 单位说明

所有带宽限制的单位都是 **字节/秒** (Bytes/Second)。

常用单位转换：
- 1 KB/s = 1024 字节/秒
- 1 MB/s = 1048576 字节/秒
- 1 GB/s = 1073741824 字节/秒

## 注意事项

1. **策略优先级**: 多个策略同时生效时，会取最严格的限制（等待时间最长的）
2. **性能影响**: 限速策略会在接收数据时进行等待，可能会影响响应时间
3. **开关控制**: 通过 `Enabled` 属性可以快速开关限速功能
4. **端点路径**: 端点路径格式为 `controller.action`（小写），例如 `usercontroller.getuser` 对应 `user.getuser`
5. **通道路径**: 通道路径对应 WebSocket 连接路径，例如 `/ws`

## 示例场景

### 场景1：限制单个连接的最大带宽

```csharp
x.BandwidthLimitPolicy = new BandwidthLimitPolicy
{
    Enabled = true,
    ChannelMaxBandwidthLimit = new Dictionary<string, long>
    {
        { "/ws", 2 * 1024 * 1024 } // 每个连接最多2MB/s
    }
};
```

### 场景2：多连接时平均分配带宽

```csharp
x.BandwidthLimitPolicy = new BandwidthLimitPolicy
{
    Enabled = true,
    GlobalChannelBandwidthLimit = new Dictionary<string, long>
    {
        { "/ws", 10 * 1024 * 1024 } // 通道总带宽10MB/s
    },
    ChannelEnableAverageBandwidth = new Dictionary<string, bool>
    {
        { "/ws", true } // 启用平均分配
    }
};
// 如果有5个连接，每个连接将获得约2MB/s的带宽
```

### 场景3：为特定端点设置限速

```csharp
x.BandwidthLimitPolicy = new BandwidthLimitPolicy
{
    Enabled = true,
    EndPointMaxBandwidthLimit = new Dictionary<string, long>
    {
        { "file.upload", 5 * 1024 * 1024 }, // 文件上传端点限制5MB/s
        { "data.download", 10 * 1024 * 1024 } // 数据下载端点限制10MB/s
    }
};
```

## 故障排查

1. **限速未生效**: 检查 `Enabled` 是否为 `true`
2. **配置未加载**: 检查配置文件路径和格式是否正确
3. **端点路径不匹配**: 确保端点路径格式为小写的 `controller.action`
4. **性能问题**: 如果限速导致性能问题，可以适当放宽限制或关闭限速功能

