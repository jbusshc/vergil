using VergilHud.Core;
using Xunit;

namespace VergilHud.Tests;

public class TiltTests
{
    static TiltInputs Calm() => new(100, 0.25, 0.17, 0.25, 0.17, 5, 30, 40, 2);

    [Fact]
    public void A_calm_session_scores_low()
    {
        var s = Tilt.Compute(Calm());
        Assert.True(s.Score < 10);
        Assert.Equal("Tranquilo", s.Level);
        Assert.Empty(s.Reasons);
    }

    [Fact]
    public void Losses_style_drift_no_breaks_and_many_tables_raise_the_score_with_reasons()
    {
        var s = Tilt.Compute(new TiltInputs(200, 0.45, 0.30, 0.25, 0.17, -80, 150, 170, 6));
        Assert.True(s.Score >= 75, s.Score.ToString());
        Assert.Equal("Muy alto", s.Level);
        Assert.Contains(s.Reasons, r => r.Contains("VPIP"));
        Assert.Contains(s.Reasons, r => r.Contains("bb en la sesión"));
        Assert.Contains(s.Reasons, r => r.Contains("Sin pausa"));
        Assert.Contains(s.Reasons, r => r.Contains("6 mesas"));
    }

    [Fact]
    public void Style_needs_a_minimum_of_hands_and_the_score_is_capped_at_100()
    {
        var few = Tilt.Compute(new TiltInputs(10, 0.9, 0.9, 0.2, 0.15, 0, 0, 5, 1));
        Assert.Equal(0, few.Score);
        var max = Tilt.Compute(new TiltInputs(500, 1, 1, 0.2, 0.1, -500, 600, 600, 12));
        Assert.Equal(100, max.Score);
    }

    [Fact]
    public void Monitor_fires_each_limit_once_and_reminds_about_breaks_periodically()
    {
        var m = new WellbeingMonitor(new WellbeingLimits { StopLossBb = 50, MaxSessionMinutes = 120, BreakEveryMinutes = 60 });
        var t0 = new DateTime(2026, 6, 1, 20, 0, 0);
        Assert.Empty(m.Check(Calm(), t0.AddMinutes(30), t0));

        var lost = Calm() with { NetBb = -55 };
        var a = m.Check(lost, t0.AddMinutes(40), t0);
        Assert.Single(a);
        Assert.Contains("stop-loss", a[0]);
        Assert.Empty(m.Check(lost, t0.AddMinutes(41), t0));                // no repite

        var longRun = Calm() with { NetBb = 0, MinutesNoBreak = 65, SessionMinutes = 65 };
        Assert.Single(m.Check(longRun, t0.AddMinutes(65), t0));            // pausa a los 60
        Assert.Empty(m.Check(longRun, t0.AddMinutes(70), t0));
        var very = Calm() with { MinutesNoBreak = 125, SessionMinutes = 125 };
        var b = m.Check(very, t0.AddMinutes(125), t0);
        Assert.Equal(2, b.Count);                                          // límite de sesión + nuevo recordatorio de pausa

        m.Reset();
        Assert.Single(m.Check(lost, t0.AddMinutes(200), t0.AddMinutes(150)));
    }

    [Fact]
    public void Limits_that_are_not_set_never_alert()
    {
        var m = new WellbeingMonitor(new WellbeingLimits());
        Assert.Empty(m.Check(new TiltInputs(500, 1, 1, 0.2, 0.1, -500, 600, 600, 12), DateTime.Now, DateTime.Now.AddHours(-10)));
    }

    [Fact]
    public void Current_session_comes_from_the_latest_hands_and_ignores_old_ones()
    {
        var t0 = new DateTime(2026, 6, 1, 20, 0, 0);
        var old = Enumerable.Range(0, 20).Select(i => Synth.PreHand(100 + i, t0.AddDays(-3).AddMinutes(i), "Hero", true)).ToList();
        var today = Enumerable.Range(0, 40).Select(i => Synth.PreHand(200 + i, t0.AddMinutes(i), "Hero", true)).ToList();
        using var db = Synth.Db(old, today);
        var engine = new StatEngine(db);
        var cur = TiltService.Current(db, engine, "Hero", t0.AddMinutes(41))!.Value;
        Assert.Equal(40, cur.Inputs.Hands);
        Assert.Equal(t0, cur.Start);
        Assert.Equal(1, cur.Inputs.SessionVpip, 6);                        // el héroe siempre sube
        Assert.InRange(cur.Inputs.SessionMinutes, 40, 42);
        Assert.InRange(cur.Inputs.MinutesNoBreak, 40, 42);                 // manos cada minuto: sin pausas
        Assert.Null(TiltService.Current(db, engine, "Hero", t0.AddHours(5)));  // ya no hay sesión en curso
        Assert.NotEmpty(TiltService.ScoreHistory(db, engine, "Hero"));
    }
}

public class BankrollTests
{
    [Fact]
    public void Closed_form_risk_of_ruin_matches_a_simulation()
    {
        // μ = 4 bb/100, σ = 80 bb/100, 3000 bb → exp(-2·4·3000/6400) ≈ 2,4 %
        double closed = RiskOfRuin.Closed(4, 80, 3000);
        Assert.InRange(closed, 0.02, 0.03);
        // Caso con riesgo alto para que la simulación sea fiable con pocas rondas
        double c2 = RiskOfRuin.Closed(4, 80, 500);
        double sim = RiskOfRuin.Simulate(4, 80, 500, steps: 4000, runs: 1500);
        Assert.InRange(sim, c2 - 0.06, c2 + 0.06);
        Assert.Equal(1, RiskOfRuin.Closed(-1, 80, 3000));
        Assert.Equal(1, RiskOfRuin.Closed(4, 80, 0));
    }

    [Fact]
    public void Range_cannot_exclude_a_losing_player_with_a_small_sample()
    {
        var r = RiskOfRuin.Range(5, 80, 10, 2000, "cash");           // 10 bloques: error estándar enorme
        Assert.Equal(1, r.High, 9);
        Assert.Contains("no se puede descartar", r.Message);
        var big = RiskOfRuin.Range(5, 80, 2000, 4000, "cash");
        Assert.True(big.High < 0.1 && big.Low < big.High);
        Assert.Contains("entre", big.Message);
        Assert.Contains("Aún no hay datos", RiskOfRuin.Range(5, 80, 2, 2000, "torneos").Message);
    }

    [Fact]
    public void Transactions_balance_buyins_and_validation()
    {
        using var db = new HudDatabase(":memory:");
        var bank = new BankrollService(db);
        bank.Add("deposit", 100, "inicial");
        bank.Add("withdrawal", 30);
        bank.Add("rakeback", 5);
        bank.Add("bonus", 10, "bono");
        var s = bank.Summarize();
        Assert.Equal(85, s.Balance);
        Assert.Equal(15, s.Extras);                                  // rakeback y bonos aparte del resultado de juego
        Assert.Null(s.CashRisk);
        Assert.Equal(4, bank.List().Count);

        Assert.Throws<ArgumentException>(() => bank.Add("regalo", 5));
        Assert.Throws<ArgumentException>(() => bank.Add("deposit", -5));
        bank.Delete(bank.List().First().Id);
        Assert.Equal(3, bank.List().Count);
    }

    [Fact]
    public void Cash_results_count_in_the_balance_and_the_minimum_buyins_alert_works()
    {
        // 600 manos: el héroe gana 3 de 2 bb por mano → neto 1,5 bb/mano
        var hands = Enumerable.Range(0, 600).Select(i => Synth.PreHand(10_000 + i, new DateTime(2026, 5, 1, 10, 0, 0).AddMinutes(i), "Hero", false)).ToList();
        using var db = Synth.Db(hands);
        var bank = new BankrollService(db);
        bank.Add("deposit", 200);
        var s = bank.Summarize();
        Assert.True(s.CashResult > 0);
        Assert.Equal(200 + s.CashResult, s.Balance, 6);
        Assert.Equal(200.0, s.AvgCashBuyin!.Value, 6);               // 100 bb de 2
        Assert.True(s.CashBuyins > 1);
        Assert.False(s.BelowMinimum(1));
        Assert.True(s.BelowMinimum(s.CashBuyins!.Value + 1));
        Assert.NotNull(s.CashRisk);                                  // ya hay 6 bloques de 100 manos
    }
}

public class JournalTests
{
    [Fact]
    public void Entries_are_validated_and_listed_newest_first()
    {
        using var db = new HudDatabase(":memory:");
        var j = new JournalService(db);
        j.Add("start", 4, 2, "bien dormido", new DateTime(2026, 6, 1, 20, 0, 0));
        j.Add("end", null, null, "", new DateTime(2026, 6, 1, 23, 0, 0));
        var l = j.List();
        Assert.Equal("end", l[0].Phase);
        Assert.Equal(4, l[1].Mood);
        Assert.Throws<ArgumentException>(() => j.Add("start", 6, 1));
        Assert.Throws<ArgumentException>(() => j.Add("medio", 3, 3));
    }

    [Fact]
    public void Without_enough_sessions_the_report_says_there_is_no_data()
    {
        using var db = Synth.Db(Enumerable.Range(0, 30).Select(i => Synth.PreHand(i + 1, new DateTime(2026, 6, 1, 10, 0, 0).AddMinutes(i), "Hero", true)).ToList());
        var rep = CorrelationReport.Build(db, new JournalService(db));
        Assert.Equal(1, rep.Sessions);
        Assert.Empty(rep.Comparisons);
        Assert.All(rep.Messages, m => Assert.Contains("aún no hay datos suficientes", m));
    }

    [Fact]
    public void Low_mood_sessions_that_lose_money_show_a_clear_difference_with_an_interval()
    {
        // 12 sesiones de 25 manos separadas por horas; las de ánimo bajo pierden (el héroe se retira), las de ánimo alto ganan
        var hands = new List<Hand>();
        var journal = new List<(DateTime, int)>();
        long id = 1;
        for (int s = 0; s < 12; s++)
        {
            var start = new DateTime(2026, 6, 1, 10, 0, 0).AddHours(s * 5);
            bool good = s % 2 == 0;
            journal.Add((start.AddMinutes(-5), good ? 5 : 1));
            for (int i = 0; i < 25; i++)
                hands.Add(good ? Synth.PreHand(id++, start.AddMinutes(i), "Hero", true) : Synth.CbetHand(id++, start.AddMinutes(i), 10, true));
        }
        using var db = Synth.Db(hands);
        var j = new JournalService(db);
        foreach (var (t, m) in journal) j.Add("start", m, null, "", t);

        var rep = CorrelationReport.Build(db, j);
        Assert.Equal(12, rep.Sessions);
        var c = rep.Comparisons.Single(x => x.Title == "Ánimo y winrate");
        Assert.Equal((6, 6), (c.NA, c.NB));
        Assert.True(c.MeanA < c.MeanB);
        Assert.True(c.Clear, $"{c.Lo} {c.Hi}");
        Assert.True(c.Hi < 0);
    }

    [Fact]
    public void Compare_uses_t_intervals_and_flags_noise_as_unclear()
    {
        var a = new[] { 1.0, -2, 3, 0, -1, 2 };
        var b = new[] { 0.5, -1, 2, 1, 0, -0.5 };
        var c = CorrelationReport.Compare("t", "a", "b", a, b, "u");
        Assert.False(c.Clear);
        Assert.True(c.Lo < 0 && c.Hi > 0);
    }
}

public class GoalsAndReportTests
{
    static readonly DateTime Wed = new(2026, 6, 3, 18, 0, 0);   // miércoles

    [Fact]
    public void Week_starts_on_monday()
    {
        Assert.Equal(new DateTime(2026, 6, 1), GoalService.WeekStart(Wed));
        Assert.Equal(new DateTime(2026, 6, 1), GoalService.WeekStart(new DateTime(2026, 6, 7, 23, 0, 0)));   // domingo
        Assert.Equal(new DateTime(2026, 6, 8), GoalService.WeekStart(new DateTime(2026, 6, 8)));
    }

    [Fact]
    public void Goals_roundtrip_and_progress_counts_only_this_week()
    {
        var hands = new List<Hand>();
        hands.AddRange(Enumerable.Range(0, 5).Select(i => Synth.PreHand(i + 1, new DateTime(2026, 5, 20, 10, 0, 0).AddMinutes(i), "Hero", true)));   // semana anterior
        hands.AddRange(Enumerable.Range(0, 8).Select(i => Synth.PreHand(100 + i, new DateTime(2026, 6, 2, 10, 0, 0).AddMinutes(i), "Hero", true)));   // esta semana
        using var db = Synth.Db(hands);
        Assert.Equal(0, GoalService.Load(db).WeeklyHands);
        GoalService.Save(db, new Goals { WeeklyHands = 100, WeeklyReviews = 10, WeeklyTournaments = -3 });
        var g = GoalService.Load(db);
        Assert.Equal((100, 10, 0), (g.WeeklyHands, g.WeeklyReviews, g.WeeklyTournaments));

        var p = GoalService.Progress(db, g, Wed);
        Assert.Equal(2, p.Count);                                    // el objetivo de torneos (0) no aparece
        Assert.Equal(8, p.Single(x => x.Name == "Manos").Done);
        Assert.Equal(0.08, p.Single(x => x.Name == "Manos").Fraction, 9);

        new StudyService(db).Add("100", "x", "", Wed);
        new StudyService(db).Review("100", StudyGrade.Good, null, Wed);
        Assert.Equal(1, GoalService.Progress(db, g, Wed).Single(x => x.Name == "Repasos de estudio").Done);
    }

    [Fact]
    public void Weekly_report_is_html_escaped_marks_itself_done_and_reports_leaks_once_as_new()
    {
        using var db = Synth6.HeroFacing3Bets(300, 80);
        var engine = new StatEngine(db);
        var det = new LeakDetector(db, engine);
        var baselines = LeakDetector.LoadBaselines().Where(b => b.Id == "f3b").ToList();
        var now = new DateTime(2026, 3, 2, 12, 0, 0);               // los datos son de 2026-03-01
        var study = new StudyService(db);

        Assert.True(WeeklyReport.IsDue(db, now));
        var dir = Path.Combine(Path.GetTempPath(), "vergil-weekly-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = WeeklyReport.Generate(db, engine, study, det, baselines, dir, now);
            Assert.True(File.Exists(path));
            Assert.EndsWith("semanal-2026-03-02.html", path);
            var html = File.ReadAllText(path);
            Assert.Contains("Informe semanal", html);
            Assert.Contains("NUEVO", html);
            Assert.Contains("Fold a 3-Bet", html);
            Assert.Contains("300", html);
            Assert.False(WeeklyReport.IsDue(db, now.AddDays(2)));    // misma semana: no se repite
            Assert.True(WeeklyReport.IsDue(db, now.AddDays(7)));

            var second = WeeklyReport.Build(db, engine, study, det, baselines, now);
            Assert.DoesNotContain("NUEVO", second);                  // ya estaba en el informe anterior
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Empty_database_produces_a_valid_report()
    {
        using var db = new HudDatabase(":memory:");
        var engine = new StatEngine(db);
        var html = WeeklyReport.Build(db, engine, new StudyService(db), new LeakDetector(db, engine), LeakDetector.LoadBaselines(), Wed);
        Assert.Contains("No hay manos esta semana", html);
        Assert.Contains("</html>", html);
    }
}

public class InfrastructureTests
{
    static string TempDir() => Path.Combine(Path.GetTempPath(), "vergil-infra-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Custom_stats_validate_the_dsl_and_roundtrip_through_the_engine()
    {
        var existing = new[] { "vpip" };
        Assert.NotNull(CustomStats.Validate(new StatDefinition { Id = "Bad Id", Label = "x", Numerator = "pf_vpip" }, existing));
        Assert.Contains("Ya existe", CustomStats.Validate(new StatDefinition { Id = "vpip", Label = "x", Numerator = "pf_vpip" }, existing)!);
        Assert.NotNull(CustomStats.Validate(new StatDefinition { Id = "mine", Label = "", Numerator = "pf_vpip" }, existing));
        Assert.NotNull(CustomStats.Validate(new StatDefinition { Id = "mine", Label = "M", Numerator = "pf_nope + (" }, existing));
        Assert.Null(CustomStats.Validate(new StatDefinition { Id = "mine", Label = "M", Numerator = "pf_vpip && pf_pfr" }, existing));

        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "custom-stats.json");
            var def = new StatDefinition { Id = "open_raise", Label = "OR", Numerator = "pf_open", Denominator = "pf_open_opp", Format = "pct0", Description = "mía" };
            CustomStats.Save(path, new[] { def });
            var loaded = CustomStats.Load(path);
            Assert.Equal("open_raise", Assert.Single(loaded).Id);

            using var db = Synth.Db(Enumerable.Range(0, 10).Select(i => Synth.PreHand(i + 1, DateTime.Now.AddMinutes(-i), "Hero", true)).ToList());
            var engine = new StatEngine(db);
            engine.Register(loaded[0]);
            Assert.Equal(1.0, engine.Get("Hero", "open_raise").Value!.Value, 6);
            Assert.Empty(CustomStats.Load(Path.Combine(dir, "missing.json")));
            File.WriteAllText(path, "no es json");
            Assert.Empty(CustomStats.Load(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Config_bundle_exports_and_restores_notes_tags_and_goals()
    {
        using var src = Synth.Db(new[] { Synth.PreHand(1, DateTime.Now, "Hero", true) });
        var notes = new NotesService(src);
        notes.Set("Villain1", "paga todo", "red");
        notes.AddTag("1", "estudiar");
        GoalService.Save(src, new Goals { WeeklyHands = 500 });
        var json = ConfigBundle.Capture(src, "{\"a\":1}", "[]", "[]", "[]").ToJson();

        var bundle = ConfigBundle.FromJson(json)!;
        Assert.Equal("{\"a\":1}", bundle.Settings);
        using var dst = Synth.Db(new[] { Synth.PreHand(1, DateTime.Now, "Hero", true) });
        var (n, t) = bundle.ApplyToDb(dst);
        Assert.Equal((1, 1), (n, t));
        Assert.Equal("paga todo", new NotesService(dst).Get("Villain1").Note);
        Assert.Contains("estudiar", new NotesService(dst).GetTags("1"));
        Assert.Equal(500, GoalService.Load(dst).WeeklyHands);
        Assert.Equal((1, 0), (bundle.ApplyToDb(dst).Notes, bundle.ApplyToDb(dst).Tags));   // idempotente: las etiquetas no se duplican

        Assert.Null(ConfigBundle.FromJson("{ roto"));
        Assert.Null(ConfigBundle.FromJson("{\"Version\": 99}"));
    }

    [Fact]
    public void Backup_is_created_once_per_day_is_a_valid_database_and_old_ones_are_pruned()
    {
        using var db = Synth.Db(new[] { Synth.PreHand(1, DateTime.Now, "Hero", true) });
        var dir = TempDir();
        try
        {
            var t = new DateTime(2026, 6, 1, 10, 0, 0);
            var first = DbBackup.CreateIfDue(db, dir, t);
            Assert.NotNull(first);
            Assert.Null(DbBackup.CreateIfDue(db, dir, t.AddHours(3)));          // mismo día
            using (var copy = new HudDatabase(first!))
                Assert.Equal(1, copy.GetHand("1") != null ? 1 : 0);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            for (int d = 1; d <= 6; d++) Assert.NotNull(DbBackup.CreateIfDue(db, dir, t.AddDays(d), keep: 3));
            Assert.Equal(3, Directory.GetFiles(dir, "hud-*.db").Length);
            Assert.True(File.Exists(Path.Combine(dir, "hud-20260607-100000.db")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Log_writes_daily_files_and_never_throws()
    {
        var dir = TempDir();
        try
        {
            AppLog.Info("antes de init");                                         // sin carpeta: se ignora
            AppLog.Init(dir);
            AppLog.Info("hola");
            AppLog.Error("fallo", new InvalidOperationException("boom"));
            var file = Directory.GetFiles(dir, "vergil-*.log").Single();
            var text = File.ReadAllText(file);
            Assert.Contains("[INFO] hola", text);
            Assert.Contains("[ERROR] fallo", text);
            Assert.Contains("boom", text);
        }
        finally { AppLog.Init(Path.Combine(Path.GetTempPath(), "vergil-log-sink")); try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Benchmarks_run_and_report_every_measure()
    {
        var r = Benchmarks.Run(500);
        Assert.Equal(4, r.Count);
        Assert.All(r, x => Assert.True(x.Value > 0));
        Assert.NotNull(r.First(x => x.Name == "Evaluador de manos").Met);
    }

    [Fact]
    public void Table_dynamics_describe_the_opponents_seated_now()
    {
        using var db = Synth.Db(Enumerable.Range(0, 60).Select(i => Synth.PreHand(i + 1, DateTime.Now.AddMinutes(-i), "Hero", true)).ToList());
        var engine = new StatEngine(db);
        var d = TableDynamics.Of(engine, new PopulationPriors(db, engine), new[] { "Villain1", "Villain2" }, null)!;
        Assert.Equal(2, d.Players);
        Assert.InRange(d.AvgVpip, 0, 1);
        Assert.False(string.IsNullOrEmpty(d.Label));
        Assert.Null(TableDynamics.Of(engine, new PopulationPriors(db, engine), Array.Empty<string>(), null));
    }

    [Fact]
    public void The_app_never_opens_a_network_connection()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "DESIGN.md"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var banned = new[] { "HttpClient", "WebClient", "TcpClient", "UdpClient", "WebRequest", "HttpWebRequest", "ClientWebSocket", "new Socket(", "System.Net.Http", "System.Net.Sockets", "System.Net.WebSockets" };
        var offenders = Directory.GetFiles(Path.Combine(dir!, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/obj/"))
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, i + 1, l)))
            .Where(x => banned.Any(b => x.l.Contains(b)) && !x.l.TrimStart().StartsWith("//") && !x.l.TrimStart().StartsWith("///") && !x.l.Contains("banned"))
            .Select(x => $"{x.f}:{x.Item2}").ToList();
        Assert.Empty(offenders);
    }
}

public class FastEvaluatorTests
{
    [Fact]
    public void Fast_seven_card_path_gives_exactly_the_reference_scores()
    {
        var rng = new Random(11);
        var cards = new int[7];
        for (int n = 0; n < 200_000; n++)
        {
            for (int k = 0; k < 7; k++)
            {
retry:
                int c = rng.Next(52);
                for (int j = 0; j < k; j++) if (cards[j] == c) goto retry;
                cards[k] = c;
            }
            Assert.Equal(HandEvaluator.EvaluateSlow(cards), HandEvaluator.Evaluate(cards));
        }
    }

    [Fact]
    public void Special_hands_keep_their_categories()
    {
        int Eval(string s) => HandEvaluator.Category(HandEvaluator.Evaluate(Cards.ParseMany(s)));
        Assert.Equal(HandEvaluator.StraightFlush, Eval("9h 8h 7h 6h 5h 2c 2d"));
        Assert.Equal(HandEvaluator.Flush, Eval("Ah 9h 7h 6h 2h Kc Kd"));
        Assert.Equal(HandEvaluator.Quads, Eval("Ah Ad Ac As 2h 3c 4d"));
        Assert.Equal(HandEvaluator.FullHouse, Eval("Ah Ad Ac Ks Kh 3c 4d"));
        Assert.Equal(HandEvaluator.Straight, Eval("Ah 2d 3c 4s 5h 9c Kd"));          // rueda
        Assert.Equal(HandEvaluator.Flush, Eval("2h 4h 6h 8h Th Jh Qh") == HandEvaluator.Flush ? HandEvaluator.Flush : -1);
    }
}

[Collection("Rendimiento")]
public class EvaluatorSpeedTests
{
    [Fact]
    public void Seven_card_evaluator_stays_well_above_the_original_6_7_million_per_second()
    {
        var rng = new Random(5);
        int n = 100_000;
        var all = new int[n * 7];
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < 7; k++)
            {
retry:
                int c = rng.Next(52);
                for (int j = 0; j < k; j++) if (all[i * 7 + j] == c) goto retry;
                all[i * 7 + k] = c;
            }
        }
        long sink = 0;
        for (int i = 0; i < n; i++) sink += HandEvaluator.Evaluate(all.AsSpan(i * 7, 7));      // calentamiento
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int rounds = 30;
        for (int r = 0; r < rounds; r++)
            for (int i = 0; i < n; i++) sink += HandEvaluator.Evaluate(all.AsSpan(i * 7, 7));
        sw.Stop();
        double perSec = (double)n * rounds / sw.Elapsed.TotalSeconds;
        GC.KeepAlive(sink);
        // Medido en un Ryzen 5 3450U: ~16 M/s en Release (el objetivo de diseño, 50 M/s, no se alcanza en este equipo). El listón es una guardia contra regresiones.
        double floor = 
#if DEBUG
            3_000_000;
#else
            10_000_000;
#endif
        Assert.True(perSec >= floor, $"{perSec / 1e6:0.0} M evals/s");
    }
}

public class SpinMultiplierTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(5)] [InlineData(10)] [InlineData(25)] [InlineData(50)]
    public void Probabilities_sum_to_one_for_every_buyin(double buyin)
    {
        var d = SpinMultipliers.Distribution(buyin);
        Assert.Equal(1.0, d.Sum(x => x.Probability), 9);
        Assert.Equal(9, d.Count);
        Assert.Equal(12000, d[^1].Multiplier);
    }

    [Fact]
    public void Published_values_are_loaded_exactly_and_buyins_snap_to_the_nearest_level()
    {
        var five = SpinMultipliers.Distribution(5).ToDictionary(x => x.Multiplier, x => x.Probability);
        Assert.Equal(0.386052, five[3], 9);
        Assert.Equal(0.488047, five[2], 9);
        Assert.Equal(0.000001, five[12000], 12);
        Assert.Equal(5, SpinMultipliers.Nearest(4.6));
        Assert.Equal(10, SpinMultipliers.Nearest(9.2));
        Assert.Equal(0.4280470, SpinMultipliers.Distribution(25).First(x => x.Multiplier == 2).Probability, 9 - 2);
    }

    [Fact]
    public void Break_even_roi_is_the_house_edge_about_minus_8_percent()
    {
        foreach (var b in SpinMultipliers.BuyIns)
            Assert.InRange(SpinMultipliers.BreakEvenRoi(b), -0.12, -0.05);
        Assert.True(SpinMultipliers.ExpectedMultiplier(5) > 2.6);
    }

    [Fact]
    public void Comparison_flags_only_clearly_unusual_counts()
    {
        // 200 victorias de un Spin de 5: lo esperado es ~98 de ×2, ~77 de ×3
        var typical = SpinMultipliers.Compare(5, new[] { (2.0, 96), (3.0, 78), (4.0, 17), (5.0, 6), (10.0, 3) });
        Assert.DoesNotContain(typical, c => c.Outside);
        Assert.Equal(200, typical.Sum(c => c.Observed));

        var odd = SpinMultipliers.Compare(5, new[] { (2.0, 40), (3.0, 40), (4.0, 120) });
        Assert.Contains(odd, c => c.Multiplier == 4 && c.Outside);
        Assert.Empty(SpinMultipliers.Compare(5, Array.Empty<(double, int)>()).Where(c => c.Observed > 0));
    }
}
