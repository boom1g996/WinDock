using System.IO;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;

namespace WinDock.Core.Services;

/// <summary>
/// 拖拽排序的**唯一状态写入口**（WD-016 / FR-302 / AC-06）。
/// 只操作 <c>DockConfig.Pinned</c> 并走 IConfigService.Save 原子落盘，
/// 不引用任何 WPF 类型（需求 14.2）；UI 层的手势识别在 WinDock/Services/DockDragController。
/// </summary>
public sealed class DockDragService : IDockDragService
{
    private const string Module = "Drag";

    private readonly IConfigService _config;
    private readonly ILogService _log;

    public DockDragService(IConfigService config, ILogService log)
    {
        _config = config;
        _log = log;
    }

    public bool IsDragging { get; private set; }

    public event EventHandler? OrderChanged;

    public void BeginDrag() => IsDragging = true;

    public void EndDrag() => IsDragging = false;

    /// <summary>固定项重排。toIndex 是插入位置，允许等于 Count（追加到末尾）。</summary>
    public void Reorder(int fromIndex, int toIndex)
    {
        List<PinnedItem> items = _config.Current.Pinned;
        if (fromIndex < 0 || fromIndex >= items.Count || toIndex < 0 || toIndex > items.Count)
        {
            return;
        }

        // 插入位置落在自身或紧邻自身右侧时，最终顺序不变，不写盘、不重建 UI。
        if (toIndex == fromIndex || toIndex == fromIndex + 1)
        {
            return;
        }

        PinnedItem moved = items[fromIndex];
        items.RemoveAt(fromIndex);
        if (toIndex > fromIndex)
        {
            toIndex--; // 移除后右侧目标整体左移一位
        }

        items.Insert(Math.Clamp(toIndex, 0, items.Count), moved);
        Commit($"固定项重排 {fromIndex} → {toIndex}");
    }

    /// <summary>拖出 Dock 停留 300ms：仅取消固定，绝不卸载应用（FR-302）。</summary>
    public void Remove(int index)
    {
        List<PinnedItem> items = _config.Current.Pinned;
        if (index < 0 || index >= items.Count)
        {
            return;
        }

        string name = items[index].Name;
        items.RemoveAt(index);
        Commit($"取消固定 {name}");
    }

    /// <summary>运行项拖入固定区；同路径已固定时改为重排，避免重复图标。</summary>
    public void Pin(int insertIndex, string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        List<PinnedItem> items = _config.Current.Pinned;
        int existing = items.FindIndex(p =>
            !string.Equals(p.Type, "separator", StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            Reorder(existing, Math.Clamp(insertIndex, 0, items.Count));
            return;
        }

        items.Insert(Math.Clamp(insertIndex, 0, items.Count), new PinnedItem
        {
            Type = "app",
            Path = path,
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name,
        });
        Commit($"固定 {name}");
    }

    /// <summary>落盘 + 通知。ConfigService.Save 自身会触发热更新重建，OrderChanged 只用于手势层收尾。</summary>
    private void Commit(string action)
    {
        _config.Save(_config.Current);
        _log.Info(Module, action);
        OrderChanged?.Invoke(this, EventArgs.Empty);
    }
}
