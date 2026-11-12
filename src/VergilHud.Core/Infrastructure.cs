using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace VergilHud.Core;

/// <summary>Estadísticas propias del usuario (editor de DSL, DESIGN.md §6.2), guardadas en custom-stats.json.</summary>
public static class CustomStats
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static List<StatDefinition> Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<StatDefinition>>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    public static void Save(string path, IEnumerable<StatDefinition> defs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(defs.ToList(), Json));
    }

    /// <summary>Texto del error si la definición no es válida (id, numerador y denominador en el DSL), o null si lo es.</summary>
    public static string? Validate(StatDefinition def, IEnumerable<string> existingIds, bool isNew = true)
    {
        if (string.IsNullOrWhiteSpace(def.Id) || !System.Text.RegularExpressions.Regex.IsMatch(def.Id, "^[a-z][a-z0-9_]{0,31}$"))
            return "El id debe empezar por una letra minúscula y usar solo minúsculas, números y _ (máx. 32).";
        if (isNew && existingIds.Contains(def.Id)) return $"Ya existe una estadística con el id «{def.Id}».";
        if (string.IsNullOrWhiteSpace(def.Label)) return "Falta la etiqueta corta (la que se ve en el panel).";
        try
        {
            StatDsl.Compile(def.Numerator, "vn");
            StatDsl.Compile(string.IsNullOrWhiteSpace(def.Denominator) ? "1" : def.Denominator, "vd");
        }
        catch (DslException ex) { return ex.Message; }
        return null;
    }
}

/// <summary>Todo lo que se puede exportar de la configuración para no perderlo al reinstalar (DESIGN.md §18.7).</summary>
public sealed class ConfigBundle
{
    public int Version { get; set; } = 1;
    public string? Settings { get; set; }
    public string? Profiles { get; set; }
    public string? LeakBaselines { get; set; }
    public string? CustomStats { get; set; }
    public Goals? Goals { get; set; }
    public List<PlayerNote> Notes { get; set; } = new();
    public List<HandTagEntry> Tags { get; set; } = new();

    public sealed class HandTagEntry { public string HandId { get; set; } = ""; public string Tag { get; set; } = ""; }

    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static ConfigBundle Capture(HudDatabase db, string? settings, string? profiles, string? baselines, string? customStats)
    {
        var notes = new NotesService(db);
        var tags = db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT hand_id, tag FROM hand_tags ORDER BY hand_id, tag";
            using var r = cmd.ExecuteReader();
            var l = new List<HandTagEntry>();
            while (r.Read()) l.Add(new HandTagEntry { HandId = r.GetString(0), Tag = r.GetString(1) });
            return l;
        });
        return new ConfigBundle { Settings = settings, Profiles = profiles, LeakBaselines = baselines, CustomStats = customStats, Goals = GoalService.Load(db), Notes = notes.ListAnnotated(), Tags = tags };
    }

    public string ToJson() => JsonSerializer.Serialize(this, Opt);

    public static ConfigBundle? FromJson(string json)
    {
        try
        {
            var b = JsonSerializer.Deserialize<ConfigBundle>(json);
            return b is { Version: 1 } ? b : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Vuelca en la base de datos las notas, etiquetas y objetivos. Los archivos de configuración los escribe quien llama.</summary>
    public (int Notes, int Tags) ApplyToDb(HudDatabase db)
    {
        var svc = new NotesService(db);
        int n = 0, t = 0;
        foreach (var note in Notes)
        {
            svc.Set(note.Player, note.Note, note.ColorLabel);
            n++;
        }
        foreach (var tag in Tags)
            if (svc.AddTag(tag.HandId, tag.Tag)) t++;
        if (Goals != null) GoalService.Save(db, Goals);
        return (n, t);
    }
}

/// <summary>Copias de seguridad automáticas de la base de datos (VACUUM INTO, consistentes aunque la app esté abierta).</summary>
public static class DbBackup
{
    public static string? CreateIfDue(HudDatabase db, string folder, DateTime now, int keep = 5)
    {
        Directory.CreateDirectory(folder);
        var today = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (Directory.GetFiles(folder, $"hud-{today}-*.db").Length > 0) return null;
        return Create(db, folder, now, keep);
    }

    public static string Create(HudDatabase db, string folder, DateTime now, int keep = 5)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"hud-{now:yyyyMMdd-HHmmss}.db");
        if (File.Exists(path)) File.Delete(path);
        db.Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "VACUUM INTO $p";
            cmd.Parameters.AddWithValue("$p", path);
            return cmd.ExecuteNonQuery();
        });
        foreach (var old in Directory.GetFiles(folder, "hud-*.db").OrderByDescending(f => f, StringComparer.Ordinal).Skip(Math.Max(1, keep)))
        {
            try { File.Delete(old); } catch { /* en uso: se borrará la próxima vez */ }
        }
        return path;
    }
}

/// <summary>Registro de errores y eventos en archivos diarios (se conservan 14). Nunca lanza excepciones.</summary>
public static class AppLog
{
    static readonly object Lock = new();
    static string? _dir;

    public static void Init(string dir)
    {
        lock (Lock)
        {
            _dir = dir;
            try
            {
                Directory.CreateDirectory(dir);
                foreach (var old in Directory.GetFiles(dir, "vergil-*.log").OrderByDescending(f => f, StringComparer.Ordinal).Skip(14))
                    File.Delete(old);
            }
            catch { /* sin logs */ }
        }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    static void Write(string level, string message, Exception? ex)
    {
        lock (Lock)
        {
            if (_dir == null) return;
            try
            {
                var file = Path.Combine(_dir, $"vergil-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss} [{level}] {message}{(ex == null ? "" : Environment.NewLine + ex)}{Environment.NewLine}");
            }
            catch { /* disco lleno o bloqueado */ }
        }
    }
}

public sealed record BenchmarkResult(string Name, double Value, string Unit, double? Target = null)
{
    public bool? Met => Target is { } t ? Value >= t : null;
}

/// <summary>Pruebas de rendimiento internas (DESIGN.md §14), con datos sintéticos y en memoria: no tocan tu base de datos.</summary>
public static class Benchmarks
{
    const string SampleHand =
        "PokerStars Hand #{0}:  Hold'em No Limit ($1/$2 USD) - 2026/03/01 10:00:00 CET [2026/03/01 04:00:00 ET]\n" +
        "Table 'Bench' 6-max Seat #3 is the button\nSeat 1: Villain1 ($200 in chips)\nSeat 2: Villain2 ($200 in chips)\nSeat 3: Hero ($200 in chips)\n" +
        "Villain1: posts small blind $1\nVillain2: posts big blind $2\n*** HOLE CARDS ***\nDealt to Hero [Ah Kd]\nHero: raises $4 to $6\n" +
        "Villain1: calls $5\nVillain2: folds\n*** FLOP *** [2c 7d Jh]\nVillain1: checks\nHero: bets $8\nVillain1: folds\nUncalled bet ($8) returned to Hero\n" +
        "Hero collected $14 from pot\n*** SUMMARY ***\nTotal pot $14 | Rake $0\n\n\n";

    public static List<BenchmarkResult> Run(int hands = 4000)
    {
        var results = new List<BenchmarkResult>();

        // Evaluador: las manos se generan antes para medir solo la evaluación
        var rng = new Random(1);
        const int sets = 50_000, rounds = 8;
        var all = new int[sets * 7];
        for (int i = 0; i < sets; i++)
            for (int k = 0; k < 7; k++)
            {
retry:
                int c = rng.Next(52);
                for (int j = 0; j < k; j++) if (all[i * 7 + j] == c) goto retry;
                all[i * 7 + k] = c;
            }
        long sink = 0;
        var sw = Stopwatch.StartNew();
        for (int r = 0; r < rounds; r++)
            for (int i = 0; i < sets; i++) sink += HandEvaluator.Evaluate(all.AsSpan(i * 7, 7));
        sw.Stop();
        results.Add(new BenchmarkResult("Evaluador de manos", sets * (double)rounds / sw.Elapsed.TotalSeconds, "evals/s", 50_000_000));

        // Análisis de historial
        var texts = Enumerable.Range(0, hands).Select(i => string.Format(SampleHand, 900000 + i)).ToList();
        sw.Restart();
        var parsed = texts.Select(t => PokerStarsParser.Parse(t)!).ToList();
        sw.Stop();
        results.Add(new BenchmarkResult("Lectura de manos", hands / sw.Elapsed.TotalSeconds, "manos/s", 5000));

        // Hechos + inserción
        using var db = new HudDatabase(":memory:");
        sw.Restart();
        var items = parsed.Select(h => (h, FactExtractor.Extract(h))).ToList();
        db.InsertHands(items);
        sw.Stop();
        results.Add(new BenchmarkResult("Hechos e inserción", hands / sw.Elapsed.TotalSeconds, "manos/s", 1200));

        // Consulta de estadísticas (sin caché)
        var engine = new StatEngine(db);
        engine.RefreshHeroNames();
        var ids = new[] { "vpip", "pfr", "3bet", "cbet_flop", "wtsd", "wsd", "af", "fts" };
        sw.Restart();
        for (int i = 0; i < 20; i++)
        {
            engine.Cache.Clear();
            engine.GetMany(new[] { "Hero", "Villain1", "Villain2" }, ids);
        }
        sw.Stop();
        results.Add(new BenchmarkResult("Consulta de estadísticas (3 jugadores × 8)", sw.Elapsed.TotalMilliseconds / 20, "ms (menos es mejor)"));
        GC.KeepAlive(sink);
        return results;
    }
}

/// <summary>Dinámica de la mesa actual: VPIP y PFR medios de los rivales sentados (DESIGN.md §18.7).</summary>
public sealed record TableDynamics(double AvgVpip, double AvgPfr, int Players, string Label)
{
    public static TableDynamics? Of(StatEngine engine, PopulationPriors priors, IEnumerable<string> opponents, IReadOnlyList<string>? formats)
    {
        var list = opponents.Distinct().ToList();
        if (list.Count == 0) return null;
        var vals = engine.GetMany(list, new[] { "vpip", "pfr" }, formats is { Count: > 0 } ? new StatFilter { Formats = formats } : null);
        double Post(string p, string id)
        {
            var def = engine.GetDefinition(id);
            var v = vals[p][id];
            return Bayes.Posterior(v.Numerator, v.Denominator, priors.Mean(def, formats), def.Prior?.Strength ?? 10).Posterior;
        }
        double vpip = list.Average(p => Post(p, "vpip")), pfr = list.Average(p => Post(p, "pfr"));
        string label = vpip >= 0.38 ? "mesa muy suelta" : vpip >= 0.30 ? "mesa suelta" : vpip <= 0.20 ? "mesa ajustada" : "mesa normal";
        if (pfr / Math.Max(0.01, vpip) < 0.4 && vpip >= 0.28) label += " y pasiva";
        else if (pfr >= 0.22) label += " y agresiva";
        return new TableDynamics(vpip, pfr, list.Count, label);
    }
}
