@echo off
chcp 65001 >nul
echo WebSocket 集群启动脚本
echo 此脚本将启动3个集群节点实例
echo.

REM 获取脚本所在目录
set SCRIPT_DIR=%~dp0
set EXE_PATH=%SCRIPT_DIR%bin\Debug\net8.0\Cyaim.WebSocketServer.Sample.Dashboard.exe

REM 检查 exe 文件是否存在
if not exist "%EXE_PATH%" (
    echo 错误: 找不到可执行文件: %EXE_PATH%
    echo 请先编译项目: dotnet build
    pause
    exit /b 1
)

echo 正在启动 WebSocket 集群（3个节点）...
echo.

REM 启动节点1
echo 启动节点1 (端口 5001)...
start "WebSocket Node1" "%EXE_PATH%" --node=node1
timeout /t 2 /nobreak >nul

REM 启动节点2
echo 启动节点2 (端口 5002)...
start "WebSocket Node2" "%EXE_PATH%" --node=node2
timeout /t 2 /nobreak >nul

REM 启动节点3
echo 启动节点3 (端口 5003)...
start "WebSocket Node3" "%EXE_PATH%" --node=node3
timeout /t 2 /nobreak >nul

echo.
echo 所有节点已启动！
echo.
echo 节点访问地址:
echo   节点1: http://localhost:5001
echo   节点2: http://localhost:5002
echo   节点3: http://localhost:5003
echo.
echo Dashboard 访问地址:
echo   节点1: http://localhost:5001/dashboard
echo   节点2: http://localhost:5002/dashboard
echo   节点3: http://localhost:5003/dashboard
echo.
echo WebSocket 连接地址:
echo   节点1: ws://localhost:5001/ws
echo   节点2: ws://localhost:5002/ws
echo   节点3: ws://localhost:5003/ws
echo.
echo 按任意键停止所有节点...
pause >nul

REM 停止所有节点
echo.
echo 正在停止所有节点...
taskkill /FI "WINDOWTITLE eq WebSocket Node1*" /F >nul 2>&1
taskkill /FI "WINDOWTITLE eq WebSocket Node2*" /F >nul 2>&1
taskkill /FI "WINDOWTITLE eq WebSocket Node3*" /F >nul 2>&1

echo 所有节点已停止。
pause

