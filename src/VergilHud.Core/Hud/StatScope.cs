namespace VergilHud.Core;

/// <summary>Una selección de manos sobre la que se calculan estadísticas: nunca mezcla dinero real con ficticio.</summary>
public sealed record StatScope(string Name, StatFilter Filter, IReadOnlyList<string>? PriorFormats);

/// <summary>De qué mesa viene el panel: sirve para elegir el ámbito por defecto de las estadísticas.</summary>
public sealed record HandContext(string Format, bool PlayMoney, string TableName, string? TournamentId, bool IsTournament)
{
    public static HandContext From(Hand h) => new(h.Format, h.IsPlayMoney, h.TableName, h.TournamentId, h.IsTournament);
}

public static class StatScopes
{
    /// <summary>Ámbitos de una mesa concreta, en el mismo orden que HudScope: mismo tipo de juego, todo el historial, esta mesa.</summary>
    public static List<StatScope> ForHand(HandContext c)
    {
        var group = HudProfiles.FormatGroup(c.Format);
        string money = c.PlayMoney ? " (dinero ficticio)" : "";
        return new()
        {
            new($"Mismo tipo de juego: {HudProfiles.GroupName(c.Format)}{money}", new StatFilter { Formats = group, PlayMoney = c.PlayMoney }, group),
            new($"Todo el historial{money}", new StatFilter { PlayMoney = c.PlayMoney }, null),
            new(c.IsTournament ? "Solo este torneo" : "Solo esta mesa",
                new StatFilter { PlayMoney = c.PlayMoney, TableName = c.IsTournament ? null : c.TableName, TournamentId = c.IsTournament ? c.TournamentId : null }, group),
        };
    }

    /// <summary>Ámbitos generales (ficha de un jugador fuera de una mesa).</summary>
    public static List<StatScope> General() => new()
    {
        new("Cash", new StatFilter { Formats = new[] { "cash" } }, new[] { "cash" }),
        new("Spin & Go", new StatFilter { Formats = new[] { "spin" } }, new[] { "spin" }),
        new("Torneos MTT/SNG", new StatFilter { Formats = new[] { "mtt", "sng" } }, new[] { "mtt", "sng" }),
        new("Zoom", new StatFilter { Formats = new[] { "zoom" } }, new[] { "zoom" }),
        new("Todo el dinero real", new StatFilter(), null),
        new("Dinero ficticio", new StatFilter { PlayMoney = true }, null),
    };

    /// <summary>Índice del ámbito general donde el jugador tiene más manos (para abrir su ficha donde hay datos).</summary>
    public static int BestGeneral(StatEngine engine, string player)
    {
        var list = General();
        int best = 4;
        double top = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (i == 4) continue;   // "todo" solo es el último recurso
            var n = engine.Get(player, "vpip", list[i].Filter).Denominator;
            if (n > top) { top = n; best = i; }
        }
        return top <= 0 ? 4 : best;
    }
}
