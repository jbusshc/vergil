using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VergilHud.Core;

namespace VergilHud.App;

public static class Theme
{
    static readonly Dictionary<string, SolidColorBrush> Cache = new();

    public static SolidColorBrush B(string hex)
    {
        if (Cache.TryGetValue(hex, out var b)) return b;
        b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return Cache[hex] = b;
    }

    /// <summary>Tema claro (se elige al arrancar en Ajustes; cambiarlo requiere reiniciar).</summary>
    public static bool Light { get; set; }

    static SolidColorBrush P(string dark, string light) => B(Light ? light : dark);

    public static SolidColorBrush Bg => P("#1B1C21", "#F4F5F8");
    public static SolidColorBrush Panel => P("#24252C", "#FFFFFF");
    public static SolidColorBrush Panel2 => P("#2A2B32", "#E9EBF0");
    public static SolidColorBrush Border => P("#3A3C45", "#C9CCD6");
    public static SolidColorBrush Text => P("#E6E6E6", "#1E2026");
    public static SolidColorBrush Muted => P("#9A9AA6", "#5F6270");
    public static SolidColorBrush Green => P("#6EDC78", "#1E8A3A");
    public static SolidColorBrush Red => P("#FF6E5F", "#C23A2B");
    public static SolidColorBrush Blue => P("#6AA9FF", "#1F5FBF");
    public static SolidColorBrush Yellow => P("#F0DC5A", "#9A7B00");
    public static SolidColorBrush Nav => P("#17181C", "#DDE0E8");
    public static SolidColorBrush NavSelected => P("#2E3038", "#FFFFFF");
    public static SolidColorBrush Button => P("#2E3038", "#E4E7EE");
    public static SolidColorBrush ButtonBorder => P("#444754", "#B5B9C6");
    public static SolidColorBrush GridBg => P("#1F2026", "#F4F5F8");
    public static SolidColorBrush GridHeaderText => P("#C8C8D0", "#3A3D48");

    /// <summary>Recursos del tema para los estilos de XAML (pestañas, ventana, barra de progreso).</summary>
    public static void ApplyResources(ResourceDictionary r)
    {
        r["WinBg"] = Bg;
        r["WinFg"] = Text;
        r["TabBg"] = P("#2A2B32", "#E4E7EE");
        r["TabHover"] = P("#33353F", "#D5D9E3");
        r["TabSel"] = P("#3A4A66", "#2F6FD0");
        r["TabFg"] = P("#C8C8D0", "#3A3D48");
        r["TabSelFg"] = B("#FFFFFF");
        r["ProgBg"] = Panel2;
        r["ComboFg"] = B("#111111");
    }

    public static SolidColorBrush Signed(double v) => v > 0 ? Green : v < 0 ? Red : Text;
}

/// <summary>Construcción de controles con el tema oscuro. La interfaz se arma en código: así se puede dibujar también fuera de pantalla.</summary>
public static class Ui
{
    public static TextBlock Label(string text, double size = 13, bool bold = false, Brush? fg = null, Thickness? margin = null) => new()
    {
        ToolTip = Help.For(text),
        Text = Loc.T(text),
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = fg ?? Theme.Text,
        TextWrapping = TextWrapping.Wrap,
        Margin = margin ?? new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border Card(string title, string value, string? sub = null, Brush? valueBrush = null, double width = 150)
    {
        var s = new StackPanel();
        s.Children.Add(Label(title, 11, false, Theme.Muted));
        s.Children.Add(Label(value, 22, true, valueBrush ?? Theme.Text, new Thickness(0, 2, 0, 0)));
        if (sub != null) s.Children.Add(Label(sub, 11, false, Theme.Muted, new Thickness(0, 2, 0, 0)));
        return new Border
        {
            Background = Theme.Panel,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 10, 10),
            MinWidth = width,
            Child = s,
            ToolTip = Help.For(title),
        };
    }

    public static Button Btn(string text, RoutedEventHandler onClick, double minWidth = 0)
    {
        var b = new Button
        {
            Content = Loc.T(text),
            ToolTip = Help.For(text),
            Background = Theme.Button,
            Foreground = Theme.Text,
            BorderBrush = Theme.ButtonBorder,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = minWidth,
        };
        b.Click += onClick;
        return b;
    }

    public static ComboBox Combo(IEnumerable<string> items, int selected, Action<int> onChange, double width = 130)
    {
        var c = new ComboBox { Width = width, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var i in items) c.Items.Add(i);
        c.SelectedIndex = selected;
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) onChange(c.SelectedIndex); };
        return c;
    }

    public static CheckBox Check(string text, bool isChecked, Action<bool> onChange)
    {
        var c = new CheckBox { Content = Loc.T(text), ToolTip = Help.For(text), IsChecked = isChecked, Foreground = Theme.Text, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        c.Click += (_, _) => onChange(c.IsChecked == true);
        return c;
    }

    public static DataGrid Grid(params (string Header, string Path, string? Format, double Width)[] cols)
    {
        var g = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            CanUserResizeRows = false,
            Background = Theme.GridBg,
            RowBackground = Theme.Panel,
            AlternatingRowBackground = Theme.Panel2,
            Foreground = Theme.Text,
            BorderBrush = Theme.Border,
            SelectionMode = DataGridSelectionMode.Single,
            MinHeight = 120,
        };
        var hs = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        hs.Setters.Add(new Setter(Control.BackgroundProperty, Theme.Panel2));
        hs.Setters.Add(new Setter(Control.ForegroundProperty, Theme.GridHeaderText));
        hs.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
        hs.Setters.Add(new Setter(Control.BorderBrushProperty, Theme.Border));
        hs.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        g.ColumnHeaderStyle = hs;
        foreach (var (header, path, fmt, width) in cols)
        {
            var binding = new Binding(path);
            if (fmt != null) binding.StringFormat = fmt;
            var colStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), hs);
            if (Help.For(header) is { } tip) colStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, tip));
            var col = new DataGridTextColumn
            {
                HeaderStyle = colStyle,
                Header = header,
                Binding = binding,
                Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star),
            };
            if (header is "Perfil" or "Arquetipo")
            {
                // En estas columnas cada celda es una etiqueta de tipo de jugador: el cursor encima explica qué significa
                var cs = new Style(typeof(TextBlock));
                cs.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(path) { Converter = BadgeTipConverter.Instance }));
                col.ElementStyle = cs;
            }
            g.Columns.Add(col);
        }
        return g;
    }

    static SolidColorBrush B(string hex) => Theme.B(hex);

    public static StackPanel Section(string title, UIElement body, Thickness? margin = null)
    {
        var s = new StackPanel { Margin = margin ?? new Thickness(0, 6, 0, 10) };
        s.Children.Add(Label(title, 14, true, Theme.Text, new Thickness(0, 0, 0, 6)));
        s.Children.Add(body);
        return s;
    }
}

/// <summary>Convierte el texto de una celda de tipo de jugador (TAG, Fish 2…) en su explicación.</summary>
sealed class BadgeTipConverter : System.Windows.Data.IValueConverter
{
    public static readonly BadgeTipConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is string s && Help.ForBadge(s) is { Length: > 0 } t ? t : null;
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Periodo y formato elegidos en la parte superior de cada vista de análisis.</summary>
public sealed class FilterBar : StackPanel
{
    public static readonly string[] PeriodNames = { "Hoy", "7 días", "30 días", "Este año", "Todo" };
    public static readonly string[] ScopeNames = { "Cash", "Spin & Go", "Torneos MTT/SNG", "Zoom", "Todo" };
    public static readonly string[] ScopeKeys = { "cash", "spin", "mtt", "zoom", "all" };
    /// <summary>Tipo de juego en el que más juega el héroe: es el que se abre por defecto (los formatos no se mezclan salvo que elijas "Todo").</summary>
    public static string PreferredScope { get; set; } = "cash";

    public int PeriodIndex { get; private set; } = 4;
    public int ScopeIndex { get; private set; }
    public event Action? Changed;

    public FilterBar(bool withScope = true, int defaultScope = -1)
    {
        Orientation = Orientation.Horizontal;
        Margin = new Thickness(0, 0, 0, 10);
        ScopeIndex = defaultScope >= 0 ? defaultScope : Math.Max(0, Array.IndexOf(ScopeKeys, PreferredScope));
        Children.Add(Ui.Label("Periodo", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        Children.Add(Ui.Combo(PeriodNames, PeriodIndex, i => { PeriodIndex = i; Changed?.Invoke(); }, 110));
        if (withScope)
        {
            Children.Add(Ui.Label("Formato", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
            Children.Add(Ui.Combo(ScopeNames, ScopeIndex, i => { ScopeIndex = i; Changed?.Invoke(); }, 110));
        }
    }

    public string Scope => ScopeKeys[ScopeIndex];

    public Period Period
    {
        get
        {
            var now = DateTime.UtcNow;
            return PeriodIndex switch
            {
                0 => Period.Today(now),
                1 => Period.LastDays(7, now),
                2 => Period.LastDays(30, now),
                3 => Period.ThisYear(now),
                _ => Period.All,
            };
        }
    }
}

/// <summary>Base de las vistas del menú lateral.</summary>
public abstract class ViewBase : UserControl
{
    protected AppServices Svc { get; }
    public abstract string Title { get; }

    protected ViewBase(AppServices svc)
    {
        Svc = svc;
        Background = Theme.Bg;
        Foreground = Theme.Text;
        Padding = new Thickness(16);
    }

    /// <summary>Vuelve a calcular y pintar la vista. Los datos se piden en segundo plano para no bloquear la interfaz.</summary>
    public abstract void Refresh();

    protected static async Task<T> Bg<T>(Func<T> work) => await Task.Run(work);

    protected static ScrollViewer Scroll(UIElement content) => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = content,
    };
}
