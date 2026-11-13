using VergilHud.Core;
using Xunit;

namespace VergilHud.Tests;

/// <summary>Matriz de equity compartida: se calcula una sola vez para todas las pruebas Nash.</summary>
static class NashFixture
{
    public static readonly Lazy<EquityMatrix> Matrix = new(() => EquityMatrix.Compute(samples: 2500));
}

public class HandClassTests
{
    [Fact]
    public void There_are_169_classes_with_1326_combinations_and_the_right_shapes()
    {
        Assert.Equal(169, HandClasses.Names.Distinct().Count());
        Assert.Equal(1326, HandClasses.ComboCount.Sum());
        Assert.Equal(6, HandClasses.ComboCount[HandClasses.IndexOf("AA")]);
        Assert.Equal(4, HandClasses.ComboCount[HandClasses.IndexOf("AKs")]);
        Assert.Equal(12, HandClasses.ComboCount[HandClasses.IndexOf("AKo")]);
        Assert.Equal(0, HandClasses.IndexOf("AA"));
        Assert.Equal("AKs", HandClasses.Names[1]);       // fila A, columna K = suited
        Assert.Equal("AKo", HandClasses.Names[13]);      // fila K, columna A = offsuit
        // Cada combinación pertenece a la clase que dice su nombre
        for (int i = 0; i < 169; i++)
            foreach (var c in HandClasses.Combos[i])
                Assert.Equal(HandClasses.Names[i], Cards.HandName(c[0], c[1]));
    }

    [Fact]
    public void Overlap_counts_non_conflicting_combinations()
    {
        int aa = HandClasses.IndexOf("AA"), kk = HandClasses.IndexOf("KK"), aks = HandClasses.IndexOf("AKs");
        Assert.Equal(36, HandClasses.Overlap[aa * 169 + kk]);        // 6 × 6 sin cartas comunes
        Assert.Equal(6, HandClasses.Overlap[aa * 169 + aa]);          // cada combinación de AA tiene exactamente otra disjunta
        Assert.True(HandClasses.Overlap[aa * 169 + aks] < 24);        // AA y AKs comparten ases
    }

    [Fact]
    public void Index_of_cards_maps_hole_cards_to_classes()
    {
        Assert.Equal(HandClasses.IndexOf("AKo"), HandClasses.IndexOfCards("Ah Kd"));
        Assert.Equal(HandClasses.IndexOf("72s"), HandClasses.IndexOfCards("7s 2s"));
        Assert.Equal(-1, HandClasses.IndexOfCards(null));
    }
}

public class NashTests
{
    static EquityMatrix M => NashFixture.Matrix.Value;

    [Fact]
    public void Equity_matrix_matches_known_preflop_equities_and_is_antisymmetric()
    {
        int aa = HandClasses.IndexOf("AA"), kk = HandClasses.IndexOf("KK"), aks = HandClasses.IndexOf("AKs"), s72 = HandClasses.IndexOf("72o");
        Assert.InRange(M[aa, kk], 0.80, 0.84);                        // ≈ 81,9 %
        Assert.InRange(M[aks, HandClasses.IndexOf("QQ")], 0.42, 0.50); // AKs vs QQ ≈ 46 %
        Assert.InRange(M[aa, s72], 0.84, 0.90);
        for (int i = 0; i < 169; i += 7)
            for (int j = 0; j < 169; j += 5)
                Assert.Equal(1, M[i, j] + M[j, i], 5);
    }

    [Fact]
    public void Heads_up_at_10bb_the_small_blind_pushes_about_58_percent()
    {
        var chart = NashSolver.SolveHeadsUp(M, 10);
        Assert.InRange(chart.RangePct("push"), 56.0 / 100, 60.0 / 100);   // ±2 puntos sobre las tablas publicadas
        Assert.InRange(chart.RangePct("call"), 0.33, 0.45);
        var f = chart.Freq["push"];
        Assert.True(f[HandClasses.IndexOf("AA")] > 0.99);
        Assert.True(f[HandClasses.IndexOf("72o")] < 0.5);
    }

    [Fact]
    public void Ranges_widen_as_stacks_shrink()
    {
        var p5 = NashSolver.SolveHeadsUp(M, 5).RangePct("push");
        var p10 = NashSolver.SolveHeadsUp(M, 10).RangePct("push");
        var p20 = NashSolver.SolveHeadsUp(M, 20).RangePct("push");
        Assert.True(p5 > p10 && p10 > p20, $"{p5} {p10} {p20}");
        Assert.True(p5 > 0.65, $"p5={p5}");
        // Con ante el SB empuja más
        Assert.True(NashSolver.SolveHeadsUp(M, 10, ante: 0.125).RangePct("push") > p10);
    }

    [Fact]
    public void Calling_advantage_has_the_right_sign_for_strong_and_weak_hands()
    {
        var chart = NashSolver.SolveHeadsUp(M, 10);
        Assert.True(chart.Advantage["call"][HandClasses.IndexOf("AA")] > 0);
        Assert.True(chart.Advantage["call"][HandClasses.IndexOf("72o")] < 0);
        Assert.True(chart.Advantage["push"][HandClasses.IndexOf("AA")] > 0);
    }

    [Fact]
    public void Three_handed_chart_has_all_roles_and_the_button_pushes_wide_with_short_stacks()
    {
        var c = NashSolver.SolveThreeHanded(M, 8, iterations: 800);
        foreach (var role in new[] { "btn_push", "sb_call", "bb_call_btn", "sb_push", "bb_call_sb" })
            Assert.Equal(169, c.Freq[role].Length);
        Assert.InRange(c.RangePct("btn_push"), 0.3, 0.9);
        Assert.True(c.RangePct("sb_push") > c.RangePct("btn_push") * 0.8);   // el SB, sin el BTN, ataca al BB con rangos amplios
        Assert.True(c.RangePct("bb_call_btn") > c.RangePct("sb_call"));      // el BB paga más que el SB (ya tiene 1 bb dentro, y el SB aún tiene al BB detrás)
        Assert.True(c.Freq["btn_push"][HandClasses.IndexOf("AA")] > 0.99);
    }

    [Fact]
    public void Library_caches_matrix_and_charts_on_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vergil-nash-" + Guid.NewGuid().ToString("N"));
        try
        {
            var lib = new NashLibrary(dir);
            Assert.False(lib.MatrixReady);
            // Se inyecta la matriz ya calculada guardándola donde la biblioteca la busca
            Directory.CreateDirectory(dir);
            M.Save(Path.Combine(dir, $"nash-equity-v{EquityMatrix.Version}.bin"));
            Assert.True(lib.MatrixReady);

            var c1 = lib.Get("hu", 10.2);                    // se redondea a 10,0
            Assert.Equal(10, c1.StackBb);
            Assert.True(File.Exists(Path.Combine(dir, "nash-charts-v1.json")));

            var lib2 = new NashLibrary(dir);                 // otra instancia: lee de disco
            var c2 = lib2.Get("hu", 10);
            Assert.Equal(c1.RangePct("push"), c2.RangePct("push"), 9);
            Assert.Equal(25, NashLibrary.Quantize(80));
            Assert.Equal(1, NashLibrary.Quantize(0.2));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Corrupt_matrix_file_is_ignored()
    {
        var path = Path.Combine(Path.GetTempPath(), "vergil-bad-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        try { Assert.Null(EquityMatrix.TryLoad(path)); }
        finally { File.Delete(path); }
    }
}

[Collection("Rendimiento")]
public class NashPerformanceTests
{
    [Fact]
    public void Full_heads_up_table_from_1_to_25bb_solves_in_under_60_seconds()
    {
        var m = NashFixture.Matrix.Value;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stacks = Enumerable.Range(0, 49).Select(i => 1 + i * 0.5).ToList();
        var charts = stacks.AsParallel().Select(s => NashSolver.SolveHeadsUp(m, s)).ToList();
        sw.Stop();
        Assert.Equal(49, charts.Count);
        Assert.True(sw.Elapsed.TotalSeconds < 60, $"Tardó {sw.Elapsed.TotalSeconds:0.0} s");
    }
}

public class NashDeviationTests
{
    static string HuHand(int id, string cards, bool hero_shoves, DateTime t)
    {
        var s = $"PokerStars Hand #{id}: Tournament #77{id}, $9000+$1000 USD Hold'em No Limit - Level I (20/40) - {t:yyyy/MM/dd HH:mm:ss} CET [2026/03/01 14:00:00 ET]\n" +
                $"Table '77{id} 1' 2-max Seat #1 is the button\nSeat 1: Hero (320 in chips)\nSeat 2: Villain (320 in chips)\n" +
                $"Hero: posts small blind 20\nVillain: posts big blind 40\n*** HOLE CARDS ***\nDealt to Hero [{cards}]\n";
        s += hero_shoves
            ? "Hero: raises 280 to 320 and is all-in\nVillain: folds\nUncalled bet (280) returned to Hero\nHero collected 80 from pot\n"
            : "Hero: folds\nUncalled bet (20) returned to Villain\nVillain collected 40 from pot\n";
        return s + "*** SUMMARY ***\nTotal pot 80 | Rake 0\n\n\n\n";
    }

    static NashLibrary Lib(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "vergil-nashdev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        NashFixture.Matrix.Value.Save(Path.Combine(dir, $"nash-equity-v{EquityMatrix.Version}.bin"));
        return new NashLibrary(dir);
    }

    [Fact]
    public void A_hero_who_never_pushes_22_with_8bb_shows_up_in_deviations()
    {
        var t0 = new DateTime(2026, 4, 1, 12, 0, 0);
        var hands = Enumerable.Range(0, 12).Select(i => PokerStarsParser.Parse(HuHand(100 + i, "2c 2d", false, t0.AddMinutes(i)))!).ToList();
        hands.Add(PokerStarsParser.Parse(HuHand(200, "Ah Ad", true, t0.AddHours(1)))!);   // empujar AA es correcto
        hands.Add(PokerStarsParser.Parse(HuHand(201, "7c 2d", true, t0.AddHours(2)))!);    // empujar 72o con 8 bb: error
        using var db = Synth.Db(hands);
        var lib = Lib(out var dir);
        try
        {
            var report = NashDeviations.Analyze(db, lib);
            Assert.Equal(14, report.Spots);
            var top = report.ByHand.First(h => h.HandClass == "22");
            Assert.Equal(12, top.Deviations);
            Assert.True(top.CostBb > 0);
            Assert.Contains(report.Deviations, d => d.HandClass == "72o" && d.Acted);
            Assert.DoesNotContain(report.Deviations, d => d.HandClass == "AA");
            Assert.Contains("22", report.Deviations.First(d => d.HandClass == "22").Description);
            Assert.True(report.TotalCostBb > 0);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Cash_hands_and_deep_stacks_are_ignored()
    {
        using var db = Synth.Db(new[] { Synth.PreHand(1, DateTime.Now, "Hero", false) });
        var lib = Lib(out var dir);
        try { Assert.Equal(0, NashDeviations.Analyze(db, lib).Spots); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class DailyPicksTests
{
    static readonly DateTime Day = new(2026, 6, 1, 9, 0, 0);

    [Fact]
    public void Picks_five_hands_ranked_by_size_and_leaks_and_keeps_them_for_the_day()
    {
        using var db = Synth6.HeroFacing3Bets(60, 90);          // 60 manos; F3B 90 % = leak
        var engine = new StatEngine(db);
        var det = new LeakDetector(db, engine);
        var leaks = det.Detect("Hero", LeakDetector.LoadBaselines().Where(b => b.Id == "f3b").ToList());
        Assert.Single(leaks);
        var study = new StudyService(db);

        var first = DailyPicks.Get(db, study, leaks, det, null, Day);
        Assert.Equal(5, first.Count);
        Assert.All(first, p => Assert.Contains("leak", p.Reason));
        Assert.Equal(5, study.GetAll().Count);                   // entran en la cola de estudio

        var again = DailyPicks.Get(db, study, leaks, det, null, Day);
        Assert.Equal(first.Select(p => p.HandId), again.Select(p => p.HandId));    // estable durante el día

        var tomorrow = DailyPicks.Get(db, study, leaks, det, null, Day.AddDays(1));
        Assert.Empty(tomorrow.Select(p => p.HandId).Intersect(first.Select(p => p.HandId)));   // no repite las que ya están en la cola
    }

    [Fact]
    public void Empty_database_gives_no_picks()
    {
        using var db = new HudDatabase(":memory:");
        var det = new LeakDetector(db, new StatEngine(db));
        Assert.Empty(DailyPicks.Get(db, new StudyService(db), Array.Empty<Leak>(), det, null, Day));
    }
}

public class BalanceTests
{
    static int[] C(string s) => Cards.ParseMany(s);

    [Theory]
    [InlineData("Ah Ad", "Ac 7d 2h", Strength.Value)]        // set
    [InlineData("Ah Kd", "Ac Kc 2h", Strength.Value)]        // doble pareja
    [InlineData("Ah Kd", "Ac 7d 2h", Strength.Medium)]       // par alto
    [InlineData("Qh Qd", "Jc 7d 2h", Strength.Medium)]       // sobrepar
    [InlineData("Qh Qd", "Ac 7d 2h", Strength.Marginal)]     // pareja bajo el tablero
    [InlineData("9h 8d", "Ac 9d 2h", Strength.Marginal)]     // par bajo
    [InlineData("7h 6h", "Kh 5h 2c", Strength.Draw)]         // proyecto de color y de escalera
    [InlineData("Jh Td", "9c 8d 2h", Strength.Draw)]         // abierta
    [InlineData("Ah 3d", "Kc 9d 2s", Strength.Air)]
    [InlineData("Th 9h", "8h 7h 6h", Strength.Value)]        // escalera de color
    public void Classify_flop_hands(string hole, string board, Strength expected) =>
        Assert.Equal(expected, HandStrength.Classify(C(hole), C(board)));

    [Fact]
    public void Draws_do_not_exist_on_the_river_and_a_board_pair_is_not_a_hand()
    {
        Assert.Equal(Strength.Air, HandStrength.Classify(C("7h 6h"), C("Kh 5h 2c 9d 3s")));      // el proyecto falló
        Assert.Equal(Strength.Air, HandStrength.Classify(C("Ah 3d"), C("Kc Kd 2s")));            // solo el par del tablero
    }

    [Fact]
    public void Findings_flag_one_sided_betting_patterns_only_with_enough_hands()
    {
        var rows = new[]
        {
            new BalanceRow("r", "small", 30, 0, 28, 1, 0, 1, 5),     // 93 % valor medio con pequeñas
            new BalanceRow("f", "check-raise", 25, 24, 0, 0, 1, 0, 40),
            new BalanceRow("t", "large", 10, 10, 0, 0, 0, 0, 20),    // pocas manos: sin conclusión
            new BalanceRow("f", "medium", 40, 10, 10, 8, 6, 6, 3),   // equilibrada
        };
        var f = BalanceReport.BuildFindings(rows);
        Assert.Equal(2, f.Count);
        Assert.Contains(f, s => s.Contains("river") && s.Contains("pequeña") && s.Contains("valor medio"));
        Assert.Contains(f, s => s.Contains("check-raise"));
    }

    [Fact]
    public void Report_builds_from_real_hands_in_the_database()
    {
        using var db = Synth.Db(Enumerable.Range(0, 5).Select(i => Synth.PreHand(700 + i, new DateTime(2026, 5, 1, 10, 0, i), "Hero", true)).ToList());
        var rep = BalanceReport.Build(db);
        Assert.Equal(5, rep.Hands);
        var row = Assert.Single(rep.Rows);                       // Ah Kd apuesta 8 al flop 2c 7d Jh: aire
        Assert.Equal(("f", 5, 5), (row.Street, row.N, row.Air));
    }
}

public class ArchetypeTests
{
    static Hand StyleHand(long id, DateTime t, string villain, int action /*0 fold, 1 call, 2 raise*/)
    {
        var s = $"PokerStars Hand #{id}:  Hold'em No Limit ($1/$2 USD) - {t:yyyy/MM/dd H:mm:ss} CET [2026/03/01 14:00:00 ET]\n" +
                $"Table 'Styles' 6-max Seat #3 is the button\nSeat 1: {villain} ($200 in chips)\nSeat 2: Dummy ($200 in chips)\nSeat 3: Hero ($200 in chips)\n" +
                $"{villain}: posts small blind $1\nDummy: posts big blind $2\n*** HOLE CARDS ***\nDealt to Hero [9c 3d]\nHero: folds\n";
        s += action switch
        {
            0 => $"{villain}: folds\nUncalled bet ($1) returned to Dummy\nDummy collected $2 from pot\n",
            1 => $"{villain}: calls $1\nDummy: checks\n*** FLOP *** [2c 7d Jh]\nDummy: checks\n{villain}: checks\n*** TURN *** [2c 7d Jh] [3s]\nDummy: checks\n{villain}: checks\n" +
                 $"*** RIVER *** [2c 7d Jh 3s] [Kd]\nDummy: checks\n{villain}: checks\n*** SHOW DOWN ***\n{villain}: shows [Ac 5c] (high card)\nDummy: shows [Qc 4h] (high card)\n{villain} collected $4 from pot\n",
            _ => $"{villain}: raises $4 to $6\nDummy: folds\nUncalled bet ($4) returned to {villain}\n{villain} collected $4 from pot\n",
        };
        return PokerStarsParser.Parse(s + "*** SUMMARY ***\nTotal pot $4 | Rake $0\n\n\n\n")!;
    }

    /// <summary>Un villano que entra en <paramref name="vpip"/>% de las manos y sube en <paramref name="pfr"/>%, durante 200 manos.</summary>
    static IEnumerable<Hand> Villain(string name, int vpip, int pfr, long idBase)
    {
        var t = new DateTime(2026, 2, 1, 10, 0, 0);
        for (int i = 0; i < 200; i++)
        {
            int r = i % 100;
            int action = r < pfr ? 2 : r < vpip ? 1 : 0;
            yield return StyleHand(idBase + i, t.AddSeconds(idBase + i), name, action);
        }
    }

    static HudDatabase Population()
    {
        var hands = new List<Hand>();
        long id = 1000;
        for (int i = 0; i < 4; i++) { hands.AddRange(Villain($"Nit{i}", 10 + i, 8, id)); id += 1000; }
        for (int i = 0; i < 4; i++) { hands.AddRange(Villain($"Fish{i}", 55 + i, 5, id)); id += 1000; }
        for (int i = 0; i < 4; i++) { hands.AddRange(Villain($"Lag{i}", 38 + i, 30, id)); id += 1000; }
        return Synth.Db(hands);
    }

    [Fact]
    public void Training_finds_readable_clusters_and_classifies_new_players()
    {
        using var db = Population();
        var engine = new StatEngine(db);
        var priors = new PopulationPriors(db, engine);
        var model = ArchetypeModel.Train(db, engine, priors, "all", minHands: 150, k: 3);
        Assert.NotNull(model);
        Assert.Equal(3, model!.Clusters.Count);
        var labels = model.Clusters.Select(c => c.Label).ToList();
        Assert.Contains("Nit", labels);
        Assert.Contains("Fish", labels);
        Assert.InRange(model.Clusters.Single(c => c.Label == "Nit").Members, 4, 5);
        Assert.Equal(13, model.Clusters.Sum(c => c.Members));   // 12 villanos + el BB «Dummy», que nunca entra voluntariamente
        Assert.Equal(model.Clusters.Count, model.Clusters.Select(c => c.Color).Distinct().Count());

        var fish = model.Classify("Fish2", engine, priors, out var n)!.Value;
        Assert.Equal(200, n);
        Assert.Equal("Fish", fish.Archetype.Label);
        Assert.True(fish.Confidence > 0.6);

        // Una muestra mínima se acerca al cluster por sus propias manos, sin inventar confianza
        var vec = ArchetypeModel.Vector("Nit0", engine, priors, "all", out _);
        Assert.Equal("Nit", model.Classify(vec)!.Value.Archetype.Label);
    }

    [Fact]
    public void Too_few_opponents_gives_no_model_and_names_follow_rules()
    {
        using var db = Synth.Db(Villain("Solo", 30, 20, 1));
        var engine = new StatEngine(db);
        Assert.Null(ArchetypeModel.Train(db, engine, new PopulationPriors(db, engine), "all", minHands: 100));

        Assert.Equal("Nit", ArchetypeModel.Name(new[] { 0.10, 0.08, 0, 0, 0, 0, 0.5 }).Label);
        Assert.Equal("Fish", ArchetypeModel.Name(new[] { 0.55, 0.05, 0, 0, 0, 0, 0.2 }).Label);
        Assert.Equal("Maníaco", ArchetypeModel.Name(new[] { 0.55, 0.40, 0, 0, 0, 0, 0.6 }).Label);
        Assert.Equal("TAG", ArchetypeModel.Name(new[] { 0.20, 0.17, 0, 0, 0, 0, 0.5 }).Label);
        Assert.Equal("LAG", ArchetypeModel.Name(new[] { 0.33, 0.25, 0, 0, 0, 0, 0.5 }).Label);
        Assert.Equal("Pasivo", ArchetypeModel.Name(new[] { 0.35, 0.10, 0, 0, 0, 0, 0.3 }).Label);
    }

    [Fact]
    public void Service_persists_the_model_and_the_hud_badge_uses_it()
    {
        using var db = Population();
        var engine = new StatEngine(db);
        var priors = new PopulationPriors(db, engine);
        var svc = new ArchetypeService(db, engine, priors);
        Assert.Null(svc.Cached("all"));
        // Con la muestra mínima por defecto (200) los 12 villanos entran
        var m = svc.Ensure("all");
        Assert.NotNull(m);
        Assert.Same(m, svc.Ensure("all"));                    // el mismo día no se reentrena

        var svc2 = new ArchetypeService(db, engine, priors);  // otra instancia: lo lee de la base de datos
        var loaded = svc2.Cached("all");
        Assert.NotNull(loaded);
        Assert.Equal(m!.Clusters.Count, loaded!.Clusters.Count);

        var builder = new HudSnapshotBuilder(db, engine, priors) { Archetypes = _ => loaded };
        var hand = StyleHand(99999, DateTime.Now, "Fish1", 0);
        var seats = builder.Build(hand, HudProfiles.CreateDefaults().First());
        Assert.Equal("Fish", seats.First(s => s.Player == "Fish1").Badge.TrimEnd('?'));
    }
}

public class PopulationReportTests
{
    [Fact]
    public void Population_excludes_the_hero_and_compares_with_hero_values()
    {
        // 300 manos del héroe abriendo; los rivales (Villain1/2) casi nunca entran
        var hands = Enumerable.Range(0, 300).Select(i => Synth.PreHand(5000 + i, new DateTime(2026, 5, 1, 10, 0, 0).AddMinutes(i), "Hero", i % 2 == 0)).ToList();
        using var db = Synth.Db(hands);
        var engine = new StatEngine(db);
        var rows = PopulationReport.Build(db, engine, new[] { "cash" });
        var vpip = rows.Single(r => r.StatId == "vpip");
        Assert.Equal(300, vpip.HeroN);
        Assert.Equal(1.0, vpip.Hero!.Value, 6);               // el héroe sube siempre
        Assert.True(vpip.Pop < 0.7);                          // la población no incluye al héroe
        Assert.True(vpip.PopLo <= vpip.Pop && vpip.Pop <= vpip.PopHi);
        Assert.True(vpip.HeroDiffers);
        Assert.Empty(PopulationReport.Build(db, engine, new[] { "spin" }));   // sin manos de ese formato
    }
}
