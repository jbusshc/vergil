using Microsoft.Data.Sqlite;
using VergilHud.Core;
using Xunit;

namespace VergilHud.Tests;

public class SessionTests
{
    static readonly DateTime T0 = new(2026, 3, 1, 10, 0, 0);

    [Fact]
    public void A_pause_of_more_than_30_minutes_opens_a_new_session()
    {
        var morning = Enumerable.Range(0, 10).Select(i => Synth.PreHand(8000 + i, T0.AddMinutes(i), "Hero", false)).ToList();
        var evening = Enumerable.Range(0, 5).Select(i => Synth.PreHand(8100 + i, T0.AddHours(3).AddMinutes(i), "Hero", true)).ToList();
        using var db = Synth.Db(morning, evening);

        Assert.Equal(2, SessionService.Rebuild(db));
        var s = SessionService.GetSessions(db, Period.All);
        Assert.Equal(new[] { 10, 5 }, s.Select(x => x.Hands).ToArray());
        Assert.Equal(T0, s[0].Start);
        Assert.Equal(T0.AddMinutes(9), s[0].End);
        Assert.Equal(TimeSpan.FromMinutes(9), s[0].Duration);
        Assert.Equal(1, s[0].TablesMax);
        Assert.Equal(10 * 1.5, s[0].NetBb, 6);          // el héroe gana 3 de 2 bb = +1,5 bb por mano
        Assert.Equal(s[0].NetBb, s[0].EvBb, 6);        // sin all-ins, EV = real

        // Cada mano del héroe queda ligada a su sesión
        var assigned = db.Query(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(DISTINCT session_id), COUNT(*) FROM hands WHERE session_id IS NOT NULL";
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetInt32(0), r.GetInt32(1));
        });
        Assert.Equal((2, 15), assigned);
        Assert.Equal(9, SessionService.GetSessions(db, new Period(T0.AddHours(2), null)).Count == 1 ? 9 : 0);
    }

    [Fact]
    public void Simultaneous_tables_are_counted_inside_a_five_minute_window()
    {
        // Dos mesas alternando manos cada minuto, y luego una sola mesa
        var hands = new List<Hand>();
        for (int i = 0; i < 6; i++) hands.Add(Synth.PreHand(8200 + i, T0.AddMinutes(i), "Hero", false, table: i % 2 == 0 ? "MesaA" : "MesaB"));
        for (int i = 0; i < 4; i++) hands.Add(Synth.PreHand(8300 + i, T0.AddMinutes(20 + i), "Hero", false, table: "MesaA"));
        using var db = Synth.Db(hands);
        SessionService.Rebuild(db);
        Assert.Equal(2, Assert.Single(SessionService.GetSessions(db, Period.All)).TablesMax);
    }

    [Fact]
    public void Sessions_are_only_rebuilt_when_there_are_newer_hands()
    {
        var first = Enumerable.Range(0, 5).Select(i => Synth.PreHand(8400 + i, T0.AddMinutes(i), "Hero", false)).ToList();
        using var db = Synth.Db(first);
        Assert.True(SessionService.EnsureUpToDate(db));    // la primera vez construye
        Assert.False(SessionService.EnsureUpToDate(db));   // ya está al día
        db.InsertHands(Enumerable.Range(0, 3).Select(i => Synth.PreHand(8500 + i, T0.AddMinutes(10 + i), "Hero", false))
            .Select(h => (h, FactExtractor.Extract(h))).ToList());
        Assert.True(SessionService.EnsureUpToDate(db));
        Assert.Equal(8, Assert.Single(SessionService.GetSessions(db, Period.All)).Hands);
    }

    [Fact]
    public void Empty_database_has_no_sessions()
    {
        using var db = new HudDatabase(":memory:");
        Assert.Equal(0, SessionService.Rebuild(db));
        Assert.False(SessionService.EnsureUpToDate(db));
        Assert.Empty(SessionService.GetSessions(db, Period.All));
    }
}

public class AnalyticsTests
{
    static readonly DateTime T0 = new(2026, 3, 1, 10, 0, 0);

    static HudDatabase Thirty()
    {
        // 30 manos seguidas, una por minuto: el héroe gana +1,5 bb cada una
        return Synth.Db(Enumerable.Range(0, 30).Select(i => Synth.PreHand(9000 + i, T0.AddMinutes(i), "Hero", false)));
    }

    [Fact]
    public void Overview_adds_up_results_hours_and_hands_per_hour()
    {
        using var db = Thirty();
        var o = new Analytics(db).GetOverview(Period.All, "cash");
        Assert.Equal(30, o.Hands);
        Assert.Equal(45, o.NetBb, 6);
        Assert.Equal(150, o.BbPer100, 6);
        Assert.Equal(o.NetBb, o.EvNetBb, 6);
        Assert.Equal(0, o.RakeBb);
        Assert.Equal(29 / 60.0, o.Hours, 6);                // de la 1.ª a la última mano: 29 minutos
        Assert.Equal(30 / (29 / 60.0), o.HandsPerHour, 3);

        // Filtros: otro ámbito o un periodo sin manos dan 0
        Assert.Equal(0, new Analytics(db).GetOverview(Period.All, "tournament").Hands);
        Assert.Equal(0, new Analytics(db).GetOverview(new Period(T0.AddDays(5), null), "cash").Hands);
        Assert.Equal(15, new Analytics(db).GetOverview(new Period(T0.AddMinutes(15), null), "all").Hands);
        Assert.Throws<ArgumentException>(() => new Analytics(db).GetOverview(Period.All, "otro"));
    }

    [Fact]
    public void Profit_series_accumulates_downsamples_and_keeps_the_last_point()
    {
        using var db = Thirty();
        var a = new Analytics(db);
        var full = a.GetProfitSeries(Period.All, "cash");
        Assert.Equal(30, full.Points.Count);
        Assert.Equal(1.5, full.Points[0].Net, 6);
        Assert.Equal(45, full.Points[^1].Net, 6);
        Assert.Equal(full.Points[^1].Net, full.Points[^1].NoShowdown + full.Points[^1].Showdown, 6);
        Assert.Equal(1.5, full.MeanPerHand, 6);
        Assert.Equal(0, full.SdPerHand, 6);                  // todas las manos ganan lo mismo

        var small = a.GetProfitSeries(Period.All, "cash", maxPoints: 7);
        Assert.InRange(small.Points.Count, 5, 8);
        Assert.Equal(30, small.Points[^1].Hand);             // el último punto nunca se pierde
        Assert.Equal(45, small.Points[^1].Net, 6);
        Assert.Empty(a.GetProfitSeries(new Period(T0.AddYears(1), null)).Points);
    }

    [Fact]
    public void Variance_band_widens_with_the_square_root_of_the_hands()
    {
        var s = new ProfitSeries(new List<ProfitPoint>(), 400, 2.0, 25.0);
        var (lo100, hi100) = s.Band(100, 1);
        var (lo400, hi400) = s.Band(400, 1);
        Assert.Equal(200 - 250, lo100, 6);                   // 2·100 − 25·√100
        Assert.Equal(200 + 250, hi100, 6);
        Assert.Equal(800 - 500, lo400, 6);                   // 2·400 − 25·√400
        Assert.Equal(2.0, (hi400 - lo400) / (hi100 - lo100), 6);   // 4× las manos → 2× la banda
        Assert.True(s.Band(100, 2).Hi > s.Band(100, 1).Hi);
    }

    [Fact]
    public void Series_standard_deviation_matches_a_direct_calculation()
    {
        using var db = Synth.Db(Fixtures.AllHands());
        var s = new Analytics(db).GetProfitSeries(Period.All, "all", 100_000);
        Assert.True(s.Hands > 30);
        var raw = db.Query(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT net_bb FROM player_hand_facts WHERE is_hero = 1";
            using var r = cmd.ExecuteReader();
            var l = new List<double>();
            while (r.Read()) l.Add(r.GetDouble(0));
            return l;
        });
        double mean = raw.Average();
        double sd = Math.Sqrt(raw.Sum(x => (x - mean) * (x - mean)) / (raw.Count - 1));
        Assert.Equal(mean, s.MeanPerHand, 9);
        Assert.Equal(sd, s.SdPerHand, 9);
    }

    [Fact]
    public void All_in_ev_separates_luck_from_results_in_the_series()
    {
        using var db = Synth.Db(Fixtures.AllHands());
        AllInEvService.ProcessAll(db);
        var s = new Analytics(db).GetProfitSeries(Period.All, "all", 100_000);
        Assert.NotEqual(s.Points[^1].Net, s.Points[^1].Ev, 3);      // hay manos con suerte/mala suerte
        var o = new Analytics(db).GetOverview(Period.All, "all");
        Assert.NotEqual(o.BbPer100, o.EvBbPer100, 3);
    }

    [Fact]
    public void Group_reports_cover_every_dimension_and_add_up()
    {
        using var db = Synth.Db(Fixtures.AllHands());
        var a = new Analytics(db);
        var total = a.GetOverview(Period.All, "all").Hands;
        foreach (var dim in Analytics.Dimensions.Keys)
        {
            var rows = a.GroupBy(dim, Period.All, "all");
            Assert.NotEmpty(rows);
            Assert.Equal(total, rows.Sum(r => r.Hands));
        }
        var byPos = a.GroupBy("position", Period.All, "cash").Select(r => r.Key).ToList();
        Assert.Equal(byPos.OrderBy(k => Array.IndexOf(Analytics.PositionOrder, k)).ToList(), byPos);   // orden de mesa, no alfabético
        Assert.Contains("BTN", byPos);
        var stacks = a.GroupBy("stack", Period.All, "all").Select(r => r.Key).ToList();
        var order = new[] { "<10", "10-20", "20-40", "40+" };
        Assert.Equal(stacks.OrderBy(k => Array.IndexOf(order, k)).ToList(), stacks);   // por tamaño de stack, no alfabético
        Assert.Matches(@"^\d{4}-\d{2}$", a.GroupBy("month", Period.All, "all")[0].Key);
        var btn = a.GroupBy("position", Period.All, "cash").First(r => r.Key == "BTN");
        Assert.InRange(btn.VpipPct, 0, 100);
    }

    [Fact]
    public void Group_dimension_is_validated_against_a_whitelist()
    {
        using var db = Thirty();
        Assert.Throws<ArgumentException>(() => new Analytics(db).GroupBy("position; DROP TABLE hands", Period.All));
        Assert.Throws<ArgumentException>(() => new Analytics(db).GroupBy("net", Period.All));
        Assert.Equal(30, db.GetHandCount());
    }

    [Fact]
    public void Hand_matrix_places_each_starting_hand_in_the_right_cell()
    {
        using var db = Synth.Db(Fixtures.AllHands());
        var a = new Analytics(db);
        var m = a.GetHandMatrix(Period.All, "cash");
        Assert.Equal("AA", m[0, 0].Name);
        Assert.Equal("AKs", m[0, 1].Name);     // suited por encima de la diagonal
        Assert.Equal("AKo", m[1, 0].Name);     // offsuit por debajo
        Assert.Equal("22", m[12, 12].Name);
        Assert.Equal(169, m.Cast<MatrixCell>().Select(c => c.Name).Distinct().Count());

        Assert.True(m[1, 0].Hands >= 2);       // AcKd (cash_14) y AsKd (cash_05)
        Assert.Equal("QQ", m[2, 2].Name);
        Assert.True(m[2, 2].Hands >= 2);       // QQ: cash_06 y cash_17
        Assert.True(m[1, 0].Played >= 1);
        Assert.Equal(m[1, 0].NetBb * 100 / m[1, 0].Hands, m[1, 0].BbPer100, 6);

        var hands = a.GetHandsFor("AKo", Period.All, "cash");
        Assert.Contains(hands, h => h.HandId == "100000014" && h.Cards == "Ac Kd");
        Assert.All(hands, h => Assert.Equal("AKo", Cards.HandName(h.Cards!)));
        Assert.Empty(a.GetHandsFor("J3s", Period.All, "cash"));   // ninguna mano del héroe en los fixtures
    }

    [Fact]
    public void Csv_export_has_headers_escapes_and_all_rows()
    {
        using var db = Synth.Db(Fixtures.AllHands());
        var a = new Analytics(db);
        var csv = ReportExport.ToCsv(a.GroupBy("position", Period.All, "cash"));
        var lines = csv.TrimEnd().Split('\n');
        Assert.Equal("grupo,manos,neto_bb,bb_100,ev_bb_100,vpip,pfr", lines[0]);
        Assert.True(lines.Length > 3);
        Assert.All(lines.Skip(1), l => Assert.Equal(7, l.Split(',').Length));
        Assert.StartsWith("mano,fecha,cartas", ReportExport.ToCsv(a.GetHandsFor("AKo", Period.All, "cash")));
        Assert.Contains("AKo", ReportExport.ToCsv(a.GetHandMatrix(Period.All, "cash")));
        Assert.Contains("\"a,b\"", ReportExport.ToCsv(new[] { new GroupRow("a,b", 1, 0, 0, 0, 0, 0) }));
    }
}

public class TournamentReportTests
{
    static TournamentRow Spin(int i, int place, double prize, double buyin = 1.0) =>
        new($"S{i}", "spin", new DateTime(2026, 3, 1).AddMinutes(i), buyin, place, prize, 10, 3);

    /// <summary>n Spins con `wins` victorias de premio `payout`·inscripción y el resto sin premio.</summary>
    static List<TournamentRow> Spins(int n, int wins, double payout) =>
        Enumerable.Range(0, n).Select(i => i < wins ? Spin(i, 1, payout) : Spin(i, 2 + i % 2, 0)).ToList();

    [Fact]
    public void Acceptance_400_spins_with_zero_roi_cannot_be_told_apart_from_zero()
    {
        // 160 victorias de 400 con premio de 2,5× la inscripción: el ROI es exactamente 0 %.
        var r = TournamentReport.Roi(Spins(400, 160, 2.5));
        Assert.Equal(400, r.N);
        Assert.Equal(0, r.Roi, 9);
        Assert.True(r.Lo < 0 && r.Hi > 0, $"intervalo {r.Lo:P1}–{r.Hi:P1}");
        Assert.Contains("aún no se puede distinguir tu ROI de 0 %", r.Message);
        Assert.Contains("400 torneos", r.Message);
        Assert.Null(r.NeededN);       // con ROI observado 0 no hay "cuántos hacen falta"
        Assert.Equal(40, r.ItmPct, 6);
    }

    [Fact]
    public void A_small_positive_roi_says_how_many_more_tournaments_are_needed()
    {
        var r = TournamentReport.Roi(Spins(400, 168, 2.5));      // ROI = 168·2,5/400 − 1 = +5 %
        Assert.Equal(0.05, r.Roi, 9);
        Assert.True(r.Lo < 0);
        Assert.NotNull(r.NeededN);
        Assert.True(r.NeededN > 400, $"{r.NeededN}");
        Assert.Contains("harían falta", r.Message);
        Assert.Contains("+5", r.Message);
    }

    [Fact]
    public void A_clear_edge_is_reported_as_significant_in_either_direction()
    {
        var good = TournamentReport.Roi(Spins(400, 200, 2.5));   // ROI +25 %
        Assert.True(good.Lo > 0);
        Assert.Contains("positivo", good.Message);
        Assert.Null(good.NeededN);
        var bad = TournamentReport.Roi(Spins(400, 100, 2.5));    // ROI −37,5 %
        Assert.True(bad.Hi < 0);
        Assert.Contains("negativo", bad.Message);
    }

    [Fact]
    public void Interval_shrinks_as_more_tournaments_are_played()
    {
        double width(int n) { var r = TournamentReport.Roi(Spins(n, n * 4 / 10, 2.5)); return r.Hi - r.Lo; }
        Assert.True(width(100) > width(400));
        Assert.True(width(400) > width(1600));
        Assert.InRange(width(400) / width(1600), 1.8, 2.2);      // 4× los torneos → la mitad de ancho
    }

    [Fact]
    public void Edge_cases_do_not_invent_numbers()
    {
        Assert.Equal(0, TournamentReport.Roi(Array.Empty<TournamentRow>()).N);
        Assert.Contains("Aún no hay", TournamentReport.Roi(Array.Empty<TournamentRow>()).Message);
        var unfinished = new[] { new TournamentRow("x", "spin", DateTime.Now, 1, null, null, 3, 3) };
        Assert.Equal(0, TournamentReport.Roi(unfinished).N);                 // un torneo en curso no cuenta
        var one = TournamentReport.Roi(Spins(1, 1, 2));
        Assert.Equal(1, one.N);
        Assert.Contains("un solo torneo", one.Message);
        Assert.Equal(1.0, one.Roi, 9);
        Assert.Equal(0, TournamentReport.Roi(new[] { Spin(1, 1, 5, buyin: 0) }).N);   // sin inscripción no hay ROI
    }

    [Fact]
    public void A_handful_of_wins_is_never_called_significant()
    {
        // Caso real: 2 Spins, los dos ganados (x2 y x3). La normal diría "ROI positivo con 90 %"; con t de Student, no.
        var rows = new[] { Spin(0, 1, 2, buyin: 1), Spin(1, 1, 3, buyin: 1) };
        var r = TournamentReport.Roi(rows);
        Assert.Equal(1.5, r.Roi, 9);                              // +150 % observado…
        Assert.True(r.Lo < 0 && r.Hi > 0, $"{r.Lo:P0}–{r.Hi:P0}"); // …pero el intervalo incluye el 0
        Assert.Contains("aún no se puede distinguir tu ROI de 0 %", r.Message);

        // Y la cola de la distribución t se acerca a la normal al crecer la muestra
        Assert.Equal(6.314, TournamentReport.TCrit90(1), 3);
        Assert.Equal(2.920, TournamentReport.TCrit90(2), 3);
        Assert.InRange(TournamentReport.TCrit90(10), 1.80, 1.82);
        Assert.InRange(TournamentReport.TCrit90(399), 1.64, 1.66);
        double previous = double.MaxValue;
        for (int df = 1; df <= 500; df++)
        {
            var t = TournamentReport.TCrit90(df);
            Assert.True(t <= previous + 1e-12, $"df={df}: el valor crítico debería bajar");
            previous = t;
        }
    }

    [Fact]
    public void Mixed_buyins_are_weighted_by_the_money_staked()
    {
        var rows = new List<TournamentRow>
        {
            Spin(0, 1, 20, buyin: 10),    // +10 sobre 10
            Spin(1, 2, 0, buyin: 1),      // −1 sobre 1
            Spin(2, 2, 0, buyin: 1),
        };
        var r = TournamentReport.Roi(rows);
        Assert.Equal(12, r.TotalBuyin, 9);
        Assert.Equal(20, r.TotalPrize, 9);
        Assert.Equal(8.0 / 12, r.Roi, 9);                         // Σ ganancia / Σ inscripciones, no la media de ROIs
        Assert.Equal(8.0 / 3, r.AvgProfit, 9);
    }

    [Fact]
    public void Multiplier_is_known_for_winning_spins_only()
    {
        Assert.Equal(2.5, Spin(0, 1, 2.5).Multiplier);
        Assert.Null(Spin(0, 2, 0).Multiplier);
        Assert.Null(new TournamentRow("m", "mtt", DateTime.Now, 5, 1, 50, 100, null).Multiplier);
    }

    [Fact]
    public void Spin_report_uses_the_real_tournament_and_counts_heads_up()
    {
        using var db = Synth.Db(Fixtures.AllHands());
        var r = TournamentReport.Spin(db, Period.All);
        Assert.True(r.Played >= 2);
        Assert.Equal(r.Played, r.Wins);                             // los dos Spin de los fixtures terminan con victoria del héroe
        Assert.Equal(100, r.WinPct, 6);
        Assert.Contains(r.WinsByMultiplier, m => m.Multiplier == 2.0 && m.Count >= 2);   // 20.000 sobre 10.000
        Assert.True(r.HeadsUpReached >= 2);
        Assert.Equal(r.HeadsUpReached, r.HeadsUpWon);
        Assert.Contains(r.Phases, p => p.Phase == "3 jugadores");
        Assert.Contains(r.Phases, p => p.Phase == "Mano a mano");
        Assert.All(r.Phases, p => Assert.True(p.Hands > 0));
        Assert.True(r.Roi.Roi > 0);
    }

    [Fact]
    public void Load_filters_by_format_and_period()
    {
        using var db = Synth.Db(Fixtures.AllHands());
        var all = TournamentReport.Load(db, Period.All);
        Assert.Contains(all, t => t.Format == "mtt");
        Assert.All(TournamentReport.Load(db, Period.All, "spin"), t => Assert.Equal("spin", t.Format));
        Assert.Empty(TournamentReport.Load(db, new Period(new DateTime(2100, 1, 1), null)));
    }
}

/// <summary>Las pruebas de rendimiento se ejecutan solas: con otras pruebas en paralelo la medida no vale.</summary>
[CollectionDefinition("Rendimiento", DisableParallelization = true)]
public class PerfCollection { }

[Collection("Rendimiento")]
public class PerformanceTests
{
    /// <summary>Genera 100.000 manos del héroe directamente con SQL: mide las consultas, no la importación.</summary>
    static HudDatabase BigDb(int hands)
    {
        var db = new HudDatabase(":memory:");
        db.Query(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < $n) " +
                "INSERT INTO hands(hand_id, site, table_name, format, played_at, big_blind, hero, raw, ev_state) " +
                "SELECT 'P'||i, 'PokerStars', 'T'||(i%4), 'cash', strftime('%Y-%m-%dT%H:%M:%S', datetime('2026-01-01', '+'||(i + (i/200)*120)||' minutes')), 2.0, 'Hero', '', 1 FROM n;" +
                "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < $n) " +
                "INSERT INTO player_hand_facts(hand_id, player, seat, played_at, is_hero, format, big_blind, net_bb, ev_net_bb, wtsd, pf_vpip, pf_pfr, position, rake_paid, hole_cards, players_dealt) " +
                "SELECT 'P'||i, 'Hero', 1, strftime('%Y-%m-%dT%H:%M:%S', datetime('2026-01-01', '+'||(i + (i/200)*120)||' minutes')), 1, 'cash', 2.0, " +
                "((i*7919)%2000 - 940)/100.0, ((i*7919)%2000 - 900)/100.0, i%5=0, i%3=0, i%7=0, CASE i%3 WHEN 0 THEN 'BTN' WHEN 1 THEN 'CO' ELSE 'BB' END, 0.1, 'Ah Kd', 6 FROM n;";
            cmd.Parameters.AddWithValue("$n", hands);
            cmd.ExecuteNonQuery();
            return 0;
        });
        return db;
    }

    [Fact]
    public void Dashboard_queries_over_100k_hands_take_under_a_second()
    {
        using var db = BigDb(100_000);
        Assert.Equal(100_000, db.GetHandCount());
        int sessions = SessionService.Rebuild(db);            // esto ocurre al importar, no al abrir el Dashboard
        Assert.InRange(sessions, 400, 600);

        var a = new Analytics(db);
        a.GetOverview(Period.All, "cash");                    // calienta la caché de SQLite
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var o = a.GetOverview(Period.All, "cash");
        var series = a.GetProfitSeries(Period.All, "cash");
        var last30 = SessionService.GetSessions(db, Period.All).TakeLast(30).ToList();
        var byPos = a.GroupBy("position", Period.All, "cash");
        sw.Stop();

        Assert.Equal(100_000, o.Hands);
        Assert.InRange(series.Points.Count, 1500, 2001);
        Assert.Equal(30, last30.Count);
        Assert.Equal(3, byPos.Count);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Dashboard: {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"Dashboard con 100.000 manos: {sw.ElapsedMilliseconds} ms");
    }
}
