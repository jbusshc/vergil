using System.Text.Json;
using VergilHud.Core;
using Xunit;

namespace VergilHud.Tests;

/// <summary>
/// Cada caso es NAME.txt (una mano) + NAME.json con los hechos esperados, escritos a mano a partir de las reglas del juego.
/// Solo se comprueban las claves presentes en el JSON; "_hand" valida datos de la mano y "_absent" jugadores que no deben aparecer.
/// </summary>
public class GoldenTests
{
    public static IEnumerable<object[]> Cases() => Fixtures.CaseNames().Select(n => new object[] { n });

    [Fact]
    public void There_are_enough_fixtures()
    {
        var names = Fixtures.CaseNames().ToList();
        Assert.True(names.Count(n => n.Contains("/cash_")) >= 30, "faltan fixtures de cash");
        Assert.True(names.Count(n => n.Contains("/tour_")) >= 10, "faltan fixtures de torneo");
        foreach (var n in names)
            Assert.True(File.Exists(Path.Combine(Fixtures.Root, n + ".json")), $"{n}: falta el .json con los hechos esperados");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Facts_match_the_hand_written_expectations(string name)
    {
        var hand = PokerStarsParser.Parse(Fixtures.Text(name));
        Assert.NotNull(hand);
        var facts = FactExtractor.Extract(hand!).ToDictionary(f => f.Player);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.Root, name + ".json")));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name == "_hand") { CheckHand(name, hand!, facts.Count, prop.Value); continue; }
            if (prop.Name == "_absent")
            {
                foreach (var a in prop.Value.EnumerateArray())
                    Assert.False(facts.ContainsKey(a.GetString()!), $"{name}: {a.GetString()} no debería tener hechos");
                continue;
            }
            Assert.True(facts.TryGetValue(prop.Name, out var f), $"{name}: no hay hechos para {prop.Name}");
            foreach (var e in prop.Value.EnumerateObject())
                Compare($"{name} · {prop.Name} · {e.Name}", f![e.Name], e.Value);
        }
    }

    static void CheckHand(string name, Hand h, int players, JsonElement exp)
    {
        foreach (var e in exp.EnumerateObject())
        {
            object? actual = e.Name switch
            {
                "format" => h.Format,
                "players" => players,
                "rake" => (double)h.Rake,
                "pot" => (double)h.Pot,
                "tournament_id" => h.TournamentId,
                "buyin" => h.Buyin is { } b ? (double)b : null,
                "level" => h.Level,
                "ante" => (double)h.Ante,
                _ => throw new InvalidOperationException($"{name}: clave _hand desconocida '{e.Name}'"),
            };
            Compare($"{name} · _hand · {e.Name}", actual, e.Value);
        }
    }

    static void Compare(string ctx, object? actual, JsonElement expected)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Null:
                Assert.True(actual is null, $"{ctx}: esperado null, hay {actual}");
                break;
            case JsonValueKind.String:
                Assert.True(expected.GetString() == actual as string, $"{ctx}: esperado \"{expected.GetString()}\", hay \"{actual}\"");
                break;
            case JsonValueKind.Number:
                Assert.True(actual is not null, $"{ctx}: esperado {expected.GetDouble()}, hay null");
                var got = Convert.ToDouble(actual);
                Assert.True(Math.Abs(got - expected.GetDouble()) <= 0.001, $"{ctx}: esperado {expected.GetDouble()}, hay {got}");
                break;
            default:
                throw new InvalidOperationException($"{ctx}: tipo JSON no soportado");
        }
    }
}

public class InvariantTests
{
    // (hecho, oportunidad): si ocurre el hecho, tiene que haber habido oportunidad.
    static readonly (string Stat, string Opp)[] Pairs =
    {
        ("pf_open", "pf_open_opp"), ("pf_cold_call", "pf_cold_call_opp"), ("pf_3bet", "pf_3bet_opp"),
        ("pf_3bet_vs_steal", "pf_3bet_vs_steal_opp"), ("pf_squeeze", "pf_squeeze_opp"), ("pf_f3b", "pf_f3b_opp"),
        ("pf_4bet", "pf_4bet_opp"), ("pf_f4b", "pf_f4b_opp"), ("pf_steal", "pf_steal_opp"), ("pf_fts", "pf_fts_opp"),
        ("pf_bb_defend", "pf_bb_defend_opp"), ("pf_shove", "pf_shove_opp"), ("pf_call_shove", "pf_call_shove_opp"),
        ("pf_reshove", "pf_reshove_opp"),
        ("f_cbet", "f_cbet_opp"), ("t_cbet", "t_cbet_opp"), ("r_cbet", "r_cbet_opp"),
        ("f_fcb", "f_fcb_opp"), ("t_fcb", "t_fcb_opp"), ("r_fcb", "r_fcb_opp"),
        ("f_raise_cb", "f_raise_cb_opp"), ("t_raise_cb", "t_raise_cb_opp"), ("r_raise_cb", "r_raise_cb_opp"),
        ("f_donk", "f_donk_opp"), ("t_donk", "t_donk_opp"), ("r_donk", "r_donk_opp"),
        ("f_float_bet", "f_float_bet_opp"), ("t_float_bet", "t_float_bet_opp"), ("r_float_bet", "r_float_bet_opp"),
        ("f_check_raise", "f_check_raise_opp"), ("t_check_raise", "t_check_raise_opp"), ("r_check_raise", "r_check_raise_opp"),
        ("t_probe", "t_probe_opp"), ("r_probe", "r_probe_opp"),
    };

    static readonly (string If, string Then)[] Implications =
    {
        ("pf_pfr", "pf_vpip"), ("pf_open", "pf_pfr"), ("pf_limp", "pf_vpip"), ("pf_3bet", "pf_pfr"), ("pf_shove", "pf_allin"),
        ("wsd", "wtsd"), ("wtsd", "f_saw"), ("wwsf", "f_saw"), ("r_saw", "t_saw"), ("t_saw", "f_saw"),
        ("f_cbet_ip", "f_cbet_opp"), ("f_cbet_oop", "f_cbet_opp"), ("f_cbet_opp", "f_saw"),
        ("pf_3bet_pot", "pf_raised_pot"), ("pf_4bet_pot", "pf_3bet_pot"),
    };

    [Fact]
    public void Structural_invariants_hold_for_every_fixture_hand()
    {
        int hands = 0;
        foreach (var h in Fixtures.AllHands())
        {
            hands++;
            var facts = FactExtractor.Extract(h);
            Assert.NotEmpty(facts);
            foreach (var f in facts)
            {
                string ctx = $"mano {h.Id} · {f.Player}";
                foreach (var (stat, opp) in Pairs)
                    Assert.True(!f.Flag(stat) || f.Flag(opp), $"{ctx}: {stat} sin {opp}");
                foreach (var (a, b) in Implications)
                    Assert.True(!f.Flag(a) || f.Flag(b), $"{ctx}: {a} implica {b}");
                Assert.False(f.Flag("f_cbet_ip") && f.Flag("f_cbet_oop"), $"{ctx}: cbet IP y OOP a la vez");
                foreach (var px in new[] { "f", "t", "r" })
                    Assert.True(f.Int(px + "_bets") + f.Int(px + "_raises") + f.Int(px + "_calls") + f.Int(px + "_checks") + f.Int(px + "_folds") == 0
                                || f.Flag(px + "_saw"), $"{ctx}: acciones en {px} sin haber visto la calle");
                for (int i = 0; i < FactCatalog.Count; i++)
                    if (FactCatalog.Columns[i].Kind == FactKind.Int)
                        Assert.True(f.Values[i] is int n && n >= 0, $"{ctx}: {FactCatalog.Columns[i].Name} inválido");
                Assert.NotNull(f.Text("position"));
            }
            Assert.Equal(facts.Select(f => f.Player).Distinct().Count(), facts.Count);
        }
        Assert.True(hands >= 60, $"solo se probaron {hands} manos");
    }

    /// <summary>La propiedad más útil para cazar errores de dinero: lo que ganan unos lo pierden otros, menos el rake.</summary>
    [Fact]
    public void Money_is_conserved_in_every_fixture_hand()
    {
        foreach (var h in Fixtures.AllHands())
        {
            var facts = FactExtractor.Extract(h);
            var sum = facts.Sum(f => f.Real("net") ?? 0);
            Assert.True(Math.Abs(sum + (double)h.Rake) < 0.011, $"mano {h.Id}: suma de net {sum} ≠ -rake {h.Rake}");
            var rakePaid = facts.Sum(f => f.Real("rake_paid") ?? 0);
            if (h.Rake > 0) Assert.True(Math.Abs(rakePaid - (double)h.Rake) < 0.011, $"mano {h.Id}: rake_paid {rakePaid} ≠ {h.Rake}");
        }
    }

    [Fact]
    public void Real_hands_parse_without_losing_any_block()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(Fixtures.Root, "real"), "*.txt"))
        {
            var text = File.ReadAllText(file);
            var (blocks, consumed) = PokerStarsParser.SplitComplete(text);
            Assert.True(blocks.Count >= 2, file);
            Assert.Equal(text.Length, consumed);
            Assert.All(blocks, b => Assert.NotNull(PokerStarsParser.Parse(b)));
        }
    }
}

public class HeaderTests
{
    [Theory]
    [InlineData("PokerStars Hand #1:  Hold'em No Limit ($0.01/$0.02 USD) - 2026/03/01 20:00:00 CET [2026/03/01 14:00:00 ET]", "cash")]
    [InlineData("PokerStars Zoom Hand #1:  Hold'em No Limit ($0.01/$0.02) - 2026/03/01 20:00:00 CET [2026/03/01 14:00:00 ET]", "zoom")]
    [InlineData("PokerStars Hand #1: Tournament #5, $1.84+$0.16 USD Hold'em No Limit - Level I (10/20) - 2026/03/01 20:00:00 CET [x]", "spin")]
    public void Format_is_detected_from_header_and_table_size(string header, string expected)
    {
        var tableSize = expected == "spin" ? 3 : 6;
        var text = header + $"\nTable 'T' {tableSize}-max Seat #1 is the button\nSeat 1: A ($100 in chips)\nSeat 2: B ($100 in chips)\n" +
                   "A: posts small blind $1\nB: posts big blind $2\n*** HOLE CARDS ***\nA: folds\n*** SUMMARY ***\nTotal pot $2 | Rake $0\n";
        Assert.Equal(expected, PokerStarsParser.Parse(text)!.Format);
    }

    [Theory]
    [InlineData("I", 1)] [InlineData("IV", 4)] [InlineData("IX", 9)] [InlineData("XII", 12)] [InlineData("XL", 40)]
    public void Roman_levels(string roman, int level)
    {
        var text = $"PokerStars Hand #1: Tournament #5, 1000+100 Hold'em No Limit - Level {roman} (10/20) - 2026/03/01 20:00:00 CET [x]\n" +
                   "Table 'T' 6-max Seat #1 is the button\nSeat 1: A (500 in chips)\nA: posts big blind 20\n*** SUMMARY ***\nTotal pot 0 | Rake 0\n";
        Assert.Equal(level, PokerStarsParser.Parse(text)!.Level);
    }
}
