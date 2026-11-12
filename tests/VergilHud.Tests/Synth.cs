using System.Globalization;
using VergilHud.Core;

namespace VergilHud.Tests;

/// <summary>Generadores de manos sintéticas para probar el HUD con muestras grandes y controladas.</summary>
static class Synth
{
    static string Time(DateTime t) => t.ToString("yyyy/MM/dd H:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Mesa de 3 (Villain1 SB, Villain2 BB, héroe en el botón). El héroe sube a 6.
    /// Si v1Calls, Villain1 paga y se retira al flop; si no, se retira preflop.
    /// </summary>
    public static Hand PreHand(long id, DateTime t, string hero, bool v1Calls, string table = "Synth")
    {
        var s = $"PokerStars Hand #{id}:  Hold'em No Limit ($1/$2 USD) - {Time(t)} CET [2026/03/01 14:00:00 ET]\n" +
                $"Table '{table}' 6-max Seat #3 is the button\n" +
                "Seat 1: Villain1 ($200 in chips)\nSeat 2: Villain2 ($200 in chips)\n" +
                $"Seat 3: {hero} ($200 in chips)\n" +
                $"Villain1: posts small blind $1\nVillain2: posts big blind $2\n*** HOLE CARDS ***\nDealt to {hero} [Ah Kd]\n" +
                $"{hero}: raises $4 to $6\n";
        s += v1Calls
            ? $"Villain1: calls $5\nVillain2: folds\n*** FLOP *** [2c 7d Jh]\nVillain1: checks\n{hero}: bets $8\nVillain1: folds\n" +
              $"Uncalled bet ($8) returned to {hero}\n{hero} collected $14 from pot\n"
            : $"Villain1: folds\nVillain2: folds\nUncalled bet ($4) returned to {hero}\n{hero} collected $5 from pot\n";
        s += "*** SUMMARY ***\nTotal pot $14 | Rake $0\n\n\n\n";
        return PokerStarsParser.Parse(s)!;
    }

    /// <summary>
    /// Mesa de 4 (Villain1 SB, Villain2 BB, Villain3 CO, Hero BTN). Villain3 sube, Hero paga y en el flop Villain3 apuesta
    /// <paramref name="bet"/> (el bote es 15). Hero se retira o sube (y Villain3 se retira).
    /// </summary>
    public static Hand CbetHand(long id, DateTime t, int bet, bool heroFolds)
    {
        var s = $"PokerStars Hand #{id}:  Hold'em No Limit ($1/$2 USD) - {Time(t)} CET [2026/03/01 14:00:00 ET]\n" +
                "Table 'Synth' 6-max Seat #4 is the button\n" +
                "Seat 1: Villain1 ($200 in chips)\nSeat 2: Villain2 ($200 in chips)\nSeat 3: Villain3 ($200 in chips)\nSeat 4: Hero ($200 in chips)\n" +
                "Villain1: posts small blind $1\nVillain2: posts big blind $2\n*** HOLE CARDS ***\nDealt to Hero [Jh Td]\n" +
                "Villain3: raises $4 to $6\nHero: calls $6\nVillain1: folds\nVillain2: folds\n" +
                $"*** FLOP *** [Qh 8d 3c]\nVillain3: bets ${bet}\n";
        s += heroFolds
            ? $"Hero: folds\nUncalled bet (${bet}) returned to Villain3\nVillain3 collected $15 from pot\n"
            : $"Hero: raises ${2 * bet} to ${3 * bet}\nVillain3: folds\nUncalled bet (${2 * bet}) returned to Hero\nHero collected ${15 + 2 * bet} from pot\n";
        s += "*** SUMMARY ***\nTotal pot $15 | Rake $0\n\n\n\n";
        return PokerStarsParser.Parse(s)!;
    }

    public static HudDatabase Db(params IEnumerable<Hand>[] groups)
    {
        var db = new HudDatabase(":memory:");
        foreach (var g in groups) db.InsertHands(g.Select(h => (h, FactExtractor.Extract(h))).ToList());
        return db;
    }

    public static Hand Case(string name) => PokerStarsParser.Parse(Fixtures.Text("cases/" + name))!;
}
