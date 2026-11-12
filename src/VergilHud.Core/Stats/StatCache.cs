namespace VergilHud.Core;

/// <summary>Caché en memoria de estadísticas: jugador → (estadística, filtro) → valor. Thread-safe.</summary>
public sealed class StatCache
{
    readonly Dictionary<string, Dictionary<(string Stat, string Filter), StatValue>> _d = new();
    readonly object _lock = new();

    public int Count { get { lock (_lock) return _d.Values.Sum(x => x.Count); } }

    public bool TryGet(string player, string stat, string filter, out StatValue v)
    {
        lock (_lock)
        {
            v = default;
            return _d.TryGetValue(player, out var m) && m.TryGetValue((stat, filter), out v);
        }
    }

    public void Set(string player, string stat, string filter, StatValue v)
    {
        lock (_lock)
        {
            if (!_d.TryGetValue(player, out var m)) _d[player] = m = new();
            m[(stat, filter)] = v;
        }
    }

    /// <summary>Descarta solo lo de los jugadores indicados (los de la mano recién importada).</summary>
    public void Invalidate(IEnumerable<string> players)
    {
        lock (_lock)
            foreach (var p in players) _d.Remove(p);
    }

    public void Clear()
    {
        lock (_lock) _d.Clear();
    }
}
