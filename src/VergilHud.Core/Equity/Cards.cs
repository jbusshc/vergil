namespace VergilHud.Core;

/// <summary>Carta como entero 0..51: rango = c &gt;&gt; 2 (0 = 2 … 12 = As), palo = c &amp; 3.</summary>
public static class Cards
{
    const string Ranks = "23456789TJQKA";
    const string Suits = "cdhs";

    public static int Parse(string card)
    {
        if (card is not { Length: 2 }) throw new FormatException($"Carta inválida '{card}'");
        int r = Ranks.IndexOf(char.ToUpperInvariant(card[0]));
        int s = Suits.IndexOf(char.ToLowerInvariant(card[1]));
        if (r < 0 || s < 0) throw new FormatException($"Carta inválida '{card}'");
        return r * 4 + s;
    }

    /// <summary>"Ah Kd" → [carta, carta]. Admite cualquier número de cartas separadas por espacios.</summary>
    public static int[] ParseMany(string? cards) =>
        string.IsNullOrWhiteSpace(cards)
            ? Array.Empty<int>()
            : cards.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToArray();

    public static string ToString(int card) => $"{Ranks[card >> 2]}{Suits[card & 3]}";

    /// <summary>Nombre canónico de unas cartas de mano: "AA", "AKs", "AKo".</summary>
    public static string HandName(int a, int b)
    {
        int ra = a >> 2, rb = b >> 2;
        if (ra < rb) (ra, rb) = (rb, ra);
        if (ra == rb) return $"{Ranks[ra]}{Ranks[rb]}";
        return $"{Ranks[ra]}{Ranks[rb]}{((a & 3) == (b & 3) ? "s" : "o")}";
    }

    public static string HandName(string holeCards)
    {
        var c = ParseMany(holeCards);
        return c.Length == 2 ? HandName(c[0], c[1]) : "";
    }

    public static string RankChars => Ranks;
}
