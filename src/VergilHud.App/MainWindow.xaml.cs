using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VergilHud.Core;

namespace VergilHud.App;

public partial class MainWindow : Window
{
    readonly AppSettings _settings = AppSettings.Load();
    readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly List<(ViewBase View, Button Nav)> _views = new();
    readonly ContentPresenter _content = new();
    readonly TextBlock _status = Ui.Label("Detenido", 11, false, Theme.Muted);
    readonly TextBlock _bannerText = Ui.Label("", 13, false, Theme.B("#F0D58A"));
    readonly Border _banner = new()
    {
        Background = Theme.B("#3A2F12"),
        BorderBrush = Theme.B("#8A6D1F"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(12, 6, 12, 6),
        Margin = new Thickness(16, 12, 16, 0),
        Visibility = Visibility.Collapsed,
    };
    /// <summary>Vista con la que arranca (parámetro --view).</summary>
    public static int InitialView { get; set; }

    AppServices? _svc;
    HandHistoryImporter? _importer;
    OverlayManager? _overlays;
    SettingsView? _settingsView;
    readonly Hotkeys _hotkeys = new();
    readonly DispatcherTimer _wellbeingTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    string? _tiltText;
    string? _dynamicsText;
    DateTime _lastHandsAt = DateTime.Now;
    DateTime _playStarted = DateTime.MinValue;
    bool _stallWarned;
    int _current;
    int _sessionImported;
    string? _lastError;
    volatile bool _dirty = true;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += (_, _) => Shutdown();
        _refreshTimer.Tick += (_, _) => OnTick();
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        _svc = new AppServices(_settings, AppSettings.DatabasePath, AppSettings.ProfilesPath);
        var svcRef = _svc;
        Task.Run(() => { foreach (var k in new[] { "cash", "spin", "mtt" }) { try { svcRef.Archetypes.Ensure(k); } catch { /* sin datos suficientes */ } } });
        _svc.Play.Changed += playing => Dispatcher.BeginInvoke(() =>
        {
            UpdateBanner();
            if (playing) { ReplayerLauncher.CloseAll(); _playStarted = DateTime.Now; _stallWarned = false; }
        });
        _importer = new HandHistoryImporter(_svc.Db);
        _importer.HandsImported += hands =>
        {
            _lastHandsAt = DateTime.Now;
            _stallWarned = false;
            foreach (var h in hands) _svc.Tracker.Update(h);
            _svc.Maintenance.Poke();
            Dispatcher.BeginInvoke(() =>
            {
                _sessionImported += hands.Count;
                _dirty = true;
                _overlays?.InvalidateAll();
            });
        };
        _importer.ImportError += msg => Dispatcher.BeginInvoke(() => _lastError = msg);
        _svc.Maintenance.Completed += () => Dispatcher.BeginInvoke(() => { _dirty = true; });
        _svc.Maintenance.Failed += ex => Dispatcher.BeginInvoke(() => _lastError = "Mantenimiento: " + ex.Message);

        _overlays = new OverlayManager(_svc);
        _overlays.Start();

        _settingsView = new SettingsView(_svc);
        _settingsView.StartStopClicked += ToggleImport;
        _settingsView.WizardClicked += RunWizard;
        _settingsView.ImportClicked += OpenBulkImport;
        _settingsView.HudOptionsChanged += () => { _settings.Save(); _overlays?.InvalidateAll(); };

        BuildShell();
        _refreshTimer.Start();
        StartBackground();

        if (!_settings.FirstRunDone) RunWizard();
        else if (Directory.Exists(_settingsView.FolderText)) StartImport();
        UpdateStatus();
    }

    // ---------- estructura ----------
    void BuildShell()
    {
        var svc = _svc!;
        var views = new ViewBase[]
        {
            new DashboardView(svc), new PlayersView(svc), new HandsView(svc), new ReportsView(svc), new ChartsView(svc),
            new SessionsView(svc), new TournamentsView(svc), new LeaksView(svc), new StudyView(svc), new NashView(svc), new PopulationView(svc), new WellbeingView(svc), new BankrollView(svc), new HelpView(svc), _settingsView!,
        };

        var nav = new StackPanel { Background = Theme.Nav, Width = 190 };
        nav.Children.Add(Ui.Label("Vergil HUD", 18, true, null, new Thickness(18, 18, 0, 18)));
        for (int i = 0; i < views.Length; i++)
        {
            int idx = i;
            var b = new Button
            {
                Content = Loc.T(views[i].Title),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(18, 9, 10, 9),
                Foreground = Theme.Text,
                Background = Theme.Nav,
                BorderThickness = new Thickness(0),
                FontSize = 14,
                ToolTip = Help.For(views[i].Title),
            };
            b.Click += (_, _) => Show(idx);
            nav.Children.Add(b);
            _views.Add((views[i], b));
        }

        _banner.Child = _bannerText;
        var statusBar = new Border { Background = Theme.Nav, Padding = new Thickness(16, 5, 16, 5), Child = _status };
        var right = new DockPanel();
        DockPanel.SetDock(_banner, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        right.Children.Add(_banner);
        right.Children.Add(statusBar);
        right.Children.Add(_content);

        var root = new DockPanel();
        DockPanel.SetDock(nav, Dock.Left);
        root.Children.Add(nav);
        root.Children.Add(right);
        Content = root;
        Show(Math.Clamp(InitialView, 0, _views.Count - 1));
    }

    void Show(int i)
    {
        _current = i;
        foreach (var (_, nav) in _views) nav.Background = Theme.Nav;
        _views[i].Nav.Background = Theme.NavSelected;
        _content.Content = _views[i].View;
        _views[i].View.Refresh();
    }

    // ---------- importación ----------
    void StartImport()
    {
        var folder = _settingsView!.FolderText;
        if (!Directory.Exists(folder))
        {
            MessageBox.Show(this, "La carpeta no existe.", "Vergil HUD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.HandHistoryFolder = folder;
        _settings.Save();
        _importer!.Start(folder);
        _settingsView.SetRunning(true);
        UpdateStatus();
    }

    void ToggleImport()
    {
        if (_importer is null) return;
        if (_importer.IsRunning)
        {
            _importer.Stop();
            _settingsView!.SetRunning(false);
            UpdateStatus();
        }
        else StartImport();
    }

    void OpenBulkImport()
    {
        var folder = _settingsView!.FolderText;
        if (!Directory.Exists(folder))
        {
            MessageBox.Show(this, "Elige primero una carpeta de historiales que exista.", "Vergil HUD", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var win = new ImportWindow(_importer!, folder) { Owner = this };
        win.ShowDialog();
        _dirty = true;
        _views[_current].View.Refresh();
    }

    void RunWizard()
    {
        var wiz = new FirstRunWindow(_settingsView!.FolderText) { Owner = this };
        wiz.ShowDialog();
        if (wiz.Folder != null) _settingsView.FolderText = wiz.Folder;
        if (wiz.Result == FirstRunResult.Cancelled) return;
        _settings.FirstRunDone = true;
        _settings.Save();
        if (!Directory.Exists(_settingsView.FolderText)) return;
        if (wiz.Result == FirstRunResult.OnlyNew) _importer!.SkipExisting(_settingsView.FolderText);
        StartImport();
    }

    // ---------- segundo plano: copias, informe semanal, bienestar, atajos ----------
    void StartBackground()
    {
        var svc = _svc!;
        Task.Run(() =>
        {
            try { if (DbBackup.CreateIfDue(svc.Db, AppSettings.BackupsDir, DateTime.Now) is { } f) AppLog.Info("Copia de seguridad: " + f); }
            catch (Exception ex) { AppLog.Error("Copia de seguridad", ex); }
            try
            {
                if (WeeklyReport.IsDue(svc.Db, DateTime.Now) && svc.Db.GetHeroName() != null)
                    AppLog.Info("Informe semanal: " + WeeklyReport.Generate(svc.Db, svc.Engine, svc.Study, svc.Leaks, svc.LeakBaselines, AppSettings.ReportsDir, DateTime.Now));
            }
            catch (Exception ex) { AppLog.Error("Informe semanal", ex); }
        });

        _wellbeingTimer.Tick += (_, _) => CheckWellbeing();
        _wellbeingTimer.Start();

        if (_settings.Hotkeys)
        {
            _hotkeys.Attach(this);
            _hotkeys.Add('H', () => { if (_overlays != null) _overlays.Hidden = !_overlays.Hidden; });
            _hotkeys.Add('G', () => _settings.CycleScope());
            _hotkeys.Add('L', () => { _settings.LockPanels = !_settings.LockPanels; _settings.Save(); _overlays?.InvalidateAll(); });
        }
    }

    async void CheckWellbeing()
    {
        var svc = _svc;
        if (svc == null) return;
        try
        {
            var hero = svc.Db.GetHeroName();
            if (hero == null) return;
            var cur = await Task.Run(() => TiltService.Current(svc.Db, svc.Engine, hero));
            if (cur is not { } c) { _tiltText = null; return; }
            var score = Tilt.Compute(c.Inputs);
            _tiltText = $"Tilt {score.Score} ({score.Level})";
            svc.TiltText = _tiltText;
            foreach (var alert in svc.Monitor.Check(c.Inputs, DateTime.Now, c.Start))
                ToastWindow.Show("Bienestar", alert);
        }
        catch (Exception ex) { AppLog.Error("Bienestar", ex); }
    }

    /// <summary>VPIP y PFR medios de los rivales sentados ahora en la mesa con la mano más reciente.</summary>
    string? ComputeDynamics()
    {
        var svc = _svc;
        if (svc == null || !svc.Play.IsPlaying) return null;
        var latest = svc.Tracker.Snapshot().Values.OrderByDescending(h => h.Time).FirstOrDefault();
        if (latest == null) return null;
        var opps = latest.Seats.Select(s => s.Player).Where(p => p != latest.Hero).ToList();
        var formats = ArchetypeModel.FormatsOf(ArchetypeModel.KeyForFormat(latest.Format));
        var d = TableDynamics.Of(svc.Engine, svc.Priors, opps, formats.Length == 0 ? null : formats);
        return d == null ? null : $"Mesa: VPIP {d.AvgVpip * 100:0}% · PFR {d.AvgPfr * 100:0}% ({d.Label})";
    }

    // ---------- estado ----------
    void OnTick()
    {
        UpdateStatus();
        if (!_dirty) return;
        _dirty = false;
        // Solo se repintan las vistas que cambian con las manos nuevas
        if (_views[_current].View is DashboardView or PlayersView or SessionsView or TournamentsView)
            _views[_current].View.Refresh();
    }

    void UpdateBanner()
    {
        var text = _svc?.Play.BannerText ?? "";
        _bannerText.Text = text;
        _banner.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateStatus()
    {
        if (_svc is null) return;
        var state = _importer?.IsRunning == true ? "Vigilando carpeta" : "Detenido";
        var errors = _svc.Db.GetImportErrorCount();
        _status.Text = $"{state}  ·  {_svc.Db.GetHandCount():N0} manos en la base de datos  ·  {_sessionImported:N0} nuevas en esta sesión  ·  " +
                       $"{_overlays?.ActiveCount ?? 0} mesas con HUD" + (errors > 0 ? $"  ·  {errors} manos no se pudieron leer" : "") +
                       (_lastError != null ? $"  ·  {_lastError}" : "");
        if (_settings.ShowTilt && _tiltText != null) _status.Text += "  ·  " + _tiltText;
        try { _dynamicsText = ComputeDynamics(); } catch { _dynamicsText = null; }
        if (_dynamicsText != null) _status.Text += "  ·  " + _dynamicsText;
        if (_svc.Play.IsPlaying)
        {
            var since = (DateTime.Now - (_lastHandsAt > _playStarted ? _lastHandsAt : _playStarted)).TotalMinutes;
            if (since >= 12)
            {
                _status.Text += $"  ·  ⚠ Hace {since:0} min que no entran manos: comprueba que PokerStars guarda el historial";
                if (!_stallWarned) { _stallWarned = true; ToastWindow.Show("Historial sin cambios", "Hay mesas abiertas pero no entran manos nuevas. Revisa que PokerStars esté guardando el historial."); }
            }
        }
        UpdateBanner();
    }

    void Shutdown()
    {
        _refreshTimer.Stop();
        _wellbeingTimer.Stop();
        _hotkeys.Dispose();
        _importer?.Dispose();
        _overlays?.Stop();
        _svc?.Dispose();
        _settings.Save();
    }
}
