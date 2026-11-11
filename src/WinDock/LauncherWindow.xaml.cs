using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinDock.Core;
using WinDock.Core.Interfaces;
using WinDock.Core.Models;
using WinDock.Core.Services;

namespace WinDock;

/// <summary>
/// M5 启动台：任务栏左段的九宫格按钮唤出的应用网格。
/// </summary>
/// <remarks>
/// 常驻复用（关闭只是 <c>Hide()</c>）：BAML 解析 + 建 HWND 一次约 100ms，
/// 每次点开都新建会让"点一下等一下"的手感非常明显（预览面板已经踩过同一个坑，见 K-28）。
/// 应用来源只扫开始菜单的 .lnk —— UWP 应用没有 .lnk，要走 AppsFolder COM，
/// 留到 M5-2 再补。
/// </remarks>
public partial class LauncherWindow : Window
{
    private const string Module = "Launcher";
    private const int BatchSize = 18;

    private readonly IIconService _icons;
    private readonly IAppLauncher _launcher;
    private readonly ILogService _log;
    private readonly DockConfig _config;

    private readonly List<LauncherEntry> _apps = new();
    private bool _loaded;
    private bool _loading;
    private int _feedToken;

    public LauncherWindow(IIconService icons, IAppLauncher launcher, ILogService log,
        DockConfig config)
    {
        InitializeComponent();
        _icons = icons;
        _launcher = launcher;
        _log = log;
        _config = config;

        Deactivated += (_, _) => ClosePanel();
        KeyDown += OnKeyDown;
        Closed += (_, _) => IsOpen = false;
    }

    public bool IsOpen { get; private set; }

    /// <summary>
    /// 在锚点上方展开：锚点 X 取任务栏中线，Y 取任务栏上沿。
    /// </summary>
    public void Open(double anchorX, double anchorY)
    {
        if (IsOpen)
        {
            return;
        }

        Show();
        IsOpen = true;
        Left = anchorX - (Width / 2);
        Top = anchorY - Height - 10;
        ClampToWorkArea();

        SearchBox.Text = "";
        SearchHint.Visibility = Visibility.Visible;
        SearchBox.Focus();

        if (!_loaded && !_loading)
        {
            LoadAppsAsync();
        }
        else if (_loaded)
        {
            Populate(_apps);
        }
    }

    /// <summary>幂等关闭：只隐藏，不销毁（保留已加载的应用列表与图标缓存）。</summary>
    public void ClosePanel()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        Hide();
    }

    private void ClampToWorkArea()
    {
        Rect work = SystemParameters.WorkArea;
        if (Left < work.Left + 8)
        {
            Left = work.Left + 8;
        }

        if (Left + Width > work.Right - 8)
        {
            Left = work.Right - Width - 8;
        }

        if (Top < work.Top + 8)
        {
            Top = work.Top + 8;
        }
    }

    private void LoadAppsAsync()
    {
        _loading = true;
        ShowStatus("正在加载应用…");
        string[] starts =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs"),
        };

        Task.Run(() =>
        {
            var found = new List<LauncherEntry>(256);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in starts)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                try
                {
                    foreach (string file in Directory.EnumerateFiles(root, "*.lnk",
                                 SearchOption.AllDirectories))
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                        {
                            continue;
                        }

                        found.Add(new LauncherEntry(name, file));
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn(Module, $"枚举开始菜单失败（{root}）：{ex.Message}");
                }
            }

            found.Sort((a, b) => string.Compare(a.Name, b.Name,
                StringComparison.CurrentCultureIgnoreCase));
            return found;
        }).ContinueWith(task =>
        {
            _loading = false;
            _loaded = true;
            if (task.IsFaulted)
            {
                _log.Warn(Module, "启动台应用列表加载失败：" + task.Exception?.GetBaseException().Message);
                ShowStatus("加载失败");
                return;
            }

            _apps.Clear();
            _apps.AddRange(task.Result);
            _log.Info(Module, $"启动台已加载 {_apps.Count} 个应用");
            if (IsOpen)
            {
                Populate(_apps);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!_loaded)
        {
            return;
        }

        string query = SearchBox.Text.Trim();
        Populate(string.IsNullOrEmpty(query)
            ? _apps
            : _apps.Where(a => a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                   .ToList());
    }

    /// <summary>
    /// 分批填充网格：图标提取走 Shell API，一次性做 300 个会把 UI 线程卡住 1~2 秒。
    /// 每批 18 个（正好一行），批与批之间让出 Dispatcher，面板先出来再逐步填满。
    /// </summary>
    private void Populate(IReadOnlyList<LauncherEntry> entries)
    {
        int token = ++_feedToken;
        AppsPanel.Children.Clear();
        StatusText.Visibility = Visibility.Collapsed;

        if (entries.Count == 0)
        {
            ShowStatus(_loaded ? "没有匹配的应用" : "正在加载应用…");
            return;
        }

        FeedBatch(entries, 0, token);
    }

    private void FeedBatch(IReadOnlyList<LauncherEntry> entries, int start, int token)
    {
        if (token != _feedToken)
        {
            return; // 又有新的搜索/刷新，旧批次作废
        }

        int end = Math.Min(start + BatchSize, entries.Count);
        for (int i = start; i < end; i++)
        {
            AppsPanel.Children.Add(CreateTile(entries[i]));
        }

        if (end < entries.Count)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => FeedBatch(entries, end, token)));
        }
    }

    private Button CreateTile(LauncherEntry entry)
    {
        int size = Math.Clamp(_config.Dock.IconSize, 32, 96);
        var image = new Image
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Source = _icons.GetIcon(entry.Path, size),
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var text = new TextBlock
        {
            Text = entry.Name,
            FontSize = 11,
            Opacity = 0.82,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 92,
            Margin = new Thickness(0, 6, 0, 0),
            MaxHeight = 30,
            TextWrapping = TextWrapping.Wrap,
        };

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(image);
        stack.Children.Add(text);

        var button = new Button
        {
            Style = (Style)FindResource("LauncherTile"),
            Content = stack,
            ToolTip = entry.Path,
        };

        button.Click += (_, _) =>
        {
            ClosePanel();
            try
            {
                _launcher.Launch(new PinnedItem
                {
                    Type = "app",
                    Path = entry.Path,
                    Name = entry.Name,
                });
            }
            catch (Exception ex)
            {
                _log.Warn(Module, $"启动失败（{entry.Path}）：{ex.Message}");
            }
        };

        return button;
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClosePanel();
            e.Handled = true;
        }
    }
}

/// <summary>启动台条目：只存路径与显示名，图标在 UI 线程按需取。</summary>
public sealed record LauncherEntry(string Name, string Path);
