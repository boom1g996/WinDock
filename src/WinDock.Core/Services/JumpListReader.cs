using System.Runtime.InteropServices;

namespace WinDock.Core.Services;

/// <summary>一条 Jump List 项（显示名 + 目标路径）。</summary>
public sealed record JumpListItem(string Title, string Path);

/// <summary>
/// Jump List 只读读取（WD-029 / FR-501 P2）：
/// 通过 <c>ICustomDestinationList.BeginList</c> 读取目标 AppID 下的「最近/常用」目标，
/// 读不到（未注册 AppID、应用未记录最近项、COM 失败）时返回空，菜单不显示该分组。
/// 只调用 BeginList/AbortList，绝不 CommitList，保证不修改系统状态。
/// </summary>
public static class JumpListReader
{
    private const int SigdnNormalDisplay = 0;
    private const int SigdnDesktopAbsoluteParsing = unchecked((int)0x80028000);

    /// <summary>读取指定 AppID 的 Jump List（最多 max 条）。任何失败都返回空列表。</summary>
    public static IReadOnlyList<JumpListItem> Read(string appId, int max = 6)
    {
        var result = new List<JumpListItem>();
        if (string.IsNullOrWhiteSpace(appId))
        {
            return result;
        }

        ICustomDestinationList? list = null;
        bool begun = false;
        try
        {
            list = (ICustomDestinationList)new DestinationList();
            if (list.SetAppID(appId) != 0)
            {
                return result;
            }

            Guid iid = typeof(IObjectArray).GUID;
            if (list.BeginList(out _, ref iid, out object arrayObj) != 0 || arrayObj is not IObjectArray array)
            {
                return result;
            }

            begun = true;
            if (array.GetCount(out uint count) != 0)
            {
                return result;
            }

            for (uint i = 0; i < count && result.Count < max; i++)
            {
                Guid si = typeof(IShellItem).GUID;
                if (array.GetAt(i, ref si, out object itemObj) != 0 || itemObj is not IShellItem shellItem)
                {
                    continue;
                }

                string? title = GetName(shellItem, SigdnNormalDisplay);
                string? path = GetName(shellItem, SigdnDesktopAbsoluteParsing);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    result.Add(new JumpListItem(title!, path ?? ""));
                }
            }
        }
        catch
        {
            // 无 COM / 无权限 / 目标无 Jump List：按「无项」处理，不影响菜单其余部分。
            result.Clear();
        }
        finally
        {
            if (list is not null && begun)
            {
                list.AbortList(); // 只读，不提交
            }
        }

        return result;
    }

    private static string? GetName(IShellItem item, int sigdn)
    {
        if (item.GetDisplayName(sigdn, out IntPtr ptr) != 0 || ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    // ---- COM 互操作（只用 PreserveSig，避免 HRESULT 被翻译成异常导致崩溃）----

    [ComImport]
    [Guid("77f10cf0-3db5-4966-b520-b7c54fd35ed6")]
    [ClassInterface(ClassInterfaceType.None)]
    private class DestinationList
    {
    }

    [ComImport]
    [Guid("6332debf-87b5-4670-90c0-5e57b408a49e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        [PreserveSig] int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        [PreserveSig] int BeginList(out uint pcMinSlots, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int AppendKnownCategory(int category);
        [PreserveSig] int AddUserTasks([MarshalAs(UnmanagedType.Interface)] object poa);
        [PreserveSig] int CommitList();
        [PreserveSig] int GetRemovedDestinations(ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int DeleteList([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        [PreserveSig] int AbortList();
    }

    [ComImport]
    [Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        [PreserveSig] int GetCount(out uint cObjects);
        [PreserveSig] int GetAt(uint iIndex, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(int sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
