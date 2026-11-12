using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>
/// Panel de stats de un asiento. Cada valor se atenúa y lleva una barra fina según su fiabilidad (modo Honesto).
/// Clic = popup con el detalle; arrastrar = mover (si no está bloqueado).
/// </summary>
public sealed class SeatPanel : Border
{
    static readonly Brush Grey = Frozen(Color.FromRgb(150, 150, 160));
    static readonly Brush White = Frozen(Color.FromRgb(238, 238, 242));
    static readonly Brush Gold = Frozen(Color.FromRgb(214, 178, 72));

    public int Visual { get; }
    public string Player { get; }
    public bool Locked { get; set; }
    public event Action<SeatPanel, Vector>? DragFinished;

    readonly Func<UIElement> _popupFactory;
    Popup? _popup;
    Point _start;
    double _origLeft, _origTop;
    bool _dragging;

    public SeatPanel(HudSeatView seat, int visual, HudOptions options, double opacity, bool locked, Func<UIElement> popupFactory)
    {
        Visual = visual;
        Player = seat.Player;
        Locked = locked;
        _popupFactory = popupFactory;

        Background = Frozen(Color.FromArgb((byte)(Math.Clamp(opacity, 0.2, 1) * 255), 18, 18, 22));
        BorderBrush = seat.IsHero ? Gold : Frozen(Color.FromArgb(160, 90, 90, 100));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(5, 2, 5, 3);
        Cursor = Cursors.Hand;
        System.Windows.Documents.TextElement.SetFontFamily(this, new FontFamily("Consolas"));
        System.Windows.Documents.TextElement.SetFontSize(this, 11);

        bool honest = options.Mode == HudMode.Honest;
        var stack = new StackPanel();

        var header = new TextBlock { FontWeight = FontWeights.Bold };
        header.Inlines.Add(new System.Windows.Documents.Run(Shorten(seat.Player, 14)) { Foreground = White });
        header.Inlines.Add(new System.Windows.Documents.Run($" {seat.Hands}") { Foreground = Grey, FontWeight = FontWeights.Normal });
        header.Inlines.Add(new System.Windows.Documents.Run($" {seat.Badge}")
            { Foreground = BadgeBrush(seat.Badge), FontWeight = FontWeights.Normal, FontSize = 9 });
        stack.Children.Add(header);

        foreach (var line in seat.Lines)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            bool first = true;
            foreach (var c in line.Cells)
            {
                if (!first && c.Sep.Length > 0) row.Children.Add(new TextBlock { Text = c.Sep, Foreground = Grey });
                first = false;
                row.Children.Add(BuildCell(c, honest));
            }
            if (row.Children.Count > 0) stack.Children.Add(row);
        }

        if (seat.ColorLabel != null && NotesPanel.ColorBrushes.TryGetValue(seat.ColorLabel, out var dot))
            header.Inlines.Add(new System.Windows.Documents.Run(" ●") { Foreground = dot });
        if (!string.IsNullOrWhiteSpace(seat.Note))
            header.Inlines.Add(new System.Windows.Documents.Run(" ✎") { Foreground = Grey });
        Child = stack;
        ToolTip = $"{seat.Player} — {seat.Badge}\nClic: detalle · Arrastrar: mover" +
                  (string.IsNullOrWhiteSpace(seat.Note) ? "" : "\nNota: " + seat.Note) +
                  (honest ? "\nLa barra bajo cada número indica lo fiable que es la muestra." : "");
    }

    static UIElement BuildCell(HudCell c, bool honest)
    {
        var tb = new TextBlock { ToolTip = Help.ForStat(c.Label, null) };
        if (c.ShowLabel) tb.Inlines.Add(new System.Windows.Documents.Run(c.Label + " ") { Foreground = Grey });
        tb.Inlines.Add(new System.Windows.Documents.Run(c.Text) { Foreground = ValueBrush(c.Color) });
        if (c.Arrow == "up") tb.Inlines.Add(new System.Windows.Documents.Run("▲") { Foreground = Frozen(Color.FromRgb(255, 170, 60)), FontSize = 8 });
        else if (c.Arrow == "down") tb.Inlines.Add(new System.Windows.Documents.Run("▼") { Foreground = Frozen(Color.FromRgb(110, 170, 255)), FontSize = 8 });
        if (c.Asterisk) tb.Inlines.Add(new System.Windows.Documents.Run("*") { Foreground = Grey });
        if (honest) tb.Opacity = 0.5 + 0.5 * c.Reliability;

        var cell = new StackPanel();
        cell.Children.Add(tb);
        if (honest)
        {
            double r = Math.Clamp(c.Reliability, 0.03, 1);
            var bar = new Grid { Height = 2, Margin = new Thickness(0, 0, 0, 1) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(r, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - r + 0.0001, GridUnitType.Star) });
            var fill = new Border { Background = ReliabilityBrush(c.Reliability) };
            var rest = new Border { Background = Frozen(Color.FromArgb(70, 120, 120, 130)) };
            Grid.SetColumn(rest, 1);
            bar.Children.Add(fill);
            bar.Children.Add(rest);
            cell.Children.Add(bar);
        }
        return cell;
    }

    // ---------- interacción ----------
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Parent is not UIElement canvas) return;
        _start = e.GetPosition(canvas);
        _origLeft = Canvas.GetLeft(this);
        _origTop = Canvas.GetTop(this);
        _dragging = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured || Locked || Parent is not UIElement canvas) return;
        var d = e.GetPosition(canvas) - _start;
        if (!_dragging && d.Length > 4) { _dragging = true; ClosePopup(); }
        if (_dragging)
        {
            Canvas.SetLeft(this, _origLeft + d.X);
            Canvas.SetTop(this, _origTop + d.Y);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        if (_dragging && Parent is UIElement canvas) DragFinished?.Invoke(this, e.GetPosition(canvas) - _start);
        else TogglePopup();
        _dragging = false;
        e.Handled = true;
    }

    void TogglePopup()
    {
        if (_popup is { IsOpen: true }) { _popup.IsOpen = false; return; }
        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            AllowsTransparency = true,
            StaysOpen = false,   // se cierra al hacer clic fuera
            Child = _popupFactory(),
        };
        _popup.IsOpen = true;
    }

    public void ClosePopup()
    {
        if (_popup != null) _popup.IsOpen = false;
    }

    // ---------- colores ----------
    static Brush ValueBrush(string? key) => key switch
    {
        "low" => Frozen(Color.FromRgb(110, 170, 255)),
        "mid" => Frozen(Color.FromRgb(110, 220, 120)),
        "loose" => Frozen(Color.FromRgb(240, 220, 90)),
        "high" => Frozen(Color.FromRgb(255, 110, 95)),
        _ => White,
    };

    static Brush ReliabilityBrush(double r) =>
        r < 0.35 ? Frozen(Color.FromRgb(200, 90, 90)) : r < 0.7 ? Frozen(Color.FromRgb(220, 180, 70)) : Frozen(Color.FromRgb(90, 190, 110));

    public static Brush BadgeBrush(string badge) => badge switch
    {
        "Nit" => Frozen(Color.FromRgb(110, 170, 255)),
        "TAG" => Frozen(Color.FromRgb(110, 220, 120)),
        "LAG" => Frozen(Color.FromRgb(255, 170, 60)),
        "Loose-pasivo" => Frozen(Color.FromRgb(240, 220, 90)),
        "Fish pasivo" or "Calling station" or "Maníaco" => Frozen(Color.FromRgb(255, 110, 95)),
        _ => Grey,
    };

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
