namespace VergilHud.Core;

/// <summary>Una fila de hechos: un jugador en una mano. Los valores siguen el orden de <see cref="FactCatalog"/>.</summary>
public sealed class PlayerHandFacts
{
    public string Player { get; }
    public int Seat { get; }
    public object?[] Values { get; }

    // Enteros pequeños ya "empaquetados": se reutilizan en lugar de crear un objeto nuevo en cada asignación.
    static readonly object[] SmallInts = Enumerable.Range(0, 256).Select(i => (object)i).ToArray();
    static object Box(int n) => (uint)n < 256 ? SmallInts[n] : n;

    public PlayerHandFacts(string player, int seat)
    {
        Player = player;
        Seat = seat;
        Values = new object?[FactCatalog.Count];
        for (int i = 0; i < Values.Length; i++)
            if (FactCatalog.Columns[i].Kind == FactKind.Int) Values[i] = SmallInts[0];
    }

    public void SetFlag(string name) => Values[FactCatalog.IndexOf(name)] = SmallInts[1];

    public void Increment(string name)
    {
        int i = FactCatalog.IndexOf(name);
        Values[i] = Box((Values[i] is int n ? n : 0) + 1);
    }

    public object? this[string name]
    {
        get => Values[FactCatalog.IndexOf(name)];
        set => Values[FactCatalog.IndexOf(name)] = value;
    }

    public int Int(string name) => this[name] is null ? 0 : Convert.ToInt32(this[name]);
    public bool Flag(string name) => Int(name) != 0;
    public double? Real(string name) => this[name] is null ? null : Convert.ToDouble(this[name]);
    public string? Text(string name) => this[name] as string;
}
