using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Importación masiva del historial antiguo, con barra de progreso, velocidad y cancelación.</summary>
public sealed class ImportWindow : Window
{
    readonly HandHistoryImporter _importer;
    readonly string _folder;
    readonly ProgressBar _bar = new() { Height = 18, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 12, 0, 8) };
    readonly TextBlock _phase = Ui.Label("Listo para importar.", 14, true);
    readonly TextBlock _detail = Ui.Label("", 12, false, Theme.Muted);
    readonly Button _start;
    readonly Button _cancel;
    CancellationTokenSource? _cts;

    public ImportSummary? Result { get; private set; }

    public ImportWindow(HandHistoryImporter importer, string folder)
    {
        _importer = importer;
        _folder = folder;
        Title = "Importar historial";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Theme.Bg;
        Foreground = Theme.Text;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        _start = Ui.Btn("Importar", async (_, _) => await RunAsync(), 100);
        _cancel = Ui.Btn("Cerrar", (_, _) => { if (_cts != null) _cts.Cancel(); else Close(); }, 90);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_start);
        buttons.Children.Add(_cancel);

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(Ui.Label("Importar historial antiguo", 18, true));
        root.Children.Add(Ui.Label(folder, 12, false, Theme.Muted, new Thickness(0, 4, 0, 0)));
        root.Children.Add(_bar);
        root.Children.Add(_phase);
        root.Children.Add(_detail);
        root.Children.Add(Ui.Label("Puede tardar unos minutos con muchos miles de manos. Puedes cancelar y reanudar más tarde: lo ya importado se conserva.",
            11, false, Theme.Muted, new Thickness(0, 10, 0, 0)));
        root.Children.Add(buttons);
        Content = root;

        Closing += (_, e) => { if (_cts != null) { _cts.Cancel(); e.Cancel = true; } };   // no se cierra a mitad: primero se cancela
    }

    async Task RunAsync()
    {
        _start.IsEnabled = false;
        _cancel.Content = "Cancelar";
        _cts = new CancellationTokenSource();
        var progress = new Progress<ImportProgress>(p =>
        {
            var ci = CultureInfo.InvariantCulture;
            _phase.Text = p.Phase;
            if (p.Phase == "Importando")
            {
                _bar.IsIndeterminate = false;
                _bar.Value = p.FilesTotal == 0 ? 0 : p.FilesDone * 100.0 / p.FilesTotal;
                _detail.Text = $"{p.FilesDone}/{p.FilesTotal} archivos · {p.HandsImported:N0} manos · {p.HandsPerSecond:N0} manos/s" +
                               (p.CurrentFile != null ? $"\n{p.CurrentFile}" : "");
            }
            else if (p.Phase is "Calculando EV de los all-in" or "Detectando sesiones")
            {
                _bar.IsIndeterminate = true;
                _detail.Text = $"{p.HandsImported:N0} manos importadas en {p.Elapsed.TotalSeconds:0} s";
            }
        });
        try
        {
            var token = _cts.Token;
            Result = await Task.Run(() => _importer.ImportFolder(_folder, progress, token));
            _bar.IsIndeterminate = false;
            _bar.Value = Result.Cancelled ? _bar.Value : 100;
            _phase.Text = Result.Cancelled ? "Cancelado" : "Terminado";
            _detail.Text = $"{Result.Hands:N0} manos nuevas en {Result.Elapsed.TotalSeconds:0.0} s ({Result.HandsPerSecond:N0} manos/s)" +
                           (Result.Errors > 0 ? $" · {Result.Errors} manos no se pudieron leer" : "");
        }
        catch (Exception ex)
        {
            _phase.Text = "Error";
            _detail.Text = ex.Message;
        }
        finally
        {
            _cts = null;
            _cancel.Content = "Cerrar";
            _start.IsEnabled = true;
            _start.Content = "Volver a importar";
        }
    }
}
