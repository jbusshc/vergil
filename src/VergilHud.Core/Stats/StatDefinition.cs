using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace VergilHud.Core;

public sealed class PriorSpec
{
    /// <summary>Pseudo-observaciones del prior: cuánto pesa la media de la población frente a los datos del jugador.</summary>
    public double Strength { get; set; } = 10;
    /// <summary>Media de partida de la población cuando aún hay pocos datos propios.</summary>
    public double? Mean { get; set; }
}

public sealed class Threshold
{
    public double? Lt { get; set; }
    public string Color { get; set; } = "";
}

/// <summary>Estadística = SUM(numerador) / SUM(denominador) sobre los hechos, escalada y con formato (DESIGN.md §6.2).</summary>
public sealed class StatDefinition
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public string Numerator { get; set; } = "";
    public string Denominator { get; set; } = "1";
    public double Scale { get; set; } = 1;
    /// <summary>pct0 | pct1 | dec1 | dec2 | int</summary>
    public string Format { get; set; } = "pct0";
    public PriorSpec? Prior { get; set; }
    public List<Threshold>? Thresholds { get; set; }

    /// <summary>Es una proporción (0..1): numerador ⊂ denominador. Solo a estas se les aplica la estadística bayesiana.</summary>
    public bool IsProportion => Scale == 1 && Format.StartsWith("pct", StringComparison.Ordinal);

    public string FormatValue(StatValue v)
    {
        if (v.Value is not { } x) return "-";
        var ci = CultureInfo.InvariantCulture;
        return Format switch
        {
            "pct1" => (x * 100).ToString("0.0", ci),
            "dec1" => x.ToString("0.0", ci),
            "dec2" => x.ToString("0.00", ci),
            "int" => x.ToString("0", ci),
            _ => (x * 100).ToString("0", ci),
        };
    }
}

/// <summary>Resultado de una estadística: numerador, denominador (la muestra) y escala.</summary>
public readonly record struct StatValue(double Numerator, double Denominator, double Scale = 1)
{
    public double? Value => Denominator <= 0 ? null : Numerator / Denominator * Scale;
    public int N => (int)Denominator;
}

public static class BuiltinStats
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static List<StatDefinition> Load()
    {
        using var s = typeof(BuiltinStats).Assembly.GetManifestResourceStream("builtin-stats.json")
                      ?? throw new InvalidOperationException("Falta el recurso embebido builtin-stats.json");
        return JsonSerializer.Deserialize<List<StatDefinition>>(s, Json) ?? new();
    }

    public static List<StatDefinition> Parse(string json) =>
        JsonSerializer.Deserialize<List<StatDefinition>>(json, Json) ?? new();
}
