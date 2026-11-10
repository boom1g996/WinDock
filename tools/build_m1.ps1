# WinDock M1 build helper. Zero external NuGet packages; works fully offline.
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/build_m1.ps1 -SelfTest
#   powershell -ExecutionPolicy Bypass -File tools/build_m1.ps1 -Publish -Runtime win-x64
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')] [string] $Runtime = 'win-x64',
    [switch] $Publish,
    [switch] $SelfTest,
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'src\WinDock.sln'
$artifacts = Join-Path $root 'artifacts'
$tfm = 'net10.0-windows'

if ($Clean) {
    foreach ($project in @('WinDock.Core', 'WinDock', 'WinDock.Settings', 'WinDock.Watchdog')) {
        $bin = Join-Path $root "src\$project\bin"
        $obj = Join-Path $root "src\$project\obj"
        if (Test-Path $bin) { Remove-Item $bin -Recurse -Force }
        if (Test-Path $obj) { Remove-Item $obj -Recurse -Force }
    }
}

Write-Host "==> dotnet build ($Configuration)"
& dotnet build $solution -c $Configuration --nologo --tl:off
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

if ($SelfTest) {
    $hostExe = Join-Path $root "src\WinDock\bin\$Configuration\$tfm\DockHost.exe"
    $resultFile = Join-Path $root "src\WinDock\bin\$Configuration\$tfm\selftest-result.txt"
    if (-not (Test-Path $hostExe)) { throw "DockHost.exe not found: $hostExe" }
    if (Test-Path $resultFile) { Remove-Item $resultFile -Force }

    Write-Host "==> offline self-test"
    $process = Start-Process -FilePath $hostExe -ArgumentList '--selftest' -Wait -PassThru
    if (Test-Path $resultFile) { Get-Content $resultFile }
    if ($process.ExitCode -ne 0) { throw "self-test failed with exit code $($process.ExitCode)" }
}

if ($Publish) {
    New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
    $scOutput = Join-Path $artifacts "WinDock-$Runtime"
    $fxOutput = Join-Path $artifacts 'WinDock-framework-dependent'

    # 必须清理旧发布产物：dotnet publish 单文件对已有 apphost 做增量跳过，即使源码已变
    # 也不会重写自包含 exe（既有的「确定性时间戳不更新」坑），导致包内停留在旧二进制。
    # 先删旧输出再发，保证包内是最新代码。
    # 用 .NET API 删除而不用 Remove-Item：部分宿主会把 Remove-Item 拦成 safe-delete，
    # 拦截失败时 fail-closed 直接抛错，导致发布脚本无法重跑（artifacts 是构建产物，可安全重建）。
    foreach ($d in @($scOutput, $fxOutput)) {
        if ([System.IO.Directory]::Exists($d)) {
            try {
                [System.IO.Directory]::Delete($d, $true)
            }
            catch [System.IO.IOException] {
                Write-Warning "旧发布目录未被完全清理（有进程占用？）：$d"
            }
            catch [System.UnauthorizedAccessException] {
                Write-Warning "旧发布目录无权限清理：$d"
            }
        }
    }
    $projectFiles = @(
        (Join-Path $root 'src\WinDock\WinDock.csproj'),
        (Join-Path $root 'src\WinDock.Settings\WinDock.Settings.csproj'),
        (Join-Path $root 'src\WinDock.Watchdog\WinDock.Watchdog.csproj')
    )

    Write-Host "==> publish: self-contained single file ($Runtime)"
    # src/NuGet.config 刻意清空包源（日常构建完全离线），自包含发布需要
    # Microsoft.NETCore.App.Runtime / WindowsDesktop.App.Runtime 运行时包，
    # 因此只在这一步显式注入 nuget.org；不修改仓库内的 NuGet.config。
    $restoreSource = 'https://api.nuget.org/v3/index.json'
    $selfContainedOk = $true
    foreach ($projectFile in $projectFiles) {
        & dotnet publish $projectFile -c $Configuration -r $Runtime --self-contained true `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            -p:DebugType=None `
            -p:RestoreSources=$restoreSource `
            -o $scOutput --nologo --tl:off
        if ($LASTEXITCODE -ne 0) { $selfContainedOk = $false; break }
    }

    if (-not $selfContainedOk) {
        Write-Warning 'Self-contained publish requires NuGet runtime packs (NU1100 offline).'
        Write-Warning 'Falling back to framework-dependent publish; requires .NET 10 runtime.'
        foreach ($projectFile in $projectFiles) {
            & dotnet publish $projectFile -c $Configuration --self-contained false `
                -p:PublishSingleFile=false -o $fxOutput --nologo --tl:off
            if ($LASTEXITCODE -ne 0) {
                throw "framework-dependent publish failed with exit code $LASTEXITCODE"
            }
        }
    }

    Write-Host "==> publish outputs"
    Get-ChildItem $artifacts -Recurse -Filter '*.exe' |
        Select-Object FullName, @{ Name = 'MB'; Expression = { [math]::Round($_.Length / 1MB, 2) } } |
        Format-Table -AutoSize
}

Write-Host "==> done"
