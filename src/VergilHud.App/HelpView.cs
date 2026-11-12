using System.Windows;
using System.Windows.Controls;

namespace VergilHud.App;

/// <summary>Glosario: qué significa cada dato y cómo leerlo. Los mismos textos salen como tooltip al pasar el cursor.</summary>
public sealed class HelpView : ViewBase
{
    public override string Title => "Ayuda";

    readonly TextBox _search = new() { Width = 260, Margin = new Thickness(0, 0, 14, 0) };
    readonly StackPanel _list = new();

    public HelpView(AppServices svc) : base(svc)
    {
        _search.Background = Theme.Panel; _search.Foreground = Theme.Text; _search.BorderBrush = Theme.Border; _search.CaretBrush = Theme.Text;
        _search.Padding = new Thickness(4, 3, 4, 3);
        _search.TextChanged += (_, _) => { if (IsLoaded) Refresh(); };

        var intro = Ui.Label("Deja el cursor sobre cualquier dato, tarjeta, cabecera de tabla o estadística del HUD para ver qué significa y cómo interpretarlo. " +
                             "Aquí está todo junto; escribe para buscar.", 13, false, Theme.Muted, new Thickness(0, 0, 0, 10));
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        bar.Children.Add(Ui.Label("Buscar", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        bar.Children.Add(_search);
        var top = new StackPanel();
        top.Children.Add(intro);
        top.Children.Add(bar);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(Scroll(_list));
        Content = root;
    }

    public override void Refresh()
    {
        var q = _search.Text.Trim();
        _list.Children.Clear();
        foreach (var g in Help.Entries.GroupBy(e => e.Group))
        {
            var items = g.Where(e => q.Length == 0 || e.Term.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Text.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (items.Count == 0) continue;
            _list.Children.Add(Ui.Label(g.Key, 15, true, null, new Thickness(0, 12, 0, 6)));
            foreach (var e in items)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                row.Children.Add(Ui.Label(e.Term, 13, true, Theme.Blue));
                row.Children.Add(Ui.Label(e.Text, 12, false, Theme.Text, new Thickness(0, 1, 0, 0)));
                _list.Children.Add(row);
            }
        }
        if (_list.Children.Count == 0) _list.Children.Add(Ui.Label("No hay nada con ese texto.", 13, false, Theme.Muted));
    }
}
