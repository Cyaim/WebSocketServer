# WebSocket 集群启动脚本
# 此脚本将启动3个集群节点实例

$ErrorActionPreference = "Stop"

# 获取脚本所在目录
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = Join-Path $scriptDir "bin\Debug\net8.0\Cyaim.WebSocketServer.Sample.Dashboard.exe"

# 检查 exe 文件是否存在
if (-not (Test-Path $exePath)) {
    Write-Host "错误: 找不到可执行文件: $exePath" -ForegroundColor Red
    Write-Host "请先编译项目: dotnet build" -ForegroundColor Yellow
    exit 1
}

Write-Host "正在启动 WebSocket 集群（3个节点）..." -ForegroundColor Green
Write-Host ""

# 启动节点1
Write-Host "启动节点1 (端口 5001)..." -ForegroundColor Cyan
$node1 = Start-Process -FilePath $exePath -ArgumentList "--node=node1" -PassThru -WindowStyle Normal
Start-Sleep -Seconds 2

# 启动节点2
Write-Host "启动节点2 (端口 5002)..." -ForegroundColor Cyan
$node2 = Start-Process -FilePath $exePath -ArgumentList "--node=node2" -PassThru -WindowStyle Normal
Start-Sleep -Seconds 2

# 启动节点3
Write-Host "启动节点3 (端口 5003)..." -ForegroundColor Cyan
$node3 = Start-Process -FilePath $exePath -ArgumentList "--node=node3" -PassThru -WindowStyle Normal
Start-Sleep -Seconds 2

Write-Host ""
Write-Host "所有节点已启动！" -ForegroundColor Green
Write-Host ""
Write-Host "节点访问地址:" -ForegroundColor Yellow
Write-Host "  节点1: http://localhost:5001" -ForegroundColor White
Write-Host "  节点2: http://localhost:5002" -ForegroundColor White
Write-Host "  节点3: http://localhost:5003" -ForegroundColor White
Write-Host ""
Write-Host "Dashboard 访问地址:" -ForegroundColor Yellow
Write-Host "  节点1: http://localhost:5001/dashboard" -ForegroundColor White
Write-Host "  节点2: http://localhost:5002/dashboard" -ForegroundColor White
Write-Host "  节点3: http://localhost:5003/dashboard" -ForegroundColor White
Write-Host ""
Write-Host "WebSocket 连接地址:" -ForegroundColor Yellow
Write-Host "  节点1: ws://localhost:5001/ws" -ForegroundColor White
Write-Host "  节点2: ws://localhost:5002/ws" -ForegroundColor White
Write-Host "  节点3: ws://localhost:5003/ws" -ForegroundColor White
Write-Host ""
Write-Host "按任意键停止所有节点..." -ForegroundColor Yellow
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")

# 停止所有节点
Write-Host ""
Write-Host "正在停止所有节点..." -ForegroundColor Yellow
Stop-Process -Id $node1.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $node2.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $node3.Id -Force -ErrorAction SilentlyContinue

Write-Host "所有节点已停止。" -ForegroundColor Green

