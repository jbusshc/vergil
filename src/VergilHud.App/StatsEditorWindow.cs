using System.Windows;
using System.Windows.Controls;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Editor de estadísticas propias con el mini-lenguaje (DESIGN.md §6.2): numerador / denominador sobre los hechos de cada mano.</summary>
public sealed class StatsEditorWindow : Window
{
    readonly AppServices _svc;
    readonly List<StatDefinition> _defs;
    readonly ListBox _list = new() { Width = 190, Background = Theme.Panel, Foreground = Theme.Text, BorderBrush = Theme.Border };
    readonly TextBox _id = Box(160), _label = Box(90), _desc = Box(360), _num = Box(420), _den = Box(420);
    readonly ComboBox _format;
    readonly TextBlock _msg = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 8, 0, 0));
    readonly ListBox _facts = new() { Height = 150, Background = Theme.Panel, Foreground = Theme.Text, BorderBrush = Theme.Border, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
    static readonly string[] Formats = { "pct0", "pct1", "dec1", "dec2", "int" };
    bool _isNew = true;

    static TextBox Box(double w) => new()
    {
        Width = w, Margin = new Thickness(0, 0, 0, 8), Background = Theme.Panel, Foreground = Theme.Text, BorderBrush = Theme.Border,
        CaretBrush = Theme.Text, Padding = new Thickness(4, 3, 4, 3), HorizontalAlignment = HorizontalAlignment.Left,
    };

    public StatsEditorWindow(AppServices svc)
    {
        _svc = svc;
        Title = "Editor de estadísticas";
        Width = 880;
        Height = 640;
        Background = Theme.Bg;
        _defs = CustomStats.Load(svc.CustomStatsPath);
        _format = Ui.Combo(Formats, 0, _ => { }, 100);
        _list.SelectionChanged += (_, _) => { if (_list.SelectedIndex >= 0) Fill(_defs[_list.SelectedIndex]); };

        foreach (var c in FactCatalog.Columns) _facts.Items.Add(c.Name);
        _facts.MouseDoubleClick += (_, _) => { if (_facts.SelectedItem is string f) { _num.Text += (_num.Text.Length > 0 ? " " : "") + f; _num.Focus(); _num.CaretIndex = _num.Text.Length; } };

        var form = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        form.Children.Add(Ui.Label("Id (minúsculas, números y _)", 12, false, Theme.Muted));
        form.Children.Add(_id);
        form.Children.Add(Ui.Label("Etiqueta corta (la del panel)", 12, false, Theme.Muted));
        form.Children.Add(_label);
        form.Children.Add(Ui.Label("Descripción", 12, false, Theme.Muted));
        form.Children.Add(_desc);
        form.Children.Add(Ui.Label("Numerador (expresión sobre los hechos; vale 1 o 0 por mano)", 12, false, Theme.Muted));
        form.Children.Add(_num);
        form.Children.Add(Ui.Label("Denominador (oportunidades; por defecto 1 = todas las manos)", 12, false, Theme.Muted));
        form.Children.Add(_den);
        form.Children.Add(Ui.Label("Formato", 12, false, Theme.Muted));
        form.Children.Add(_format);
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Ui.Btn("Nueva", (_, _) => Clear()));
        buttons.Children.Add(Ui.Btn("Probar", (_, _) => Test()));
        buttons.Children.Add(Ui.Btn("Guardar", (_, _) => Save()));
        buttons.Children.Add(Ui.Btn("Borrar", (_, _) => Delete()));
        form.Children.Add(buttons);
        form.Children.Add(_msg);
        form.Children.Add(Ui.Label("Hechos disponibles (doble clic para añadir al numerador). Operadores: && || ! == != < <= > >= + - * in [..]", 12, false, Theme.Muted, new Thickness(0, 10, 0, 4)));
        form.Children.Add(_facts);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(_list, Dock.Left);
        root.Children.Add(_list);
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        RefreshList();
        Clear();
    }

    void RefreshList()
    {
        _list.Items.Clear();
        foreach (var d in _defs) _list.Items.Add($"{d.Label}  ({d.Id})");
    }

    void Clear()
    {
        _isNew = true;
        _list.SelectedIndex = -1;
        _id.Text = ""; _label.Text = ""; _desc.Text = ""; _num.Text = ""; _den.Text = "1";
        _format.SelectedIndex = 0;
        _id.IsEnabled = true;
        _msg.Text = "";
    }

    void Fill(StatDefinition d)
    {
        _isNew = false;
        _id.Text = d.Id; _label.Text = d.Label; _desc.Text = d.Description; _num.Text = d.Numerator; _den.Text = d.Denominator;
        _format.SelectedIndex = Math.Max(0, Array.IndexOf(Formats, d.Format));
        _id.IsEnabled = false;
        _msg.Text = "";
    }

    StatDefinition Read() => new()
    {
        Id = _id.Text.Trim(), Label = _label.Text.Trim(), Description = _desc.Text.Trim(), Numerator = _num.Text.Trim(),
        Denominator = string.IsNullOrWhiteSpace(_den.Text) ? "1" : _den.Text.Trim(), Format = Formats[Math.Max(0, _format.SelectedIndex)],
        Prior = new PriorSpec { Mean = 0.3 },
    };

    string? Validate(StatDefinition d) => CustomStats.Validate(d, _svc.Engine.Definitions.Select(x => x.Id).Concat(_defs.Select(x => x.Id)).Except(_isNew ? Array.Empty<string>() : new[] { d.Id }), _isNew);

    void Test()
    {
        var d = Read();
        var err = Validate(d);
        if (err != null) { _msg.Foreground = Theme.Red; _msg.Text = err; return; }
        try
        {
            var hero = _svc.Db.GetHeroName();
            if (hero == null) { _msg.Foreground = Theme.Green; _msg.Text = "La expresión es válida. Aún no hay manos tuyas para probarla."; return; }
            var tmp = Read();
            tmp.Id = "zz_test_" + Environment.TickCount;
            _svc.Engine.Register(tmp);
            var v = _svc.Engine.Get(hero, tmp.Id);
            _msg.Foreground = Theme.Green;
            _msg.Text = $"Válida. Tu valor: {tmp.FormatValue(v)}{(tmp.IsProportion ? "%" : "")} sobre {v.N} oportunidades.";
        }
        catch (Exception ex) { _msg.Foreground = Theme.Red; _msg.Text = ex.Message; }
    }

    void Save()
    {
        var d = Read();
        var err = Validate(d);
        if (err != null) { _msg.Foreground = Theme.Red; _msg.Text = err; return; }
        var i = _defs.FindIndex(x => x.Id == d.Id);
        if (i >= 0) _defs[i] = d; else _defs.Add(d);
        try
        {
            CustomStats.Save(_svc.CustomStatsPath, _defs);
            _svc.Engine.Register(d);
            _msg.Foreground = Theme.Green;
            _msg.Text = "Guardada. Ya puedes añadirla a un perfil en el Editor de HUD.";
            RefreshList();
        }
        catch (Exception ex) { _msg.Foreground = Theme.Red; _msg.Text = "No se pudo guardar: " + ex.Message; }
    }

    void Delete()
    {
        var i = _defs.FindIndex(x => x.Id == _id.Text.Trim());
        if (i < 0) return;
        _defs.RemoveAt(i);
        CustomStats.Save(_svc.CustomStatsPath, _defs);
        RefreshList();
        Clear();
        _msg.Foreground = Theme.Muted;
        _msg.Text = "Borrada (deja de estar disponible al reiniciar la aplicación).";
    }
}
