using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Lista de tus manos con filtros; doble clic abre el replayer.</summary>
public sealed class HandsView : ViewBase
{
    public override string Title => "Manos";

    static readonly string[] ScopeNames = { "Todos", "Cash", "Torneos" };
    static readonly string[] ScopeKeys = { "all", "cash", "tournament" };

    readonly DataGrid _grid;
    readonly TextBlock _info = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 0, 0, 8));
    readonly TextBox _search = new() { Width = 170, Margin = new Thickness(0, 0, 14, 0) };
    ComboBox _tagBox = new();
    int _scope;
    bool _onlyShowdown;
    string? _tag;
    bool _loading;
    int _version;

    public HandsView(AppServices svc) : base(svc)
    {
        _search.Background = Theme.Panel;
        _search.Foreground = Theme.Text;
        _search.BorderBrush = Theme.Border;
        _search.CaretBrush = Theme.Text;
        _search.Padding = new Thickness(4, 3, 4, 3);
        _search.ToolTip = "Número de mano, cartas (Ac Kd) o posición";
        _search.TextChanged += (_, _) => { if (IsLoaded && !_loading) Refresh(); };

        _grid = Ui.Grid(("Mano", "HandId", null, 130), ("Fecha", "Time", "dd/MM/yy HH:mm", 120), ("Formato", "Format", null, 70),
            ("Cartas", "Cards", null, 80), ("Posición", "Position", null, 80), ("Neto (bb)", "NetBb", "0.0", 90), ("EV (bb)", "EvNetBb", "0.0", 90),
            ("Etiquetas", "Tags", null, 0));
        _grid.MouseDoubleClick += (_, _) => OpenSelected();
        _grid.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; } };

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(Ui.Label("Formato", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        bar.Children.Add(Ui.Combo(ScopeNames, 0, i => { _scope = i; Refresh(); }, 110));
        bar.Children.Add(Ui.Label("Etiqueta", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        _tagBox = Ui.Combo(new[] { "Todas" }, 0, i => { if (!_loading) { _tag = i <= 0 ? null : (string)_tagBox.Items[i]; Refresh(); } }, 140);
        bar.Children.Add(_tagBox);
        bar.Children.Add(Ui.Label("Buscar", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        bar.Children.Add(_search);
        bar.Children.Add(Ui.Check("Solo showdown", false, v => { _onlyShowdown = v; Refresh(); }));
        bar.Children.Add(Ui.Btn("Abrir en el replayer", (_, _) => OpenSelected()));

        var root = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(bar);
        top.Children.Add(_info);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_grid);
        Content = root;
    }

    void OpenSelected()
    {
        if (_grid.SelectedItem is HandListRow row) ReplayerLauncher.TryOpen(Svc, Window.GetWindow(this), row.HandId);
        else _info.Text = "Elige una mano de la lista (doble clic o Intro abren el replayer).";
    }

    public override async void Refresh()
    {
        int v = ++_version;
        try
        {
            var filter = new HandFilter(ScopeKeys[_scope], _tag, _search.Text, _onlyShowdown);
            var (rows, tags) = await Bg(() => (Svc.Analytics.ListHands(filter), Svc.Notes.AllTags()));
            if (v != _version) return;
            Show(rows, tags);
        }
        catch (Exception ex) { _info.Text = "No se pudo cargar: " + ex.Message; }
    }

    public void Show(IReadOnlyList<HandListRow> rows, IReadOnlyList<(string Tag, int Hands)> tags)
    {
        _loading = true;
        var selected = _tag;
        _tagBox.Items.Clear();
        _tagBox.Items.Add("Todas");
        foreach (var (t, n) in tags) _tagBox.Items.Add(t);
        _tagBox.SelectedIndex = selected != null && tags.Any(x => x.Tag == selected) ? tags.ToList().FindIndex(x => x.Tag == selected) + 1 : 0;
        _loading = false;

        _grid.ItemsSource = rows;
        _info.Text = rows.Count == 0
            ? "No hay manos con ese filtro."
            : $"{rows.Count} manos (las 500 más recientes como máximo) · doble clic: replayer · neto {rows.Sum(r => r.NetBb):+0.0;-0.0;0.0} bb";
    }
}
