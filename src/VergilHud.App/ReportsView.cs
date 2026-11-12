using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using VergilHud.Core;

namespace VergilHud.App;

public sealed class ReportsView : ViewBase
{
    public override string Title => "Informes";

    static readonly (string Name, string Key)[] Dims =
    {
        ("Posición", "position"), ("Stack efectivo", "stack"), ("Nº de jugadores", "players"), ("Mes", "month"),
        ("Límite (ciega grande)", "limit"), ("Hora del día", "hour"), ("Día de la semana", "weekday"), ("Formato", "format"),
    };
    static readonly string[] Metrics = { "bb/100", "% jugadas", "Nº de manos" };
    static readonly string[] Days = { "Dom", "Lun", "Mar", "Mié", "Jue", "Vie", "Sáb" };

    readonly FilterBar _bar = new();
    readonly TabControl _tabs = new();
    readonly DataGrid _groupGrid;
    readonly DataGrid _handsGrid;
    readonly Grid _matrix = new();
    readonly TextBlock _matrixInfo = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 6, 0, 6));
    int _dim;
    int _metric;
    MatrixCell[,]? _cells;
    List<GroupRow> _rows = new();
    List<HandRow> _hands = new();
    string _selected = "";

    public ReportsView(AppServices svc) : base(svc)
    {
        _groupGrid = Ui.Grid(("Grupo", "Key", null, 150), ("Manos", "Hands", "N0", 80), ("Neto (bb)", "NetBb", "0.0", 90),
            ("bb/100", "BbPer100", "0.0", 80), ("EV bb/100", "EvBbPer100", "0.0", 90), ("VPIP", "VpipPct", "0.0", 70), ("PFR", "PfrPct", "0.0", 70));
        _handsGrid = Ui.Grid(("Mano", "HandId", null, 130), ("Fecha", "Time", "dd/MM/yy HH:mm", 120), ("Cartas", "Cards", null, 70),
            ("Posición", "Position", null, 80), ("Neto (bb)", "NetBb", "0.0", 90), ("EV (bb)", "EvNetBb", "0.0", 90));
        _handsGrid.MaxHeight = 220;
        _handsGrid.MouseDoubleClick += (_, _) => { if (_handsGrid.SelectedItem is HandRow hr) ReplayerLauncher.TryOpen(Svc, Window.GetWindow(this), hr.HandId); };

        var groupTab = new DockPanel();
        var gbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        gbar.Children.Add(Ui.Label("Agrupar por", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        gbar.Children.Add(Ui.Combo(Dims.Select(d => d.Name), 0, i => { _dim = i; Refresh(); }, 180));
        gbar.Children.Add(Ui.Btn("Exportar CSV", (_, _) => Export(ReportExport.ToCsv(_rows), "informe.csv")));
        DockPanel.SetDock(gbar, Dock.Top);
        groupTab.Children.Add(gbar);
        groupTab.Children.Add(_groupGrid);

        var matrixTab = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var mp = new StackPanel();
        var mbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        mbar.Children.Add(Ui.Label("Colorear por", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        mbar.Children.Add(Ui.Combo(Metrics, 0, i => { _metric = i; DrawMatrix(); }, 130));
        mbar.Children.Add(Ui.Btn("Exportar matriz (CSV)", (_, _) => { if (_cells != null) Export(ReportExport.ToCsv(_cells), "matriz.csv"); }));
        mp.Children.Add(mbar);
        mp.Children.Add(_matrix);
        mp.Children.Add(_matrixInfo);
        var hbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        hbar.Children.Add(Ui.Btn("Exportar manos (CSV)", (_, _) => Export(ReportExport.ToCsv(_hands), "manos.csv")));
        mp.Children.Add(hbar);
        mp.Children.Add(_handsGrid);
        matrixTab.Content = mp;

        _tabs.Background = Theme.Bg;
        _tabs.BorderThickness = new Thickness(0);
        var tabs = _tabs;
        tabs.Items.Add(new TabItem { Header = "Por grupo", Content = groupTab });
        tabs.Items.Add(new TabItem { Header = "Matriz de manos", Content = matrixTab });

        var root = new DockPanel();
        DockPanel.SetDock(_bar, Dock.Top);
        root.Children.Add(_bar);
        root.Children.Add(tabs);
        Content = root;
        _bar.Changed += Refresh;
    }

    public void SelectTab(int i) => _tabs.SelectedIndex = i;

    public override async void Refresh()
    {
        try
        {
            var period = _bar.Period;
            var scope = _bar.Scope;
            var key = Dims[_dim].Key;
            var (rows, cells) = await Bg(() => (Svc.Analytics.GroupBy(key, period, scope), Svc.Analytics.GetHandMatrix(period, scope)));
            Show(rows, cells);
        }
        catch (Exception ex) { _matrixInfo.Text = "No se pudo calcular: " + ex.Message; }
    }

    public void Show(List<GroupRow> rows, MatrixCell[,] cells)
    {
        if (Dims[_dim].Key == "weekday")
            rows = rows.Select(r => int.TryParse(r.Key, out var i) && i is >= 0 and <= 6 ? r with { Key = Days[i] } : r).ToList();
        _rows = rows;
        _groupGrid.ItemsSource = rows;
        _cells = cells;
        DrawMatrix();
    }

    // ---------- matriz 13×13 ----------
    void DrawMatrix()
    {
        _matrix.Children.Clear();
        _matrix.RowDefinitions.Clear();
        _matrix.ColumnDefinitions.Clear();
        if (_cells == null) return;
        for (int i = 0; i < 14; i++)
        {
            _matrix.RowDefinitions.Add(new RowDefinition { Height = new GridLength(i == 0 ? 20 : 38) });
            _matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == 0 ? 20 : 58) });
        }
        for (int i = 0; i < 13; i++)
        {
            var r = Cards.RankChars[12 - i].ToString();
            Place(Ui.Label(r, 11, true, Theme.Muted), 0, i + 1, center: true);
            Place(Ui.Label(r, 11, true, Theme.Muted), i + 1, 0, center: true);
        }

        var all = _cells.Cast<MatrixCell>().Where(c => c.Hands > 0).ToList();
        double maxAbs = Math.Max(1, all.Select(c => Math.Abs(c.BbPer100)).DefaultIfEmpty(1).Max());
        double maxHands = Math.Max(1, all.Select(c => c.Hands).DefaultIfEmpty(1).Max());
        for (int i = 0; i < 13; i++)
            for (int j = 0; j < 13; j++)
            {
                var c = _cells[i, j];
                var (bg, txt) = Describe(c, maxAbs, maxHands);
                var l1 = Ui.Label(c.Name, 11, true, Theme.B("#F0F0F4"));
                var l2 = Ui.Label(txt, 10, false, Theme.B("#DADAE2"));
                l1.HorizontalAlignment = l2.HorizontalAlignment = HorizontalAlignment.Center;
                var cell = new Border
                {
                    Background = bg,
                    Margin = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Cursor = Cursors.Hand,
                    ToolTip = c.Hands == 0 ? $"{c.Name}: sin manos"
                        : $"{c.Name}\n{c.Hands} manos · jugada {c.PlayedPct:0}%\n{c.BbPer100:+0.0;-0.0;0.0} bb/100 · {c.NetBb:+0.0;-0.0;0.0} bb",
                    Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { l1, l2 } },
                };
                var name = c.Name;
                cell.MouseLeftButtonUp += (_, _) => SelectHand(name);
                Place(cell, i + 1, j + 1);
            }
        _matrixInfo.Text = all.Count == 0
            ? "Sin manos con cartas conocidas en este periodo."
            : "Pares en la diagonal, suited por encima y offsuit por debajo. Clic en una casilla para ver sus manos.";
    }

    (Brush Bg, string Text) Describe(MatrixCell c, double maxAbs, double maxHands)
    {
        if (c.Hands == 0) return (Theme.B("#24252C"), "");
        byte Mix(double t) => (byte)(40 + 140 * Math.Clamp(t, 0, 1));
        switch (_metric)
        {
            case 0:
            {
                double t = Math.Abs(c.BbPer100) / maxAbs;
                var b = c.BbPer100 >= 0 ? Color.FromRgb(30, Mix(t), 50) : Color.FromRgb(Mix(t), 40, 40);
                return (new SolidColorBrush(b), c.BbPer100.ToString("+0;-0;0", CultureInfo.InvariantCulture));
            }
            case 1:
                return (new SolidColorBrush(Color.FromRgb(35, 60, Mix(c.PlayedPct / 100))), c.PlayedPct.ToString("0", CultureInfo.InvariantCulture) + "%");
            default:
                return (new SolidColorBrush(Color.FromRgb(35, 60, Mix(c.Hands / maxHands))), c.Hands.ToString(CultureInfo.InvariantCulture));
        }
    }

    void Place(UIElement e, int row, int col, bool center = false)
    {
        if (center && e is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Center; fe.VerticalAlignment = VerticalAlignment.Center; }
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        _matrix.Children.Add(e);
    }

    async void SelectHand(string name)
    {
        _selected = name;
        var period = _bar.Period;
        var scope = _bar.Scope;
        _hands = await Bg(() => Svc.Analytics.GetHandsFor(name, period, scope));
        _handsGrid.ItemsSource = _hands;
        _matrixInfo.Text = $"{name}: {_hands.Count} manos (las más recientes primero).";
    }

    static void Export(string csv, string defaultName)
    {
        var dlg = new SaveFileDialog { FileName = defaultName, Filter = "CSV (*.csv)|*.csv" };
        if (dlg.ShowDialog() == true) File.WriteAllText(dlg.FileName, csv, new System.Text.UTF8Encoding(true));
    }
}
