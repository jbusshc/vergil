using System.Windows;
using System.Windows.Controls;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Cómo juega la población (tus rivales) y qué arquetipos hay en tu base de datos. Solo análisis.</summary>
public sealed class PopulationView : GatedView
{
    public override string Title => "Población";

    static readonly string[] ScopeNames = { "Cash", "Spin & Go", "Torneos MTT/SNG", "Todos" };
    static readonly string[][] ScopeFormats = { new[] { "cash" }, new[] { "spin" }, new[] { "mtt", "sng" }, Array.Empty<string>() };
    static readonly string[] Keys = { "cash", "spin", "mtt", "all" };

    readonly DataGrid _grid;
    readonly DataGrid _arch;
    readonly TextBlock _info = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 0, 0, 8));
    readonly TextBlock _archInfo = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 6, 0, 6));
    int _scope;
    int _version;

    public sealed record Row(string Spot, double PopPct, string Interval, int N, string You, string Diff);
    public sealed record ArchRow(string Label, string Description, int Members, string Centro);

    public PopulationView(AppServices svc) : base(svc)
    {
        _grid = Ui.Grid(("Spot", "Spot", null, 0), ("Población %", "PopPct", "0.0", 100), ("Intervalo 95 %", "Interval", null, 110), ("Muestra", "N", "N0", 80),
            ("Tú", "You", null, 120), ("", "Diff", null, 90));
        _arch = Ui.Grid(("Arquetipo", "Label", null, 130), ("Descripción", "Description", null, 0), ("Jugadores", "Members", null, 90), ("VPIP/PFR/3B/CB/WTSD", "Centro", null, 200));
        _arch.MaxHeight = 220;
    }

    protected override UIElement BuildBody()
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(Ui.Label("Formato", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        _scope = Math.Max(0, Array.IndexOf(new[] { "cash", "spin", "mtt" }, FilterBar.PreferredScope));
        bar.Children.Add(Ui.Combo(ScopeNames, _scope, i => { _scope = i; Load(); }, 130));
        bar.Children.Add(Ui.Btn("Reentrenar arquetipos", (_, _) => Retrain()));
        var root = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(bar);
        top.Children.Add(_info);
        top.Children.Add(Ui.Label("Arquetipos aprendidos de tus rivales", 14, true));
        top.Children.Add(_archInfo);
        top.Children.Add(_arch);
        top.Children.Add(Ui.Label("Tendencias de la población (todos tus rivales, sin ti)", 14, true, null, new Thickness(0, 12, 0, 6)));
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_grid);
        return root;
    }

    protected override async void Load()
    {
        int v = ++_version;
        _info.Text = "Calculando…";
        try
        {
            var formats = ScopeFormats[_scope];
            var key = Keys[_scope];
            var (rows, model) = await Task.Run(() => (PopulationReport.Build(Svc.Db, Svc.Engine, formats.Length == 0 ? null : formats), Svc.Archetypes.Cached(key)));
            if (v != _version) return;
            _grid.ItemsSource = rows.Select(r => new Row(r.Label + " · " + r.Description, r.Pop * 100, $"{r.PopLo * 100:0}–{r.PopHi * 100:0}", r.PopN,
                r.Hero is { } h ? $"{h * 100:0}% ({r.HeroN})" : "-", r.HeroDiffers ? (r.Hero > r.PopHi ? "▲ más" : "▼ menos") : "")).ToList();
            _info.Text = rows.Count == 0 ? "Aún no hay datos suficientes de este formato (mínimo 30 oportunidades por spot)." : $"{rows.Count} spots con muestra suficiente.";
            ShowModel(model, key);
        }
        catch (Exception ex) { _info.Text = "No se pudo calcular: " + ex.Message; }
    }

    void ShowModel(ArchetypeModel? model, string key)
    {
        if (model == null)
        {
            _arch.ItemsSource = null;
            _archInfo.Text = $"Todavía no hay modelo: hacen falta al menos 6 rivales con {ArchetypeModel.MinHands(key)} manos. Mientras tanto el HUD usa el clasificador fijo.";
            return;
        }
        _arch.ItemsSource = model.Clusters.OrderByDescending(c => c.Members)
            .Select(c => new ArchRow(c.Label, c.Description, c.Members, string.Join(" / ", new[] { 0, 1, 2, 3, 5 }.Select(i => $"{c.Centroid[i] * 100:0}")))).ToList();
        _archInfo.Text = $"Entrenado el {model.Trained:dd/MM/yyyy HH:mm}. Describe cómo han jugado, no es un consejo; el HUD añade «?» cuando dudaría entre dos.";
    }

    async void Retrain()
    {
        _archInfo.Text = "Entrenando…";
        var key = Keys[_scope];
        try
        {
            var m = await Task.Run(() => Svc.Archetypes.Ensure(key, force: true));
            ShowModel(m, key);
        }
        catch (Exception ex) { _archInfo.Text = "Error: " + ex.Message; }
    }
}
