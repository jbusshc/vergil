using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using VergilHud.Core;

namespace VergilHud.App;

public enum FirstRunResult { Cancelled, ImportAll, OnlyNew }

/// <summary>
/// Asistente de primer arranque: comprueba la carpeta de historiales (que exista, que esté en inglés, qué formatos has jugado,
/// qué nick usas) y pregunta si se importa todo el historial antiguo o solo lo que se juegue a partir de ahora.
/// </summary>
public sealed class FirstRunWindow : Window
{
    readonly TextBox _folder = new();
    readonly TextBlock _report = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(230, 230, 235)), Margin = new Thickness(0, 10, 0, 0) };

    public FirstRunResult Result { get; private set; } = FirstRunResult.Cancelled;
    public string? Folder { get; private set; }

    public FirstRunWindow(string initialFolder)
    {
        Title = "Vergil HUD · Primer arranque";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(27, 28, 33));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = "Configuración inicial", FontSize = 18, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = "Vergil HUD lee los historiales de manos que guarda PokerStars en tu equipo. No se conecta a internet.",
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 170)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 12),
        });

        var row = new DockPanel();
        var browse = new Button { Content = "Examinar…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        DockPanel.SetDock(browse, Dock.Right);
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Carpeta HandHistory de PokerStars" };
            if (dlg.ShowDialog(this) == true) { _folder.Text = dlg.FolderName; Analyze(); }
        };
        row.Children.Add(browse);
        row.Children.Add(_folder);
        root.Children.Add(row);
        root.Children.Add(_report);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var all = new Button { Content = "Importar todo el historial", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var onlyNew = new Button { Content = "Solo manos nuevas", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Ahora no", Padding = new Thickness(12, 5, 12, 5), IsCancel = true };
        all.Click += (_, _) => Finish(FirstRunResult.ImportAll);
        onlyNew.Click += (_, _) => Finish(FirstRunResult.OnlyNew);
        buttons.Children.Add(all);
        buttons.Children.Add(onlyNew);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        _folder.Text = initialFolder;
        Loaded += (_, _) => Analyze();
    }

    void Finish(FirstRunResult r)
    {
        Result = r;
        Folder = _folder.Text.Trim();
        Close();
    }

    void Analyze()
    {
        var r = FirstRunCheck.Analyze(_folder.Text.Trim());
        var lines = new List<string>();
        if (r.FolderExists) lines.Add($"✔ Carpeta encontrada: {r.Files} archivo(s) de historial.");
        if (r.HandsFound > 0) lines.Add($"✔ Se han leído manos en inglés ({r.HandsFound} en los archivos más recientes).");
        if (r.Hero != null) lines.Add($"✔ Tu nick: {r.Hero}.");
        if (r.FormatsSeen.Count > 0) lines.Add("✔ Formatos detectados: " + string.Join(", ", r.FormatsSeen.Keys.Select(Describe)) + ".");
        foreach (var p in r.Problems) lines.Add("⚠ " + p);
        if (r.Ok && r.HandsFound > 0)
            lines.Add("\n«Importar todo» añade a las estadísticas todo el historial que ya tienes; «Solo manos nuevas» empieza de cero desde ahora.");
        _report.Text = string.Join("\n", lines);
    }

    static string Describe(string f) => f switch
    {
        "cash" => "cash",
        "spin" => "Spin & Go",
        "mtt" => "torneos",
        "zoom" => "Zoom",
        _ => f,
    };
}
