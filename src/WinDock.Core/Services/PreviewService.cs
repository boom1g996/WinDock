using System.Runtime.InteropServices;
using System.Windows;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;
using WinDock.Core.Native;

namespace WinDock.Core.Services;

/// <summary>
/// DWM 实时缩略图生命周期（WD-014 / FR-401~404 / M2 设计决策 D6）。
/// 独占 HTHUMBNAIL 的注册与注销：面板关闭、窗口切换、进程退出都必须走到 Hide()，
/// 否则系统会一直保留缩略图表面（R7）。最多注册 PreviewMaxWindows 个（≤6）。
/// 最小化窗口不注册 DWM（源已不可刷新），保留占位条目由 UI 显示半透明遮罩。
/// </summary>
public sealed class PreviewService : IPreviewService
{
    private const string Module = "Preview";

    private sealed record Thumbnail(IntPtr Id, IntPtr Window, string Title,
        int SourceWidth, int SourceHeight, bool IsMinimized);

    private readonly ILogService _log;
    private readonly List<Thumbnail> _items = new();
    private bool _disposed;

    public PreviewService(ILogService log)
    {
        _log = log;
        // 预研已实测 4 个 DWM 导出存在；这里仍按需求 14.4 在运行时复核，
        // 未导出或 DWM 合成关闭时整体降级为「只显示窗口标题列表」。
        IsAvailable = NativeMethods.DwmIsCompositionEnabled(out int enabled) == 0
                      && enabled != 0
                      && NativeMethods.HasExport("dwmapi.dll", "DwmRegisterThumbnail")
                      && NativeMethods.HasExport("dwmapi.dll", "DwmUnregisterThumbnail")
                      && NativeMethods.HasExport("dwmapi.dll", "DwmUpdateThumbnailProperties");
        MaxThumbnails = 6;
        if (!IsAvailable)
        {
            _log.Warn(Module, "DWM 缩略图不可用，预览面板降级为窗口标题列表");
        }
    }

    public bool IsAvailable { get; }

    /// <summary>当前允许的最大注册数；由配置热更新，上限恒为 6（FR-404）。</summary>
    public int MaxThumbnails { get; private set; }

    /// <summary>当前实际注册的 DWM 缩略图数量（含最小化占位则为占位总数）。</summary>
    public int ActiveCount => _items.Count;

    public event EventHandler? StateChanged;

    public void ApplyConfig(DockConfig config)
    {
        MaxThumbnails = Math.Clamp(config.Behavior.PreviewMaxWindows,
            ConfigRanges.PreviewMaxWindows.Min, ConfigRanges.PreviewMaxWindows.Max);
        if (_items.Count > MaxThumbnails)
        {
            // 上限被调小：立即释放多出来的句柄，不能等下次面板打开。
            for (int i = _items.Count - 1; i >= MaxThumbnails; i--)
            {
                Unregister(_items[i]);
                _items.RemoveAt(i);
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>在宿主窗口上注册缩略图；返回实际成功注册的数量（最小化占位不计入）。</summary>
    public int Show(IntPtr host, IReadOnlyList<WindowPreviewTarget> targets)
    {
        Hide(silent: true);
        if (_disposed || targets.Count == 0)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return 0;
        }

        int registered = 0;
        foreach (WindowPreviewTarget target in targets.Take(MaxThumbnails))
        {
            if (target.Handle == IntPtr.Zero)
            {
                continue;
            }

            // FR-402：最小化窗口的 DWM 源不刷新，不做无效注册，UI 用半透明占位。
            if (target.IsMinimized)
            {
                _items.Add(new Thumbnail(IntPtr.Zero, target.Handle, target.Title,
                    0, 0, IsMinimized: true));
                continue;
            }

            if (!IsAvailable || host == IntPtr.Zero)
            {
                _items.Add(new Thumbnail(IntPtr.Zero, target.Handle, target.Title,
                    0, 0, IsMinimized: false));
                continue;
            }

            int hr = NativeMethods.DwmRegisterThumbnail(host, target.Handle,
                out IntPtr thumbnailId);
            if (hr != 0 || thumbnailId == IntPtr.Zero)
            {
                _log.Warn(Module, $"DwmRegisterThumbnail 失败 hr={NativeMethods.HResultText(hr)}, "
                    + $"Win32Error={Marshal.GetLastWin32Error()}, hwnd={target.Handle}");
                if (thumbnailId != IntPtr.Zero)
                {
                    NativeMethods.DwmUnregisterThumbnail(thumbnailId);
                }

                // 注册失败仍保留占位，保证面板条目与标题一一对应。
                _items.Add(new Thumbnail(IntPtr.Zero, target.Handle, target.Title,
                    0, 0, IsMinimized: false));
                continue;
            }

            int width = 0;
            int height = 0;
            int sizeHr = NativeMethods.DwmQueryThumbnailSourceSize(thumbnailId,
                out NativeMethods.SIZE size);
            if (sizeHr == 0 && size.cx > 0 && size.cy > 0)
            {
                width = size.cx;
                height = size.cy;
            }
            else
            {
                _log.Warn(Module, $"DwmQueryThumbnailSourceSize 失败 hr={NativeMethods.HResultText(sizeHr)}");
            }

            _items.Add(new Thumbnail(thumbnailId, target.Handle, target.Title,
                width, height, IsMinimized: false));
            registered++;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        _log.Info(Module, $"预览面板打开：条目={_items.Count}, DWM 注册={registered}, "
            + $"上限={MaxThumbnails}");
        return registered;
    }

    /// <summary>
    /// 按面板给出的设备像素矩形更新每个缩略图；矩形的顺序与 Show 的 targets 一致。
    /// 源与目标宽高比不一致时按比例居中留边，避免拉伸变形（FR-402）。
    /// </summary>
    public void UpdateLayout(IReadOnlyList<Rect> destinations)
    {
        if (_disposed || !IsAvailable)
        {
            return;
        }

        for (int i = 0; i < _items.Count && i < destinations.Count; i++)
        {
            Thumbnail item = _items[i];
            if (item.Id == IntPtr.Zero)
            {
                continue;
            }

            Rect rect = item.SourceWidth > 0 && item.SourceHeight > 0
                ? FitRect(destinations[i], item.SourceWidth, item.SourceHeight)
                : destinations[i];
            var props = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION
                          | NativeMethods.DWM_TNP_OPACITY
                          | NativeMethods.DWM_TNP_VISIBLE
                          | NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new NativeMethods.RECT
                {
                    Left = (int)Math.Round(rect.Left),
                    Top = (int)Math.Round(rect.Top),
                    Right = (int)Math.Round(rect.Right),
                    Bottom = (int)Math.Round(rect.Bottom),
                },
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = true,
            };

            int hr = NativeMethods.DwmUpdateThumbnailProperties(item.Id, ref props);
            if (hr != 0)
            {
                _log.Warn(Module, $"DwmUpdateThumbnailProperties 失败 hr={NativeMethods.HResultText(hr)}");
            }
        }
    }

    /// <summary>注销全部缩略图；面板关闭、切换图标、隐藏 Dock、退出都必须调用。</summary>
    public void Hide() => Hide(silent: false);

    private void Hide(bool silent)
    {
        if (_items.Count == 0)
        {
            if (!silent)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        foreach (Thumbnail item in _items)
        {
            Unregister(item);
        }

        _items.Clear();
        if (!silent)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Unregister(Thumbnail item)
    {
        if (item.Id == IntPtr.Zero)
        {
            return;
        }

        int hr = NativeMethods.DwmUnregisterThumbnail(item.Id);
        if (hr != 0)
        {
            _log.Warn(Module, $"DwmUnregisterThumbnail 失败 hr={NativeMethods.HResultText(hr)}, "
                + $"Win32Error={Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>纯函数：保持源宽高比、在目标矩形内居中（离线自检可直接断言）。</summary>
    public static Rect FitRect(Rect destination, double sourceWidth, double sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || destination.Width <= 0
            || destination.Height <= 0)
        {
            return destination;
        }

        double scale = Math.Min(destination.Width / sourceWidth,
            destination.Height / sourceHeight);
        double width = sourceWidth * scale;
        double height = sourceHeight * scale;
        return new Rect(destination.Left + ((destination.Width - width) / 2),
            destination.Top + ((destination.Height - height) / 2), width, height);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide(silent: true);
        _disposed = true;
    }
}
