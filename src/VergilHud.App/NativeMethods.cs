using System.Runtime.InteropServices;
using System.Text;

namespace VergilHud.App;

static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public sealed record TopWindow(IntPtr Hwnd, string Title, string Class, uint Pid);

    /// <summary>Ventanas de nivel superior (de otros procesos) con título: visibles o minimizadas.</summary>
    public static List<TopWindow> GetTopLevelWindows()
    {
        var own = (uint)Environment.ProcessId;
        var list = new List<TopWindow>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) && !IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == own) return true;
            int len = GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            var cls = new StringBuilder(256);
            GetClassName(h, cls, cls.Capacity);
            list.Add(new TopWindow(h, sb.ToString(), cls.ToString(), pid));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static readonly Dictionary<uint, string> ProcNames = new();

    public static string ProcessName(uint pid)
    {
        if (ProcNames.TryGetValue(pid, out var n)) return n;
        try { n = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { n = ""; }
        return ProcNames[pid] = n;
    }

    /// <summary>Área cliente de la ventana en coordenadas de pantalla (píxeles físicos).</summary>
    public static bool TryGetClientScreenRect(IntPtr hwnd, out RECT rect)
    {
        rect = default;
        if (!GetClientRect(hwnd, out var c)) return false;
        var p = new POINT();
        if (!ClientToScreen(hwnd, ref p)) return false;
        rect = new RECT { Left = p.X, Top = p.Y, Right = p.X + c.Right, Bottom = p.Y + c.Bottom };
        return true;
    }
}
