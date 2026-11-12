using System.Globalization;
using System.Text.RegularExpressions;

namespace VergilHud.Core;

/// <summary>
/// Parser de historiales de PokerStars (cliente en inglés: Ajustes &gt; Historial de manos &gt; idioma English).
/// </summary>
public static class PokerStarsParser
{
    const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    static readonly Regex HandStartRx = new(@"^PokerStars (Zoom |Home Game |Rush )?Hand #(\d+):", Opts | RegexOptions.Multiline);
    static readonly Regex TourneyRx = new(@"Tournament #(\d+), (.*?)Hold'em", Opts);
    static readonly Regex LevelRx = new(@"Level ([IVXLCDM]+) \(", Opts);
    static readonly Regex FinishRx = new(@"^(.+?) finished the tournament in (\d+)(?:st|nd|rd|th) place(?: and received [$€£]?([\d,]+(?:\.\d+)?))?", Opts);
    static readonly Regex WinsRx = new(@"^(.+?) wins the tournament and receives [$€£]?([\d,]+(?:\.\d+)?)", Opts);
    static readonly Regex PotRx =new(@"Total pot [$€£]?([\d.,]+).*?\| Rake [$€£]?([\d.,]+)", Opts);
    static readonly Regex BlindsRx = new(@"\([$€£]?([\d.,]+)/[$€£]?([\d.,]+)", Opts);
    static readonly Regex TimeRx = new(@"(\d{4}/\d{2}/\d{2} \d{1,2}:\d{2}:\d{2})", Opts);
    static readonly Regex TableRx = new(@"^Table '(.+?)' (\d+)-max.*?Seat #(\d+) is the button", Opts);
    static readonly Regex SeatRx = new(@"^Seat (\d+): (.+?) \([$€£]?([\d.,]+) in chips[^)]*\)(.*)$", Opts);
    static readonly Regex DealtRx = new(@"^Dealt to (.+?) \[(.+?)\]\s*$", Opts);
    static readonly Regex UncalledRx = new(@"^Uncalled bet \([$€£]?([\d.,]+)\) returned to (.+?)\s*$", Opts);
    static readonly Regex CollectedRx = new(@"^(.+?) collected [$€£]?([\d.,]+) from (?:side |main )?pot", Opts);
    static readonly Regex CardsRx = new(@"\[([^\]]+)\]", Opts);
    static readonly Regex AmountRx = new(@"[$€£]?(\d[\d,]*(?:\.\d+)?)", Opts);
    static readonly Regex RaiseRx = new(@"raises [$€£]?([\d.,]+) to [$€£]?([\d.,]+)", Opts);
    static readonly Regex CompleteTailRx = new(@"\n[ \t]*\r?\n\s*$", Opts);

    /// <summary>
    /// Divide el texto en manos completas. Devuelve también cuántos caracteres se han consumido,
    /// para que la última mano a medio escribir se procese en la siguiente lectura.
    /// </summary>
    public static (List<string> Hands, int Consumed) SplitComplete(string text)
    {
        var result = new List<string>();
        var starts = HandStartRx.Matches(text);
        int consumed = 0;
        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i].Index;
            int end = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
            var block = text[start..end];
            bool isLast = i + 1 == starts.Count;
            if (isLast && !(block.Contains("*** SUMMARY ***") && CompleteTailRx.IsMatch(block)))
                break;
            result.Add(block);
            consumed = end;
        }
        return (result, consumed);
    }

    public static IEnumerable<Hand> ParseAll(string text)
    {
        foreach (var block in SplitComplete(text).Hands)
            if (Parse(block) is { } h) yield return h;
    }

    public static Hand? Parse(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        int i = 0;
        while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i].Trim('﻿'))) i++;
        if (i >= lines.Length) return null;

        var header = lines[i].Trim('﻿');
        var hm = HandStartRx.Match(header);
        if (!hm.Success) return null;

        var hand = new Hand { Id = hm.Groups[2].Value, Raw = text.Trim() };
        hand.IsTournament = header.Contains("Tournament #");
        if (TourneyRx.Match(header) is { Success: true } tr)
        {
            hand.TournamentId = tr.Groups[1].Value;
            var parts = Regex.Matches(tr.Groups[2].Value, @"\d[\d.,]*").Select(m => Num(m.Value)).ToList();
            if (parts.Count > 0) hand.Buyin = parts.Sum();
            hand.BuyinParts.AddRange(parts);
        }
        if (LevelRx.Match(header) is { Success: true } lv) hand.Level = Roman(lv.Groups[1].Value);
        var afterColon = header[(hm.Length)..].Trim();
        var dash = afterColon.IndexOf(" - ", StringComparison.Ordinal);
        hand.Game = dash > 0 ? afterColon[..dash] : afterColon;

        var bm = BlindsRx.Match(header);
        if (bm.Success)
        {
            hand.SmallBlind = Num(bm.Groups[1].Value);
            hand.BigBlind = Num(bm.Groups[2].Value);
        }
        var tm = TimeRx.Match(header);
        if (tm.Success && DateTime.TryParseExact(tm.Groups[1].Value, "yyyy/MM/dd H:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            hand.Time = t;
        i++;

        if (i < lines.Length && TableRx.Match(lines[i]) is { Success: true } tbl)
        {
            hand.TableName = tbl.Groups[1].Value;
            hand.MaxSeats = int.Parse(tbl.Groups[2].Value, CultureInfo.InvariantCulture);
            hand.ButtonSeat = int.Parse(tbl.Groups[3].Value, CultureInfo.InvariantCulture);
            i++;
        }

        var street = Street.Preflop;
        var names = new List<string>();

        for (; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0) continue;

            if (line.StartsWith("*** ", StringComparison.Ordinal))
            {
                if (line.StartsWith("*** SUMMARY", StringComparison.Ordinal))
                {
                    for (int j = i + 1; j < lines.Length && j < i + 4; j++)
                        if (PotRx.Match(lines[j]) is { Success: true } pm)
                        {
                            hand.Pot = Num(pm.Groups[1].Value);
                            hand.Rake = Num(pm.Groups[2].Value);
                            break;
                        }
                    break;
                }
                if (line.StartsWith("*** FLOP", StringComparison.Ordinal)) { street = Street.Flop; AddBoard(hand, line); }
                else if (line.StartsWith("*** TURN", StringComparison.Ordinal)) { street = Street.Turn; AddBoard(hand, line); }
                else if (line.StartsWith("*** RIVER", StringComparison.Ordinal)) { street = Street.River; AddBoard(hand, line); }
                else if (line.StartsWith("*** SHOW DOWN", StringComparison.Ordinal)) hand.HasShowdown = true;
                continue;
            }

            if (street == Street.Preflop && line.StartsWith("Seat ", StringComparison.Ordinal) && SeatRx.Match(line) is { Success: true } sm)
            {
                var seat = new Seat
                {
                    Number = int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture),
                    Player = sm.Groups[2].Value,
                    Stack = Num(sm.Groups[3].Value),
                    SittingOut = sm.Groups[4].Value.Contains("sitting out"),
                };
                hand.Seats.Add(seat);
                names.Add(seat.Player);
                continue;
            }

            if (line.StartsWith("Dealt to ", StringComparison.Ordinal) && DealtRx.Match(line) is { Success: true } dm)
            {
                hand.Hero = dm.Groups[1].Value;
                hand.HeroCards = dm.Groups[2].Value;
                continue;
            }

            if (UncalledRx.Match(line) is { Success: true } um)
            {
                var p = um.Groups[2].Value;
                hand.Returned[p] = hand.Returned.GetValueOrDefault(p) + Num(um.Groups[1].Value);
                continue;
            }

            var player = MatchPlayer(line, names);
            if (player == null)
            {
                if (FinishRx.Match(line) is { Success: true } fm)
                    hand.Results.Add(new TournamentResult(fm.Groups[1].Value, int.Parse(fm.Groups[2].Value, CultureInfo.InvariantCulture),
                        fm.Groups[3].Success ? Num(fm.Groups[3].Value) : null));
                else if (WinsRx.Match(line) is { Success: true } wm)
                    hand.Results.Add(new TournamentResult(wm.Groups[1].Value, 1, Num(wm.Groups[2].Value)));
                else if (CollectedRx.Match(line) is { Success: true } cm)
                {
                    var p = cm.Groups[1].Value;
                    hand.Collected[p] = hand.Collected.GetValueOrDefault(p) + Num(cm.Groups[2].Value);
                }
                continue;
            }

            var rest = line[(player.Length + 2)..];
            bool allIn = rest.Contains("all-in");
            HandAction? action = null;

            if (rest.StartsWith("folds")) action = new(player, street, ActionKind.Fold, 0, 0, false);
            else if (rest.StartsWith("checks")) action = new(player, street, ActionKind.Check, 0, 0, false);
            else if (rest.StartsWith("calls ")) action = new(player, street, ActionKind.Call, Amount(rest), 0, allIn);
            else if (rest.StartsWith("bets ")) action = new(player, street, ActionKind.Bet, Amount(rest), 0, allIn);
            else if (rest.StartsWith("raises ") && RaiseRx.Match(rest) is { Success: true } rm)
                action = new(player, street, ActionKind.Raise, Num(rm.Groups[1].Value), Num(rm.Groups[2].Value), allIn);
            else if (rest.StartsWith("posts small & big blinds")) action = new(player, street, ActionKind.PostDead, Amount(rest), 0, allIn);
            else if (rest.StartsWith("posts small blind")) action = new(player, street, ActionKind.PostSmallBlind, Amount(rest), 0, allIn);
            else if (rest.StartsWith("posts big blind")) action = new(player, street, ActionKind.PostBigBlind, Amount(rest), 0, allIn);
            else if (rest.StartsWith("posts the ante")) action = new(player, street, ActionKind.PostAnte, Amount(rest), 0, allIn);
            else if (rest.StartsWith("shows ") && CardsRx.Match(rest) is { Success: true } shm)
                hand.ShownCards[player] = shm.Groups[1].Value;

            if (action != null) hand.Actions.Add(action);
        }

        if (hand.BigBlind == 0)
            hand.BigBlind = hand.Actions.FirstOrDefault(a => a.Kind == ActionKind.PostBigBlind)?.Amount ?? 1;
        if (hand.MaxSeats == 0) hand.MaxSeats = Math.Max(2, hand.Seats.Count == 0 ? 2 : hand.Seats.Max(s => s.Number));
        hand.Ante = hand.Actions.FirstOrDefault(a => a.Kind == ActionKind.PostAnte)?.Amount ?? 0;
        var fast = hm.Groups[1].Value is "Zoom " or "Rush ";
        hand.Format = fast ? "zoom" : hand.IsTournament ? (hand.MaxSeats == 3 ? "spin" : "mtt") : "cash";
        return hand;
    }

    static int Roman(string s)
    {
        static int Val(char c) => c switch { 'I' => 1, 'V' => 5, 'X' => 10, 'L' => 50, 'C' => 100, 'D' => 500, 'M' => 1000, _ => 0 };
        int total = 0;
        for (int i = 0; i < s.Length; i++)
        {
            int v = Val(s[i]);
            total += i + 1 < s.Length && Val(s[i + 1]) > v ? -v : v;
        }
        return total;
    }

    static void AddBoard(Hand hand, string line)
    {
        var ms = CardsRx.Matches(line);
        if (ms.Count == 0) return;
        var last = ms[^1].Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (hand.Board.Count == 0) hand.Board.AddRange(last);
        else hand.Board.Add(last[^1]);
    }

    static string? MatchPlayer(string line, List<string> names)
    {
        string? best = null;
        foreach (var n in names)
            if (line.Length > n.Length + 1 && line.StartsWith(n, StringComparison.Ordinal)
                && line[n.Length] == ':' && line[n.Length + 1] == ' '
                && (best == null || n.Length > best.Length))
                best = n;
        return best;
    }

    static decimal Amount(string s)
    {
        var m = AmountRx.Match(s);
        return m.Success ? Num(m.Groups[1].Value) : 0;
    }

    static decimal Num(string s) =>
        decimal.TryParse(s.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0;
}
