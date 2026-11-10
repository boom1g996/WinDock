using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using WinDock.Core.Interfaces;
using WinDock.Core.Services;

namespace WinDock.Watchdog;

/// <summary>
/// 独立守护进程（需求 5.1 / WD-020 / D8）：只做三件事——接心跳、判失联、恢复任务栏。
/// 不注册服务、不提权、不注入、不创建窗口；异常时写恢复事件供主进程下次启动提示。
/// </summary>
internal static class Program
{
    private const string Module = "Watchdog";

    private static async Task<int> Main(string[] args)
    {
        int pid = IntArg(args, "--pid", 0);
        string pipe = StringArg(args, "--pipe", "");
        int timeout = WatchdogProtocol.ClampTimeout(IntArg(args, "--timeout", 10000));
        var log = new LogService();

        if (pid <= 0 || string.IsNullOrWhiteSpace(pipe))
        {
            log.Error(Module, "缺少 --pid / --pipe 参数，看门狗退出");
            return 2;
        }

        log.Info(Module, $"启动：宿主 pid={pid}，失联阈值={timeout}ms");
        string reason = "unknown";

        try
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.In);
            try
            {
                await client.ConnectAsync(5000);
            }
            catch (TimeoutException)
            {
                reason = "pipe-connect-timeout";
                log.Warn(Module, "5s 内未连上心跳管道");
                Recover(log, pid, reason);
                return 1;
            }

            using var reader = new StreamReader(client, new UTF8Encoding(false));
            DateTime lastBeat = DateTime.UtcNow;
            Task<string?>? pending = null;
            int beats = 0;

            while (true)
            {
                // 保留未完成的 ReadLineAsync：每轮只创建一个读任务，避免并发读同一 StreamReader。
                Task<string?> read = pending ??= reader.ReadLineAsync();
                Task finished = await Task.WhenAny(read, Task.Delay(WatchdogProtocol.PollIntervalMs));
                if (finished == read)
                {
                    string? frame = await read;
                    pending = null;
                    if (frame is null)
                    {
                        reason = "pipe-closed";
                        break;
                    }

                    if (WatchdogProtocol.IsShutdown(frame))
                    {
                        log.Info(Module, "收到正常退出帧，看门狗退出（不恢复、不提示）");
                        return 0;
                    }

                    if (WatchdogProtocol.IsHeartbeat(frame))
                    {
                        lastBeat = DateTime.UtcNow;
                        beats++;
                        continue;
                    }

                    continue; // 未知帧忽略，不重置心跳时间
                }

                // 宿主被强杀：无需等满 10s，500ms 轮询内即可恢复（需求 4.4 的 5s）。
                if (!IsHostAlive(pid))
                {
                    reason = "host-exited";
                    break;
                }

                if ((DateTime.UtcNow - lastBeat).TotalMilliseconds >= timeout)
                {
                    // 把现场写清楚：宿主是否仍存活 + 收到多少帧 + 距最后一帧多久。
                    // 「宿主存活却判失联」是心跳链路的假阳性，必须能从日志一眼分辨。
                    reason = "heartbeat-timeout";
                    log.Warn(Module,
                        $"心跳超时：已收 {beats} 帧，最后帧 {DateTime.UtcNow - lastBeat:mm\\:ss\\.fff} 前，" +
                        $"宿主存活={IsHostAlive(pid)}，阈值={timeout}ms");
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            reason = "pipe-error";
            log.Error(Module, "心跳管道异常", ex);
        }

        Recover(log, pid, reason);
        return 1;
    }

    private static void Recover(ILogService log, int pid, string reason)
    {
        try
        {
            var taskbar = new TaskbarInterop(log);
            bool snapshot = taskbar.HasPendingRestore;
            bool restored = taskbar.Restore();
            WatchdogProtocol.WriteRecovery(
                WatchdogProtocol.Create(pid, reason, restored, snapshot));
            log.Warn(Module,
                $"判定失联（{reason}）；任务栏恢复={restored}，隐藏快照存在={snapshot}。"
                + (snapshot ? "" : " 模式 C 下无隐藏快照，恢复为 no-op，仅验证检测链路。")
                + " 已写恢复事件");
        }
        catch (Exception ex)
        {
            log.Error(Module, "恢复任务栏失败", ex);
        }
    }

    private static bool IsHostAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // PID 已不存在
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static int IntArg(string[] args, string name, int fallback)
    {
        string value = StringArg(args, name, "");
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;
    }

    private static string StringArg(string[] args, string name, string fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return fallback;
    }
}
