using System.Runtime.CompilerServices;

// 自检（主程序集 DockHost，src/WinDock）需要替换 AppLauncher.PostCloseHook
// 做无副作用断言（WD-037）：CloseWindow 与 CloseAll 必须走同一条 WM_CLOSE 路径。
[assembly: InternalsVisibleTo("DockHost")]
