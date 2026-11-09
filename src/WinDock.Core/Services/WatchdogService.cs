using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;

namespace WinDock.Core.Services;

/// <summary>
/// 主进程侧的看门狗宿主（WD-020 / AC-11 / D8）：启动独立进程、建立命名管道、
/// 由唯一 ITimerScheduler 在 **UI 线程** 发心跳。UI 卡死 → 心跳停摆 → 看门狗恢复任务栏。
/// 正常退出必须发送 S 帧，否则管道断开会被判为崩溃。
/// </summary>
public sealed class WatchdogService : IWatchdogService
{
    private const string Module = "Watchdog";
    private const string HeartbeatKey = "watchdog.heartbeat";

    private readonly ILogService _log;
    private readonly ITimerScheduler _timers;
    private readonly string _executable;
    private readonly string _pipeName;

    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private Process? _process;
    private int _timeoutMs = 10000;
    private bool _running;
    private bool _disposed;

    /// <param name="executable">看门狗 exe 路径；自检与发布用不同目录时可注入。</param>
    public WatchdogService(ILogService log, ITimerScheduler timers, string? executable = null)
    {
        _log = log;
        _timers = timers;
        _executable = executable ?? Path.Combine(AppContext.BaseDirectory, "WinDock.Watchdog.exe");
        _pipeName = WatchdogProtocol.PipeName(Environment.ProcessId);
    }

    public bool IsRunning => _running;

    public void ApplyConfig(DockConfig config)
    {
        _timeoutMs = WatchdogProtocol.ClampTimeout(config.Behavior.WatchdogHeartbeatMs);
        if (config.Behavior.WatchdogEnabled)
        {
            if (!_running)
            {
                Start();
            }
            else
            {
                ScheduleNext(); // 阈值变化时立即按新间隔重排
            }
        }
        else if (_running)
        {
            Stop();
        }
    }

    public void Start()
    {
        if (_running || _disposed)
        {
            return;
        }

        if (!File.Exists(_executable))
        {
            _log.Warn(Module, $"未找到 {_executable}，看门狗降级为关闭（发布产物需包含 WinDock.Watchdog.exe）");
            return;
        }

        try
        {
            _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var start = new ProcessStartInfo(_executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_executable) ?? AppContext.BaseDirectory,
            };
            start.ArgumentList.Add("--pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add("--pipe");
            start.ArgumentList.Add(_pipeName);
            start.ArgumentList.Add("--timeout");
            start.ArgumentList.Add(_timeoutMs.ToString());
            _process = Process.Start(start);
            if (_process is null)
            {
                throw new InvalidOperationException("Process.Start 返回 null");
            }

            _running = true;
            _ = WaitForConnectionAsync(_pipe);
            ScheduleNext();
            _log.Info(Module, $"看门狗已启动 pid={_process.Id}，阈值={_timeoutMs}ms，心跳间隔={WatchdogProtocol.HeartbeatIntervalMs(_timeoutMs)}ms");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.Warn(Module, $"看门狗启动失败，降级为关闭：{ex.Message}");
            CleanupProcess();
        }
    }

    private async Task WaitForConnectionAsync(NamedPipeServerStream pipe)
    {
        try
        {
            await pipe.WaitForConnectionAsync().ConfigureAwait(false);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            Heartbeat(); // 建立连接后立刻给一帧，避免冷启动窗口被误判
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 正常退出时 Stop() 会释放管道；这不是故障。
            _log.Info(Module, $"管道已关闭：{ex.Message}");
        }
    }

    private void ScheduleNext() => _timers.Schedule(
        HeartbeatKey, WatchdogProtocol.HeartbeatIntervalMs(_timeoutMs), OnHeartbeatTick);

    private void OnHeartbeatTick()
    {
        if (!_running || _disposed)
        {
            return;
        }

        Heartbeat();
        ScheduleNext();
    }

    /// <summary>显式发一帧心跳；自检可直接调用验证协议不依赖真实子进程。</summary>
    public void Heartbeat()
    {
        StreamWriter? writer = _writer;
        if (writer is null)
        {
            return;
        }

        try
        {
            writer.WriteLine(WatchdogProtocol.HeartbeatFrame);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _writer = null;
            _log.Warn(Module, $"心跳写入失败：{ex.Message}");
        }
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _timers.Cancel(HeartbeatKey);

        // 正常退出握手：S 帧让看门狗直接退出，不产生恢复事件。
        if (_writer is not null)
        {
            try
            {
                _writer.WriteLine(WatchdogProtocol.ShutdownFrame);
                _writer.Flush();
                _process?.WaitForExit(400);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                          or System.ComponentModel.Win32Exception)
            {
                _log.Warn(Module, $"退出握手失败：{ex.Message}");
            }
        }

        CleanupProcess();
        _log.Info(Module, "看门狗已停止（正常退出）");
    }

    private void CleanupProcess()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        try
        {
            _writer?.Dispose();
            _pipe?.Dispose();
        }
        catch (IOException)
        {
        }

        _process?.Dispose();
        _writer = null;
        _pipe = null;
        _process = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }
}
