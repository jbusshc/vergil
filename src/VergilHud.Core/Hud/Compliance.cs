namespace VergilHud.Core;

/// <summary>
/// Marca las vistas de análisis profundo que NO deben estar disponibles mientras hay mesas abiertas
/// (replayer, detector de leaks, estudio, calculadora de equity). Ver DESIGN.md §2.
/// </summary>
public interface IRestrictedDuringPlay { }

/// <summary>
/// Modo Juego: hay al menos una mesa abierta. Mientras dura, el HUD solo enseña estadísticas históricas de manos ya jugadas.
/// </summary>
public sealed class PlaySessionState
{
    public bool IsPlaying { get; private set; }
    public int OpenTables { get; private set; }

    /// <summary>Se dispara solo cuando cambia entre jugando / sin mesas.</summary>
    public event Action<bool>? Changed;

    public void Update(int openTables)
    {
        OpenTables = openTables;
        var playing = openTables > 0;
        if (playing == IsPlaying) return;
        IsPlaying = playing;
        Changed?.Invoke(playing);
    }

    /// <summary>Texto del aviso para una vista bloqueada, o null si se puede usar.</summary>
    public string? BlockedMessage(object view) =>
        IsPlaying && view is IRestrictedDuringPlay ? "Disponible al cerrar las mesas" : null;

    public string BannerText => IsPlaying
        ? $"Modo Juego · {OpenTables} {(OpenTables == 1 ? "mesa abierta" : "mesas abiertas")}: replayer, leaks y estudio disponibles al cerrarlas"
        : "";
}

/// <summary>En qué formatos se muestra el HUD en vivo. Editable por el usuario.</summary>
public sealed class FormatPolicy
{
    public Dictionary<string, bool> Allowed { get; set; } = new()
    {
        ["cash"] = true,
        ["mtt"] = true,
        ["sng"] = true,
        ["spin"] = true,   // verificado por el usuario (2026-11-11): permitido con estadísticas históricas propias
        ["zoom"] = false,  // la mesa cambia en cada mano: el HUD no se puede anclar. Sus manos sí se importan.
    };

    public bool IsAllowed(string format) => !Allowed.TryGetValue(format, out var v) || v;
}

/// <summary>
/// El replayer es análisis profundo de manos: no se puede abrir mientras hay mesas abiertas (DESIGN.md §2).
/// La interfaz pregunta a <see cref="PlaySessionState.BlockedMessage"/> con una instancia de esta clase.
/// </summary>
public sealed class ReplayerAccess : IRestrictedDuringPlay { }

/// <summary>El análisis ICM posterior tampoco se usa con mesas abiertas.</summary>
public sealed class IcmAccess : IRestrictedDuringPlay { }
