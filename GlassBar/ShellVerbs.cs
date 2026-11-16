using System.Runtime.InteropServices;

namespace GlassBar;

/// <summary>
/// Führt Windows-Shell-Befehle ("Verben") für eine Datei aus – genau so, wie der Explorer es tut:
/// kopieren, ausschneiden, löschen, freigeben, an Start anheften, Eigenschaften …
/// </summary>
public static class ShellVerbs
{
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idcmd, uint uflags, IntPtr reserved, IntPtr commandstring, uint cch);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public int ptX, ptY;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hmenu);

    private static readonly Guid BHID_SFUIObject = new("3981e225-f559-11d3-8e3a-00c04f6837d5");
    private static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");

    /// <summary>Führt das Verb aus. Gibt false zurück, wenn Windows es für diese Datei nicht kennt.</summary>
    public static bool Invoke(string path, string verb, IntPtr hwnd)
    {
        IntPtr menu = IntPtr.Zero, pcm = IntPtr.Zero, verbA = IntPtr.Zero, verbW = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
            item.BindToHandler(IntPtr.Zero, BHID_SFUIObject, IID_IContextMenu, out pcm);
            var cm = (IContextMenu)Marshal.GetObjectForIUnknown(pcm);

            menu = CreatePopupMenu();
            cm.QueryContextMenu(menu, 0, 1, 0x7FFF, 0x100 /* CMF_EXTENDEDVERBS */);

            verbA = Marshal.StringToHGlobalAnsi(verb);
            verbW = Marshal.StringToHGlobalUni(verb);
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = 0x4000 | 0x20000000, // CMIC_MASK_UNICODE | CMIC_MASK_ASYNCOK
                hwnd = hwnd, lpVerb = verbA, lpVerbW = verbW, nShow = 1,
            };
            return cm.InvokeCommand(ref info) == 0;
        }
        catch { return false; }
        finally
        {
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (pcm != IntPtr.Zero) Marshal.Release(pcm);
            if (verbA != IntPtr.Zero) Marshal.FreeHGlobal(verbA);
            if (verbW != IntPtr.Zero) Marshal.FreeHGlobal(verbW);
        }
    }
}
