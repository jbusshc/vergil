using System.IO;
using System.Text.Json;
using VergilHud.Core;

namespace VergilHud.App;

public sealed class AppSettings
{
    /// <summary>%APPDATA%\VergilHud, o la carpeta indicada en la variable de entorno VERGIL_DATA_DIR (pruebas / uso portátil).</summary>
    public static readonly string DataDir = Environment.GetEnvironmentVariable("VERGIL_DATA_DIR") is { Length: > 0 } d
        ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VergilHud");
    public static string DatabasePath => Path.Combine(DataDir, "hud.db");
    public static string ProfilesPath => Path.Combine(DataDir, "hud-profiles.json");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");

    public string HandHistoryFolder { get; set; } = "";
    public bool RotateHeroToBottom { get; set; } = true;
    public bool ShowHeroPanel { get; set; } = true;
    public double PanelOpacity { get; set; } = 0.85;
    /// <summary>true = valor posterior bayesiano con barra de fiabilidad; false = x/n crudo como los HUD clásicos.</summary>
    public bool HonestMode { get; set; } = true;
    /// <summary>Qué manos alimentan el HUD: Format (mismo tipo de juego), Global (todo el historial) o Table (solo esta mesa).</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public HudScope HudScope { get; set; } = HudScope.Format;
    /// <summary>Bloquea el arrastre de los paneles.</summary>
    public bool LockPanels { get; set; }

    /// <summary>Pasa al siguiente ámbito del HUD (tipo de juego → todo el historial → esta mesa) y lo guarda.</summary>
    public void SetScope(HudScope s)
    {
        if (HudScope == s) return;
        HudScope = s;
        Save();
    }

    public void CycleScope()
    {
        HudScope = (HudScope)(((int)HudScope + 1) % 3);
        Save();
    }
    public bool FirstRunDone { get; set; }
    /// <summary>es | en. Se aplica al reiniciar.</summary>
    public string Language { get; set; } = "es";
    /// <summary>dark | light. Se aplica al reiniciar.</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>Aviso cuando el bankroll baja de estos buy-ins (cash o torneos).</summary>
    public double MinBuyins { get; set; } = 20;
    public WellbeingLimits Limits { get; set; } = new();
    /// <summary>Muestra el tilt score en la barra de estado mientras juegas.</summary>
    public bool ShowTilt { get; set; } = true;
    /// <summary>Atajos globales Ctrl+Alt+H (mostrar/ocultar paneles) y Ctrl+Alt+L (fijar paneles).</summary>
    public bool Hotkeys { get; set; } = true;
    public static string ReportsDir => Path.Combine(DataDir, "reports");
    public static string BackupsDir => Path.Combine(DataDir, "backups");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string CustomStatsPath => Path.Combine(DataDir, "custom-stats.json");
    /// <summary>En qué formatos se muestra el HUD en vivo (DESIGN.md §18.6).</summary>
    public FormatPolicy FormatPolicy { get; set; } = new();

    /// <summary>Desplazamientos de panel arrastrados por el usuario, clave "maxSeats-posVisual", valor [dx, dy] relativos.</summary>
    public Dictionary<string, double[]> PanelOffsets { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch { /* configuración corrupta: usar valores por defecto */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Busca la carpeta de historiales de PokerStars en las ubicaciones habituales.</summary>
    public static string? GuessHandHistoryFolder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var client in new[] { "PokerStars.ES", "PokerStars.EU", "PokerStars", "PokerStars.FR", "PokerStars.IT", "PokerStars.UK" })
        {
            var dir = Path.Combine(local, client, "HandHistory");
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }
}
