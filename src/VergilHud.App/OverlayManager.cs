using System.Windows.Threading;
using VergilHud.Core;

namespace VergilHud.App;

/// <summary>Detecta las ventanas de mesa abiertas y mantiene un overlay por cada una.</summary>
public sealed class OverlayManager
{
    readonly AppServices _svc;
    readonly Dictionary<IntPtr, OverlayWindow> _overlays = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    /// <summary>Mesas con HUD visible.</summary>
    public int ActiveCount => _overlays.Count;
    /// <summary>Mesas de PokerStars abiertas (con o sin HUD): alimentan el Modo Juego.</summary>
    public int OpenTables { get; private set; }
    /// <summary>Paneles ocultos con el atajo Ctrl+Alt+H (las mesas siguen igual).</summary>
    public bool Hidden { get; set; }
    HudScope _lastScope;

    public OverlayManager(AppServices svc)
    {
        _svc = svc;
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();

    public void Stop()
    {
        _timer.Stop();
        foreach (var o in _overlays.Values) SafeClose(o);
        _overlays.Clear();
        _svc.Play.Update(0);
    }

    static void SafeClose(OverlayWindow w)
    {
        try { w.Close(); } catch (InvalidOperationException) { /* ya cerrada junto a su mesa */ }
    }

    /// <summary>Fuerza a recalcular los paneles de todas las mesas en el siguiente ciclo.</summary>
    public void InvalidateAll()
    {
        foreach (var o in _overlays.Values) o.LastHandId = null;
    }

    /// <summary>¿Es la ventana de una mesa de PokerStars? (proceso de PokerStars, clase GLFW, y no el lobby).</summary>
    static bool IsTableWindow(NativeMethods.TopWindow w) =>
        w.Class == "GLFW30" && w.Title.Contains(" - ", StringComparison.Ordinal)
        && !w.Title.Contains("Lobby", StringComparison.OrdinalIgnoreCase)
        && NativeMethods.ProcessName(w.Pid).StartsWith("PokerStars", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// La ventana de cash se llama "Machado - No Limit Hold'em 250/500 ...": empieza por el nombre de la mesa.
    /// En torneos el nombre interno es "&lt;id&gt; &lt;n&gt;" y el título lleva el id del torneo.
    /// </summary>
    static bool Matches(string tableName, Hand hand, string title)
    {
        if (title == tableName || title.StartsWith(tableName + " ", StringComparison.Ordinal)) return true;
        return hand.TournamentId != null && title.Contains(hand.TournamentId, StringComparison.Ordinal);
    }

    void Tick()
    {
        var windows = NativeMethods.GetTopLevelWindows();
        var tableWindows = windows.Where(IsTableWindow).ToList();
        OpenTables = tableWindows.Count;
        _svc.Play.Update(OpenTables);

        if (_svc.Settings.HudScope != _lastScope) { _lastScope = _svc.Settings.HudScope; InvalidateAll(); }
        var tables = _svc.Tracker.Snapshot();
        var seen = new HashSet<IntPtr>();
        foreach (var w in tableWindows)
        {
            var hit = tables.Where(kv => Matches(kv.Key, kv.Value, w.Title)).OrderByDescending(kv => kv.Key.Length).FirstOrDefault();
            if (hit.Key == null) continue;
            var hand = hit.Value;
            if (!_svc.Settings.FormatPolicy.IsAllowed(hand.Format)) continue;
            seen.Add(w.Hwnd);

            if (!_overlays.TryGetValue(w.Hwnd, out var ov) || ov.TableName != hit.Key)
            {
                if (ov != null) SafeClose(ov);
                ov = new OverlayWindow(w.Hwnd, hit.Key, _svc);
                _overlays[w.Hwnd] = ov;
            }
            if (ov.LastHandId != hand.Id) ov.SetHand(hand);
            ov.UserHidden = Hidden;
            ov.Follow();
        }

        foreach (var hwnd in _overlays.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            SafeClose(_overlays[hwnd]);
            _overlays.Remove(hwnd);
        }
    }
}
