using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Rangos de push/fold de Nash (heads-up y 3-handed) y tus desviaciones. Solo estudio: nunca con mesas abiertas ni en el HUD.</summary>
public sealed class NashView : GatedView
{
    public override string Title => "Nash";

    static readonly string[] Modes = { "Heads-up", "3 jugadores (Spin)" };
    static readonly string[][] Roles =
    {
        new[] { "push", "call" },
        new[] { "btn_push", "sb_call", "bb_call_btn", "sb_push", "bb_call_sb" },
    };
    static readonly string[][] RoleNames =
    {
        new[] { "SB empuja", "BB paga" },
        new[] { "BTN empuja", "SB paga al BTN", "BB paga al BTN", "SB empuja (BTN retirado)", "BB paga al SB" },
    };

    readonly Grid _matrix = new();
    readonly TextBlock _range = Ui.Label("", 13, true);
    readonly TextBlock _info = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 6, 0, 6));
    readonly ProgressBar _progress = new() { Height = 6, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 4) };
    readonly DataGrid _dev;
    readonly TextBlock _devInfo = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 6, 0, 6));
    ComboBox _roleBox = new();
    int _mode, _role;
    double _stack = 10;
    bool _busy;
    NashReport? _report;

    public NashView(AppServices svc) : base(svc)
    {
        _dev = Ui.Grid(("Mano", "HandId", null, 120), ("Cartas", "Cards", null, 70), ("Clase", "HandClass", null, 60), ("Stack (bb)", "StackBb", "0.0", 80),
            ("Spot", "Spot", null, 260), ("Coste (bb)", "CostBb", "0.00", 90));
        _dev.MaxHeight = 260;
        _dev.MouseDoubleClick += (_, _) => { if (_dev.SelectedItem is NashDeviation d) ReplayerLauncher.TryOpen(Svc, Window.GetWindow(this), d.HandId); };
    }

    protected override UIElement BuildBody()
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(Ui.Label("Juego", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        bar.Children.Add(Ui.Combo(Modes, 0, i => { _mode = i; _role = 0; FillRoles(); Draw(); }, 160));
        bar.Children.Add(Ui.Label("Rol", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        _roleBox = Ui.Combo(RoleNames[0], 0, i => { _role = i; Draw(); }, 200);
        bar.Children.Add(_roleBox);
        bar.Children.Add(Ui.Label("Stack (bb)", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        bar.Children.Add(Ui.Combo(Enumerable.Range(1, 25).Select(i => i.ToString()), 9, i => { _stack = i + 1; Draw(); }, 70));
        bar.Children.Add(Ui.Btn("Calcular todas las tablas", (_, _) => BuildAllTables()));

        for (int i = 0; i < 13; i++)
        {
            _matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _matrix.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        }
        _matrix.Width = 560;
        _matrix.HorizontalAlignment = HorizontalAlignment.Left;

        var devHead = Ui.Label("Tus desviaciones (Spin y torneos cortos de stack)", 14, true, null, new Thickness(0, 16, 0, 0));
        var root = new StackPanel();
        root.Children.Add(bar);
        root.Children.Add(_range);
        root.Children.Add(_progress);
        root.Children.Add(_info);
        root.Children.Add(_matrix);
        root.Children.Add(devHead);
        root.Children.Add(_devInfo);
        root.Children.Add(_dev);
        root.Children.Add(Ui.Btn("Enviar desviaciones al Estudio", (_, _) => SendToStudy()));
        return Scroll(root);
    }

    void FillRoles()
    {
        _roleBox.Items.Clear();
        foreach (var n in RoleNames[_mode]) _roleBox.Items.Add(n);
        _roleBox.SelectedIndex = 0;
    }

    protected override void Load()
    {
        Draw();
        LoadDeviations();
    }

    async void Draw()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _info.Text = Svc.Nash.MatrixReady ? "" : "Primera vez: calculando la matriz de equity 169×169 (unos segundos, solo ocurre una vez)…";
            var mode = _mode == 0 ? "hu" : "3h";
            var stack = _stack;
            var chart = await Task.Run(() => Svc.Nash.Get(mode, stack));
            var role = Roles[_mode][Math.Min(_role, Roles[_mode].Length - 1)];
            Paint(chart, role);
            _info.Text = "Equilibrio con EV de fichas (en Spin & Go el ganador se lo lleva todo, así que es el valor real). " +
                         (_mode == 1 ? "Aproximación: las ramas con dos all-in a la vez se resuelven por pares." : "Sin ante.");
        }
        catch (Exception ex) { _info.Text = "No se pudo calcular: " + ex.Message; }
        finally { _busy = false; }
    }

    void Paint(NashChart chart, string role)
    {
        var f = chart.Freq[role];
        _range.Text = $"{RoleNames[_mode][Array.IndexOf(Roles[_mode], role)]} con {chart.StackBb:0.#} bb: {chart.RangePct(role) * 100:0.0}% de las manos";
        _matrix.Children.Clear();
        for (int i = 0; i < 169; i++)
        {
            double v = f[i];
            var color = Color.FromRgb((byte)(40 + (1 - v) * 20), (byte)(48 + v * 130), (byte)(56 + (1 - v) * 10));
            var cell = new Border
            {
                Background = new SolidColorBrush(color), BorderBrush = Theme.B("#14161F"), BorderThickness = new Thickness(0.5),
                ToolTip = $"{HandClasses.Names[i]}: {v * 100:0}%",
                Child = new TextBlock { Text = HandClasses.Names[i], FontSize = 10, Foreground = Theme.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Grid.SetRow(cell, i / 13);
            Grid.SetColumn(cell, i % 13);
            _matrix.Children.Add(cell);
        }
    }

    async void BuildAllTables()
    {
        _progress.Visibility = Visibility.Visible;
        _info.Text = "Calculando todas las tablas de 1 a 25 bb…";
        try
        {
            var prog = new Progress<double>(p => _progress.Value = p);
            await Task.Run(() => Svc.Nash.BuildAll(prog));
            _info.Text = "Listo: las tablas quedan guardadas en disco.";
        }
        catch (Exception ex) { _info.Text = "Error: " + ex.Message; }
        _progress.Visibility = Visibility.Collapsed;
    }

    async void LoadDeviations()
    {
        try
        {
            _devInfo.Text = "Calculando…";
            _report = await Task.Run(() => NashDeviations.Analyze(Svc.Db, Svc.Nash));
            _dev.ItemsSource = _report.Deviations;
            _devInfo.Text = _report.Spots == 0
                ? "Aún no hay spots de push/fold tuyos (Spin o torneo con ≤ 25 bb de stack efectivo)."
                : $"{_report.Spots} spots de push/fold analizados, {_report.Deviations.Count} se alejan claramente de Nash (coste estimado {_report.TotalCostBb:0.0} bb). " +
                  (_report.ByHand.Count > 0 ? "Más costosas: " + string.Join(", ", _report.ByHand.Take(4).Select(h => $"{h.HandClass} ({h.CostBb:0.0} bb)")) + ". " : "") +
                  "Doble clic abre la mano.";
        }
        catch (Exception ex) { _devInfo.Text = "No se pudo analizar: " + ex.Message; }
    }

    void SendToStudy()
    {
        if (_report == null) return;
        int n = 0;
        foreach (var d in _report.Deviations.Take(50)) if (Svc.Study.Add(d.HandId, "nash", "nash")) n++;
        _devInfo.Text = $"{n} manos nuevas en el Estudio.";
    }
}
