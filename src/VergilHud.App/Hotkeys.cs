using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VergilHud.App;

/// <summary>Atajos globales de teclado (RegisterHotKey). Si otra aplicación ya usa la combinación, simplemente no se registra.</summary>
public sealed class Hotkeys : IDisposable
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;

    readonly Dictionary<int, Action> _actions = new();
    HwndSource? _source;
    IntPtr _hwnd;

    public void Attach(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(Hook);
    }

    /// <summary>Registra Ctrl+Alt+<paramref name="key"/> (letra mayúscula). Devuelve false si el atajo ya estaba ocupado.</summary>
    public bool Add(char key, Action action)
    {
        int id = 0x7000 + _actions.Count;
        if (!RegisterHotKey(_hwnd, id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, key)) return false;
        _actions[id] = action;
        return true;
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a))
        {
            a();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_hwnd, id);
        _actions.Clear();
        _source?.RemoveHook(Hook);
    }
}
