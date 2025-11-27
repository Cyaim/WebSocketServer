# WebSocket 客户端消息发送测试脚本
# WebSocket Client Message Sending Test Script

param(
    [string]$BaseUrl = "http://localhost:5001",
    [string]$TargetConnectionId = "",
    [string]$Message = "Hello from Client1!"
)

Write-Host "=== WebSocket 客户端消息发送测试 ===" -ForegroundColor Cyan
Write-Host ""

# 1. 获取所有连接
Write-Host "1. 获取所有连接..." -ForegroundColor Yellow
try {
    $clientsResponse = Invoke-RestMethod -Uri "$BaseUrl/ws_server/api/client" -Method GET
    if ($clientsResponse.success -and $clientsResponse.data.Count -gt 0) {
        Write-Host "找到 $($clientsResponse.data.Count) 个连接:" -ForegroundColor Green
        $clientsResponse.data | ForEach-Object {
            Write-Host "  - ConnectionId: $($_.connectionId), NodeId: $($_.nodeId), State: $($_.state)" -ForegroundColor Gray
        }
        
        # 如果没有指定目标连接ID，使用第二个连接（假设第一个是Client1，第二个是Client2）
        if ([string]::IsNullOrEmpty($TargetConnectionId) -and $clientsResponse.data.Count -ge 2) {
            $TargetConnectionId = $clientsResponse.data[1].connectionId
            Write-Host ""
            Write-Host "自动选择目标连接: $TargetConnectionId" -ForegroundColor Cyan
        }
    }
    else {
        Write-Host "未找到任何连接，请先建立 WebSocket 连接" -ForegroundColor Red
        exit 1
    }
}
catch {
    Write-Host "获取连接列表失败: $_" -ForegroundColor Red
    exit 1
}

Write-Host ""

# 2. 检查目标连接ID
if ([string]::IsNullOrEmpty($TargetConnectionId)) {
    Write-Host "错误: 未指定目标连接ID" -ForegroundColor Red
    Write-Host "用法: .\test-client-message.ps1 -TargetConnectionId <连接ID> -Message <消息内容>" -ForegroundColor Yellow
    exit 1
}

# 3. 发送消息
Write-Host "2. 发送消息到连接: $TargetConnectionId" -ForegroundColor Yellow
Write-Host "   消息内容: $Message" -ForegroundColor Gray
Write-Host ""

try {
    $body = @{
        connectionId = $TargetConnectionId
        content      = $Message
        messageType  = "Text"
    } | ConvertTo-Json

    $response = Invoke-RestMethod -Uri "$BaseUrl/ws_server/api/messages/send" `
        -Method POST `
        -ContentType "application/json" `
        -Body $body

    if ($response.success) {
        Write-Host "✓ 消息发送成功!" -ForegroundColor Green
        Write-Host "  响应: $($response | ConvertTo-Json -Compress)" -ForegroundColor Gray
    }
    else {
        Write-Host "✗ 消息发送失败: $($response.error)" -ForegroundColor Red
    }
}
catch {
    Write-Host "✗ 发送消息时出错: $_" -ForegroundColor Red
    if ($_.Exception.Response) {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        $responseBody = $reader.ReadToEnd()
        Write-Host "  错误详情: $responseBody" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "=== 测试完成 ===" -ForegroundColor Cyan

