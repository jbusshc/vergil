using Microsoft.Data.Sqlite;
using VergilHud.Core;
using Xunit;

namespace VergilHud.Tests;

public class ReplayTests
{
    static HandReplay R(string fixture, Func<decimal, string>? fmt = null) => HandReplay.Build(Synth.Case(fixture), fmt);

    [Fact]
    public void A_postflop_hand_is_rebuilt_step_by_step()
    {
        var r = R("cash_02_check_raise_flop");
        var s = r.Steps;
        Assert.Equal(Enumerable.Range(0, s.Count), s.Select(x => x.Index));          // índices consecutivos
        Assert.Equal("Inicio de la mano", s[0].Description);
        Assert.True(s[^1].IsFinal);
        Assert.Contains(s, x => x.Description == "Villain1 pone la ciega pequeña 1");
        Assert.Equal(3, s.First(x => x.Description.Contains("ciega grande")).Pot);   // SB 1 + BB 2

        var deals = s.Where(x => x.IsDeal).ToList();
        Assert.Equal(new[] { Street.Flop, Street.Turn, Street.River }, deals.Select(d => d.Street));
        Assert.Equal(new[] { 3, 4, 5 }, deals.Select(d => d.Board.Count));
        Assert.Equal("Flop [Kd 7c 2h]", deals[0].Description);
        Assert.Equal(13, deals[0].Pot);                                              // bote preflop: 6 + 6 + 1

        // Solo se ven las cartas del héroe
        Assert.All(s.SelectMany(x => x.Seats).Where(x => x.Player != "Hero"), x => Assert.Null(x.Cards));
        Assert.Equal("Ks Qs", s[0].Seats.Single(x => x.IsHero).Cards);

        // Resultado: el héroe cobra 61 y los stacks cuadran
        var last = s[^1];
        Assert.Equal(61, last.Seats.Single(x => x.IsHero).Won);
        Assert.Equal(231, last.Seats.Single(x => x.IsHero).Stack);
        Assert.Equal(s[0].Seats.Sum(x => x.Stack), last.Seats.Sum(x => x.Stack));
        Assert.Equal(0, last.Pot);
    }

    [Fact]
    public void The_hero_decision_comes_with_pot_odds_and_spr()
    {
        var r = R("cash_02_check_raise_flop");
        // El héroe sube a 24 la apuesta de 6 de Villain2 en el flop
        var step = r.Steps.Single(x => x.Actor == "Hero" && x.Description.Contains("sube a 24"));
        Assert.NotNull(step.Decision);
        Assert.Equal(19, step.Decision!.PotBefore);                 // 13 del preflop + 6 de la apuesta
        Assert.Equal(6, step.Decision.ToCall);
        Assert.Equal(6.0 / 25 * 100, step.Decision.PotOddsPct, 6);  // 24 %
        Assert.Equal(188.0 / 19, step.Decision.Spr, 6);             // stack efectivo 188 / bote 19

        // Las acciones de los demás no llevan decisión
        Assert.All(r.Steps.Where(x => x.Actor != "Hero"), x => Assert.Null(x.Decision));
        // Si no hay nada que pagar, las odds son 0
        var check = r.Steps.First(x => x.Actor == "Hero" && x.Description.Contains("pasa"));
        Assert.Equal(0, check.Decision!.PotOddsPct);
    }

    [Fact]
    public void All_in_hands_show_equity_from_the_last_all_in_street_by_street()
    {
        var r = R("cash_16_preflop_allin_showdown");
        var s = r.Steps;
        int call = s.ToList().FindIndex(x => x.Description.Contains("Villain2 iguala") && x.Description.Contains("all-in"));
        Assert.True(call > 0);
        Assert.All(s.Take(call), x => Assert.Null(x.Equity));       // antes de que se cierre el all-in no hay equity

        var pre = s[call].Equity!;
        Assert.InRange(pre["Hero"], 0.80, 0.85);                    // AcAd contra KcKd
        Assert.Equal(1.0, pre.Values.Sum(), 9);
        Assert.Equal(2, pre.Count);                                 // Villain1 se retiró

        var flop = s.First(x => x.IsDeal && x.Street == Street.Flop);
        Assert.NotNull(flop.Equity);
        Assert.NotEqual(pre["Hero"], flop.Equity!["Hero"], 3);      // cambia con el tablero
        Assert.Null(s.First(x => x.IsDeal && x.Street == Street.River).Equity);   // con el tablero completo ya no hay incertidumbre
        Assert.Null(s[^1].Equity);

        // Al calcular el equity se muestran las cartas de los que siguen
        Assert.Equal("Kc Kd", s[call].Seats.Single(x => x.Player == "Villain2").Cards);
        // Y el showdown las revela
        Assert.Equal("Kc Kd", s.Single(x => x.Description == "Showdown").Seats.Single(x => x.Player == "Villain2").Cards);
    }

    [Fact]
    public void Multiway_all_in_with_side_pot_has_three_equities_and_the_total_prize()
    {
        var r = R("tour_05_allin_eliminations");
        // El equity empieza en el último all-in (el de Villain2 en el flop), no en el reparto del flop, que es anterior
        var allIn = r.Steps.First(x => x.Equity != null);
        Assert.Contains("Villain2 apuesta 150", allIn.Description);
        Assert.EndsWith("(all-in)", allIn.Description);
        Assert.Equal(3, allIn.Board.Count);
        var flopEq = allIn.Equity;
        Assert.Equal(3, flopEq!.Count);
        Assert.Equal(1.0, flopEq.Values.Sum(), 9);
        Assert.True(flopEq["Hero"] > flopEq["Villain1"]);           // KK por delante de JJ en un tablero 2-5-9
        Assert.Equal(750, r.Steps[^1].Seats.Single(x => x.IsHero).Won);   // 450 del bote principal + 300 del lateral
    }

    [Fact]
    public void Hands_without_a_clean_all_in_never_show_equity()
    {
        foreach (var f in new[] { "cash_14_barrels_showdown_win", "cash_15_showdown_loss", "cash_03_open_steal_btn", "cash_01_fold_to_bb" })
            Assert.All(R(f).Steps, x => Assert.Null(x.Equity));
    }

    [Fact]
    public void Uncalled_bets_dead_blinds_and_unit_formatting()
    {
        Assert.Contains(R("cash_03_open_steal_btn").Steps, x => x.Description == "Se devuelven 3 a Hero (apuesta sin igualar)");
        Assert.Contains(R("cash_18_dead_blind").Steps, x => x.Description == "Villain3 pone ciegas 3");

        // El formateador de cantidades se aplica a todas las descripciones
        var bb = R("cash_14_barrels_showdown_win", v => (v / 2).ToString("0.#") + "bb");
        Assert.Contains(bb.Steps, x => x.Description == "Hero sube a 3bb");
        Assert.Contains(bb.Steps, x => x.Description == "Hero apuesta 4bb (62% del bote)");
        Assert.Contains("gana 84.5bb", bb.Steps[^1].Description);
    }

    [Fact]
    public void Street_jumps_land_on_the_deal_steps()
    {
        var r = R("cash_14_barrels_showdown_win");
        Assert.Equal(0, r.FirstStepOf(Street.Preflop));
        Assert.Equal("Flop [Jd 7s 2c]", r.Steps[r.FirstStepOf(Street.Flop)].Description);
        Assert.StartsWith("Turn", r.Steps[r.FirstStepOf(Street.Turn)].Description);
        Assert.StartsWith("River", r.Steps[r.FirstStepOf(Street.River)].Description);
        Assert.Equal(-1, R("cash_03_open_steal_btn").FirstStepOf(Street.Turn));      // esa mano no llegó al turn
    }

    [Fact]
    public void Every_fixture_hand_replays_and_chips_are_conserved()
    {
        int n = 0;
        foreach (var h in Fixtures.AllHands())
        {
            var r = HandReplay.Build(h);
            n++;
            var first = r.Steps[0].Seats.Sum(x => x.Stack);
            var last = r.Steps[^1].Seats.Sum(x => x.Stack);
            Assert.True(Math.Abs(first - last - h.Rake) <= 0.011m, $"mano {h.Id}: stacks {first} → {last}, rake {h.Rake}");
            Assert.True(r.Steps[^1].IsFinal && r.Steps[^1].Pot == 0, $"mano {h.Id}: el bote final no es 0");
            Assert.All(r.Steps.SelectMany(x => x.Seats), s => Assert.True(s.Stack >= 0 && s.Bet >= 0, $"mano {h.Id}: cantidad negativa"));
            // el bote nunca baja salvo en el reparto final
            var pots = r.Steps.Take(r.Steps.Count - 1).Select(x => x.Pot).ToList();
            for (int i = 1; i < pots.Count; i++)
                Assert.True(pots[i] >= pots[i - 1] || r.Steps[i].Description.StartsWith("Se devuelven"), $"mano {h.Id}: el bote baja en el paso {i}");
        }
        Assert.True(n >= 60);
    }
}

public class HandTextTests
{
    [Fact]
    public void Text_is_readable_anonymous_and_in_big_blinds()
    {
        var t = HandText.Build(Synth.Case("cash_14_barrels_showdown_win"));
        Assert.Contains("Cash 1/2 — Hold'em No Limit — 3 jugadores", t);
        Assert.Contains("BTN  Hero (100bb)  [Ac Kd]", t);
        Assert.Contains("Flop [Jd 7s 2c] (bote 6.5bb):", t);
        Assert.Contains("Hero apuesta 4bb (62% del bote)", t);
        Assert.Contains("Muestra: BB [9c 9d]", t);
        Assert.Contains("Resultado: Hero gana 84.5bb", t);
        Assert.DoesNotContain("Villain", t);                       // ningún nick
    }

    [Fact]
    public void Text_can_keep_names_and_use_chips()
    {
        var t = HandText.Build(Synth.Case("cash_14_barrels_showdown_win"), inBb: false, anonymize: false);
        Assert.Contains("Villain2 iguala 8", t);
        Assert.Contains("Hero apuesta 8 (62% del bote)", t);
        Assert.DoesNotContain("bb", t);
    }

    [Fact]
    public void Tournament_header_and_every_fixture_produces_text()
    {
        Assert.Contains("Torneo (nivel 1, 20/40) — Hold'em No Limit — 3 jugadores", HandText.Build(Synth.Case("tour_02_spin_shove_call")));
        Assert.Contains("ante 100", HandText.Build(Synth.Case("tour_07_bounty_level_xii")));
        foreach (var h in Fixtures.AllHands())
        {
            var t = HandText.Build(h);
            Assert.Contains("Resultado:", t);
            Assert.Contains("Preflop:", t);
        }
    }
}

public class PredicateTests
{
    static readonly string[] Expressions =
    {
        "pf_vpip", "pf_3bet && !pf_3bet_pot", "position == \"BTN\"", "position != \"BTN\"", "position in [\"CO\", \"BTN\"]",
        "hole_cards != \"\"", "hole_cards == \"Ac Kd\" || pf_open", "r_bet_size_pct >= 50", "!(r_bet_size_pct > 100)",
        "f_bets + f_raises > 1", "net_bb < -3 && wtsd", "players_dealt in [2, 3]", "eff_stack_bb <= 12.5", "true", "false",
        "(pf_vpip || pf_pfr) && !f_saw", "f_cbet_size_pct > 40 && f_cbet_size_pct < 60", "stack_bucket == \"20-40\"",
        "pf_open_size_bb * 2 > 5", "wsd - wtsd == 0", "m_ratio < 20 || pf_shove",
    };

    [Fact]
    public void In_memory_predicate_matches_the_sql_on_every_row_of_every_fixture()
    {
        var hands = Fixtures.AllHands().ToList();
        using var db = Synth.Db(hands);
        var facts = hands.SelectMany(h => FactExtractor.Extract(h).Select(f => (h.Id, f))).ToList();
        Assert.True(facts.Count > 150);

        foreach (var expr in Expressions)
        {
            var test = StatDsl.CompilePredicate(expr);
            var sql = StatDsl.Compile(expr, "q");
            var fromSql = db.Query(c =>
            {
                using var cmd = c.CreateCommand();
                foreach (var (k, v) in sql.Parameters) cmd.Parameters.AddWithValue(k, v);
                cmd.CommandText = $"SELECT hand_id, player, {sql.Sql} <> 0 FROM player_hand_facts";
                using var r = cmd.ExecuteReader();
                var d = new Dictionary<(string, string), bool>();
                while (r.Read()) d[(r.GetString(0), r.GetString(1))] = r.GetInt32(2) == 1;
                return d;
            });
            int trues = 0;
            foreach (var (id, f) in facts)
            {
                bool expected = fromSql[(id, f.Player)];
                bool actual = test(f);
                Assert.True(expected == actual, $"'{expr}' en mano {id} · {f.Player}: SQL {expected}, memoria {actual}");
                if (actual) trues++;
            }
            if (expr is "pf_vpip" or "true" or "position == \"BTN\"") Assert.True(trues > 0, expr);
        }
    }

    [Fact]
    public void Predicate_validation_is_the_same_as_the_sql_compiler()
    {
        Assert.Throws<DslException>(() => StatDsl.CompilePredicate("no_existe"));
        Assert.Throws<DslException>(() => StatDsl.CompilePredicate("pf_vpip &&"));
        Assert.Throws<DslException>(() => StatDsl.CompilePredicate("position"));            // texto suelto
        Assert.Throws<DslException>(() => StatDsl.CompilePredicate("position == 3"));
        Assert.Throws<DslException>(() => StatDsl.CompilePredicate(""));
        Assert.Throws<DslException>(() => StatDsl.CompilePredicate("'; DROP TABLE hands"));
    }

    [Fact]
    public void A_missing_value_makes_the_condition_false_even_when_negated()
    {
        var f = new PlayerHandFacts("X", 1);           // r_bet_size_pct y hole_cards son NULL
        Assert.False(StatDsl.CompilePredicate("r_bet_size_pct > 100")(f));
        Assert.False(StatDsl.CompilePredicate("!(r_bet_size_pct > 100)")(f));   // NOT NULL sigue siendo NULL → falso
        Assert.False(StatDsl.CompilePredicate("hole_cards == \"Ah Kd\"")(f));
        Assert.False(StatDsl.CompilePredicate("hole_cards != \"Ah Kd\"")(f));
        Assert.True(StatDsl.CompilePredicate("r_bet_size_pct > 100 || true")(f));   // true OR NULL = true
        Assert.False(StatDsl.CompilePredicate("r_bet_size_pct > 100 && false")(f));
    }
}

public class AutoNoteTests
{
    // Villain3 abre, Hero 3-betea, Villain3 hace 4-bet all-in y Hero paga: se ven las cartas.
    const string FourBetShowdown = """
PokerStars Hand #300000001:  Hold'em No Limit ($1/$2 USD) - 2026/03/01 20:00:00 CET [2026/03/01 14:00:00 ET]
Table 'Alpha' 6-max Seat #4 is the button
Seat 1: Villain1 ($200 in chips)
Seat 2: Villain2 ($200 in chips)
Seat 3: Villain3 ($200 in chips)
Seat 4: Hero ($200 in chips)
Villain1: posts small blind $1
Villain2: posts big blind $2
*** HOLE CARDS ***
Dealt to Hero [Kc Kd]
Villain3: raises $4 to $6
Hero: raises $14 to $20
Villain1: folds
Villain2: folds
Villain3: raises $180 to $200 and is all-in
Hero: calls $180 and is all-in
*** FLOP *** [2c 7d Th]
*** TURN *** [2c 7d Th] [4s]
*** RIVER *** [2c 7d Th 4s] [9c]
*** SHOW DOWN ***
Villain3: shows [Ac Ad] (a pair of Aces)
Hero: shows [Kc Kd] (a pair of Kings)
Villain3 collected $403 from pot
*** SUMMARY ***
Total pot $403 | Rake $0



""";

    static (Hand, List<PlayerHandFacts>) Item(string text)
    {
        var h = PokerStarsParser.Parse(text)!;
        return (h, FactExtractor.Extract(h));
    }

    [Fact]
    public void Built_in_rules_all_validate_and_the_fingerprint_is_stable()
    {
        var rules = AutoNoteRules.Load();
        Assert.True(rules.Count >= 6);
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        foreach (var r in rules) AutoNoteRules.Validate(r);
        var json = AutoNoteRules.LoadRawJson();
        Assert.Equal(AutoNoteRules.Fingerprint(json), AutoNoteRules.Fingerprint(json));
        Assert.NotEqual(AutoNoteRules.Fingerprint(json), AutoNoteRules.Fingerprint(json + " "));
    }

    [Fact]
    public void Bad_rules_are_rejected_with_a_clear_message()
    {
        Assert.Throws<DslException>(() => AutoNoteRules.Validate(new AutoNoteRule { Id = "a", When = "no_existe", Text = "x" }));
        var ex = Assert.Throws<ArgumentException>(() => AutoNoteRules.Validate(new AutoNoteRule { Id = "a", When = "pf_vpip", Text = "valor {inventado}" }));
        Assert.Contains("inventado", ex.Message);
        Assert.Throws<ArgumentException>(() => AutoNoteRules.Validate(new AutoNoteRule { Id = "", When = "pf_vpip", Text = "x" }));
        Assert.Throws<ArgumentException>(() => AutoNoteRules.Validate(new AutoNoteRule { Id = "a", When = "pf_vpip", Text = "" }));
        using var db = new HudDatabase(":memory:");
        Assert.Throws<ArgumentException>(() => new AutoNoteService(db, "[{\"id\":\"x\",\"when\":\"pf_vpip\",\"text\":\"{nada}\"}]"));
    }

    [Fact]
    public void A_villain_four_bet_with_shown_cards_creates_a_note_and_the_hero_does_not()
    {
        using var db = new HudDatabase(":memory:");
        var svc = new AutoNoteService(db);
        var item = Item(FourBetShowdown);
        Assert.Equal(1, svc.Process(new[] { item }));
        var notes = svc.GetNotes("Villain3");
        var n = Assert.Single(notes);
        Assert.Equal("four_bet_shown", n.RuleId);
        Assert.Equal("4-bet desde CO con Ac Ad", n.Text);
        Assert.Equal("300000001", n.HandId);
        Assert.Empty(svc.GetNotes("Hero"));                    // el héroe no recibe notas por defecto
        Assert.Empty(svc.GetNotes("Villain1"));
    }

    [Fact]
    public void Processing_the_same_hand_twice_does_not_duplicate_notes()
    {
        using var db = new HudDatabase(":memory:");
        var svc = new AutoNoteService(db);
        var item = Item(FourBetShowdown);
        Assert.Equal(1, svc.Process(new[] { item }));
        Assert.Equal(0, svc.Process(new[] { item }));
        Assert.Equal(1, svc.Count());
    }

    [Fact]
    public void Notes_are_generated_automatically_when_hands_are_imported()
    {
        using var db = new HudDatabase(":memory:");
        var svc = new AutoNoteService(db);
        db.HandsInserted += items => svc.Process(items);
        var (h, f) = Item(FourBetShowdown);
        db.InsertHands(new[] { (h, f) });
        Assert.Single(svc.GetNotes("Villain3"));
        db.InsertHands(new[] { (h, f) });                      // la mano ya existía: no hay aviso ni duplicado
        Assert.Equal(1, svc.Count());
    }

    [Fact]
    public void Custom_rules_render_facts_and_can_include_the_hero()
    {
        using var db = Synth.Db(new[] { Synth.Case("cash_22_overbet_river") });
        var json = "[{\"id\":\"ob\",\"when\":\"r_bet_size_pct >= 150\",\"text\":\"Overbet {r_bet_size_pct}% desde {position}\"}," +
                   "{\"id\":\"yo\",\"when\":\"pf_open\",\"text\":\"Abrí a {pf_open_size_bb}bb\",\"includeHero\":true}]";
        var svc = new AutoNoteService(db, json);
        var h = Synth.Case("cash_22_overbet_river");
        svc.Process(new[] { (h, FactExtractor.Extract(h)) });
        Assert.Equal("Overbet 307.7% desde BB", Assert.Single(svc.GetNotes("Villain2")).Text);
        Assert.Equal("Abrí a 3bb", Assert.Single(svc.GetNotes("Hero")).Text);
    }

    [Fact]
    public void Rebuild_regenerates_from_the_stored_hands_and_only_when_rules_change()
    {
        var h = PokerStarsParser.Parse(FourBetShowdown)!;
        using var db = Synth.Db(new[] { h });
        var svc = new AutoNoteService(db);
        Assert.True(svc.EnsureUpToDate());                      // nunca se generaron: lo hace
        Assert.Equal(1, svc.Count());
        Assert.False(svc.EnsureUpToDate());                     // ya están al día

        db.Query(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM auto_notes"; return cmd.ExecuteNonQuery(); });
        Assert.Equal(1, svc.Rebuild());                         // se regeneran desde las manos guardadas

        var other = new AutoNoteService(db, "[{\"id\":\"nueva\",\"when\":\"pf_pfr\",\"text\":\"sube {position}\"}]");
        Assert.True(other.EnsureUpToDate());                    // reglas distintas: se regenera todo
        Assert.All(other.GetNotes("Villain3"), n => Assert.Equal("nueva", n.RuleId));
    }
}

public class NotesTests
{
    [Fact]
    public void Notes_and_colors_are_saved_updated_and_cleared()
    {
        using var db = new HudDatabase(":memory:");
        var n = new NotesService(db);
        Assert.True(n.Get("Pepe").IsEmpty);

        n.Set("Pepe", "  paga todo en el river  ", "red");
        var p = n.Get("Pepe");
        Assert.Equal("paga todo en el river", p.Note);
        Assert.Equal("red", p.ColorLabel);

        n.Set("Pepe", "cambia de marcha", null);                 // solo nota
        Assert.Equal(new PlayerNote("Pepe", "cambia de marcha", null), n.Get("Pepe"));
        n.Set("Pepe", null, "green");                            // solo color
        Assert.Equal(new PlayerNote("Pepe", null, "green"), n.Get("Pepe"));

        Assert.Single(n.ListAnnotated());
        n.Set("Pepe", "", "");                                   // todo vacío: se borra la ficha
        Assert.True(n.Get("Pepe").IsEmpty);
        Assert.Empty(n.ListAnnotated());
    }

    [Fact]
    public void Invalid_input_is_rejected_and_long_notes_are_capped()
    {
        using var db = new HudDatabase(":memory:");
        var n = new NotesService(db);
        Assert.Throws<ArgumentException>(() => n.Set("X", "a", "fucsia"));
        Assert.Throws<ArgumentException>(() => n.Set("", "a", "red"));
        n.Set("Largo", new string('x', 10_000), null);
        Assert.Equal(4000, n.Get("Largo").Note!.Length);
        n.Set("Nick con 'comillas' y \"dobles\"", "ok; DROP TABLE players", "blue");   // los datos van como parámetros
        Assert.Equal("blue", n.Get("Nick con 'comillas' y \"dobles\"").ColorLabel);
        Assert.Equal(2, n.ListAnnotated().Count);
    }

    [Fact]
    public void GetMany_returns_only_annotated_players()
    {
        using var db = new HudDatabase(":memory:");
        var n = new NotesService(db);
        n.Set("A", "nota", null);
        n.Set("B", null, "purple");
        var m = n.GetMany(new[] { "A", "B", "C" });
        Assert.Equal(new[] { "A", "B" }, m.Keys.OrderBy(x => x).ToArray());
        Assert.Empty(n.GetMany(Array.Empty<string>()));
    }

    [Fact]
    public void Notes_survive_closing_and_reopening_the_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vergil-notes-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new HudDatabase(path)) new NotesService(db).Set("Persistente", "sigue ahí", "orange");
            SqliteConnection.ClearAllPools();
            using var again = new HudDatabase(path);
            Assert.Equal(new PlayerNote("Persistente", "sigue ahí", "orange"), new NotesService(again).Get("Persistente"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }

    [Fact]
    public void The_hud_snapshot_carries_each_players_color_and_note()
    {
        var t0 = new DateTime(2026, 3, 1, 10, 0, 0);
        var hands = Enumerable.Range(0, 5).Select(i => Synth.PreHand(9100 + i, t0.AddMinutes(i), "Hero", true)).ToList();
        using var db = Synth.Db(hands);
        var notes = new NotesService(db);
        notes.Set("Villain1", "siempre se retira al flop", "red");
        var engine = new StatEngine(db);
        var builder = new HudSnapshotBuilder(db, engine, new PopulationPriors(db, engine), notes);
        var seats = builder.Build(hands[^1], HudProfiles.CreateDefaults()[1]);
        var v1 = seats.Single(s => s.Player == "Villain1");
        Assert.Equal("red", v1.ColorLabel);
        Assert.Equal("siempre se retira al flop", v1.Note);
        Assert.Null(seats.Single(s => s.Player == "Villain2").ColorLabel);
        Assert.Null(new HudSnapshotBuilder(db, engine, new PopulationPriors(db, engine)).Build(hands[^1], HudProfiles.CreateDefaults()[1])[0].Note);
    }
}

public class HandTagTests
{
    [Fact]
    public void Tags_are_normalized_idempotent_and_removable()
    {
        using var db = Synth.Db(Fixtures.AllHands());
        var n = new NotesService(db);
        Assert.True(n.AddTag("100000014", "  Estudiar "));
        Assert.False(n.AddTag("100000014", "estudiar"));        // misma etiqueta: no se repite
        Assert.True(n.AddTag("100000014", "Error"));
        Assert.Equal(new[] { "error", "estudiar" }, n.GetTags("100000014"));

        Assert.True(n.RemoveTag("100000014", "ERROR"));
        Assert.False(n.RemoveTag("100000014", "error"));
        Assert.Equal(new[] { "estudiar" }, n.GetTags("100000014"));
        Assert.Empty(n.GetTags("100000015"));
    }

    [Fact]
    public void Bad_tags_are_rejected_and_listings_work()
    {
        using var db = Synth.Db(Fixtures.AllHands());
        var n = new NotesService(db);
        Assert.Throws<ArgumentException>(() => n.AddTag("1", "   "));
        Assert.Throws<ArgumentException>(() => n.AddTag("1", new string('x', 41)));
        n.AddTag("100000014", "bluff");
        n.AddTag("100000015", "bluff");
        n.AddTag("100000016", "estudiar");
        Assert.Equal(new[] { ("bluff", 2), ("estudiar", 1) }, n.AllTags().ToArray());
        Assert.Equal(2, n.HandsWithTag("BLUFF").Count);
    }

    [Fact]
    public void Hand_list_filters_by_format_tag_text_and_showdown()
    {
        using var db = Synth.Db(Fixtures.AllHands());
        var a = new Analytics(db);
        var n = new NotesService(db);
        n.AddTag("100000014", "estudiar");
        n.AddTag("100000014", "valor");

        var all = a.ListHands(new HandFilter());
        Assert.True(all.Count > 60);
        Assert.True(all.Zip(all.Skip(1)).All(p => p.First.Time >= p.Second.Time));       // más recientes primero

        Assert.All(a.ListHands(new HandFilter("cash")), r => Assert.Contains(r.Format, new[] { "cash", "zoom" }));
        Assert.All(a.ListHands(new HandFilter("tournament")), r => Assert.Contains(r.Format, new[] { "mtt", "spin", "sng" }));

        var tagged = Assert.Single(a.ListHands(new HandFilter(Tag: "Estudiar")));
        Assert.Equal("100000014", tagged.HandId);
        Assert.Equal("estudiar, valor", tagged.Tags);
        Assert.Equal("Ac Kd", tagged.Cards);

        var byCards = a.ListHands(new HandFilter(Text: "Ac Kd"));
        Assert.Contains(byCards, r => r.HandId == "100000014");
        Assert.Contains(a.ListHands(new HandFilter(Text: "100000015")), r => r.HandId == "100000015");

        var sd = a.ListHands(new HandFilter(OnlyShowdown: true));
        Assert.NotEmpty(sd);
        Assert.True(sd.Count < all.Count);
        Assert.Equal(3, a.ListHands(new HandFilter(Limit: 3)).Count);
        Assert.Empty(a.ListHands(new HandFilter(Tag: "no-existe")));
    }

    [Fact]
    public void The_replayer_is_blocked_while_tables_are_open_and_free_otherwise()
    {
        var play = new PlaySessionState();
        Assert.Null(play.BlockedMessage(new ReplayerAccess()));
        play.Update(2);
        Assert.Equal("Disponible al cerrar las mesas", play.BlockedMessage(new ReplayerAccess()));
        play.Update(0);
        Assert.Null(play.BlockedMessage(new ReplayerAccess()));
    }

    [Fact]
    public void Migration_4_removes_duplicates_before_adding_the_unique_indexes()
    {
        using var db = new HudDatabase(":memory:");
        db.Query(c =>
        {
            foreach (var sql in new[] { "DROP INDEX ux_auto_notes", "DROP INDEX ux_hand_tags" })
            { using var d = c.CreateCommand(); d.CommandText = sql; d.ExecuteNonQuery(); }
            for (int i = 0; i < 3; i++)
            {
                using var a = c.CreateCommand();
                a.CommandText = "INSERT INTO auto_notes(player, created_at, rule_id, text, hand_id) VALUES('P','2026-01-01T00:00:00','r','t','h1')";
                a.ExecuteNonQuery();
                using var b = c.CreateCommand();
                b.CommandText = "INSERT INTO hand_tags(hand_id, tag, created_at) VALUES('h1','x','2026-01-01T00:00:00')";
                b.ExecuteNonQuery();
            }
            using var tx = c.BeginTransaction();
            new M004Notes().Up(c, tx);
            tx.Commit();
            using var q = c.CreateCommand();
            q.CommandText = "SELECT (SELECT COUNT(*) FROM auto_notes), (SELECT COUNT(*) FROM hand_tags)";
            using var r = q.ExecuteReader();
            r.Read();
            Assert.Equal((1L, 1L), (r.GetInt64(0), r.GetInt64(1)));
            return 0;
        });
        Assert.Equal(Migrator.Latest, db.SchemaVersion);
        Assert.Equal(Migrator.Latest, db.SchemaVersion);
    }
}
