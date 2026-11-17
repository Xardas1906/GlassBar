using System.Runtime.InteropServices;

namespace GlassBar;

/// <summary>
/// Öffnet das Windows-Startmenü auf einem bestimmten Bildschirm.
/// Über die Windows-Taste geht es sonst immer auf dem Hauptbildschirm auf, weil die Windows-Taskleisten ausgeblendet sind.
/// Nutzt die (nicht dokumentierte) ImmersiveLauncher-Schnittstelle des Explorers, wie ExplorerPatcher und Open-Shell.
/// </summary>
public static class StartMenu
{
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);
    }

    [ComImport, Guid("4D4C1E64-E410-4FAA-BAFA-59CA069BFEC2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImmersiveMonitorService
    {
        [PreserveSig] int GetCount();
        [PreserveSig] int GetConnectedCount();
        [PreserveSig] int GetAt();
        [PreserveSig] int GetFromHandle(IntPtr hMonitor, [MarshalAs(UnmanagedType.IUnknown)] out object? monitor);
    }

    [ComImport, Guid("D8D60399-A0F1-F987-5551-321FD1B49864"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImmersiveLauncher
    {
        [PreserveSig] int ShowStartView(int method, int flags);
        [PreserveSig] int Dismiss();
        [PreserveSig] int Method5();
        [PreserveSig] int Method6();
        [PreserveSig] int IsVisible([MarshalAs(UnmanagedType.Bool)] out bool visible);
        [PreserveSig] int Method8();
        [PreserveSig] int Method9();
        [PreserveSig] int ConnectToMonitor([MarshalAs(UnmanagedType.IUnknown)] object monitor);
    }

    private static readonly Guid ClsidImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid SidMonitorService = new("47094E3A-0CF2-430F-806F-CF9E4F0F12DD");
    private static readonly Guid SidLauncher = new("6F86E01C-C649-4D61-BE23-F1322DDECA9D");

    /// <summary>Startmenü auf diesem Bildschirm öffnen bzw. schließen, wenn es offen ist. false = hat nicht geklappt.</summary>
    public static bool Toggle(IntPtr hMonitor)
    {
        object? shell = null, service = null, monitor = null, launcher = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidImmersiveShell);
            if (type == null) return false;
            shell = Activator.CreateInstance(type);
            if (shell is not IServiceProvider sp) return false;

            Guid sid = SidMonitorService, iid = typeof(IImmersiveMonitorService).GUID;
            if (sp.QueryService(ref sid, ref iid, out service) != 0 || service is not IImmersiveMonitorService ms) return false;
            if (ms.GetFromHandle(hMonitor, out monitor) != 0 || monitor == null) return false;

            sid = SidLauncher; iid = typeof(IImmersiveLauncher).GUID;
            if (sp.QueryService(ref sid, ref iid, out launcher) != 0 || launcher is not IImmersiveLauncher l) return false;

            if (l.IsVisible(out bool visible) == 0 && visible)
                return l.Dismiss() == 0;
            l.ConnectToMonitor(monitor);
            return l.ShowStartView(11, 0) == 0;
        }
        catch { return false; }
        finally
        {
            foreach (var o in new[] { launcher, monitor, service, shell })
                if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
    }
}
