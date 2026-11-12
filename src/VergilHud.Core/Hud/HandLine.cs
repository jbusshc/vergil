using System.Globalization;

namespace VergilHud.Core;

/// <summary>
/// Resume en una línea lo que hizo un jugador en una mano: "CO open · call 3B · flop X/C · turn B 75%".
/// X = check, C = call, F = fold, R = raise, B n% = apuesta de n % del bote.
/// </summary>
public static class HandLine
{
    public static string Build(Hand h, string player)
    {
        var participants = new HashSet<string>(h.Actions.Select(a => a.Player));
        if (h.Hero != null) participants.Add(h.Hero);
        var active = h.Seats.Where(s => participants.Contains(s.Player)).OrderBy(s => s.Number).ToList();
        var position = Positions.Assign(h, active).GetValueOrDefault(player, "");
        var bb = h.BigBlind > 0 ? h.BigBlind : 1;

        var parts = new List<string>();

        // Preflop
        var pre = new List<string>();
        int raises = 0;
        foreach (var a in h.Actions.Where(x => x.Street == Street.Preflop && !x.IsPost))
        {
            if (a.Player == player)
            {
                switch (a.Kind)
                {
                    case ActionKind.Raise:
                        pre.Add(raises switch { 0 => "open", 1 => "3bet", 2 => "4bet", _ => "5bet+" });
                        break;
                    case ActionKind.Call:
                        pre.Add(raises == 0 ? "limp" : raises == 1 ? "call" : raises == 2 ? "call 3B" : "call 4B");
                        break;
                    case ActionKind.Fold:
                        pre.Add("fold");
                        break;
                }
            }
            if (a.Kind == ActionKind.Raise) raises++;
        }
        if (pre.Count == 0) parts.Add(position);
        else
        {
            parts.Add($"{position} {pre[0]}".Trim());
            parts.AddRange(pre.Skip(1));
        }

        // Postflop: se reconstruye el bote para expresar las apuestas en % del bote.
        decimal potBase = 0;
        var streetInv = new Dictionary<string, decimal>();
        var current = Street.Preflop;
        var tokens = new Dictionary<Street, List<string>>();
        foreach (var a in h.Actions)
        {
            if (a.Street != current)
            {
                potBase += streetInv.Values.Sum();
                streetInv.Clear();
                current = a.Street;
            }
            streetInv.TryGetValue(a.Player, out var mine);
            var potBefore = potBase + streetInv.Values.Sum();

            switch (a.Kind)
            {
                case ActionKind.PostAnte: potBase += a.Amount; break;
                case ActionKind.PostDead:
                    var live = Math.Min(a.Amount, bb);
                    streetInv[a.Player] = mine + live;
                    potBase += a.Amount - live;
                    break;
                case ActionKind.PostSmallBlind:
                case ActionKind.PostBigBlind:
                case ActionKind.Call:
                case ActionKind.Bet:
                    streetInv[a.Player] = mine + a.Amount;
                    break;
                case ActionKind.Raise:
                    streetInv[a.Player] = a.ToAmount;
                    break;
            }

            if (a.Player != player || a.Street == Street.Preflop) continue;
            var token = a.Kind switch
            {
                ActionKind.Check => "X",
                ActionKind.Call => "C",
                ActionKind.Fold => "F",
                ActionKind.Raise => "R",
                ActionKind.Bet => potBefore > 0
                    ? "B" + ((int)Math.Round(a.Amount / potBefore * 100, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "%"
                    : "B",
                _ => null,
            };
            if (token == null) continue;
            if (!tokens.TryGetValue(a.Street, out var l)) tokens[a.Street] = l = new();
            l.Add(token);
        }
        foreach (var st in new[] { Street.Flop, Street.Turn, Street.River })
            if (tokens.TryGetValue(st, out var l))
                parts.Add($"{st.ToString().ToLowerInvariant()} {string.Join("/", l)}");

        return string.Join(" · ", parts);
    }
}
