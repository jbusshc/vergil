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
    Hand? _hand;
    UIElement? _chip;

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
        _hand = hand;
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
            var ctx = HandContext.From(hand);
            var panel = new SeatPanel(view, visual, options, s.PanelOpacity, s.LockPanels,
                () => PlayerPopup.Create(_svc, view.Player, profile, 0, false, ctx, _svc.Settings.HudScope));
            if (view.IsHero && s.ShowTilt && _svc.TiltText != null) panel.ToolTip += Environment.NewLine + _svc.TiltText;
            panel.DragFinished += OnPanelDragged;
            _panels.Add(panel);
            _canvas.Children.Add(panel);
        }
        _canvas.Children.Add(ScopeChip(hand));
        LayoutPanels();
    }

    /// <summary>
    /// Botón flotante, como los paneles (se arrastra y recuerda su sitio): elige qué manos alimentan las estadísticas de TODOS los
    /// jugadores, incluido el héroe. Tres opciones: tipo de juego · todo el historial · esta mesa. También Ctrl+Alt+G.
    /// </summary>
    UIElement ScopeChip(Hand hand)
    {
        string first = hand.Format switch { "spin" => "Spin", "cash" => "Cash", "zoom" => "Zoom", _ => "Torneo" };
        string[] names = { first, "Todo", hand.IsTournament ? "Torneo" : "Mesa" };
        if (hand.IsTournament && first == "Torneo") names[2] = "Este";
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        int current = (int)_svc.Settings.HudScope;
        var segs = new List<TextBlock>();
        for (int i = 0; i < 3; i++)
        {
            var tb = new TextBlock
            {
                Text = names[i], FontSize = 10, Padding = new Thickness(6, 1, 6, 1),
                Foreground = new SolidColorBrush(i == current ? Color.FromRgb(20, 20, 24) : Color.FromRgb(200, 200, 210)),
                Background = i == current ? new SolidColorBrush(Color.FromRgb(214, 178, 72)) : Brushes.Transparent,
            };
            segs.Add(tb);
            row.Children.Add(tb);
        }
        var chip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(215, 18, 18, 22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 90, 90, 100)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = row,
            ToolTip = hand.IsPlayMoney ? "Qué manos cuentan en todos los paneles (dinero ficticio)" : "Qué manos cuentan en todos los paneles",
        };
        Point start = default;
        double ol = 0, ot = 0;
        bool dragging = false;
        chip.MouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(_canvas); ol = Canvas.GetLeft(chip); ot = Canvas.GetTop(chip); dragging = false;
            chip.CaptureMouse(); e.Handled = true;
        };
        chip.MouseMove += (_, e) =>
        {
            if (!chip.IsMouseCaptured || _svc.Settings.LockPanels) return;
            var d = e.GetPosition(_canvas) - start;
            if (!dragging && d.Length > 4) dragging = true;
            if (dragging) { Canvas.SetLeft(chip, ol + d.X); Canvas.SetTop(chip, ot + d.Y); }
        };
        chip.MouseLeftButtonUp += (_, e) =>
        {
            if (!chip.IsMouseCaptured) return;
            chip.ReleaseMouseCapture();
            if (dragging && ActualWidth > 1 && ActualHeight > 1)
            {
                var d = e.GetPosition(_canvas) - start;
                var off = _svc.Settings.PanelOffsets.TryGetValue("scope", out var o) && o.Length == 2 ? o : new double[2];
                _svc.Settings.PanelOffsets["scope"] = new[] { off[0] + d.X / ActualWidth, off[1] + d.Y / ActualHeight };
                _svc.Settings.Save();
                LayoutPanels();
            }
            else
            {
                // Clic: se elige la opción sobre la que se pulsó
                var p = e.GetPosition(row);
                double x = 0;
                for (int i = 0; i < segs.Count; i++)
                {
                    x += segs[i].ActualWidth;
                    if (p.X <= x) { _svc.Settings.SetScope((HudScope)i); break; }
                }
            }
            dragging = false;
            e.Handled = true;
        };
        _chip = chip;
        return chip;
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
        if (_chip != null)
        {
            double cx = 0.02, cy = 0.03;
            if (_svc.Settings.PanelOffsets.TryGetValue("scope", out var co) && co.Length == 2) { cx += co[0]; cy += co[1]; }
            _chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_chip, Math.Clamp(cx * w, 0, Math.Max(0, w - _chip.DesiredSize.Width)));
            Canvas.SetTop(_chip, Math.Clamp(cy * h, 0, Math.Max(0, h - _chip.DesiredSize.Height)));
        }
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
