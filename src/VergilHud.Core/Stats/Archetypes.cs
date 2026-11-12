using System.Globalization;
using System.Text.Json;

namespace VergilHud.Core;

public sealed class Archetype
{
    public int Id { get; set; }
    /// <summary>Etiqueta corta para el badge del HUD.</summary>
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public string Color { get; set; } = "grey";
    /// <summary>Centro del cluster en proporciones (0..1), en el orden de <see cref="ArchetypeModel.Dims"/>.</summary>
    public double[] Centroid { get; set; } = Array.Empty<double>();
    public int Members { get; set; }
}

/// <summary>
/// Arquetipos aprendidos de tu propia base de datos (DESIGN.md §19.2): k-means sobre el vector de estadísticas posteriores de los rivales
/// con muestra suficiente. Cada cluster recibe una etiqueta legible a partir de su centro. Describe cómo han jugado; no aconseja.
/// </summary>
public sealed class ArchetypeModel
{
    public static readonly string[] Dims = { "vpip", "pfr", "3bet", "cbet_flop", "fcb_flop", "wtsd", "afq" };
    static readonly string[] Palette = { "blue", "green", "orange", "red", "purple", "yellow", "grey" };

    public string FormatKey { get; set; } = "all";
    public DateTime Trained { get; set; }
    public double[] Scale { get; set; } = Array.Empty<double>();
    public List<Archetype> Clusters { get; set; } = new();

    public static string[] FormatsOf(string key) => key switch
    {
        "cash" => new[] { "cash", "zoom" },
        "spin" => new[] { "spin" },
        "mtt" => new[] { "mtt", "sng" },
        _ => Array.Empty<string>(),
    };

    /// <summary>Clave de modelo para el formato de una mano (cash/zoom → cash, mtt/sng → mtt, spin → spin).</summary>
    public static string KeyForFormat(string? format) => format switch { "cash" or "zoom" => "cash", "spin" => "spin", "mtt" or "sng" => "mtt", _ => "all" };

    public static int MinHands(string key) => key == "spin" ? 50 : 200;

    /// <summary>Vector posterior (proporciones) del jugador en las estadísticas de <see cref="Dims"/> para ese formato.</summary>
    public static double[] Vector(string player, StatEngine engine, PopulationPriors priors, string key, out int hands)
    {
        var formats = FormatsOf(key);
        var filter = formats.Length == 0 ? null : new StatFilter { Formats = formats };
        var vals = engine.GetMany(new[] { player }, Dims, filter)[player];
        hands = (int)vals["vpip"].Denominator;
        var v = new double[Dims.Length];
        for (int i = 0; i < Dims.Length; i++)
        {
            var def = engine.GetDefinition(Dims[i]);
            var s = vals[Dims[i]];
            v[i] = Bayes.Posterior(s.Numerator, s.Denominator, priors.Mean(def, formats.Length == 0 ? null : formats), def.Prior?.Strength ?? 10).Posterior;
        }
        return v;
    }

    public static ArchetypeModel? Train(HudDatabase db, StatEngine engine, PopulationPriors priors, string key = "all", int? minHands = null, int k = 6, int seed = 1)
    {
        int min = minHands ?? MinHands(key);
        var heroes = engine.HeroNames.ToHashSet();
        var data = new List<double[]>();
        foreach (var p in db.GetAllPlayerStats(null, min, 5000))
        {
            if (heroes.Contains(p.Player)) continue;
            var v = Vector(p.Player, engine, priors, key, out var hands);
            if (hands >= min) data.Add(v);
        }
        if (data.Count < 6) return null;
        k = Math.Min(k, Math.Max(2, data.Count / 3));

        int d = Dims.Length;
        var scale = new double[d];
        for (int j = 0; j < d; j++)
        {
            double mean = data.Average(x => x[j]);
            scale[j] = Math.Max(0.08, Math.Sqrt(data.Average(x => (x[j] - mean) * (x[j] - mean))));
        }
        var z = data.Select(x => x.Select((val, j) => val / scale[j]).ToArray()).ToList();
        var cents = KMeans(z, k, seed, out var assign);

        var clusters = new List<Archetype>();
        var used = new Dictionary<string, int>();
        for (int c = 0; c < cents.Length; c++)
        {
            var members = Enumerable.Range(0, z.Count).Where(i => assign[i] == c).ToList();
            if (members.Count == 0) continue;
            var raw = Enumerable.Range(0, d).Select(j => members.Average(i => data[i][j])).ToArray();
            var (label, desc) = Name(raw);
            used[label] = used.GetValueOrDefault(label) + 1;
            clusters.Add(new Archetype { Id = c, Label = label, Description = desc, Centroid = raw, Members = members.Count });
        }
        // Etiquetas repetidas: se distinguen con un número, y cada una recibe su color
        var seen = new Dictionary<string, int>();
        foreach (var a in clusters.OrderByDescending(a => a.Members))
        {
            if (used[a.Label] > 1) a.Label += " " + (seen[a.Label] = seen.GetValueOrDefault(a.Label) + 1);
            a.Color = Palette[Math.Min(Palette.Length - 1, clusters.IndexOf(a))];
        }
        return new ArchetypeModel { FormatKey = key, Trained = DateTime.Now, Scale = scale, Clusters = clusters };
    }

    /// <summary>Etiqueta legible a partir del centro (proporciones): VPIP, PFR y agresividad.</summary>
    public static (string Label, string Description) Name(double[] c)
    {
        double vpip = c[0], pfr = c[1], afq = c[6], ratio = vpip > 0 ? pfr / vpip : 0;
        if (vpip >= 0.42) return ratio < 0.35 ? ("Fish", "Paga casi todo y casi nunca sube") : ("Maníaco", "Entra en muchísimos botes y sube mucho");
        if (vpip < 0.15) return ("Nit", "Juega muy pocas manos");
        if (vpip >= 0.30) return ratio < 0.5 ? ("Pasivo", "Suelto que paga más de lo que sube") : ("LAG", "Suelto y agresivo");
        if (vpip >= 0.22) return ratio >= 0.65 ? ("TAG", "Selectivo y agresivo, de rango algo amplio") : ("Regular", "Rango medio con bastante cold-call");
        return ratio >= 0.6 ? ("TAG", "Selectivo y agresivo") : (afq < 0.35 ? ("Roca pasiva", "Pocas manos y poca iniciativa") : ("Tight", "Selectivo, mezcla subir y pagar"));
    }

    /// <summary>Cluster más cercano y confianza (0,5 si dudoso entre dos, → 1 si claro).</summary>
    public (Archetype Archetype, double Confidence)? Classify(double[] vector)
    {
        if (Clusters.Count == 0 || vector.Length != Scale.Length) return null;
        var dist = Clusters.Select(c => Math.Sqrt(Enumerable.Range(0, vector.Length).Sum(j => Math.Pow((vector[j] - c.Centroid[j]) / Scale[j], 2)))).ToArray();
        var order = Enumerable.Range(0, dist.Length).OrderBy(i => dist[i]).ToList();
        if (order.Count == 1) return (Clusters[order[0]], 1);
        double d1 = dist[order[0]], d2 = dist[order[1]];
        double conf = d1 + d2 <= 0 ? 1 : 1 - d1 / (d1 + d2);
        return (Clusters[order[0]], conf);
    }

    public (Archetype Archetype, double Confidence)? Classify(string player, StatEngine engine, PopulationPriors priors, out int hands) =>
        Classify(Vector(player, engine, priors, FormatKey, out hands));

    static double[][] KMeans(List<double[]> z, int k, int seed, out int[] assign)
    {
        var rng = new Random(seed);
        int n = z.Count, d = z[0].Length;
        // k-means++
        var cents = new List<double[]> { (double[])z[rng.Next(n)].Clone() };
        while (cents.Count < k)
        {
            var dd = z.Select(p => cents.Min(c => Sq(p, c))).ToArray();
            double total = dd.Sum(), pick = rng.NextDouble() * total, acc = 0;
            int chosen = n - 1;
            for (int i = 0; i < n; i++) { acc += dd[i]; if (acc >= pick) { chosen = i; break; } }
            cents.Add((double[])z[chosen].Clone());
        }
        var asg = new int[n];
        for (int it = 0; it < 60; it++)
        {
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                int best = 0; double bd = double.MaxValue;
                for (int c = 0; c < cents.Count; c++) { var s = Sq(z[i], cents[c]); if (s < bd) { bd = s; best = c; } }
                if (asg[i] != best) { asg[i] = best; changed = true; }
            }
            for (int c = 0; c < cents.Count; c++)
            {
                var m = Enumerable.Range(0, n).Where(i => asg[i] == c).ToList();
                if (m.Count == 0) continue;
                for (int j = 0; j < d; j++) cents[c][j] = m.Average(i => z[i][j]);
            }
            if (!changed && it > 0) break;
        }
        assign = asg;
        return cents.ToArray();
    }

    static double Sq(double[] a, double[] b) { double s = 0; for (int i = 0; i < a.Length; i++) { var x = a[i] - b[i]; s += x * x; } return s; }

    public string ToJson() => JsonSerializer.Serialize(this);
    public static ArchetypeModel? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<ArchetypeModel>(json); } catch { return null; }
    }
}

/// <summary>Guarda y reutiliza los modelos de arquetipos (uno por tipo de juego), recalculándolos como mucho una vez al día.</summary>
public sealed class ArchetypeService
{
    readonly HudDatabase _db;
    readonly StatEngine _engine;
    readonly PopulationPriors _priors;
    readonly Dictionary<string, ArchetypeModel?> _models = new();
    readonly HashSet<string> _training = new();
    readonly object _lock = new();

    public ArchetypeService(HudDatabase db, StatEngine engine, PopulationPriors priors)
    {
        _db = db;
        _engine = engine;
        _priors = priors;
    }

    static string MetaKey(string key) => "archetypes_" + key;

    string? ReadMeta(string key) => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", MetaKey(key));
        return cmd.ExecuteScalar() as string;
    });

    void WriteMeta(string key, string json) => _db.Query(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", MetaKey(key));
        cmd.Parameters.AddWithValue("$v", json);
        return cmd.ExecuteNonQuery();
    });

    /// <summary>Modelo guardado (puede estar caducado); no entrena nada, así que es seguro llamarlo desde el HUD.</summary>
    public ArchetypeModel? Cached(string key)
    {
        lock (_lock)
        {
            if (_models.TryGetValue(key, out var m)) return m;
            var json = ReadMeta(key);
            return _models[key] = json == null ? null : ArchetypeModel.FromJson(json);
        }
    }

    /// <summary>Entrena (si no hay modelo de hoy) y lo guarda. Bloquea: llamar en segundo plano.</summary>
    public ArchetypeModel? Ensure(string key, bool force = false, DateTime? now = null)
    {
        var current = Cached(key);
        var today = (now ?? DateTime.Now).Date;
        if (!force && current != null && current.Trained.Date >= today) return current;
        lock (_lock)
        {
            if (!_training.Add(key)) return current;
        }
        try
        {
            var m = ArchetypeModel.Train(_db, _engine, _priors, key);
            if (m != null)
            {
                m.Trained = now ?? DateTime.Now;
                WriteMeta(key, m.ToJson());
                lock (_lock) _models[key] = m;
            }
            return m ?? current;
        }
        finally { lock (_lock) _training.Remove(key); }
    }
}
