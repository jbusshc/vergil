using System.Globalization;
using System.Text;

namespace VergilHud.Core;

/// <summary>Texto de una mano para pegarla en un foro o enviarla a un amigo (DESIGN.md §9).</summary>
public static class HandText
{
    /// <param name="inBb">Cantidades en ciegas grandes (si no, en fichas).</param>
    /// <param name="anonymize">Los rivales se nombran por su posición y el héroe como "Hero"; no aparece ningún nick.</param>
    public static string Build(Hand h, bool inBb = true, bool anonymize = true)
    {
        var participants = new HashSet<string>(h.Actions.Select(a => a.Player));
        if (h.Hero != null) participants.Add(h.Hero);
        var active = h.Seats.Where(s => participants.Contains(s.Player)).OrderBy(s => s.Number).ToList();
        var pos = Positions.Assign(h, active);
        decimal bb = h.BigBlind > 0 ? h.BigBlind : 1;
        var ci = CultureInfo.InvariantCulture;

        string Money(decimal v) => inBb ? (v / bb).ToString("0.##", ci) + "bb" : v.ToString("0.##", ci);
        string Name(string p) => !anonymize ? p : p == h.Hero ? "Hero" : pos.GetValueOrDefault(p, p);

        var sb = new StringBuilder();
        string stakes = $"{h.SmallBlind.ToString("0.##", ci)}/{h.BigBlind.ToString("0.##", ci)}";
        sb.AppendLine(h.IsTournament
            ? $"Torneo (nivel {h.Level}, {stakes}{(h.Ante > 0 ? $", ante {h.Ante.ToString("0.##", ci)}" : "")}) — Hold'em No Limit — {active.Count} jugadores"
            : $"Cash {stakes} — Hold'em No Limit — {active.Count} jugadores");
        sb.AppendLine();

        foreach (var s in active)
        {
            bool hero = s.Player == h.Hero;
            string line = $"{pos.GetValueOrDefault(s.Player, "?"),-4} {Name(s.Player)} ({(inBb ? $"{(s.Stack / bb):0.#}bb" : s.Stack.ToString("0.##", ci))})";
            if (hero && h.HeroCards != null) line += $"  [{h.HeroCards}]";
            sb.AppendLine(line);
        }
        sb.AppendLine();

        // Acciones por calle con el bote acumulado
        decimal potBase = 0;
        var inv = new Dictionary<string, decimal>();
        var cur = Street.Preflop;
        var lines = new Dictionary<Street, List<string>> { [Street.Preflop] = new() };
        var potAt = new Dictionary<Street, decimal>();

        foreach (var a in h.Actions)
        {
            if (a.Street != cur)
            {
                potBase += inv.Values.Sum();
                inv.Clear();
                cur = a.Street;
                lines[cur] = new();
                potAt[cur] = potBase;
            }
            inv.TryGetValue(a.Player, out var mine);
            decimal potBefore = potBase + inv.Values.Sum();
            string who = Name(a.Player);
            string? text = null;
            switch (a.Kind)
            {
                case ActionKind.PostAnte: potBase += a.Amount; break;
                case ActionKind.PostSmallBlind: inv[a.Player] = mine + a.Amount; text = $"{who} pone SB {Money(a.Amount)}"; break;
                case ActionKind.PostBigBlind: inv[a.Player] = mine + a.Amount; text = $"{who} pone BB {Money(a.Amount)}"; break;
                case ActionKind.PostDead: inv[a.Player] = mine + Math.Min(a.Amount, bb); potBase += a.Amount - Math.Min(a.Amount, bb); text = $"{who} pone ciegas {Money(a.Amount)}"; break;
                case ActionKind.Call: inv[a.Player] = mine + a.Amount; text = $"{who} iguala {Money(a.Amount)}"; break;
                case ActionKind.Bet:
                    inv[a.Player] = mine + a.Amount;
                    text = potBefore > 0 ? $"{who} apuesta {Money(a.Amount)} ({Math.Round(a.Amount / potBefore * 100, MidpointRounding.AwayFromZero):0}% del bote)" : $"{who} apuesta {Money(a.Amount)}";
                    break;
                case ActionKind.Raise: inv[a.Player] = a.ToAmount; text = $"{who} sube a {Money(a.ToAmount)}"; break;
                case ActionKind.Fold: text = $"{who} se retira"; break;
                case ActionKind.Check: text = $"{who} pasa"; break;
            }
            if (text != null && !a.IsPost)
                lines[cur].Add(a.AllIn ? text + " (all-in)" : text);
        }

        var preSb = new StringBuilder();
        foreach (var street in new[] { Street.Preflop, Street.Flop, Street.Turn, Street.River })
        {
            if (!lines.TryGetValue(street, out var l) || (l.Count == 0 && street != Street.Preflop)) continue;
            string title = street switch
            {
                Street.Preflop => "Preflop",
                Street.Flop => $"Flop [{string.Join(" ", h.Board.Take(3))}] (bote {Money(potAt.GetValueOrDefault(street))})",
                Street.Turn => $"Turn [{string.Join(" ", h.Board.Take(3))}] [{h.Board.ElementAtOrDefault(3)}] (bote {Money(potAt.GetValueOrDefault(street))})",
                _ => $"River [{string.Join(" ", h.Board.Take(4))}] [{h.Board.ElementAtOrDefault(4)}] (bote {Money(potAt.GetValueOrDefault(street))})",
            };
            sb.AppendLine(title + ":");
            sb.AppendLine("  " + (l.Count == 0 ? "—" : string.Join("; ", l)));
        }

        if (h.HasShowdown)
        {
            sb.AppendLine();
            foreach (var (p, cards) in h.ShownCards)
                sb.AppendLine($"Muestra: {Name(p)} [{cards}]");
        }
        sb.AppendLine();
        var won = h.Collected.Where(kv => kv.Value > 0).Select(kv => $"{Name(kv.Key)} gana {Money(kv.Value)}").ToList();
        sb.AppendLine(won.Count > 0 ? "Resultado: " + string.Join("; ", won) : "Resultado: —");
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }
}
