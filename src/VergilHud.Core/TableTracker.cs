namespace VergilHud.Core;

/// <summary>Recuerda la última mano de cada mesa (quién está sentado y dónde).</summary>
public sealed class TableTracker
{
    readonly Dictionary<string, Hand> _tables = new();
    readonly object _lock = new();

    public void Update(Hand hand)
    {
        if (string.IsNullOrEmpty(hand.TableName)) return;
        lock (_lock)
        {
            if (!_tables.TryGetValue(hand.TableName, out var cur) || hand.Time >= cur.Time)
                _tables[hand.TableName] = hand;
        }
    }

    public Dictionary<string, Hand> Snapshot()
    {
        lock (_lock) return new Dictionary<string, Hand>(_tables);
    }
}
