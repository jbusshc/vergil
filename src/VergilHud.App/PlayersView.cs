using System.Windows;
using System.Windows.Controls;
using VergilHud.Core;

namespace VergilHud.App;

public sealed class PlayersView : ViewBase
{
    public override string Title => "Jugadores";

    readonly TextBlock _hero = Ui.Label("Sin datos todavía", 15, true);
    readonly TextBox _search = new() { Width = 220, Margin = new Thickness(0, 0, 14, 0) };
    readonly TextBox _min = new() { Width = 60, Text = "1" };
    readonly DataGrid _grid;
    int _version;
    int _scope = Math.Max(0, Array.IndexOf(new[] { "cash", "spin", "mtt", "zoom" }, FilterBar.PreferredScope));
    static readonly string[] ScopeNames = { "Cash", "Spin & Go", "Torneos MTT/SNG", "Zoom", "Todo el dinero real", "Dinero ficticio" };
    static readonly string[][] ScopeFormats = { new[] { "cash" }, new[] { "spin" }, new[] { "mtt", "sng" }, new[] { "zoom" }, Array.Empty<string>(), Array.Empty<string>() };

    public PlayersView(AppServices svc) : base(svc)
    {
        foreach (var t in new[] { _search, _min })
        {
            t.Background = Theme.Panel;
            t.Foreground = Theme.Text;
            t.BorderBrush = Theme.Border;
            t.CaretBrush = Theme.Text;
            t.Padding = new Thickness(4, 3, 4, 3);
            t.TextChanged += (_, _) => { if (IsLoaded) Refresh(); };
        }
        _grid = Ui.Grid(("Jugador", "Player", null, 0), ("Perfil", "Profile", null, 140), ("Manos", "Hands", null, 70),
            ("VPIP", "VpipPct", "0.0", 60), ("PFR", "PfrPct", "0.0", 60), ("3Bet", "ThreeBetPct", "0.0", 60), ("F3B", "FoldTo3BetPct", "0.0", 60),
            ("Steal", "StealPct", "0.0", 60), ("FtS", "FoldToStealPct", "0.0", 60), ("CB", "CbetPct", "0.0", 60), ("FCB", "FoldToCbetPct", "0.0", 60),
            ("AF", "Af", "0.00", 60), ("AFq", "Afq", "0.0", 60), ("WTSD", "WtsdPct", "0.0", 65), ("W$SD", "WsdPct", "0.0", 65),
            ("bb/100", "BbPer100", "0.0", 70), ("Neto", "Net", "0.00", 80));

        _grid.MouseDoubleClick += (_, _) => { if (_grid.SelectedItem is PlayerStats ps) new PlayerCardWindow(Svc, ps.Player) { Owner = Window.GetWindow(this) }.Show(); };
        var heroBox = new Border { Background = Theme.Panel, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10), Child = _hero };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(Ui.Label("Buscar jugador", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        bar.Children.Add(_search);
        bar.Children.Add(Ui.Label("Tipo de juego", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        bar.Children.Add(Ui.Combo(ScopeNames, _scope, i => { _scope = i; if (IsLoaded) Refresh(); }, 150));
        bar.Children.Add(Ui.Label("Mín. manos", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        bar.Children.Add(_min);

        var root = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(heroBox);
        top.Children.Add(bar);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_grid);
        Content = root;
    }

    public override async void Refresh()
    {
        int v = ++_version;
        try
        {
            var filter = _search.Text.Trim();
            int.TryParse(_min.Text, out var min);
            int scope = _scope;
            var (rows, hero) = await Bg(() =>
            {
                var rows = Svc.Db.GetAllPlayerStats(filter, Math.Max(1, min), 2000, ScopeFormats[scope].Length == 0 ? null : ScopeFormats[scope], scope == 5);
                string? text = null;
                var heroes = Svc.Engine.HeroNames;
                if (heroes.Count > 0)
                {
                    var name = Svc.Db.GetHeroName() ?? heroes.First();
                    var s = Svc.Db.GetPlayerStats(name);
                    var hands = (int)Svc.Engine.Get(name, "vpip").Denominator;
                    var bb100 = Svc.Engine.Get(name, "bb100");
                    var who = heroes.Count > 1 ? $"{string.Join(" + ", heroes.OrderBy(x => x))} (unificados)" : name;
                    text = $"{who}  ·  {hands} manos  ·  {PlayerStats.Fmt(bb100.Value, "0.0")} bb/100  ·  " +
                           $"VPIP {PlayerStats.Fmt(s.VpipPct)} / PFR {PlayerStats.Fmt(s.PfrPct)} / 3Bet {PlayerStats.Fmt(s.ThreeBetPct)}";
                }
                return (rows, text);
            });
            if (v != _version) return;   // llegó una búsqueda más reciente
            _grid.ItemsSource = rows;
            if (hero != null) _hero.Text = hero;
        }
        catch (Exception ex) { _hero.Text = "No se pudo cargar: " + ex.Message; }
    }
}
