# 一键构建安装包：发布 self-contained 程序 → 编译 Inno Setup 脚本 → dist\
# 用法：powershell -ExecutionPolicy Bypass -File installer\build.ps1

param(
    [string]$Version = "0.1.1",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    $iscc = "C:\Program Files\Inno Setup 6\ISCC.exe"
}
if (-not (Test-Path $iscc)) {
    throw "找不到 ISCC.exe，请先安装 Inno Setup 6"
}

# ---- WinFsp 安装源 ---------------------------------------------------------
# 不进 git：GPL 二进制不适合放仓库里，按需下载。
$msiName = "winfsp-2.1.25156.msi"
$msiPath = Join-Path $PSScriptRoot "redist\$msiName"
if (-not (Test-Path $msiPath)) {
    Write-Host "下载 WinFsp $msiName ..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force (Split-Path $msiPath) | Out-Null
    Invoke-WebRequest -UseBasicParsing `
        -Uri "https://github.com/winfsp/winfsp/releases/download/v2.1/$msiName" `
        -OutFile $msiPath
}

# ---- 发布 ----------------------------------------------------------------
$common = @(
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "--nologo", "-v", "quiet"
)

Write-Host "发布主程序..." -ForegroundColor Cyan
& $dotnet publish "src\FileGuard.App\FileGuard.App.csproj" @common -o "publish\app-full"
if ($LASTEXITCODE -ne 0) { throw "主程序发布失败" }

Write-Host "发布守护服务..." -ForegroundColor Cyan
& $dotnet publish "src\FileGuard.Service\FileGuard.Service.csproj" @common -o "publish\service-full"
if ($LASTEXITCODE -ne 0) { throw "守护服务发布失败" }

# ---- 编译安装包 ------------------------------------------------------------
Write-Host "编译安装包..." -ForegroundColor Cyan
& $iscc "/DMyAppVersion=$Version" "installer\FileGuard.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败" }

$output = Get-ChildItem "dist\FileGuard-$Version-setup.exe"
Write-Host ""
Write-Host "完成：$($output.FullName)" -ForegroundColor Green
Write-Host ("体积：{0:N1} MB" -f ($output.Length / 1MB))
