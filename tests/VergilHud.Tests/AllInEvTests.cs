using VergilHud.Core;
using Xunit;

namespace VergilHud.Tests;

public class AllInEvTests
{
    static Dictionary<string, AllInResult>? Ev(string fixture)
    {
        var h = Synth.Case(fixture);
        return AllInEv.Compute(h, FactExtractor.Extract(h));
    }

    [Fact]
    public void Preflop_all_in_aces_against_kings_splits_the_pot_by_equity()
    {
        var r = Ev("cash_16_preflop_allin_showdown")!;
        Assert.Equal(new[] { "Hero", "Villain2" }, r.Keys.OrderBy(x => x).ToArray());   // Villain1 se retiró: no cuenta
        var e = EquityCalculator.Equity(new[] { Cards.ParseMany("Ac Ad"), Cards.ParseMany("Kc Kd") }, ReadOnlySpan<int>.Empty)[0];
        Assert.InRange(e, 0.80, 0.84);

        // Bote de 401 ($1 de la SB muerta + 2 × 200): EV del héroe = 401·e − 200
        Assert.Equal(401 * e - 200, r["Hero"].EvNet, 6);
        Assert.Equal(401 * (1 - e) - 200, r["Villain2"].EvNet, 6);
        Assert.Equal("preflop", r["Hero"].Street);
        Assert.Equal(e * 401 / 401, r["Hero"].Equity, 6);

        // Ganó los 201 reales, pero esperaba menos: el resto es suerte.
        Assert.Equal(201, r["Hero"].Net);
        Assert.True(r["Hero"].EvNet < r["Hero"].Net);
        Assert.True(r["Villain2"].EvNet > r["Villain2"].Net);
    }

    [Fact]
    public void Multiway_all_in_with_a_side_pot_uses_one_equity_per_layer_and_is_zero_sum()
    {
        var r = Ev("tour_05_allin_eliminations")!;
        Assert.Equal(3, r.Count);
        Assert.Equal("flop", r["Hero"].Street);   // el último all-in (Villain2) fue en el flop
        Assert.Equal(0, r.Values.Sum(x => x.EvNet), 6);          // sin rake: lo que gana uno lo pierden los otros
        Assert.Equal(0, r.Values.Sum(x => x.Net), 6);
        Assert.InRange(r["Hero"].Equity, 0.4, 0.97);
        Assert.True(r["Hero"].EvNet < r["Hero"].Net);            // ganó todo (+450), pero no era seguro
        Assert.True(r["Villain1"].EvNet > r["Villain1"].Net);    // perdió 150 con equity en el bote principal
    }

    [Fact]
    public void Open_shove_and_call_in_a_spin_is_computed_with_the_shown_cards()
    {
        var r = Ev("tour_02_spin_shove_call")!;
        Assert.Equal(new[] { "Hero", "Villain2" }, r.Keys.OrderBy(x => x).ToArray());
        Assert.Equal(0, r["Hero"].EvNet + r["Villain2"].EvNet + (-20), 6);   // Villain1 perdió su ciega pequeña (-20)
        Assert.InRange(r["Hero"].Equity, 0.25, 0.31);   // AQ contra KK: ~27 %, lo normal es perder
    }

    [Theory]
    [InlineData("cash_17_side_pot_allin")]        // tras el all-in siguen apostando otros: no es un all-in "limpio"
    [InlineData("cash_14_barrels_showdown_win")]  // showdown sin all-in
    [InlineData("cash_15_showdown_loss")]
    [InlineData("cash_03_open_steal_btn")]        // sin showdown
    [InlineData("tour_09_spin_limped_pot")]
    public void Hands_without_a_clean_all_in_have_no_ev(string fixture)
    {
        Assert.Null(Ev(fixture));
    }

    [Fact]
    public void Unknown_cards_mean_no_ev()
    {
        var h = Synth.Case("cash_16_preflop_allin_showdown");
        h.ShownCards.Remove("Villain2");
        Assert.Null(AllInEv.Compute(h, FactExtractor.Extract(h)));
    }

    [Fact]
    public void Rake_reduces_the_expected_share_of_the_pot()
    {
        var h = Synth.Case("cash_16_preflop_allin_showdown");
        h.Rake = 20;
        h.Collected["Hero"] = 381;   // 401 − 20
        var r = AllInEv.Compute(h, FactExtractor.Extract(h))!;
        var e = EquityCalculator.Equity(new[] { Cards.ParseMany("Ac Ad"), Cards.ParseMany("Kc Kd") }, ReadOnlySpan<int>.Empty)[0];
        Assert.Equal(381 * e - 200, r["Hero"].EvNet, 4);
    }

    [Fact]
    public void Ev_is_zero_sum_minus_rake_on_every_fixture_where_it_applies()
    {
        int applied = 0;
        foreach (var h in Fixtures.AllHands())
        {
            var facts = FactExtractor.Extract(h);
            var r = AllInEv.Compute(h, facts);
            if (r == null) continue;
            applied++;
            double sum = r.Values.Sum(x => x.EvNet) + facts.Where(f => !r.ContainsKey(f.Player)).Sum(f => f.Real("net") ?? 0);
            Assert.True(Math.Abs(sum + (double)h.Rake) < 0.02, $"mano {h.Id}: suma de EV {sum} ≠ −rake {h.Rake}");
        }
        Assert.True(applied >= 3, $"solo se aplicó a {applied} manos");
    }

    [Fact]
    public void Service_stores_the_ev_once_and_leaves_the_rest_untouched()
    {
        using var db = Synth.Db(Fixtures.AllHands());
        var engine = new StatEngine(db);
        int withEv = AllInEvService.ProcessAll(db);
        Assert.True(withEv >= 3);
        Assert.Equal(0, AllInEvService.ProcessPending(db));   // nada pendiente la segunda vez

        var hero16 = db.Query(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT net_bb, ev_net_bb FROM player_hand_facts WHERE hand_id = '100000016' AND player = 'Hero'";
            using var rd = cmd.ExecuteReader();
            rd.Read();
            return (Net: rd.GetDouble(0), Ev: rd.GetDouble(1));
        });
        Assert.Equal(100.5, hero16.Net, 3);                 // +201 con ciega grande 2
        Assert.InRange(hero16.Ev, 60, 70);                  // (401·0,8264 − 200)/2 ≈ 65,7 bb
        Assert.True(hero16.Ev < hero16.Net);

        // Una mano sin all-in conserva ev = neto real
        var plain = db.Query(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT net_bb, ev_net_bb FROM player_hand_facts WHERE hand_id = '100000014' AND player = 'Hero'";
            using var rd = cmd.ExecuteReader();
            rd.Read();
            return (rd.GetDouble(0), rd.GetDouble(1));
        });
        Assert.Equal(plain.Item1, plain.Item2);

        // Y ninguna mano quedó pendiente
        Assert.Equal(0L, db.Query(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM hands WHERE ev_state = 0";
            return (long)cmd.ExecuteScalar()!;
        }));
        Assert.NotEmpty(engine.Definitions);
    }
}

public class TournamentParsingTests
{
    [Fact]
    public void Finish_lines_give_place_and_prize()
    {
        var h = Synth.Case("tour_10_spin_heads_up_win");
        Assert.Equal(2, h.Results.Count);
        Assert.Contains(new TournamentResult("Villain1", 2, null), h.Results);
        Assert.Contains(new TournamentResult("Hero", 1, 20000m), h.Results);
        Assert.Equal(new[] { 9000m, 1000m }, h.BuyinParts);

        var bounty = Synth.Case("tour_07_bounty_level_xii");
        Assert.Equal(new[] { 4.60m, 4.60m, 0.80m }, bounty.BuyinParts);
    }

    [Fact]
    public void Money_finish_with_a_prize_is_read()
    {
        var text = "PokerStars Hand #9: Tournament #77, $5.00+$0.50 USD Hold'em No Limit - Level I (10/20) - 2026/03/01 20:00:00 CET [x]\n" +
                   "Table '77 1' 9-max Seat #1 is the button\nSeat 1: Hero (500 in chips)\nSeat 2: V (500 in chips)\n" +
                   "Hero: posts small blind 10\nV: posts big blind 20\n*** HOLE CARDS ***\nDealt to Hero [Ah Ad]\nHero: folds\n" +
                   "V collected 30 from pot\nHero finished the tournament in 14th place and received $9.50.\n" +
                   "V finished the tournament in 15th place\n*** SUMMARY ***\nTotal pot 30 | Rake 0\n";
        var h = PokerStarsParser.Parse(text)!;
        Assert.Contains(new TournamentResult("Hero", 14, 9.50m), h.Results);
        Assert.Contains(new TournamentResult("V", 15, null), h.Results);
    }

    [Fact]
    public void Tournaments_table_is_built_from_the_hands()
    {
        using var db = Synth.Db(Fixtures.AllHands());
        var rows = db.Query(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, format, buyin, rake, entrants, hands, finish_position, prize, hero FROM tournaments ORDER BY id";
            using var r = cmd.ExecuteReader();
            var l = new List<(string, string, double, double, object, long, object, object, object)>();
            while (r.Read()) l.Add((r.GetString(0), r.GetString(1), r.GetDouble(2), r.GetDouble(3), r.GetValue(4), r.GetInt64(5), r.GetValue(6), r.GetValue(7), r.GetValue(8)));
            return l;
        });
        // El Spin real: 25 manos, ganado, 20.000 de premio
        var real = rows.Single(x => x.Item1 == "4046679805");
        Assert.Equal("spin", real.Item2);
        Assert.Equal(10000, real.Item3);
        Assert.Equal(1000, real.Item4);
        Assert.Equal(26L, real.Item6);   // 25 manos reales + tour_08, que comparte id de torneo a propósito
        Assert.Equal(1L, Convert.ToInt64(real.Item7));
        Assert.Equal(20000.0, Convert.ToDouble(real.Item8));
        Assert.Equal(3L, Convert.ToInt64(real.Item5));
        // Un torneo en curso (solo una mano, sin línea de final) no tiene puesto
        var open = rows.Single(x => x.Item1 == "900006");
        Assert.True(open.Item7 is DBNull);
    }
}
