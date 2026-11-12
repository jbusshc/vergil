using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Pestaña de notas de un jugador: nota libre, color y autonotas. Editable solo en ventanas que pueden recibir teclado.</summary>
public static class NotesPanel
{
    public static readonly Dictionary<string, Brush> ColorBrushes = new()
    {
        ["red"] = Theme.B("#E0554F"), ["orange"] = Theme.B("#E8913A"), ["yellow"] = Theme.B("#E6CF4A"), ["green"] = Theme.B("#4FB86B"),
        ["blue"] = Theme.B("#4F8EE0"), ["purple"] = Theme.B("#9A66D8"), ["grey"] = Theme.B("#8A8C96"),
    };

    public static UIElement Create(AppServices svc, string player, bool editable)
    {
        var note = svc.Notes.Get(player);
        string? color = note.ColorLabel;
        var root = new StackPanel { Margin = new Thickness(10) };

        var box = new TextBox
        {
            Text = note.Note ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxLength = 4000,
            IsReadOnly = !editable, Background = Theme.Panel, Foreground = Theme.Text, BorderBrush = Theme.Border, CaretBrush = Theme.Text,
            Padding = new Thickness(6),
        };
        var status = Ui.Label("", 11, false, Theme.Muted, new Thickness(0, 6, 0, 0));
        var colorRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        colorRow.Children.Add(Ui.Label("Color:", 12, false, Theme.Muted, new Thickness(0, 0, 8, 0)));

        void Paint()
        {
            foreach (var c in colorRow.Children.OfType<Button>())
                c.BorderThickness = new Thickness((string?)c.Tag == color ? 3 : 1);
        }
        foreach (var name in NotesService.Colors)
        {
            var n = name;
            var b = new Button { Tag = n, Width = 22, Height = 22, Margin = new Thickness(0, 0, 6, 0), Background = ColorBrushes[n], BorderBrush = Theme.Text, IsEnabled = editable, ToolTip = n };
            b.Click += (_, _) => { color = color == n ? null : n; Paint(); };
            colorRow.Children.Add(b);
        }
        Paint();

        root.Children.Add(colorRow);
        root.Children.Add(box);
        if (editable)
        {
            root.Children.Add(Ui.Btn("Guardar nota", (_, _) =>
            {
                try { svc.Notes.Set(player, box.Text, color); status.Text = "Guardada."; }
                catch (Exception ex) { status.Text = "No se pudo guardar: " + ex.Message; }
            }));
        }
        else root.Children.Add(Ui.Label("Para editar la nota abre al jugador desde la pestaña Jugadores (doble clic).", 11, false, Theme.Muted, new Thickness(0, 6, 0, 0)));
        root.Children.Add(status);

        root.Children.Add(Ui.Label("Autonotas (observaciones automáticas de sus manos)", 12, true, null, new Thickness(0, 12, 0, 4)));
        var auto = svc.AutoNotes.GetNotes(player, 40);
        if (auto.Count == 0) root.Children.Add(Ui.Label("Todavía no hay observaciones.", 12, false, Theme.Muted));
        foreach (var a in auto)
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            if (editable)
            {
                var id = a.HandId;
                var open = Ui.Btn("ver mano", (_, _) => ReplayerLauncher.TryOpen(svc, null, id));
                DockPanel.SetDock(open, Dock.Right);
                row.Children.Add(open);
            }
            row.Children.Add(Ui.Label($"{a.Created:dd/MM} · {a.Text}", 12));
            root.Children.Add(row);
        }
        return root;
    }
}
