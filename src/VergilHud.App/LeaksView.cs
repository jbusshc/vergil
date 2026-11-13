using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Detector de leaks (DESIGN.md §10): solo marca lo que está fuera del rango objetivo con todo el intervalo.</summary>
public sealed class LeaksView : GatedView
{
    public override string Title => "Leaks";

    static readonly string[] ScopeNames = { "Cash", "Spin & Go", "Torneos MTT/SNG", "Todos" };
    static readonly string[][] ScopeFormats = { new[] { "cash" }, new[] { "spin" }, new[] { "mtt", "sng" }, Array.Empty<string>() };

    readonly StackPanel _list = new();
    readonly TextBlock _info = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 0, 0, 10));
    int _scope;
    int _version;

    public LeaksView(AppServices svc) : base(svc) { }

    protected override UIElement BuildBody()
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(Ui.Label("Formato", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        _scope = Math.Max(0, Array.IndexOf(new[] { "cash", "spin", "mtt" }, FilterBar.PreferredScope));
        bar.Children.Add(Ui.Combo(ScopeNames, _scope, i => { _scope = i; Load(); }, 130));
        bar.Children.Add(Ui.Btn("Recalcular", (_, _) => Load()));
        var root = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(bar);
        top.Children.Add(_info);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(Scroll(_list));
        return root;
    }

    protected override async void Load()
    {
        int v = ++_version;
        _info.Text = "Calculando…";
        try
        {
            var hero = Svc.Db.GetHeroName();
            if (hero == null) { _info.Text = "Todavía no hay manos tuyas importadas."; _list.Children.Clear(); return; }
            var formats = ScopeFormats[_scope];
            var leaks = await Task.Run(() => Svc.Leaks.Detect(hero, Svc.LeakBaselines, formats.Length == 0 ? null : formats));
            if (v != _version) return;
            Show(leaks);
            var balance = await Task.Run(() => BalanceReport.Build(Svc.Db));
            if (v != _version) return;
            ShowBalance(balance);
        }
        catch (Exception ex) { _info.Text = "No se pudo calcular: " + ex.Message; }
    }

    void ShowBalance(BalanceReport rep)
    {
        var box = new StackPanel { Margin = new Thickness(12) };
        box.Children.Add(Ui.Label("¿Cómo construyes tus apuestas?", 14, true));
        box.Children.Add(Ui.Label($"Fuerza de mano en tus apuestas postflop ({rep.Hands} manos con cartas conocidas). Solo habla de tu juego, no del de los rivales.",
            12, false, Theme.Muted, new Thickness(0, 2, 0, 6)));
        if (rep.Rows.Count == 0) box.Children.Add(Ui.Label("Aún no hay apuestas postflop tuyas con cartas conocidas.", 12, false, Theme.Muted));
        foreach (var r in rep.Rows)
            box.Children.Add(Ui.Label($"{(r.Street == "f" ? "Flop" : r.Street == "t" ? "Turn" : "River")} · {r.Bucket} ({r.N}): " +
                $"fuerte {r.Share(Strength.Value) * 100:0}% · medio {r.Share(Strength.Medium) * 100:0}% · marginal {r.Share(Strength.Marginal) * 100:0}% · " +
                $"proyecto {r.Share(Strength.Draw) * 100:0}% · aire {r.Share(Strength.Air) * 100:0}% · {r.AvgNetBb:+0.0;-0.0;0.0} bb/mano", 12));
        foreach (var f in rep.Findings) box.Children.Add(Ui.Label("⚠ " + f, 12, true, Theme.Yellow, new Thickness(0, 4, 0, 0)));
        _list.Children.Add(new Border { Background = Theme.Panel, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 10), Child = box });
    }

    public void Show(IReadOnlyList<Leak> leaks)
    {
        _list.Children.Clear();
        _info.Text = leaks.Count == 0
            ? "No hay leaks con la muestra actual: ninguna estadística se sale del rango objetivo con todo su intervalo del 90 %. " +
              "Con pocas manos es lo normal; el detector no acusa sin pruebas."
            : $"{leaks.Count} leaks, ordenados por lo que te cuestan. Los rangos objetivo son orientativos (regular de NL baja 6-max) y puedes editarlos en leak-baselines.json (carpeta de datos de Vergil HUD).";
        foreach (var l in leaks)
        {
            var card = new StackPanel { Margin = new Thickness(12) };
            card.Children.Add(Ui.Label(LeakDetector.Describe(l), 14, true));
            card.Children.Add(Ui.Label($"Estás {l.Direction} del rango. En esos spots haces {l.SpotNetBb:+0.00;-0.00;0.00} bb por mano; " +
                                       $"impacto sobre tu winrate: {l.ImpactBb100:+0.0;-0.0;0.0} bb/100.", 12, false, Theme.Muted, new Thickness(0, 4, 0, 0)));
            if (!string.IsNullOrEmpty(l.Baseline.Hint))
                card.Children.Add(Ui.Label(l.Baseline.Hint, 12, false, Theme.Muted, new Thickness(0, 2, 0, 0)));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var leak = l;
            buttons.Children.Add(Ui.Btn("Ver manos", (_, _) =>
            {
                var rows = Svc.Analytics.ListHands(Svc.Leaks.SpotFilter(leak) with { Limit = 300 });
                new HandListWindow(Svc, "Manos del leak: " + leak.Baseline.Label, rows, "leak", leak.Baseline.Id) { Owner = Window.GetWindow(this) }.Show();
            }));
            card.Children.Add(buttons);
            _list.Children.Add(new Border { Background = Theme.Panel, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 10), Child = card });
        }
    }
}
