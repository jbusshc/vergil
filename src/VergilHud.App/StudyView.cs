using System.Windows;
using System.Windows.Controls;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Estudio con repaso espaciado (DESIGN.md §11): ves la mano hasta tu decisión, respondes, se revela y te puntúas.</summary>
public sealed class StudyView : GatedView
{
    public override string Title => "Estudio";

    readonly TextBlock _stats = Ui.Label("", 13, false, null, new Thickness(0, 0, 0, 8));
    readonly TextBlock _msg = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 0, 0, 8));
    readonly TextBlock _categories = Ui.Label("", 12, false, Theme.Muted, new Thickness(0, 0, 0, 8));
    readonly ContentControl _session = new();
    ReviewCard? _card;
    string? _guess;

    public StudyView(AppServices svc) : base(svc) { }

    protected override UIElement BuildBody()
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(Ui.Btn("Empezar repaso", (_, _) => Next()));
        bar.Children.Add(Ui.Btn("Buscar manos para estudiar", (_, _) => Sync()));
        bar.Children.Add(Ui.Btn("Proponer las 5 manos de hoy", (_, _) => Daily()));
        var top = new StackPanel();
        top.Children.Add(_stats);
        top.Children.Add(_categories);
        top.Children.Add(bar);
        top.Children.Add(_msg);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(Scroll(_session));
        return root;
    }

    protected override void Load()
    {
        Sync(false);
        UpdateStats();
    }

    void Sync(bool announce = true)
    {
        try
        {
            int n = Svc.Study.SyncAutomatic();
            if (announce) _msg.Text = n == 0 ? "No hay manos nuevas con etiqueta «estudiar»/«error» ni botes grandes perdidos." : $"{n} manos nuevas en la cola.";
        }
        catch (Exception ex) { _msg.Text = "Error: " + ex.Message; }
        UpdateStats();
    }

    async void Daily()
    {
        _msg.Text = "Eligiendo manos…";
        try
        {
            var picks = await Task.Run(() =>
            {
                var hero = Svc.Db.GetHeroName();
                var leaks = hero == null ? new List<Leak>() : Svc.Leaks.Detect(hero, Svc.LeakBaselines);
                // Nash solo si la matriz ya está calculada (si no, la primera vez tardaría unos segundos)
                var nash = Svc.Nash.MatrixReady ? NashDeviations.Analyze(Svc.Db, Svc.Nash) : null;
                return DailyPicks.Get(Svc.Db, Svc.Study, leaks, Svc.Leaks, nash);
            });
            _msg.Text = picks.Count == 0
                ? "No hay manos nuevas que proponer."
                : "Hoy: " + string.Join(" · ", picks.Select(p => $"#{p.HandId} ({p.Reason})")) + ". Ya están en la cola.";
            UpdateStats();
        }
        catch (Exception ex) { _msg.Text = "Error: " + ex.Message; }
    }

    void UpdateStats()
    {
        var s = Svc.Study.GetStats();
        _stats.Text = $"{s.Pending} pendientes · {s.Total} en la cola · racha {s.Streak} {(s.Streak == 1 ? "día" : "días")} · {s.ReviewedToday} repasadas hoy";
        _categories.Text = s.ByCategory.Count == 0 ? "" : "Aciertos («Bien») por categoría: " +
            string.Join(" · ", s.ByCategory.Select(c => $"{(c.Category.Length == 0 ? "sin categoría" : c.Category)} {c.Good * 100 / Math.Max(1, c.Reviews)}% de {c.Reviews}"));
    }

    void Next()
    {
        _card = Svc.Study.GetDue(null, 1).FirstOrDefault();
        _guess = null;
        UpdateStats();
        if (_card == null)
        {
            _session.Content = null;
            _msg.Text = "No tienes manos pendientes. Etiqueta manos como «estudiar» o «error» en el replayer, o envía las de un leak.";
            return;
        }
        var hand = Svc.Db.GetHand(_card.HandId);
        if (hand == null)
        {
            Svc.Study.Remove(_card.HandId);
            Next();
            return;
        }
        _msg.Text = $"Mano #{hand.Id} ({(_card.Category.Length == 0 ? _card.Source : _card.Category)}). ¿Qué harías ahora?";
        var view = new ReplayerView(Svc, hand);
        int q = StudyService.QuestionStep(view.Replay);
        view.EnterQuiz(q);

        var answer = new WrapPanel { Margin = new Thickness(14, 0, 0, 8) };
        var chosen = Ui.Label("", 12, false, Theme.Yellow, new Thickness(8, 0, 0, 0));
        foreach (var g in new[] { "Fold", "Call / Check", "Bet / Raise pequeño", "Bet / Raise grande", "All-in" })
        {
            var guess = g;
            answer.Children.Add(Ui.Btn(guess, (_, _) => { _guess = guess; chosen.Text = "Tu respuesta: " + guess; }));
        }
        answer.Children.Add(chosen);

        var reveal = new StackPanel { Margin = new Thickness(14, 0, 0, 14) };
        var grade = new WrapPanel { Visibility = Visibility.Collapsed };
        var outcome = Ui.Label("", 13, true, Theme.Blue, new Thickness(0, 0, 0, 6));
        outcome.Visibility = Visibility.Collapsed;
        var revealBtn = Ui.Btn("Revelar lo que hiciste", (_, _) =>
        {
            var steps = view.Replay.Steps;
            int actual = Math.Min(q + 1, steps.Count - 1);
            view.Reveal(actual);
            outcome.Text = (_guess != null ? $"Tú dijiste: {_guess}. " : "") + "En la mano: " + steps[actual].Description + ". " + steps[^1].Description + ".";
            outcome.Visibility = Visibility.Visible;
            grade.Visibility = Visibility.Visible;
        });
        reveal.Children.Add(revealBtn);
        reveal.Children.Add(outcome);
        void Grade(StudyGrade gr)
        {
            Svc.Study.Review(_card!.HandId, gr, _guess);
            Next();
        }
        grade.Children.Add(Ui.Label("¿Cómo lo valoras?", 13, false, Theme.Muted, new Thickness(0, 0, 10, 0)));
        grade.Children.Add(Ui.Btn("Mal", (_, _) => Grade(StudyGrade.Bad), 70));
        grade.Children.Add(Ui.Btn("Dudoso", (_, _) => Grade(StudyGrade.Doubtful), 70));
        grade.Children.Add(Ui.Btn("Bien", (_, _) => Grade(StudyGrade.Good), 70));
        reveal.Children.Add(grade);

        var panel = new StackPanel();
        panel.Children.Add(answer);
        panel.Children.Add(reveal);
        panel.Children.Add(view);
        _session.Content = panel;
    }
}
