using System.Globalization;
using VergilHud.Core;
using Xunit;

namespace VergilHud.Tests;

static class Synth6
{
    static string Time(DateTime t) => t.ToString("yyyy/MM/dd H:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Héroe abre desde el botón, Villain1 (SB) le hace 3-Bet a 20; el héroe se retira o paga.</summary>
    public static Hand ThreeBetHand(long id, DateTime t, bool heroFolds)
    {
        var s = $"PokerStars Hand #{id}:  Hold'em No Limit ($1/$2 USD) - {Time(t)} CET [2026/03/01 14:00:00 ET]\n" +
                "Table 'Synth' 6-max Seat #3 is the button\n" +
                "Seat 1: Villain1 ($200 in chips)\nSeat 2: Villain2 ($200 in chips)\nSeat 3: Hero ($200 in chips)\n" +
                "Villain1: posts small blind $1\nVillain2: posts big blind $2\n*** HOLE CARDS ***\nDealt to Hero [9h 8h]\n" +
                "Hero: raises $4 to $6\nVillain1: raises $14 to $20\nVillain2: folds\n";
        s += heroFolds
            ? "Hero: folds\nUncalled bet ($14) returned to Villain1\nVillain1 collected $13 from pot\n"
            : "Hero: calls $14\n*** FLOP *** [2c 7d Jh]\nVillain1: checks\nHero: checks\n*** TURN *** [2c 7d Jh] [3s]\nVillain1: checks\nHero: checks\n" +
              "*** RIVER *** [2c 7d Jh 3s] [Kd]\nVillain1: checks\nHero: checks\n*** SHOW DOWN ***\nVillain1: shows [Ac Ad] (a pair of Aces)\n" +
              "Hero: mucks hand\nVillain1 collected $41 from pot\n";
        s += "*** SUMMARY ***\nTotal pot $41 | Rake $0\n\n\n\n";
        return PokerStarsParser.Parse(s)!;
    }

    /// <summary>Héroe con F3B real de <paramref name="foldPct"/> en <paramref name="n"/> oportunidades.</summary>
    public static HudDatabase HeroFacing3Bets(int n, int foldPct)
    {
        var t0 = new DateTime(2026, 3, 1, 10, 0, 0);
        var hands = Enumerable.Range(0, n).Select(i => ThreeBetHand(9000 + i, t0.AddMinutes(i), (i * 100 / n) < foldPct)).ToList();
        return Synth.Db(hands);
    }
}

public class LeakDetectorTests
{
    static List<LeakBaseline> F3b => LeakDetector.LoadBaselines().Where(b => b.Id == "f3b").ToList();

    [Fact]
    public void Baselines_are_valid_and_reference_known_stats()
    {
        var engine = new StatEngine(new HudDatabase(":memory:"));
        var all = LeakDetector.LoadBaselines();
        Assert.True(all.Count >= 10);
        foreach (var b in all)
        {
            Assert.True(engine.Has(b.Stat), $"Estadística desconocida {b.Stat}");
            Assert.InRange(b.Lo, 0, 1);
            Assert.True(b.Hi > b.Lo);
        }
        Assert.Equal(all.Count, all.Select(b => b.Id).Distinct().Count());
    }

    [Fact]
    public void An_80_percent_fold_to_3bet_is_a_leak_with_300_opportunities()
    {
        using var db = Synth6.HeroFacing3Bets(300, 80);
        var engine = new StatEngine(db);
        var leaks = new LeakDetector(db, engine).Detect("Hero", F3b);
        var leak = Assert.Single(leaks);
        Assert.True(leak.Above);
        Assert.Equal(300, leak.N);
        Assert.True(leak.Lo > 0.62);
        Assert.Contains("Fold a 3-Bet", LeakDetector.Describe(leak));
    }

    [Fact]
    public void The_same_percentage_with_only_20_opportunities_is_not_a_leak()
    {
        using var db = Synth6.HeroFacing3Bets(20, 80);
        var engine = new StatEngine(db);
        Assert.Empty(new LeakDetector(db, engine).Detect("Hero", F3b));
    }

    [Fact]
    public void A_value_inside_the_target_range_is_not_a_leak()
    {
        using var db = Synth6.HeroFacing3Bets(400, 56);
        var engine = new StatEngine(db);
        Assert.Empty(new LeakDetector(db, engine).Detect("Hero", F3b));
    }

    [Fact]
    public void Folding_too_little_is_detected_below_the_range()
    {
        using var db = Synth6.HeroFacing3Bets(300, 25);
        var engine = new StatEngine(db);
        var leak = Assert.Single(new LeakDetector(db, engine).Detect("Hero", F3b));
        Assert.False(leak.Above);
        Assert.True(leak.Hi < 0.50);
    }

    [Fact]
    public void Impact_compares_the_spot_with_the_overall_average_and_the_spot_filter_lists_those_hands()
    {
        using var db = Synth6.HeroFacing3Bets(300, 80);
        var engine = new StatEngine(db);
        var det = new LeakDetector(db, engine);
        var leak = Assert.Single(det.Detect("Hero", F3b));
        // Todas las manos son del spot, así que el spot es la media y el impacto es 0
        Assert.Equal(0, leak.ImpactBb100, 6);

        var hands = new Analytics(db).ListHands(det.SpotFilter(leak) with { Limit = 1000 });
        Assert.Equal(300, hands.Count);
    }

    [Fact]
    public void Credible_interval_narrows_with_sample_size()
    {
        var small = LeakDetector.Credible(16, 20, 0.56);
        var big = LeakDetector.Credible(240, 300, 0.56);
        Assert.True(big.Hi - big.Lo < small.Hi - small.Lo);
    }
}

public class StudyTests
{
    static readonly DateTime T = new(2026, 5, 1, 12, 0, 0);

    [Fact]
    public void Sm2_intervals_follow_1_6_then_ease_and_reset_on_bad_grades()
    {
        var (e1, i1, r1) = StudyService.Schedule(2.5, 0, 0, StudyGrade.Good);
        Assert.Equal((1, 1), (i1, r1));
        Assert.Equal(2.6, e1, 6);
        var (e2, i2, r2) = StudyService.Schedule(e1, i1, r1, StudyGrade.Good);
        Assert.Equal((6, 2), (i2, r2));
        var (e3, i3, r3) = StudyService.Schedule(e2, i2, r2, StudyGrade.Good);
        Assert.Equal((int)Math.Round(6 * e3), i3);
        Assert.Equal(3, r3);

        var (e4, i4, r4) = StudyService.Schedule(e3, i3, r3, StudyGrade.Bad);
        Assert.Equal((1, 0), (i4, r4));
        Assert.True(e4 < e3);
    }

    [Fact]
    public void Doubtful_lowers_ease_but_keeps_progress_and_ease_has_a_floor()
    {
        var (e, i, r) = StudyService.Schedule(2.5, 6, 2, StudyGrade.Doubtful);
        Assert.True(e < 2.5);
        Assert.Equal(3, r);
        Assert.True(i >= 1);
        double ease = 1.4;
        for (int k = 0; k < 20; k++) ease = StudyService.Schedule(ease, 5, 3, StudyGrade.Bad).Ease;
        Assert.Equal(1.3, ease, 6);
    }

    [Fact]
    public void Queue_add_review_due_dates_and_stats()
    {
        using var db = Synth.Db(new[] { Synth.PreHand(1, T, "Hero", true), Synth.PreHand(2, T.AddMinutes(1), "Hero", false) });
        var study = new StudyService(db);
        Assert.True(study.Add("1", "etiqueta", "error", T));
        Assert.False(study.Add("1", "etiqueta", "error", T));   // sin duplicados
        study.Add("2", "leak", "f3b", T);

        Assert.Equal(2, study.GetDue(T).Count);
        study.Review("1", StudyGrade.Good, "call", T);
        study.Review("2", StudyGrade.Bad, null, T);

        var due = study.GetDue(T);                                // "2" vuelve mañana, "1" también
        Assert.Empty(due);
        var tomorrow = study.GetDue(T.Date.AddDays(1));
        Assert.Equal(2, tomorrow.Count);

        var stats = study.GetStats(T);
        Assert.Equal(2, stats.Total);
        Assert.Equal(0, stats.Pending);
        Assert.Equal(2, stats.ReviewedToday);
        Assert.Equal(1, stats.Streak);
        Assert.Equal(1, stats.ByCategory.Single(c => c.Category == "error").Good);
        Assert.Equal(0, stats.ByCategory.Single(c => c.Category == "f3b").Good);

        Assert.Throws<KeyNotFoundException>(() => study.Review("999", StudyGrade.Good));
    }

    [Fact]
    public void Streak_counts_consecutive_days_and_breaks_on_a_gap()
    {
        using var db = Synth.Db(new[] { Synth.PreHand(1, T, "Hero", true) });
        var study = new StudyService(db);
        study.Add("1", "etiqueta", "", T);
        study.Review("1", StudyGrade.Good, null, T.AddDays(-3));
        study.Review("1", StudyGrade.Good, null, T.AddDays(-1));
        study.Review("1", StudyGrade.Good, null, T);
        Assert.Equal(2, study.GetStats(T).Streak);
        Assert.Equal(2, study.GetStats(T.AddDays(1)).Streak);    // hoy aún no ha repasado: cuenta desde ayer
        Assert.Equal(0, study.GetStats(T.AddDays(5)).Streak);
    }

    [Fact]
    public void Automatic_sync_picks_tagged_hands_and_big_showdown_losses()
    {
        var big = Synth6.ThreeBetHand(50, T, false);            // el héroe paga 20 y pierde 20 en showdown = 10 bb: no basta
        using var db = Synth.Db(new[] { big, Synth.PreHand(51, T.AddMinutes(1), "Hero", true) });
        var notes = new NotesService(db);
        notes.AddTag("51", "error");
        notes.AddTag("51", "bluff");                             // otra etiqueta: no entra

        var study = new StudyService(db);
        Assert.Equal(1, study.SyncAutomatic(T));
        Assert.Equal(0, study.SyncAutomatic(T));                 // idempotente
        Assert.Equal("51", study.GetAll().Single().HandId);

        study.Remove("51");
        Assert.Empty(study.GetAll());
        Assert.True(study.Add("51", "etiqueta", "error", T) == false);
        Assert.Single(study.GetAll());                           // volver a añadirla la reactiva
    }

    [Fact]
    public void Question_step_stops_before_the_last_hero_decision()
    {
        var h = Synth6.ThreeBetHand(60, T, true);
        var replay = HandReplay.Build(h);
        int q = StudyService.QuestionStep(replay);
        Assert.Equal("Villain2 se retira", replay.Steps[q].Description);
        Assert.Contains("Hero se retira", replay.Steps[q + 1].Description);
        Assert.DoesNotContain("Resultado", replay.Steps[q].Description);
    }

    [Fact]
    public void Migration_5_creates_the_study_tables()
    {
        using var db = new HudDatabase(":memory:");
        Assert.True(db.SchemaVersion >= 5);
        Assert.Equal(0, new StudyService(db).GetStats().Total);
    }
}

public class IcmTests
{
    static readonly double[] P = { 0.5, 0.3, 0.2 };

    [Fact]
    public void Icm_distributes_exactly_the_prize_pool_and_orders_by_stack()
    {
        var eq = Icm.Equities(new double[] { 5000, 3000, 2000 }, P);
        Assert.Equal(1.0, eq.Sum(), 9);
        Assert.True(eq[0] > eq[1] && eq[1] > eq[2]);
        // El chip leader vale menos que su proporción de fichas (50 %) del primer premio y el corto más que la suya
        Assert.True(eq[0] < 0.5 * 1.0);
        Assert.True(eq[2] > 0.2 * 0.2);
    }

    [Fact]
    public void Equal_stacks_are_worth_the_same_and_heads_up_is_linear()
    {
        var eq = Icm.Equities(new double[] { 1000, 1000, 1000 }, P);
        Assert.All(eq, e => Assert.Equal(1.0 / 3, e, 9));
        var hu = Icm.Equities(new double[] { 2000, 1000 }, new[] { 0.7, 0.3 });
        Assert.Equal(0.7 * 2 / 3 + 0.3 / 3, hu[0], 9);
        Assert.Equal(0.7 / 3 + 0.3 * 2 / 3, hu[1], 9);
    }

    [Fact]
    public void Winner_takes_all_makes_icm_equal_chip_proportions()
    {
        var eq = Icm.Equities(new double[] { 600, 300, 100 }, new[] { 1.0 });
        Assert.Equal(new[] { 0.6, 0.3, 0.1 }, eq.Select(x => Math.Round(x, 9)).ToArray());
    }

    [Fact]
    public void Default_payouts_only_exist_for_sng()
    {
        Assert.Equal(new[] { 0.5, 0.3, 0.2 }, Icm.DefaultPayouts("sng", 9));
        Assert.Null(Icm.DefaultPayouts("spin", 3));
        Assert.Null(Icm.DefaultPayouts("cash", 6));
    }

    const string AllInHand =
        "PokerStars Hand #300000001: Tournament #910001, 9000+1000 Hold'em No Limit - Level I (50/100) - 2026/03/01 20:00:00 CET [2026/03/01 14:00:00 ET]\n" +
        "Table '910001 1' 6-max Seat #3 is the button\n" +
        "Seat 1: Villain1 (1000 in chips)\nSeat 2: Villain2 (3000 in chips)\nSeat 3: Hero (2000 in chips)\n" +
        "Villain1: posts small blind 50\nVillain2: posts big blind 100\n*** HOLE CARDS ***\nDealt to Hero [Ah Ad]\n" +
        "Hero: raises 1900 to 2000 and is all-in\nVillain1: folds\nVillain2: calls 1900\n" +
        "*** FLOP *** [2c 7d Jh]\n*** TURN *** [2c 7d Jh] [3s]\n*** RIVER *** [2c 7d Jh 3s] [9d]\n*** SHOW DOWN ***\n" +
        "Villain2: shows [Kc Kd] (a pair of Kings)\nHero: shows [Ah Ad] (a pair of Aces)\nHero collected 4050 from pot\n" +
        "*** SUMMARY ***\nTotal pot 4050 | Rake 0\n\n\n\n";

    [Fact]
    public void Heads_up_all_in_spot_combines_equity_with_icm_values()
    {
        var h = PokerStarsParser.Parse(AllInHand)!;
        var s = IcmAnalysis.Analyze(h, P)!;
        Assert.NotNull(s);
        Assert.InRange(s.Equity, 0.80, 0.84);
        Assert.Equal("Villain2", s.Villain);
        Assert.Equal(20, s.EffectiveBb, 6);
        Assert.True(s.IfWin > s.Before && s.Before > s.IfLose);
        Assert.Equal(s.Equity * s.IfWin + (1 - s.Equity) * s.IfLose - s.Before, s.DollarEv, 9);
        Assert.True(s.ChipEv > 0);
    }

    [Fact]
    public void Icm_tax_appears_when_a_chip_positive_call_loses_prize_value()
    {
        // Rival grande llama al héroe corto con una mano ligeramente favorita
        var text = AllInHand.Replace("Dealt to Hero [Ah Ad]", "Dealt to Hero [Kh Qd]").Replace("Hero: shows [Ah Ad] (a pair of Aces)", "Hero: shows [Kh Qd] (nothing)")
            .Replace("Villain2: shows [Kc Kd] (a pair of Kings)", "Villain2: shows [7c 6c] (a pair of Sevens)");
        var s = IcmAnalysis.Analyze(PokerStarsParser.Parse(text)!, new[] { 0.7, 0.3 });
        Assert.NotNull(s);
        Assert.Equal(s!.ChipEv > 0 && s.DollarEv < 0, s.IcmTax);
    }

    [Fact]
    public void Scanning_the_database_finds_the_heros_tournament_all_ins()
    {
        using var db = Synth.Db(new[] { PokerStarsParser.Parse(AllInHand)! });
        var spots = IcmAnalysis.ScanHero(db, P);
        Assert.Equal("300000001", Assert.Single(spots).HandId);
    }

    [Fact]
    public void Spins_and_hands_without_visible_cards_are_not_analyzed()
    {
        var spin = Synth.Case("tour_02_spin_shove_call");
        Assert.Null(IcmAnalysis.Analyze(spin, P));
        var cash = Synth.PreHand(1, DateTime.Now, "Hero", true);
        Assert.Null(IcmAnalysis.Analyze(cash, P));
    }
}
