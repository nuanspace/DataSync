@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "IMAGE_NAME=datasync-lhyy-v2:esb-message-archive"
set "PACKAGE_DIR=%~dp0"

for %%I in ("%PACKAGE_DIR%..") do set "REPO_ROOT=%%~fI"

set "PROJECT_DIR=%REPO_ROOT%\DataSync.LHYY.V2"

for /f %%I in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set "BUILD_STAMP=%%I"
set "PACKAGE_NAME=DataSync.LHYY.V2_ESBArchiveUpgrade_%BUILD_STAMP%"
set "ZIP_FILE=%PACKAGE_DIR%%PACKAGE_NAME%.zip"
set "STAGING_ROOT=%TEMP%\%PACKAGE_NAME%_build"
set "STAGING_DIR=%STAGING_ROOT%\%PACKAGE_NAME%"
set "IMAGE_PACKAGE=%STAGING_DIR%\datasync-lhyy-v2.tar"

echo.
echo [1/5] 检查 Docker 和项目目录
where docker >nul 2>nul
if errorlevel 1 (
    echo 未找到 docker 命令，请先安装并启动 Docker Desktop。
    exit /b 1
)

docker version >nul 2>nul
if errorlevel 1 (
    echo Docker 当前不可用，请先启动 Docker Desktop。
    exit /b 1
)

if not exist "%PROJECT_DIR%\Dockerfile" (
    echo 未找到项目 Dockerfile：%PROJECT_DIR%\Dockerfile
    exit /b 1
)

echo.
echo [2/5] 构建新版镜像：%IMAGE_NAME%
pushd "%PROJECT_DIR%"
docker build --progress=plain -t "%IMAGE_NAME%" .
if errorlevel 1 (
    popd
    echo 镜像构建失败。
    exit /b 1
)
popd

echo.
echo [3/5] 准备临时打包目录并导出镜像包
if exist "%STAGING_ROOT%" rmdir /s /q "%STAGING_ROOT%"
mkdir "%STAGING_DIR%"
if errorlevel 1 (
    echo 临时打包目录创建失败：%STAGING_DIR%
    exit /b 1
)

xcopy "%PACKAGE_DIR%deploy.sh" "%STAGING_DIR%\" /Y >nul
xcopy "%PACKAGE_DIR%README.txt" "%STAGING_DIR%\" /Y >nul
xcopy "%PACKAGE_DIR%docs" "%STAGING_DIR%\docs\" /E /I /Y >nul
xcopy "%PACKAGE_DIR%sql" "%STAGING_DIR%\sql\" /E /I /Y >nul

docker save -o "%IMAGE_PACKAGE%" "%IMAGE_NAME%"
if errorlevel 1 (
    if exist "%STAGING_ROOT%" rmdir /s /q "%STAGING_ROOT%"
    echo 镜像导出失败。
    exit /b 1
)

if not exist "%IMAGE_PACKAGE%" (
    if exist "%STAGING_ROOT%" rmdir /s /q "%STAGING_ROOT%"
    echo 镜像包未生成：%IMAGE_PACKAGE%
    exit /b 1
)

echo.
echo [4/5] 生成 SHA256SUMS.txt
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; function Get-Sha256([string]$path){ $sha=[System.Security.Cryptography.SHA256]::Create(); $stream=[System.IO.File]::OpenRead($path); try { ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant() } finally { $stream.Dispose(); $sha.Dispose() } }; $packageDir=(Resolve-Path -LiteralPath '%STAGING_DIR%').Path; $files=@('datasync-lhyy-v2.tar','deploy.sh','README.txt','docs/README_IMPLEMENTATION.md','docs/MANUAL_UPGRADE_REFERENCE.md','sql/upgrade_esb_messages_archive_optimization.sql'); $lines=foreach($f in $files){ $p=Join-Path $packageDir $f; if(-not (Test-Path -LiteralPath $p)){ throw ('缺少文件：' + $f) }; ((Get-Sha256 $p) + '  ' + $f) }; Set-Content -LiteralPath (Join-Path $packageDir 'SHA256SUMS.txt') -Value $lines -Encoding ASCII; Push-Location $packageDir; try { Get-Content .\SHA256SUMS.txt | ForEach-Object { if($_ -match '^([0-9a-f]{64})\s+(.+)$'){ $expected=$matches[1].ToLowerInvariant(); $file=$matches[2]; $actual=Get-Sha256 (Join-Path $packageDir $file); if($actual -ne $expected){ throw ('SHA256 校验失败：' + $file) } } }; } finally { Pop-Location }"
if errorlevel 1 (
    if exist "%STAGING_ROOT%" rmdir /s /q "%STAGING_ROOT%"
    echo SHA256SUMS.txt 生成或校验失败。
    exit /b 1
)

echo.
echo [5/5] 生成最终交付压缩包：%ZIP_FILE%
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $target=(Resolve-Path -LiteralPath '%STAGING_DIR%').Path; $zip='%ZIP_FILE%'; if(Test-Path -LiteralPath $zip){ Remove-Item -LiteralPath $zip -Force }; Compress-Archive -LiteralPath $target -DestinationPath $zip -CompressionLevel Optimal; if(-not (Test-Path -LiteralPath $zip)){ throw 'zip 文件未生成' }; Write-Host ('已生成：' + $zip)"
if errorlevel 1 (
    if exist "%STAGING_ROOT%" rmdir /s /q "%STAGING_ROOT%"
    echo 最终交付压缩包生成失败。
    exit /b 1
)

if exist "%STAGING_ROOT%" rmdir /s /q "%STAGING_ROOT%"

echo.
echo 完成。请将以下文件上传到现场 Linux：
echo %ZIP_FILE%
exit /b 0
