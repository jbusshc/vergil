using System.IO;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Todo lo que comparten la ventana principal, el gestor de overlays y los popups.</summary>
public sealed class AppServices : IDisposable
{
    public AppSettings Settings { get; }
    public HudDatabase Db { get; }
    public StatEngine Engine { get; }
    public PopulationPriors Priors { get; }
    public HudSnapshotBuilder Builder { get; }
    public PlayerDetailService Detail { get; }
    public List<HudProfile> Profiles { get; }
    public PlaySessionState Play { get; } = new();
    public TableTracker Tracker { get; } = new();
    public ArchetypeService Archetypes { get; }
    public NashLibrary Nash { get; }
    public NotesService Notes { get; }
    public AutoNoteService AutoNotes { get; }
    public LeakDetector Leaks { get; }
    public StudyService Study { get; }
    public BankrollService Bankroll { get; }
    public JournalService Journal { get; }
    public WellbeingMonitor Monitor { get; }
    public string CustomStatsPath { get; }
    /// <summary>Último tilt calculado (lo actualiza la ventana principal); se ve en el tooltip de tu propio panel.</summary>
    public string? TiltText { get; set; }
    public List<LeakBaseline> LeakBaselines { get; }
    public Analytics Analytics { get; }
    public BackgroundMaintenance Maintenance { get; }

    public AppServices(AppSettings settings, string dbPath, string profilesPath)
    {
        Settings = settings;
        Db = new HudDatabase(dbPath);
        Engine = new StatEngine(Db);
        Priors = new PopulationPriors(Db, Engine);
        Notes = new NotesService(Db);
        AutoNotes = new AutoNoteService(Db);
        Db.HandsInserted += items => { try { AutoNotes.Process(items); } catch { /* las autonotas nunca deben romper la importación */ } };
        Archetypes = new ArchetypeService(Db, Engine, Priors);
        Nash = new NashLibrary(Path.GetDirectoryName(dbPath) is { Length: > 0 } dir ? dir : ".");
        Builder = new HudSnapshotBuilder(Db, Engine, Priors, Notes) { Archetypes = key => Archetypes.Cached(key) };
        Detail = new PlayerDetailService(Db, Engine, Priors);
        Analytics = new Analytics(Db);
        Leaks = new LeakDetector(Db, Engine);
        Study = new StudyService(Db);
        Bankroll = new BankrollService(Db);
        Journal = new JournalService(Db);
        Monitor = new WellbeingMonitor(settings.Limits);

        // Estadísticas propias del usuario (editor de DSL)
        CustomStatsPath = Path.Combine(Path.GetDirectoryName(profilesPath) is { Length: > 0 } cd ? cd : ".", "custom-stats.json");
        foreach (var def in CustomStats.Load(CustomStatsPath))
        {
            try { if (!Engine.Has(def.Id)) Engine.Register(def); }
            catch (Exception ex) { AppLog.Error("Estadística propia no válida: " + def.Id, ex); }
        }
        Maintenance = new BackgroundMaintenance(Db, null, AutoNotes);

        // Los perfiles se guardan en un archivo editable la primera vez.
        if (!File.Exists(profilesPath))
        {
            try { HudProfiles.Save(profilesPath, HudProfiles.CreateDefaults()); } catch { /* solo lectura: se usan los de serie */ }
        }
        Profiles = HudProfiles.LoadOrDefault(profilesPath);

        // Rangos objetivo del detector de leaks: copia editable junto a los perfiles del HUD
        var baselinesPath = Path.Combine(Path.GetDirectoryName(profilesPath) ?? ".", "leak-baselines.json");
        LeakBaselines = LeakDetector.LoadBaselines();
        try
        {
            if (File.Exists(baselinesPath)) LeakBaselines = LeakDetector.LoadBaselines(File.ReadAllText(baselinesPath));
            else File.WriteAllText(baselinesPath, System.Text.Json.JsonSerializer.Serialize(LeakBaselines, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* archivo ilegible o de solo lectura: se usan los de serie */ LeakBaselines = LeakDetector.LoadBaselines(); }
    }

    public HudOptions Options => new() { Mode = Settings.HonestMode ? HudMode.Honest : HudMode.Classic };

    public void Dispose()
    {
        Maintenance.Dispose();
        Db.Dispose();
    }
}
