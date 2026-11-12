using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Bienestar: tilt score de la sesión, tus límites, el diario y las correlaciones con tu juego (DESIGN.md §12 y §19.7).</summary>
public sealed class WellbeingView : GatedView
{
    public override string Title => "Bienestar";

    readonly TextBlock _tilt = Ui.Label("", 22, true);
    readonly TextBlock _reasons = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 2, 0, 10));
    readonly TextBlock _limitMsg = Ui.Label("", 12, false, Theme.Green);
    readonly TextBlock _journalMsg = Ui.Label("", 12, false, Theme.Green);
    readonly TextBlock _goalMsg = Ui.Label("", 12, false, Theme.Green);
    readonly StackPanel _corr = new();
    readonly StackPanel _goalBars = new();
    readonly TextBox _stop = Box(), _max = Box(), _break = Box(), _note = new() { Width = 360, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox _gHands = Box(), _gTour = Box(), _gRev = Box();
    ComboBox _mood = new(), _fatigue = new();

    public WellbeingView(AppServices svc) : base(svc) { }

    static TextBox Box() => new()
    {
        Width = 70, Margin = new Thickness(0, 0, 14, 0), Background = Theme.Panel, Foreground = Theme.Text, BorderBrush = Theme.Border,
        CaretBrush = Theme.Text, Padding = new Thickness(4, 3, 4, 3),
    };

    static UIElement Card(UIElement child) => new Border { Background = Theme.Panel, CornerRadius = new CornerRadius(6), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12), Child = child };

    protected override UIElement BuildBody()
    {
        var root = new StackPanel();

        var tiltBox = new StackPanel();
        tiltBox.Children.Add(Ui.Label("Tilt de la sesión actual", 13, false, Theme.Muted));
        tiltBox.Children.Add(_tilt);
        tiltBox.Children.Add(_reasons);
        tiltBox.Children.Add(Ui.Label("Es información sobre ti, no un consejo de juego. Combina cuánto se aleja tu estilo de hoy del habitual, las pérdidas, el tiempo sin pausa y las mesas a la vez.", 11, false, Theme.Muted));
        root.Children.Add(Card(tiltBox));

        var limits = new StackPanel();
        limits.Children.Add(Ui.Label("Límites que defines tú (avisan, nunca cierran mesas)", 14, true, null, new Thickness(0, 0, 0, 8)));
        var row = new WrapPanel();
        row.Children.Add(Ui.Label("Stop-loss (bb)", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        row.Children.Add(_stop);
        row.Children.Add(Ui.Label("Sesión máxima (min)", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        row.Children.Add(_max);
        row.Children.Add(Ui.Label("Pausa cada (min)", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        row.Children.Add(_break);
        row.Children.Add(Ui.Btn("Guardar límites", (_, _) => SaveLimits()));
        limits.Children.Add(row);
        limits.Children.Add(Ui.Label("Déjalos vacíos para no tener límite. Los avisos son una ventana discreta que no se roba el foco.", 11, false, Theme.Muted, new Thickness(0, 4, 0, 0)));
        limits.Children.Add(_limitMsg);
        root.Children.Add(Card(limits));

        var journal = new StackPanel();
        journal.Children.Add(Ui.Label("Diario y correlaciones", 14, true, null, new Thickness(0, 0, 0, 8)));
        var jrow = new WrapPanel();
        _mood = Ui.Combo(new[] { "-", "1", "2", "3", "4", "5" }, 0, _ => { }, 60);
        _fatigue = Ui.Combo(new[] { "-", "1", "2", "3", "4", "5" }, 0, _ => { }, 60);
        _note.Background = Theme.Panel; _note.Foreground = Theme.Text; _note.BorderBrush = Theme.Border; _note.CaretBrush = Theme.Text;
        jrow.Children.Add(Ui.Label("Ánimo (1-5)", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        jrow.Children.Add(_mood);
        jrow.Children.Add(Ui.Label("Cansancio (1-5)", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        jrow.Children.Add(_fatigue);
        jrow.Children.Add(Ui.Label("Nota", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        jrow.Children.Add(_note);
        jrow.Children.Add(Ui.Btn("Registrar al empezar", (_, _) => AddJournal("start")));
        jrow.Children.Add(Ui.Btn("Registrar al terminar", (_, _) => AddJournal("end")));
        journal.Children.Add(jrow);
        journal.Children.Add(_journalMsg);
        journal.Children.Add(_corr);
        root.Children.Add(Card(journal));

        var goals = new StackPanel();
        goals.Children.Add(Ui.Label("Objetivos semanales", 14, true, null, new Thickness(0, 0, 0, 8)));
        var grow = new WrapPanel();
        grow.Children.Add(Ui.Label("Manos", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        grow.Children.Add(_gHands);
        grow.Children.Add(Ui.Label("Torneos", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        grow.Children.Add(_gTour);
        grow.Children.Add(Ui.Label("Repasos de estudio", 13, false, Theme.Muted, new Thickness(0, 0, 8, 0)));
        grow.Children.Add(_gRev);
        grow.Children.Add(Ui.Btn("Guardar objetivos", (_, _) => SaveGoals()));
        goals.Children.Add(grow);
        goals.Children.Add(_goalMsg);
        goals.Children.Add(_goalBars);
        root.Children.Add(Card(goals));
        return Scroll(root);
    }

    static string Fmt(double? v) => v is { } x ? x.ToString("0.##", CultureInfo.InvariantCulture) : "";

    static double? Parse(string t) => double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    protected override async void Load()
    {
        var l = Svc.Settings.Limits;
        _stop.Text = Fmt(l.StopLossBb);
        _max.Text = Fmt(l.MaxSessionMinutes);
        _break.Text = Fmt(l.BreakEveryMinutes);
        var g = GoalService.Load(Svc.Db);
        _gHands.Text = g.WeeklyHands > 0 ? g.WeeklyHands.ToString() : "";
        _gTour.Text = g.WeeklyTournaments > 0 ? g.WeeklyTournaments.ToString() : "";
        _gRev.Text = g.WeeklyReviews > 0 ? g.WeeklyReviews.ToString() : "";
        ShowGoals(g);

        try
        {
            var hero = Svc.Db.GetHeroName();
            var (cur, rep) = await Task.Run(() =>
            {
                var c = hero == null ? null : TiltService.Current(Svc.Db, Svc.Engine, hero);
                return (c, CorrelationReport.Build(Svc.Db, Svc.Journal));
            });
            if (cur is { } t)
            {
                var score = Tilt.Compute(t.Inputs);
                _tilt.Text = $"{score.Score} · {score.Level}";
                _reasons.Text = score.Reasons.Count == 0 ? "Nada que destacar." : string.Join(" · ", score.Reasons);
            }
            else
            {
                _tilt.Text = "Sin sesión en curso";
                _reasons.Text = "El tilt se calcula con las manos de la sesión actual (la última, si fue hace menos de 30 minutos).";
            }
            _corr.Children.Clear();
            _corr.Children.Add(Ui.Label($"{rep.Sessions} sesiones registradas. Cada comparación necesita al menos {CorrelationReport.MinPerGroup} sesiones por grupo y se da con su intervalo del 90 %.",
                11, false, Theme.Muted, new Thickness(0, 8, 0, 4)));
            foreach (var m in rep.Messages) _corr.Children.Add(Ui.Label("• " + m, 12, false, null, new Thickness(0, 2, 0, 0)));
        }
        catch (Exception ex) { _reasons.Text = "No se pudo calcular: " + ex.Message; }
    }

    void SaveLimits()
    {
        var l = Svc.Settings.Limits;
        l.StopLossBb = Parse(_stop.Text);
        l.MaxSessionMinutes = Parse(_max.Text) is { } m ? (int)m : null;
        l.BreakEveryMinutes = Parse(_break.Text) is { } b ? (int)b : null;
        Svc.Settings.Save();
        Svc.Monitor.Reset();
        _limitMsg.Text = "Límites guardados.";
    }

    void AddJournal(string phase)
    {
        try
        {
            int? mood = _mood.SelectedIndex > 0 ? _mood.SelectedIndex : null, fat = _fatigue.SelectedIndex > 0 ? _fatigue.SelectedIndex : null;
            Svc.Journal.Add(phase, mood, fat, _note.Text.Trim());
            _note.Text = "";
            _journalMsg.Text = phase == "start" ? "Anotado el inicio de la sesión." : "Anotado el cierre de la sesión.";
            Load();
        }
        catch (Exception ex) { _journalMsg.Text = "No se pudo guardar: " + ex.Message; }
    }

    void SaveGoals()
    {
        var g = new Goals
        {
            WeeklyHands = Parse(_gHands.Text) is { } a ? (int)a : 0,
            WeeklyTournaments = Parse(_gTour.Text) is { } b ? (int)b : 0,
            WeeklyReviews = Parse(_gRev.Text) is { } c ? (int)c : 0,
        };
        GoalService.Save(Svc.Db, g);
        _goalMsg.Text = "Objetivos guardados.";
        ShowGoals(g);
    }

    void ShowGoals(Goals g)
    {
        _goalBars.Children.Clear();
        foreach (var p in GoalService.Progress(Svc.Db, g))
        {
            _goalBars.Children.Add(Ui.Label($"{Loc.T(p.Name)}: {p.Done} / {p.Target}", 12, false, null, new Thickness(0, 6, 0, 2)));
            _goalBars.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = p.Fraction, Height = 8, Foreground = p.Fraction >= 1 ? Theme.Green : Theme.Blue });
        }
    }
}
