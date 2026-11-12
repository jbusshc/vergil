using System.Windows;
using System.Windows.Controls;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Lista de manos de un leak o desviación: doble clic abre el replayer; permite enviarlas al Estudio.</summary>
public sealed class HandListWindow : Window
{
    public HandListWindow(AppServices svc, string title, IReadOnlyList<HandListRow> rows, string source, string category)
    {
        Title = title;
        Width = 760;
        Height = 560;
        Background = Theme.Bg;
        var grid = Ui.Grid(("Mano", "HandId", null, 130), ("Fecha", "Time", "dd/MM/yy HH:mm", 120), ("Formato", "Format", null, 70),
            ("Cartas", "Cards", null, 80), ("Posición", "Position", null, 80), ("Neto (bb)", "NetBb", "0.0", 90), ("Etiquetas", "Tags", null, 0));
        grid.ItemsSource = rows;
        grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is HandListRow r) ReplayerLauncher.TryOpen(svc, this, r.HandId); };

        var info = Ui.Label($"{rows.Count} manos · doble clic para verlas en el replayer", 12, false, Theme.Muted, new Thickness(0, 0, 0, 8));
        var status = Ui.Label("", 12, false, Theme.Green, new Thickness(12, 0, 0, 0));
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(Ui.Btn("Enviar todas al Estudio", (_, _) =>
        {
            int n = 0;
            foreach (var r in rows) if (svc.Study.Add(r.HandId, source, category)) n++;
            status.Text = $"{n} manos nuevas en el Estudio.";
        }));
        bar.Children.Add(status);

        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel();
        top.Children.Add(info);
        top.Children.Add(bar);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(grid);
        Content = root;
    }
}
