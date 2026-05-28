@echo off
setlocal
chcp 65001 >nul

cd /d "%~dp0"

set "CONNECTION_NAME=%~1"
if "%CONNECTION_NAME%"=="" set "CONNECTION_NAME=DataSyncDb"
set "BACKUP_STAMP=%TEMP%\datasync_lhyy_esb_messages_%RANDOM%_%RANDOM%.stamp"

if exist "DataSync.LHYY.V2.exe" (
    set "RUNNER=DataSync.LHYY.V2.exe"
) else (
    set "RUNNER=dotnet run --project DataSync.LHYY.V2.csproj --"
)

echo.
echo [1/4] 执行 ESB 消息性能优化升级，连接：%CONNECTION_NAME%
%RUNNER% message-archive upgrade --connection "%CONNECTION_NAME%" --backup-stamp "%BACKUP_STAMP%"
if errorlevel 1 goto fail

echo.
echo [2/4] 迁移现有历史终态消息到归档分区
%RUNNER% message-archive migrate --connection "%CONNECTION_NAME%" --batch-size 50000 --backup-stamp "%BACKUP_STAMP%"
if errorlevel 1 goto fail

echo.
echo [3/4] 执行归档结构与数据一致性校验
%RUNNER% message-archive verify --connection "%CONNECTION_NAME%"
if errorlevel 1 goto fail

echo.
echo [4/4] 升级完成，验证通过
if exist "%BACKUP_STAMP%" del /q "%BACKUP_STAMP%" >nul 2>nul
pause
exit /b 0

:fail
echo.
echo 升级或验证失败，请根据上方错误信息处理后重试。
if exist "%BACKUP_STAMP%" del /q "%BACKUP_STAMP%" >nul 2>nul
pause
exit /b 1
