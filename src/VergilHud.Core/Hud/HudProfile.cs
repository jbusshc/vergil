using System.Text.Json;
using System.Text.Json.Serialization;

namespace VergilHud.Core;

/// <summary>Una estadística dentro de una línea del panel.</summary>
public sealed class HudStatRef
{
    public string Id { get; set; } = "";
    /// <summary>Muestra la etiqueta ("CB 50") o solo el valor ("50").</summary>
    public bool ShowLabel { get; set; } = true;
    /// <summary>Separador que se pone ANTES de esta estadística ("/" para VPIP/PFR/3B).</summary>
    public string Sep { get; set; } = " ";
    /// <summary>Calcula la estadística solo con las manos en que el jugador tenía el mismo cubo de stack que ahora.</summary>
    public bool StackAware { get; set; }
    /// <summary>Muestra "pequeña·grande" (p. ej. FC 35·70) cuando hay muestra suficiente de ambos tamaños.</summary>
    public bool SizeSplit { get; set; }
}

public sealed class HudLine
{
    public List<HudStatRef> Stats { get; set; } = new();
}

/// <summary>Qué se ve en el panel de una mesa. Se elige solo por formato y tamaño de mesa.</summary>
public sealed class HudProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Formats { get; set; } = new();
    public int MinSeats { get; set; } = 2;
    public int MaxSeats { get; set; } = 10;
    /// <summary>cash | tournament | all: qué manos alimentan las estadísticas de este perfil.</summary>
    public string DataScope { get; set; } = "all";
    /// <summary>total | last100 | session.</summary>
    public string Window { get; set; } = "total";
    public List<HudLine> Lines { get; set; } = new();

    public bool Matches(string format, int maxSeats) =>
        Formats.Contains(format) && maxSeats >= MinSeats && maxSeats <= MaxSeats;

    /// <summary>Formatos de las manos que cuentan para este perfil (null = todos).</summary>
    public IReadOnlyList<string>? ScopeFormats() => DataScope switch
    {
        "cash" => new[] { "cash", "zoom" },
        "tournament" => new[] { "mtt", "sng", "spin" },
        _ => null,
    };
}

public static class HudProfiles
{
    static HudStatRef R(string id, bool label = true, string sep = " ", bool stackAware = false, bool split = false) =>
        new() { Id = id, ShowLabel = label, Sep = sep, StackAware = stackAware, SizeSplit = split };

    static HudLine Line(params HudStatRef[] s) => new() { Stats = s.ToList() };

    public static List<HudProfile> CreateDefaults() => new()
    {
        new HudProfile
        {
            Id = "cash-hu", Name = "Cash mano a mano", Formats = { "cash" }, MinSeats = 2, MaxSeats = 2, DataScope = "cash",
            Lines =
            {
                Line(R("vpip", false, ""), R("pfr", false, "/"), R("3bet", false, "/"), R("af")),
                Line(R("cbet_flop"), R("fcb_flop", split: true), R("wtsd")),
                Line(R("bb_defend"), R("rfi"), R("check_raise_flop")),
            },
        },
        new HudProfile
        {
            Id = "cash-6max", Name = "Cash 6-max", Formats = { "cash" }, MinSeats = 3, MaxSeats = 6, DataScope = "cash",
            Lines =
            {
                Line(R("vpip", false, ""), R("pfr", false, "/"), R("3bet", false, "/"), R("af")),
                Line(R("cbet_flop"), R("fcb_flop", split: true), R("wtsd")),
                Line(R("steal"), R("fts"), R("4bet")),
            },
        },
        new HudProfile
        {
            Id = "cash-9max", Name = "Cash 9-max", Formats = { "cash" }, MinSeats = 7, MaxSeats = 10, DataScope = "cash",
            Lines =
            {
                Line(R("vpip", false, ""), R("pfr", false, "/"), R("3bet", false, "/"), R("af")),
                Line(R("cbet_flop"), R("fcb_flop", split: true), R("wtsd")),
                Line(R("steal"), R("fts")),
            },
        },
        new HudProfile
        {
            Id = "spin", Name = "Spin & Go (3-max)", Formats = { "spin" }, MinSeats = 2, MaxSeats = 3, DataScope = "tournament",
            Lines =
            {
                Line(R("vpip", false, ""), R("pfr", false, "/")),
                Line(R("shove", stackAware: true), R("call_shove", stackAware: true), R("limp")),
                Line(R("af"), R("cbet_flop"), R("wtsd")),
            },
        },
        new HudProfile
        {
            Id = "tournament", Name = "Torneo MTT / SNG", Formats = { "mtt", "sng" }, MinSeats = 2, MaxSeats = 10, DataScope = "tournament",
            Lines =
            {
                Line(R("vpip", false, ""), R("pfr", false, "/"), R("3bet", false, "/"), R("af")),
                Line(R("shove", stackAware: true), R("call_shove", stackAware: true), R("steal")),
                Line(R("cbet_flop"), R("fcb_flop", split: true), R("wtsd")),
            },
        },
    };

    /// <summary>
    /// Elige el perfil según formato y tamaño de mesa. Si varios encajan, gana el de rango de asientos más estrecho.
    /// Si ninguno encaja se usa el primer perfil de cash (nunca devuelve null).
    /// </summary>
    public static HudProfile Select(IReadOnlyList<HudProfile> profiles, string format, int maxSeats, int? playersDealt = null)
    {
        // Mano a mano en una mesa de más asientos (los demás se han levantado): se trata como mesa de 2.
        int seats = playersDealt is <= 2 ? 2 : maxSeats;
        var hit = profiles.Where(p => p.Matches(format, seats)).OrderBy(p => p.MaxSeats - p.MinSeats).FirstOrDefault();
        if (hit != null) return hit;
        // Formato sin perfil propio (p. ej. zoom): el de cash que mejor encaje con el tamaño de la mesa.
        return profiles.Where(p => p.Formats.Contains("cash") && seats >= p.MinSeats && seats <= p.MaxSeats)
                   .OrderBy(p => p.MaxSeats - p.MinSeats).FirstOrDefault()
               ?? profiles.FirstOrDefault(p => p.Formats.Contains("cash")) ?? profiles.FirstOrDefault() ?? CreateDefaults()[1];
    }

    /// <summary>Elige el perfil para la mano que se acaba de jugar (formato, tamaño de mesa y jugadores en la mano).</summary>
    public static HudProfile ForHand(IReadOnlyList<HudProfile> profiles, Hand h)
    {
        var inHand = new HashSet<string>(h.Actions.Select(a => a.Player));
        if (h.Hero != null) inHand.Add(h.Hero);
        return Select(profiles, h.Format, h.MaxSeats, inHand.Count);
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize(IEnumerable<HudProfile> profiles) => JsonSerializer.Serialize(profiles.ToList(), Json);

    public static List<HudProfile> Parse(string json) => JsonSerializer.Deserialize<List<HudProfile>>(json, Json) ?? new();

    /// <summary>Lee el archivo de perfiles; si no existe o está corrupto, devuelve los de serie.</summary>
    public static List<HudProfile> LoadOrDefault(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var list = Parse(File.ReadAllText(path));
                if (list.Count > 0) return list;
            }
        }
        catch { /* archivo corrupto: se usan los de serie */ }
        return CreateDefaults();
    }

    public static void Save(string path, IEnumerable<HudProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Serialize(profiles));
    }
}
