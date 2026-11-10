# WinDock 卸载辅助脚本（需求 6.15 / 4.4：卸载时 100% 还原原生任务栏）
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools/uninstall_windock.ps1            # 还原 + 删除程序文件
#   powershell -ExecutionPolicy Bypass -File tools/uninstall_windock.ps1 -KeepFiles # 只还原系统状态
#
# 做三件事（全部可重复执行，幂等）：
#   1. 结束 DockHost / WinDock.Watchdog / WinDock.Settings 进程；
#   2. 把上次隐藏的原生任务栏按快照原样搬回屏幕内（任务管理器里任务栏"消失"时的救命步骤）；
#   3. 还原模式 B 精简共存改动的 HKCU 任务栏项，并清理开机自启项。
param(
    [string] $InstallDir = '',
    [switch] $KeepFiles
)

$ErrorActionPreference = 'Stop'
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$winDockData = Join-Path $localAppData 'WinDock'

Write-Host '==> 结束 WinDock 进程'
foreach ($name in @('DockHost', 'WinDock.Watchdog', 'WinDock.Settings')) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "    stop $($_.ProcessName) (pid=$($_.Id))"
        $_.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 300
        if (-not $_.HasExited) { $_.Kill() }
    }
}

Write-Host '==> 还原原生任务栏'
$snapshotPath = Join-Path $winDockData 'taskbar-snapshot.json'
if (Test-Path $snapshotPath) {
    # 快照结构：{ Handle, Left, Top, Right, Bottom, Edge, OffsetX, OffsetY }
    $snap = Get-Content $snapshotPath -Raw | ConvertFrom-Json
    Add-Type -Namespace WinDock -Name Native -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)]
public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
'@
    $handle = [IntPtr]::new([int64]$snap.Handle)
    $width = [int]($snap.Right - $snap.Left)
    $height = [int]($snap.Bottom - $snap.Top)
    $flags = 0x0004 -bor 0x0010 -bor 0x0020  # SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED
    $ok = [WinDock.Native]::SetWindowPos($handle, [IntPtr]::Zero, [int]$snap.Left, [int]$snap.Top, $width, $height, $flags)
    if ($ok) {
        Write-Host "    任务栏已还原到 ($($snap.Left),$($snap.Top))"
        Remove-Item $snapshotPath -Force
    }
    else {
        Write-Warning "    还原失败（窗口句柄可能已失效，Explorer 重启后会自动重建任务栏）"
    }
}
else {
    Write-Host '    无待还原快照（模式 C 从未隐藏过任务栏）'
}

Write-Host '==> 还原模式 B 精简共存改动的任务栏项'
$compactPath = Join-Path $winDockData 'taskbar-compact.json'
if (Test-Path $compactPath) {
    $values = Get-Content $compactPath -Raw | ConvertFrom-Json
    foreach ($prop in $values.PSObject.Properties) {
        $parts = $prop.Name -split '\|', 2
        if ($parts.Count -ne 2) { continue }
        $key = "HKCU:\$($parts[0])"
        if (-not (Test-Path $key)) { continue }
        Set-ItemProperty -Path $key -Name $parts[1] -Value ([int]$prop.Value) -Type DWord
        Write-Host "    还原 $($parts[1]) = $($prop.Value)"
    }

    Remove-Item $compactPath -Force
}
else {
    Write-Host '    无精简共存改动需要还原'
}

Write-Host '==> 清理开机自启'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Test-Path $runKey) {
    Remove-ItemProperty -Path $runKey -Name 'WinDock' -ErrorAction SilentlyContinue
}
$taskName = 'WinDock'
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

if (-not $KeepFiles) {
    if ([string]::IsNullOrWhiteSpace($InstallDir)) {
        $InstallDir = Split-Path -Parent $PSScriptRoot
    }

    Write-Host "==> 删除程序文件：$InstallDir"
    foreach ($file in @('DockHost.exe', 'WinDock.Settings.exe', 'WinDock.Watchdog.exe')) {
        $path = Join-Path $InstallDir $file
        if (Test-Path $path) { Remove-Item $path -Force }
    }

    Write-Host "    配置与日志保留在 $winDockData（如需彻底清理请手动删除该目录）"
}

Write-Host '==> 完成。若任务栏仍未出现，请在任务管理器结束 explorer.exe 并重新启动它。'
