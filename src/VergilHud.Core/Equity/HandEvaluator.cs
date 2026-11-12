namespace VergilHud.Core;

/// <summary>
/// Evaluador de manos de 5 a 7 cartas. Devuelve un entero: cuanto mayor, mejor la mano.
/// Bits 20+: categoría (0 carta alta … 8 escalera de color). Los 20 bits bajos: desempates, un rango por nibble.
/// </summary>
public static class HandEvaluator
{
    public const int HighCard = 0, Pair = 1, TwoPair = 2, Trips = 3, Straight = 4, Flush = 5, FullHouse = 6, Quads = 7, StraightFlush = 8;

    /// <summary>Para cada combinación de rangos (13 bits): rango alto de la mejor escalera o -1.</summary>
    static readonly sbyte[] StraightHigh = BuildStraights();

    static sbyte[] BuildStraights()
    {
        var t = new sbyte[8192];
        for (int mask = 0; mask < 8192; mask++)
        {
            sbyte best = -1;
            for (int high = 12; high >= 4; high--)
            {
                int pat = 0x1F << (high - 4);
                if ((mask & pat) == pat) { best = (sbyte)high; break; }
            }
            if (best < 0 && (mask & 0x100F) == 0x100F) best = 3; // rueda A-2-3-4-5
            t[mask] = best;
        }
        return t;
    }

    public static int Category(int score) => score >> 20;

    /// <summary>Evaluador general de 5 a 7 cartas (referencia; las tablas del camino rápido se generan con él).</summary>
    public static int Evaluate(ReadOnlySpan<int> cards) => cards.Length == 7 ? Fast7.Evaluate(cards) : EvaluateSlow(cards);

    public static int EvaluateSlow(ReadOnlySpan<int> cards)
    {
        Span<int> cnt = stackalloc int[13];
        Span<int> suitMask = stackalloc int[4];
        Span<int> suitCount = stackalloc int[4];
        int rankMask = 0;
        foreach (var c in cards)
        {
            int r = c >> 2, s = c & 3;
            cnt[r]++;
            suitMask[s] |= 1 << r;
            suitCount[s]++;
            rankMask |= 1 << r;
        }

        for (int s = 0; s < 4; s++)
        {
            if (suitCount[s] < 5) continue;
            int fm = suitMask[s];
            int sf = StraightHigh[fm];
            if (sf >= 0) return (StraightFlush << 20) | (sf << 16);
            return (Flush << 20) | (PackTop(fm, 5) );
        }

        int quad = -1, trips1 = -1, trips2 = -1, pair1 = -1, pair2 = -1;
        for (int r = 12; r >= 0; r--)
        {
            switch (cnt[r])
            {
                case 4: if (quad < 0) quad = r; break;
                case 3: if (trips1 < 0) trips1 = r; else if (trips2 < 0) trips2 = r; break;
                case 2: if (pair1 < 0) pair1 = r; else if (pair2 < 0) pair2 = r; break;
            }
        }

        if (quad >= 0)
            return (Quads << 20) | (quad << 16) | (PackTop(rankMask & ~(1 << quad), 1) << 12);

        if (trips1 >= 0 && (trips2 >= 0 || pair1 >= 0))
        {
            int p = Math.Max(trips2, pair1);
            return (FullHouse << 20) | (trips1 << 16) | (p << 12);
        }

        int sh = StraightHigh[rankMask];
        if (sh >= 0) return (Straight << 20) | (sh << 16);

        if (trips1 >= 0)
            return (Trips << 20) | (trips1 << 16) | (PackTop(rankMask & ~(1 << trips1), 2) << 8);

        if (pair1 >= 0 && pair2 >= 0)
            return (TwoPair << 20) | (pair1 << 16) | (pair2 << 12) | (PackTop(rankMask & ~(1 << pair1) & ~(1 << pair2), 1) << 8);

        if (pair1 >= 0)
            return (Pair << 20) | (pair1 << 16) | (PackTop(rankMask & ~(1 << pair1), 3) << 4);

        return (HighCard << 20) | PackTop(rankMask, 5);
    }

    /// <summary>Los n rangos más altos de la máscara, un nibble cada uno (el mayor en el nibble más alto de 4n bits).</summary>
    static int PackTop(int mask, int n)
    {
        int v = 0, taken = 0;
        for (int r = 12; r >= 0 && taken < n; r--)
            if ((mask & (1 << r)) != 0)
            {
                v = (v << 4) | r;
                taken++;
            }
        return v << (4 * (n - taken)); // rellena con ceros si faltan cartas (manos de 5 cartas con pocos kickers)
    }

    public static string CategoryName(int score) => Category(score) switch
    {
        HighCard => "carta alta",
        Pair => "pareja",
        TwoPair => "doble pareja",
        Trips => "trío",
        Straight => "escalera",
        Flush => "color",
        FullHouse => "full",
        Quads => "póker",
        _ => "escalera de color",
    };
}

/// <summary>
/// Camino rápido para 7 cartas: una clave por rangos (3 bits por rango, suma de 7 términos) buscada en una tabla hash abierta precalculada
/// con el evaluador de referencia, y una tabla por máscara de rangos para el color. Mismos valores que <see cref="HandEvaluator.EvaluateSlow"/>.
/// </summary>
static class Fast7
{
    const int HashBits = 17;
    const int HashSize = 1 << HashBits;
    /// <summary>Clave (39 bits) y puntuación (24 bits) juntas en un solo ulong: una única lectura de memoria por evaluación.</summary>
    static readonly ulong[] Table = new ulong[HashSize];
    static readonly int[] FlushScores = new int[8192];
    static readonly long[] Weight = new long[13];

    static Fast7()
    {
        for (int r = 0; r < 13; r++) Weight[r] = 1L << (3 * r);
        Array.Fill(Table, ulong.MaxValue);
        var counts = new int[13];
        Build(0, 7, counts);
        for (int mask = 0; mask < 8192; mask++)
        {
            int bits = System.Numerics.BitOperations.PopCount((uint)mask);
            if (bits < 5 || bits > 7) continue;
            var cards = new List<int>();
            for (int r = 0; r < 13; r++) if ((mask & (1 << r)) != 0) cards.Add(r * 4);   // todas del palo 0
            FlushScores[mask] = HandEvaluator.EvaluateSlow(cards.ToArray());
        }
    }

    static void Build(int rank, int left, int[] counts)
    {
        if (rank == 13)
        {
            if (left != 0) return;
            // Palos en reparto cíclico: nunca salen 5 del mismo palo con 7 cartas
            var cards = new int[7];
            int n = 0;
            long key = 0;
            for (int r = 0; r < 13; r++)
                for (int k = 0; k < counts[r]; k++)
                {
                    cards[n] = r * 4 + (n % 4);
                    key += Weight[r];
                    n++;
                }
            Insert(key, HandEvaluator.EvaluateSlow(cards));
            return;
        }
        for (int c = 0; c <= Math.Min(4, left); c++)
        {
            counts[rank] = c;
            Build(rank + 1, left - c, counts);
        }
        counts[rank] = 0;
    }

    static int Slot(long key) => (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> (64 - HashBits));

    static void Insert(long key, int score)
    {
        int i = Slot(key);
        while (Table[i] != ulong.MaxValue) i = (i + 1) & (HashSize - 1);
        Table[i] = ((ulong)key << 24) | (uint)score;
    }

    public static int Evaluate(ReadOnlySpan<int> c)
    {
        long key = 0;
        int suits = 0;
        for (int i = 0; i < 7; i++)
        {
            int card = c[i];
            key += Weight[card >> 2];
            suits += 1 << ((card & 3) << 2);
        }
        int flush = (suits + 0x3333) & 0x8888;
        if (flush != 0)
        {
            int suit = flush == 0x8 ? 0 : flush == 0x80 ? 1 : flush == 0x800 ? 2 : 3;
            int mask = 0;
            for (int i = 0; i < 7; i++)
                if ((c[i] & 3) == suit) mask |= 1 << (c[i] >> 2);
            return FlushScores[mask];
        }
        int slot = Slot(key);
        ulong e = Table[slot];
        while ((long)(e >> 24) != key)
        {
            slot = (slot + 1) & (HashSize - 1);
            e = Table[slot];
        }
        return (int)(e & 0xFFFFFF);
    }
}
