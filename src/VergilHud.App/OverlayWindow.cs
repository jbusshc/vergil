using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Ventana transparente que se coloca sobre una mesa de PokerStars y la sigue.</summary>
public sealed class OverlayWindow : Window
{
    readonly IntPtr _table;
    readonly AppServices _svc;
    readonly Canvas _canvas = new();
    readonly List<SeatPanel> _panels = new();
    NativeMethods.RECT _lastRect;
    int _maxSeats = 6;

    public string TableName { get; }
    public string? LastHandId { get; set; }
    public HudProfile? Profile { get; private set; }

    public OverlayWindow(IntPtr table, string tableName, AppServices svc)
    {
        _table = table;
        _svc = svc;
        TableName = tableName;
        Title = "Vergil HUD – " + tableName;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000; Top = -10000; Width = 10; Height = 10;
        Content = _canvas;
        new WindowInteropHelper(this).Owner = table; // queda por encima de su mesa y se oculta con ella
        SizeChanged += (_, _) => LayoutPanels();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Sigue la posición de la mesa. Devuelve false si la mesa ya no existe.</summary>
    /// <summary>El usuario ocultó los paneles con el atajo de teclado.</summary>
    public bool UserHidden { get; set; }

    public bool Follow()
    {
        if (UserHidden)
        {
            if (IsVisible) Hide();
            return NativeMethods.IsWindow(_table);
        }
        if (!NativeMethods.IsWindow(_table)) return false;
        if (NativeMethods.IsIconic(_table) || !NativeMethods.IsWindowVisible(_table))
        {
            if (IsVisible) Hide();
            return true;
        }
        if (!IsVisible) Show();
        if (!NativeMethods.TryGetClientScreenRect(_table, out var r)) return true;
        if (r.Left == _lastRect.Left && r.Top == _lastRect.Top && r.Right == _lastRect.Right && r.Bottom == _lastRect.Bottom) return true;
        _lastRect = r;

        // Se coloca en píxeles físicos con la API de Windows: así funciona igual con cualquier escala por monitor
        // (la app es PerMonitorV2) y WPF se reescala solo al DPI del monitor donde cae la ventana.
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, Math.Max(10, r.Right - r.Left), Math.Max(10, r.Bottom - r.Top),
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        return true;
    }

    public void SetHand(Hand hand)
    {
        LastHandId = hand.Id;
        foreach (var p in _panels) p.ClosePopup();
        _canvas.Children.Clear();
        _panels.Clear();

        _maxSeats = Math.Max(hand.MaxSeats, hand.Seats.Count == 0 ? 2 : hand.Seats.Max(s => s.Number));
        Profile = HudProfiles.ForHand(_svc.Profiles, hand);
        var s = _svc.Settings;
        var options = _svc.Options;
        var views = _svc.Builder.Build(hand, Profile, options);
        int? heroSeat = hand.Hero == null ? null : hand.Seats.FirstOrDefault(x => x.Player == hand.Hero)?.Number;

        foreach (var view in views)
        {
            if (view.IsHero && !s.ShowHeroPanel) continue;
            int visual = s.RotateHeroToBottom && heroSeat != null
                ? ((view.Seat - heroSeat.Value) % _maxSeats + _maxSeats) % _maxSeats
                : (view.Seat - 1) % _maxSeats;

            var profile = Profile;
            var panel = new SeatPanel(view, visual, options, s.PanelOpacity, s.LockPanels,
                () => PlayerPopup.Create(_svc, view.Player, profile));
            if (view.IsHero && s.ShowTilt && _svc.TiltText != null) panel.ToolTip += Environment.NewLine + _svc.TiltText;
            panel.DragFinished += OnPanelDragged;
            _panels.Add(panel);
            _canvas.Children.Add(panel);
        }
        LayoutPanels();
    }

    string Key(int visual) => $"{_maxSeats}-{visual}";

    void OnPanelDragged(SeatPanel p, Vector delta)
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        var k = Key(p.Visual);
        var off = _svc.Settings.PanelOffsets.TryGetValue(k, out var o) && o.Length == 2 ? o : new double[2];
        _svc.Settings.PanelOffsets[k] = new[] { off[0] + delta.X / ActualWidth, off[1] + delta.Y / ActualHeight };
        _svc.Settings.Save();
        LayoutPanels();
    }

    void LayoutPanels()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20) return;
        var fontSize = PanelLayout.FontSize(w);
        foreach (var p in _panels)
        {
            p.Locked = _svc.Settings.LockPanels;
            System.Windows.Documents.TextElement.SetFontSize(p, fontSize);
            var (ax, ay) = PanelLayout.Anchor(_maxSeats, p.Visual);
            if (_svc.Settings.PanelOffsets.TryGetValue(Key(p.Visual), out var off) && off.Length == 2)
            {
                ax += off[0];
                ay += off[1];
            }
            p.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(p, Math.Clamp(ax * w - p.DesiredSize.Width / 2, 0, Math.Max(0, w - p.DesiredSize.Width)));
            Canvas.SetTop(p, Math.Clamp(ay * h - p.DesiredSize.Height / 2, 0, Math.Max(0, h - p.DesiredSize.Height)));
        }
    }
}
