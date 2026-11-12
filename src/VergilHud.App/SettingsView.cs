using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Carpeta de historiales, importación, opciones del HUD y formatos con HUD en vivo.</summary>
public sealed class SettingsView : ViewBase
{
    public override string Title => "Ajustes";

    readonly TextBox _folder = new();
    readonly Button _startStop;
    readonly TextBlock _maintenance = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 4, 0, 0));
    readonly RadioButton _honest = new() { Content = "Honestos", GroupName = "mode", Margin = new Thickness(0, 0, 12, 0), Foreground = Theme.Text };
    readonly RadioButton _classic = new() { Content = "Clásicos (x/n)", GroupName = "mode", Margin = new Thickness(0, 0, 24, 0), Foreground = Theme.Text };
    bool _loading = true;

    public event Action? StartStopClicked;
    public event Action? WizardClicked;
    public event Action? ImportClicked;
    /// <summary>Cambió algo que afecta a lo que se pinta en las mesas.</summary>
    public event Action? HudOptionsChanged;

    public string FolderText
    {
        get => _folder.Text.Trim();
        set => _folder.Text = value;
    }

    public SettingsView(AppServices svc) : base(svc)
    {
        var s = svc.Settings;
        _folder.Background = Theme.Panel;
        _folder.Foreground = Theme.Text;
        _folder.BorderBrush = Theme.Border;
        _folder.CaretBrush = Theme.Text;
        _folder.Padding = new Thickness(4, 3, 4, 3);
        _folder.Text = !string.IsNullOrEmpty(s.HandHistoryFolder) ? s.HandHistoryFolder : AppSettings.GuessHandHistoryFolder() ?? "";

        _startStop = Ui.Btn("Iniciar", (_, _) => StartStopClicked?.Invoke(), 90);

        var root = new StackPanel { MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };

        // --- historiales ---
        var folderRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(Ui.Btn("Examinar…", (_, _) => Browse()));
        buttons.Children.Add(_startStop);
        DockPanel.SetDock(buttons, Dock.Right);
        folderRow.Children.Add(buttons);
        folderRow.Children.Add(_folder);

        var importRow = new WrapPanel();
        importRow.Children.Add(Ui.Btn("Importar historial antiguo…", (_, _) => ImportClicked?.Invoke()));
        importRow.Children.Add(Ui.Btn("Asistente de configuración…", (_, _) => WizardClicked?.Invoke()));
        importRow.Children.Add(Ui.Btn("Recalcular EV de all-ins y sesiones", async (_, _) =>
        {
            _maintenance.Text = "Calculando…";
            try
            {
                await Svc.Maintenance.RunNowAsync();
                _maintenance.Text = "Listo.";
            }
            catch (Exception ex) { _maintenance.Text = "Error: " + ex.Message; }
        }));

        importRow.Children.Add(Ui.Btn("Regenerar autonotas", async (_, _) =>
        {
            _maintenance.Text = "Regenerando autonotas…";
            try { var n = await Task.Run(() => Svc.AutoNotes.Rebuild()); _maintenance.Text = $"Listo: {n} autonotas."; }
            catch (Exception ex) { _maintenance.Text = "Error: " + ex.Message; }
        }));

        var history = new StackPanel();
        history.Children.Add(Ui.Label("Vigila esta carpeta mientras juegas e importa las manos nuevas. «Importar historial antiguo» lee de golpe todo lo que haya.",
            12, false, Theme.Muted, new Thickness(0, 0, 0, 8)));
        history.Children.Add(folderRow);
        history.Children.Add(importRow);
        history.Children.Add(_maintenance);
        root.Children.Add(Ui.Section("Historiales de manos", history));

        // --- HUD ---
        var hud = new StackPanel();
        var mode = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        mode.Children.Add(Ui.Label("Valores:", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        _honest.ToolTip = "Valor ajustado con la media de la población y barra de fiabilidad bajo cada número";
        _classic.ToolTip = "x/n en crudo, como los HUD tradicionales";
        _honest.IsChecked = s.HonestMode;
        _classic.IsChecked = !s.HonestMode;
        _honest.Click += (_, _) => Changed();
        _classic.Click += (_, _) => Changed();
        mode.Children.Add(_honest);
        mode.Children.Add(_classic);
        hud.Children.Add(mode);

        var opts = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        opts.Children.Add(Ui.Check("Héroe siempre abajo", s.RotateHeroToBottom, v => { s.RotateHeroToBottom = v; Changed(); }));
        opts.Children.Add(Ui.Check("Mostrar mi panel", s.ShowHeroPanel, v => { s.ShowHeroPanel = v; Changed(); }));
        opts.Children.Add(Ui.Check("Fijar paneles", s.LockPanels, v => { s.LockPanels = v; Changed(); }));
        opts.Children.Add(Ui.Label("Opacidad", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        var slider = new Slider { Width = 110, Minimum = 0.3, Maximum = 1, Value = s.PanelOpacity, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { if (!_loading) { s.PanelOpacity = Math.Round(e.NewValue, 2); Changed(); } };
        opts.Children.Add(slider);
        opts.Children.Add(new Button { Content = "Restablecer posiciones", Margin = new Thickness(16, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4),
            Background = Theme.Button, Foreground = Theme.Text, BorderBrush = Theme.ButtonBorder });
        ((Button)opts.Children[^1]).Click += (_, _) => { s.PanelOffsets.Clear(); Changed(); };
        hud.Children.Add(opts);

        var fmt = new WrapPanel();
        fmt.Children.Add(Ui.Label("HUD en vivo en:", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        fmt.Children.Add(Ui.Check("Cash", s.FormatPolicy.IsAllowed("cash"), v => { s.FormatPolicy.Allowed["cash"] = v; Changed(); }));
        fmt.Children.Add(Ui.Check("Spin & Go", s.FormatPolicy.IsAllowed("spin"), v => { s.FormatPolicy.Allowed["spin"] = v; Changed(); }));
        fmt.Children.Add(Ui.Check("MTT / SNG", s.FormatPolicy.IsAllowed("mtt"), v => { s.FormatPolicy.Allowed["mtt"] = s.FormatPolicy.Allowed["sng"] = v; Changed(); }));
        var zoom = Ui.Check("Zoom", s.FormatPolicy.IsAllowed("zoom"), v => { s.FormatPolicy.Allowed["zoom"] = v; Changed(); });
        zoom.ToolTip = "En Zoom la mesa cambia en cada mano: el HUD no se puede anclar. Sus manos se importan igualmente.";
        fmt.Children.Add(zoom);
        hud.Children.Add(fmt);
        hud.Children.Add(Ui.Label("Comprueba la lista de herramientas permitidas de PokerStars para cada formato.", 12, false, Theme.Muted, new Thickness(0, 6, 0, 0)));
        root.Children.Add(Ui.Section("HUD en mesa", hud));

        // Herramientas, copias y apariencia
        var tools = new StackPanel();
        var toolMsg = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 8, 0, 0));
        var row1 = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        row1.Children.Add(Ui.Btn("Editor de HUD…", (_, _) => new HudEditorWindow(Svc, () => HudOptionsChanged?.Invoke()) { Owner = Window.GetWindow(this) }.Show()));
        row1.Children.Add(Ui.Btn("Editor de estadísticas…", (_, _) => new StatsEditorWindow(Svc) { Owner = Window.GetWindow(this) }.Show()));
        row1.Children.Add(Ui.Btn("Abrir informe semanal", (_, _) => OpenWeekly(toolMsg)));
        tools.Children.Add(row1);
        var row2 = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        row2.Children.Add(Ui.Btn("Copia de seguridad ahora", (_, _) =>
        {
            try { var f = DbBackup.Create(Svc.Db, AppSettings.BackupsDir, DateTime.Now); toolMsg.Text = "Copia guardada en " + f; }
            catch (Exception ex) { toolMsg.Text = "No se pudo: " + ex.Message; AppLog.Error("Copia de seguridad", ex); }
        }));
        row2.Children.Add(Ui.Btn("Exportar configuración…", (_, _) => ExportConfig(toolMsg)));
        row2.Children.Add(Ui.Btn("Importar configuración…", (_, _) => ImportConfig(toolMsg)));
        row2.Children.Add(Ui.Btn("Pruebas de rendimiento", async (_, _) =>
        {
            toolMsg.Text = "Midiendo (unos segundos)…";
            try
            {
                var r = await Task.Run(() => Benchmarks.Run());
                toolMsg.Text = string.Join(Environment.NewLine, r.Select(x => $"{x.Name}: {(x.Value >= 1000 ? x.Value.ToString("N0") : x.Value.ToString("0.0"))} {x.Unit}" +
                    (x.Target is { } t ? $" (objetivo {t:N0}) {(x.Met == true ? "✔" : "✘")}" : "")));
            }
            catch (Exception ex) { toolMsg.Text = "Error: " + ex.Message; }
        }));
        tools.Children.Add(row2);
        var row3 = new WrapPanel();
        row3.Children.Add(Ui.Label("Idioma", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        row3.Children.Add(Ui.Combo(new[] { "Español", "English" }, s.Language == "en" ? 1 : 0, i => { if (!_loading) { s.Language = i == 1 ? "en" : "es"; s.Save(); toolMsg.Text = Loc.T("Se aplica al reiniciar la aplicación."); } }, 110));
        row3.Children.Add(Ui.Label("Tema", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        row3.Children.Add(Ui.Combo(new[] { "Oscuro", "Claro" }, s.Theme == "light" ? 1 : 0, i => { if (!_loading) { s.Theme = i == 1 ? "light" : "dark"; s.Save(); toolMsg.Text = Loc.T("Se aplica al reiniciar la aplicación."); } }, 110));
        row3.Children.Add(Ui.Check("Atajos Ctrl+Alt+H (ocultar paneles) y Ctrl+Alt+L (fijar paneles)", s.Hotkeys, v => { s.Hotkeys = v; s.Save(); toolMsg.Text = Loc.T("Se aplica al reiniciar la aplicación."); }));
        row3.Children.Add(Ui.Check("Mostrar el tilt en la barra de estado", s.ShowTilt, v => { s.ShowTilt = v; s.Save(); }));
        tools.Children.Add(row3);
        tools.Children.Add(toolMsg);
        root.Children.Add(Ui.Section("Herramientas y copias", tools));

        Content = Scroll(root);
        _loading = false;
    }

    void OpenWeekly(TextBlock msg)
    {
        try
        {
            var path = System.IO.Path.Combine(AppSettings.ReportsDir, WeeklyReport.FileName(DateTime.Now));
            if (!System.IO.File.Exists(path))
            {
                var hero = Svc.Db.GetHeroName();
                path = WeeklyReport.Generate(Svc.Db, Svc.Engine, Svc.Study, Svc.Leaks, Svc.LeakBaselines, AppSettings.ReportsDir, DateTime.Now);
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            msg.Text = "Informe: " + path;
        }
        catch (Exception ex) { msg.Text = "No se pudo abrir el informe: " + ex.Message; AppLog.Error("Informe semanal", ex); }
    }

    static string? ReadIfExists(string path) => System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;

    void ExportConfig(TextBlock msg)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "vergil-config.json", Filter = "Configuración (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Svc.Settings.Save();
            var baselines = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(AppSettings.ProfilesPath)!, "leak-baselines.json");
            var bundle = ConfigBundle.Capture(Svc.Db, ReadIfExists(AppSettings.SettingsPath), ReadIfExists(AppSettings.ProfilesPath), ReadIfExists(baselines), ReadIfExists(Svc.CustomStatsPath));
            System.IO.File.WriteAllText(dlg.FileName, bundle.ToJson());
            msg.Text = $"Exportado: {bundle.Notes.Count} notas, {bundle.Tags.Count} etiquetas, perfiles, estadísticas propias y ajustes.";
        }
        catch (Exception ex) { msg.Text = "No se pudo exportar: " + ex.Message; }
    }

    void ImportConfig(TextBlock msg)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Configuración (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var bundle = ConfigBundle.FromJson(System.IO.File.ReadAllText(dlg.FileName));
            if (bundle == null) { msg.Text = "El archivo no es una configuración válida de Vergil HUD."; return; }
            var dir = System.IO.Path.GetDirectoryName(AppSettings.ProfilesPath)!;
            if (bundle.Settings != null) System.IO.File.WriteAllText(AppSettings.SettingsPath, bundle.Settings);
            if (bundle.Profiles != null) System.IO.File.WriteAllText(AppSettings.ProfilesPath, bundle.Profiles);
            if (bundle.LeakBaselines != null) System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "leak-baselines.json"), bundle.LeakBaselines);
            if (bundle.CustomStats != null) System.IO.File.WriteAllText(Svc.CustomStatsPath, bundle.CustomStats);
            var (n, t) = bundle.ApplyToDb(Svc.Db);
            msg.Text = $"Importado: {n} notas y {t} etiquetas nuevas. Reinicia la aplicación para aplicar ajustes, perfiles y estadísticas.";
        }
        catch (Exception ex) { msg.Text = "No se pudo importar: " + ex.Message; }
    }

    void Changed()
    {
        if (_loading) return;
        Svc.Settings.HonestMode = _honest.IsChecked == true;
        Svc.Settings.Save();
        HudOptionsChanged?.Invoke();
    }

    void Browse()
    {
        var dlg = new OpenFolderDialog { Title = "Carpeta HandHistory de PokerStars" };
        if (Directory.Exists(_folder.Text)) dlg.InitialDirectory = _folder.Text;
        if (dlg.ShowDialog() == true) _folder.Text = dlg.FolderName;
    }

    public void SetRunning(bool running) => _startStop.Content = running ? "Detener" : "Iniciar";

    public override void Refresh() { }
}
