namespace VergilHud.Core;

/// <summary>
/// Tras importar manos nuevas, recalcula sesiones y EV de all-ins en segundo plano (nunca en el camino crítico del HUD).
/// Varias llamadas seguidas a <see cref="Poke"/> se agrupan en un solo cálculo.
/// </summary>
public sealed class BackgroundMaintenance : IDisposable
{
    readonly HudDatabase _db;
    readonly Timer _timer;
    readonly TimeSpan _delay;
    int _running;
    volatile bool _dirty;

    /// <summary>Se dispara (en un hilo de fondo) cuando termina un ciclo que ha cambiado algo.</summary>
    public event Action? Completed;
    public event Action<Exception>? Failed;

    readonly AutoNoteService? _autoNotes;

    public BackgroundMaintenance(HudDatabase db, TimeSpan? delay = null, AutoNoteService? autoNotes = null)
    {
        _db = db;
        _autoNotes = autoNotes;
        _delay = delay ?? TimeSpan.FromSeconds(5);
        _timer = new Timer(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Poke()
    {
        _dirty = true;
        _timer.Change(_delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Ejecuta ya (y espera). Lo usan los botones de "recalcular" y las pruebas.</summary>
    public Task RunNowAsync() => Task.Run(() => Run(force: true));

    void Run(bool force = false)
    {
        if (!force && !_dirty) return;
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            if (!force) _timer.Change(_delay, Timeout.InfiniteTimeSpan);   // ya hay uno en marcha: reintentar luego
            return;
        }
        try
        {
            _dirty = false;
            AllInEvService.ProcessAll(_db);
            SessionService.EnsureUpToDate(_db);
            _autoNotes?.EnsureUpToDate();   // regenera las autonotas solo si cambiaron las reglas
            Completed?.Invoke();
        }
        catch (Exception ex) { Failed?.Invoke(ex); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    public void Dispose() => _timer.Dispose();
}
