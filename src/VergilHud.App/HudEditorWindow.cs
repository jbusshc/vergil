using System.Windows;
using System.Windows.Controls;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Editor visual de los perfiles del HUD (DESIGN.md §7.1): qué estadísticas ve cada formato, en qué líneas y con qué opciones.</summary>
public sealed class HudEditorWindow : Window
{
    static readonly string[] AllFormats = { "cash", "zoom", "mtt", "sng", "spin" };
    static readonly string[] Scopes = { "all", "cash", "tournament" };
    static readonly string[] Windows = { "total", "last100", "session" };

    readonly AppServices _svc;
    readonly Action _applied;
    readonly ComboBox _profiles;
    readonly TextBox _name = Box(180), _minSeats = Box(50), _maxSeats = Box(50), _sep = Box(40);
    readonly List<CheckBox> _formatChecks = new();
    readonly ComboBox _scope, _window, _statPick;
    readonly ListBox _lines = new() { Height = 120, Background = Theme.Panel, Foreground = Theme.Text, BorderBrush = Theme.Border };
    readonly ListBox _stats = new() { Height = 130, Background = Theme.Panel, Foreground = Theme.Text, BorderBrush = Theme.Border };
    readonly CheckBox _showLabel = new() { Content = "Etiqueta", Foreground = Theme.Text, Margin = new Thickness(0, 0, 12, 0) };
    readonly CheckBox _stackAware = new() { Content = "Según mi stack", Foreground = Theme.Text, Margin = new Thickness(0, 0, 12, 0), ToolTip = "Solo manos con el mismo stack que ahora" };
    readonly CheckBox _sizeSplit = new() { Content = "Pequeña·grande", Foreground = Theme.Text, ToolTip = "Muestra el valor ante apuestas pequeñas y grandes" };
    readonly TextBlock _msg = Ui.Label("", 12, false, Theme.Green, new Thickness(0, 8, 0, 0));
    bool _loading;

    HudProfile Profile => _svc.Profiles[Math.Max(0, _profiles.SelectedIndex)];
    HudLine? Line => _lines.SelectedIndex >= 0 && _lines.SelectedIndex < Profile.Lines.Count ? Profile.Lines[_lines.SelectedIndex] : null;
    HudStatRef? Stat => Line is { } l && _stats.SelectedIndex >= 0 && _stats.SelectedIndex < l.Stats.Count ? l.Stats[_stats.SelectedIndex] : null;

    static TextBox Box(double w) => new()
    {
        Width = w, Margin = new Thickness(0, 0, 12, 0), Background = Theme.Panel, Foreground = Theme.Text, BorderBrush = Theme.Border,
        CaretBrush = Theme.Text, Padding = new Thickness(4, 3, 4, 3),
    };

    public HudEditorWindow(AppServices svc, Action applied)
    {
        _svc = svc;
        _applied = applied;
        Title = "Editor de HUD";
        Width = 760;
        Height = 760;
        Background = Theme.Bg;

        _profiles = Ui.Combo(svc.Profiles.Select(p => p.Name), 0, _ => LoadProfile(), 260);
        _scope = Ui.Combo(new[] { "Todas las manos", "Solo cash", "Solo torneos" }, 0, i => { if (!_loading) Profile.DataScope = Scopes[i]; }, 150);
        _window = Ui.Combo(new[] { "Total", "Últimas 100", "Sesión" }, 0, i => { if (!_loading) Profile.Window = Windows[i]; }, 120);
        _statPick = Ui.Combo(svc.Engine.Definitions.OrderBy(d => d.Label).Select(d => $"{d.Label} · {d.Description}"), 0, _ => { }, 380);
        _name.TextChanged += (_, _) => { if (!_loading) Profile.Name = _name.Text; };
        _minSeats.TextChanged += (_, _) => { if (!_loading && int.TryParse(_minSeats.Text, out var v) && v is >= 2 and <= 10) Profile.MinSeats = v; };
        _maxSeats.TextChanged += (_, _) => { if (!_loading && int.TryParse(_maxSeats.Text, out var v) && v is >= 2 and <= 10) Profile.MaxSeats = v; };
        _sep.TextChanged += (_, _) => { if (!_loading && Stat is { } s) s.Sep = _sep.Text; };
        _showLabel.Click += (_, _) => { if (!_loading && Stat is { } s) s.ShowLabel = _showLabel.IsChecked == true; };
        _stackAware.Click += (_, _) => { if (!_loading && Stat is { } s) s.StackAware = _stackAware.IsChecked == true; };
        _sizeSplit.Click += (_, _) => { if (!_loading && Stat is { } s) s.SizeSplit = _sizeSplit.IsChecked == true; };
        _lines.SelectionChanged += (_, _) => { if (!_loading) FillStats(); };
        _stats.SelectionChanged += (_, _) => { if (!_loading) FillStatOptions(); };

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Ui.Label("Perfil de HUD", 13, true));
        var top = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        top.Children.Add(_profiles);
        top.Children.Add(Ui.Label("Nombre", 12, false, Theme.Muted, new Thickness(0, 0, 6, 0)));
        top.Children.Add(_name);
        root.Children.Add(top);

        var fm = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        fm.Children.Add(Ui.Label("Se usa en:", 12, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        foreach (var f in AllFormats)
        {
            var name = f;
            var cb = new CheckBox { Content = name, Foreground = Theme.Text, Margin = new Thickness(0, 0, 12, 0), Tag = name, VerticalAlignment = VerticalAlignment.Center };
            cb.Click += (_, _) =>
            {
                if (_loading) return;
                if (cb.IsChecked == true) { if (!Profile.Formats.Contains(name)) Profile.Formats.Add(name); } else Profile.Formats.Remove(name);
            };
            _formatChecks.Add(cb);
            fm.Children.Add(cb);
        }
        fm.Children.Add(Ui.Label("Asientos mín.", 12, false, Theme.Muted, new Thickness(12, 0, 6, 0)));
        fm.Children.Add(_minSeats);
        fm.Children.Add(Ui.Label("máx.", 12, false, Theme.Muted, new Thickness(0, 0, 6, 0)));
        fm.Children.Add(_maxSeats);
        root.Children.Add(fm);

        var data = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        data.Children.Add(Ui.Label("Datos", 12, false, Theme.Muted, new Thickness(0, 0, 6, 0)));
        data.Children.Add(_scope);
        data.Children.Add(Ui.Label("Ventana", 12, false, Theme.Muted, new Thickness(0, 0, 6, 0)));
        data.Children.Add(_window);
        root.Children.Add(data);

        root.Children.Add(Ui.Label("Líneas del panel (de arriba abajo)", 13, true));
        root.Children.Add(_lines);
        var lb = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        lb.Children.Add(Ui.Btn("Línea nueva", (_, _) => { Profile.Lines.Add(new HudLine()); FillLines(Profile.Lines.Count - 1); }));
        lb.Children.Add(Ui.Btn("Borrar línea", (_, _) => { if (Line is { } l) { Profile.Lines.Remove(l); FillLines(0); } }));
        lb.Children.Add(Ui.Btn("Subir", (_, _) => MoveLine(-1)));
        lb.Children.Add(Ui.Btn("Bajar", (_, _) => MoveLine(1)));
        root.Children.Add(lb);

        root.Children.Add(Ui.Label("Estadísticas de la línea elegida (de izquierda a derecha)", 13, true));
        root.Children.Add(_stats);
        var sb = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        sb.Children.Add(_statPick);
        sb.Children.Add(Ui.Btn("Añadir", (_, _) => AddStat()));
        sb.Children.Add(Ui.Btn("Quitar", (_, _) => RemoveStat()));
        sb.Children.Add(Ui.Btn("◀", (_, _) => MoveStat(-1), 36));
        sb.Children.Add(Ui.Btn("▶", (_, _) => MoveStat(1), 36));
        root.Children.Add(sb);
        var opts = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        opts.Children.Add(Ui.Label("Separador antes", 12, false, Theme.Muted, new Thickness(0, 0, 6, 0)));
        opts.Children.Add(_sep);
        opts.Children.Add(_showLabel);
        opts.Children.Add(_stackAware);
        opts.Children.Add(_sizeSplit);
        root.Children.Add(opts);

        var actions = new WrapPanel();
        actions.Children.Add(Ui.Btn("Guardar y aplicar", (_, _) => SaveAll()));
        actions.Children.Add(Ui.Btn("Restablecer perfiles de serie", (_, _) => Reset()));
        root.Children.Add(actions);
        root.Children.Add(_msg);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        LoadProfile();
    }

    string StatText(HudStatRef s)
    {
        var def = _svc.Engine.Has(s.Id) ? _svc.Engine.GetDefinition(s.Id).Label : s.Id;
        return $"{(s.Sep.Trim().Length > 0 ? s.Sep.Trim() + " " : "")}{def}{(s.StackAware ? " [stack]" : "")}{(s.SizeSplit ? " [pequeña·grande]" : "")}{(s.ShowLabel ? "" : " (solo valor)")}";
    }

    void LoadProfile()
    {
        _loading = true;
        var p = Profile;
        _name.Text = p.Name;
        _minSeats.Text = p.MinSeats.ToString();
        _maxSeats.Text = p.MaxSeats.ToString();
        foreach (var cb in _formatChecks) cb.IsChecked = p.Formats.Contains((string)cb.Tag);
        _scope.SelectedIndex = Math.Max(0, Array.IndexOf(Scopes, p.DataScope));
        _window.SelectedIndex = Math.Max(0, Array.IndexOf(Windows, p.Window));
        _loading = false;
        FillLines(0);
    }

    void FillLines(int select)
    {
        _loading = true;
        _lines.Items.Clear();
        foreach (var (l, i) in Profile.Lines.Select((l, i) => (l, i)))
            _lines.Items.Add($"{i + 1}:  " + (l.Stats.Count == 0 ? "(vacía)" : string.Join("  ", l.Stats.Select(StatText))));
        _lines.SelectedIndex = Profile.Lines.Count == 0 ? -1 : Math.Clamp(select, 0, Profile.Lines.Count - 1);
        _loading = false;
        FillStats();
    }

    void FillStats(int select = 0)
    {
        _loading = true;
        _stats.Items.Clear();
        if (Line is { } l) foreach (var s in l.Stats) _stats.Items.Add(StatText(s));
        _stats.SelectedIndex = Line == null || Line.Stats.Count == 0 ? -1 : Math.Clamp(select, 0, Line.Stats.Count - 1);
        _loading = false;
        FillStatOptions();
    }

    void FillStatOptions()
    {
        _loading = true;
        var s = Stat;
        _sep.Text = s?.Sep ?? "";
        _showLabel.IsChecked = s?.ShowLabel ?? false;
        _stackAware.IsChecked = s?.StackAware ?? false;
        _sizeSplit.IsChecked = s?.SizeSplit ?? false;
        _loading = false;
    }

    void RefreshLineText()
    {
        int sel = _lines.SelectedIndex;
        FillLines(sel);
    }

    void MoveLine(int d)
    {
        int i = _lines.SelectedIndex, j = i + d;
        if (i < 0 || j < 0 || j >= Profile.Lines.Count) return;
        (Profile.Lines[i], Profile.Lines[j]) = (Profile.Lines[j], Profile.Lines[i]);
        FillLines(j);
    }

    void AddStat()
    {
        if (Line is not { } l || _statPick.SelectedIndex < 0) return;
        var def = _svc.Engine.Definitions.OrderBy(d => d.Label).ElementAt(_statPick.SelectedIndex);
        l.Stats.Add(new HudStatRef { Id = def.Id, Sep = l.Stats.Count == 0 ? "" : " " });
        int line = _lines.SelectedIndex;
        FillLines(line);
        FillStats(l.Stats.Count - 1);
    }

    void RemoveStat()
    {
        if (Line is { } l && Stat is { } s) { l.Stats.Remove(s); RefreshLineText(); }
    }

    void MoveStat(int d)
    {
        if (Line is not { } l) return;
        int i = _stats.SelectedIndex, j = i + d;
        if (i < 0 || j < 0 || j >= l.Stats.Count) return;
        (l.Stats[i], l.Stats[j]) = (l.Stats[j], l.Stats[i]);
        int line = _lines.SelectedIndex;
        FillLines(line);
        FillStats(j);
    }

    void SaveAll()
    {
        try
        {
            HudProfiles.Save(AppSettings.ProfilesPath, _svc.Profiles);
            _applied();
            _msg.Foreground = Theme.Green;
            _msg.Text = "Perfiles guardados y aplicados a las mesas abiertas.";
        }
        catch (Exception ex) { _msg.Foreground = Theme.Red; _msg.Text = "No se pudo guardar: " + ex.Message; }
    }

    void Reset()
    {
        if (MessageBox.Show(this, "Se perderán tus cambios en los perfiles. ¿Seguir?", "Vergil HUD", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _svc.Profiles.Clear();
        _svc.Profiles.AddRange(HudProfiles.CreateDefaults());
        _profiles.Items.Clear();
        foreach (var p in _svc.Profiles) _profiles.Items.Add(p.Name);
        _profiles.SelectedIndex = 0;
        LoadProfile();
        SaveAll();
    }
}
