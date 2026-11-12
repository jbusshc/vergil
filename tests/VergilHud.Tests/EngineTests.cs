using Microsoft.Data.Sqlite;
using VergilHud.Core;
using Xunit;

namespace VergilHud.Tests;

public class EngineTests
{
    static HudDatabase DbWith(params string[] hands)
    {
        var db = new HudDatabase(":memory:");
        var items = hands.SelectMany(PokerStarsParser.ParseAll).Select(h => (h, FactExtractor.Extract(h))).ToList();
        db.InsertHands(items);
        return db;
    }

    static HudDatabase DbAll()
    {
        var db = new HudDatabase(":memory:");
        db.InsertHands(Fixtures.AllHands().Select(h => (h, FactExtractor.Extract(h))).ToList());
        return db;
    }

    [Fact]
    public void Dsl_vpip_pfr_3bet_match_the_phase1_calculator_on_all_fixtures()
    {
        var hands = Fixtures.AllHands().ToList();
        Assert.True(hands.Count >= 40);
        using var db = new HudDatabase(":memory:");
        db.InsertHands(hands.Select(h => (h, FactExtractor.Extract(h))).ToList());
        var engine = new StatEngine(db);

        // Oráculo: el cálculo independiente de la Fase 1, sumado por jugador.
        var legacy = new Dictionary<string, (int Hands, int Vpip, int Pfr, int Tb, int TbOpp, int Cb, int CbOpp)>();
        foreach (var h in hands)
            foreach (var s in StatsCalculator.Compute(h))
            {
                legacy.TryGetValue(s.Player, out var a);
                legacy[s.Player] = (a.Hands + 1, a.Vpip + (s.Vpip ? 1 : 0), a.Pfr + (s.Pfr ? 1 : 0),
                    a.Tb + (s.ThreeBet ? 1 : 0), a.TbOpp + (s.ThreeBetOpp ? 1 : 0), a.Cb + (s.Cbet ? 1 : 0), a.CbOpp + (s.CbetOpp ? 1 : 0));
            }

        var players = legacy.Keys.ToList();
        var got = engine.GetMany(players, new[] { "vpip", "pfr", "3bet", "cbet_flop" });
        foreach (var p in players)
        {
            var e = legacy[p];
            Assert.Equal(e.Vpip, got[p]["vpip"].Numerator);
            Assert.Equal(e.Hands, got[p]["vpip"].Denominator);
            Assert.Equal(e.Pfr, got[p]["pfr"].Numerator);
            Assert.Equal(e.Tb, got[p]["3bet"].Numerator);
            Assert.Equal(e.TbOpp, got[p]["3bet"].Denominator);
            Assert.Equal(e.Cb, got[p]["cbet_flop"].Numerator);
            Assert.Equal(e.CbOpp, got[p]["cbet_flop"].Denominator);
        }
    }

    [Fact]
    public void Af_uses_sums_and_bb100_uses_scale()
    {
        using var db = DbWith(Fixtures.Text("cases/cash_02_check_raise_flop"));
        var e = new StatEngine(db);
        // Villain2: 1 apuesta (flop) y 1 call (flop) -> AF = 1 / 1
        var af = e.Get("Villain2", "af");
        Assert.Equal(1, af.Numerator);
        Assert.Equal(1, af.Denominator);
        Assert.Equal(1.0, af.Value!.Value, 6);
        // Hero no paga nunca: sin denominador no hay AF (no se inventa un número)
        Assert.Null(e.Get("Hero", "af").Value);
        var bb = e.Get("Hero", "bb100");
        Assert.Equal(1, bb.Denominator);
        Assert.Equal(31.0 * 100 / 2, bb.Value!.Value, 6); // neto +31 con ciega grande 2 -> 15.5 bb -> 1550 bb/100
    }

    [Fact]
    public void Grouped_query_returns_all_players_and_stats_at_once()
    {
        using var db = DbAll();
        var e = new StatEngine(db);
        var players = new[] { "Hero", "Villain1", "Villain2", "Nadie" };
        var ids = e.Definitions.Select(d => d.Id).ToList();
        var r = e.GetMany(players, ids);
        Assert.Equal(players.Length, r.Count);
        Assert.All(r.Values, m => Assert.Equal(ids.Count, m.Count));
        Assert.Equal(0, r["Nadie"]["vpip"].N);
        Assert.True(r["Hero"]["vpip"].N > 5);
    }

    [Fact]
    public void Cache_serves_repeat_calls_and_is_invalidated_by_new_hands()
    {
        using var db = DbWith(Fixtures.Text("cases/cash_01_fold_to_bb"));
        var e = new StatEngine(db);
        var before = e.Get("Hero", "vpip");
        Assert.True(e.Cache.Count > 0);
        Assert.Equal(before, e.Get("Hero", "vpip"));

        var more = PokerStarsParser.ParseAll(Fixtures.Text("cases/cash_03_open_steal_btn")).Select(h => (h, FactExtractor.Extract(h))).ToList();
        db.InsertHands(more);
        var after = e.Get("Hero", "vpip");
        Assert.Equal(before.Denominator + 1, after.Denominator);
    }

    [Fact]
    public void Filters_by_format_position_last_hands_and_vs_player()
    {
        using var db = DbAll();
        var e = new StatEngine(db);
        var all = e.Get("Hero", "vpip");
        var cash = e.Get("Hero", "vpip", new StatFilter { Format = "cash" });
        var spin = e.Get("Hero", "vpip", new StatFilter { Format = "spin" });
        Assert.Equal(all.N, cash.N + spin.N + e.Get("Hero", "vpip", new StatFilter { Format = "mtt" }).N);
        Assert.True(cash.N > 0 && spin.N > 0);

        var btn = e.Get("Hero", "vpip", new StatFilter { Positions = new[] { "BTN" } });
        Assert.InRange(btn.N, 1, all.N - 1);

        var last3 = e.Get("Hero", "vpip", new StatFilter { LastHands = 3 });
        Assert.Equal(3, last3.N);

        var vs = e.Get("Hero", "vpip", new StatFilter { VsPlayer = "Villain1" });
        Assert.InRange(vs.N, 1, all.N);

        var empty = e.Get("Hero", "vpip", new StatFilter { From = new DateTime(2000, 1, 1), To = new DateTime(2000, 1, 2) });
        Assert.Equal(0, empty.N);
    }

    [Fact]
    public void Custom_stat_can_be_registered_and_bad_one_is_rejected()
    {
        using var db = DbAll();
        var e = new StatEngine(db);
        e.Register(new StatDefinition { Id = "btn_open", Label = "BTN open", Numerator = "pf_open && position == \"BTN\"", Denominator = "pf_open_opp && position == \"BTN\"" });
        Assert.True(e.Get("Hero", "btn_open").Denominator >= 1);
        Assert.Throws<DslException>(() => e.Register(new StatDefinition { Id = "malo", Numerator = "pf_vpip &&", Denominator = "1" }));
        Assert.Throws<KeyNotFoundException>(() => e.Get("Hero", "no_existe"));
    }

    [Fact]
    public void Stat_formatting()
    {
        var pct = new StatDefinition { Format = "pct0" };
        Assert.Equal("67", pct.FormatValue(new StatValue(2, 3)));
        Assert.Equal("-", pct.FormatValue(new StatValue(0, 0)));
        Assert.Equal("1.5", new StatDefinition { Format = "dec1" }.FormatValue(new StatValue(3, 2)));
        Assert.Equal("12.5", new StatDefinition { Format = "dec1" }.FormatValue(new StatValue(0.125, 1, 100)));
    }

    // ---------- migración de la base de datos de la Fase 1 ----------
    [Fact]
    public void Phase1_database_is_migrated_and_reimported_from_raw()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vergil-mig-{Guid.NewGuid():N}.db");
        try
        {
            var raw = Fixtures.Text("cases/cash_02_check_raise_flop").Trim();
            using (var c = new SqliteConnection($"Data Source={path}"))
            {
                c.Open();
                using (var tx = c.BeginTransaction())
                {
                    new M001Initial().Up(c, tx);
                    tx.Commit();
                }
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO hands(hand_id,site,table_name,game,is_tournament,played_at,small_blind,big_blind,max_seats,hero,hero_cards,board,raw) " +
                                  "VALUES('1','PokerStars','x','g',0,'2024-01-01',1,2,6,'Hero','Ah Kd','','' || $raw)";
                cmd.Parameters.AddWithValue("$raw", raw);
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            using var db = new HudDatabase(path);
            Assert.Equal(Migrator.Latest, db.SchemaVersion);
            Assert.Equal(1, db.GetHandCount());
            var hero = db.GetPlayerStats("Hero");
            Assert.Equal(1, hero.Hands);
            Assert.NotEqual(0, new StatEngine(db).Get("Hero", "vpip").N);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }

    [Fact]
    public void Fresh_database_is_at_latest_version_and_reopening_is_idempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vergil-new-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new HudDatabase(path))
            {
                Assert.Equal(Migrator.Latest, db.SchemaVersion);
                db.InsertHands(PokerStarsParser.ParseAll(Fixtures.Text("cases/cash_01_fold_to_bb")).Select(h => (h, FactExtractor.Extract(h))).ToList());
            }
            SqliteConnection.ClearAllPools();
            using var again = new HudDatabase(path);
            Assert.Equal(Migrator.Latest, again.SchemaVersion);
            Assert.Equal(1, again.GetHandCount());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }

    [Fact]
    public void Importer_reads_new_text_incrementally_and_logs_bad_hands()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vergil-imp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "HH.txt");
            using var db = new HudDatabase(":memory:");
            using var imp = new HandHistoryImporter(db);

            var h1 = Fixtures.Text("cases/cash_01_fold_to_bb");
            var h2 = Fixtures.Text("cases/cash_03_open_steal_btn");
            File.WriteAllText(file, h1 + h2[..120]); // la segunda mano llega a medias
            Assert.Equal(1, imp.ImportFile(file));
            File.WriteAllText(file, h1 + h2);
            Assert.Equal(1, imp.ImportFile(file));
            Assert.Equal(0, imp.ImportFile(file));
            Assert.Equal(2, db.GetHandCount());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Importer_does_not_lose_the_first_hand_of_a_utf8_bom_file()
    {
        // Regresión: la app real perdía la 1.ª mano de cada archivo porque PokerStars escribe un BOM al principio.
        var dir = Path.Combine(Path.GetTempPath(), $"vergil-bom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "HH.txt");
            var h1 = Fixtures.Text("cases/cash_01_fold_to_bb");
            var h2 = Fixtures.Text("cases/cash_03_open_steal_btn");
            var h3 = Fixtures.Text("cases/cash_04_utg_open_call_cbet_fold");
            var bom = new System.Text.UTF8Encoding(true);
            File.WriteAllText(file, h1 + h2, bom);
            using var db = new HudDatabase(":memory:");
            using var imp = new HandHistoryImporter(db);
            Assert.Equal(2, imp.ImportFile(file));
            Assert.Equal(0, imp.ImportFile(file));
            File.AppendAllText(file, h3, bom); // el offset guardado tiene que seguir cuadrando con los bytes
            Assert.Equal(1, imp.ImportFile(file));
            Assert.Equal(3, db.GetHandCount());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Importer_warns_once_about_non_english_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vergil-lang-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "HH.txt");
            File.WriteAllText(file, "Mano de PokerStars n.º 1234: Hold'em No Limit (10/20) - 2026/11/12\n" + new string('x', 400));
            using var db = new HudDatabase(":memory:");
            using var imp = new HandHistoryImporter(db);
            var warnings = new List<string>();
            imp.ImportError += warnings.Add;
            Assert.Equal(0, imp.ImportFile(file));
            Assert.Single(warnings);
            Assert.Contains("inglés", warnings[0]);
        }
        finally { Directory.Delete(dir, true); }
    }
}
