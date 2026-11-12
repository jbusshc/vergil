using System.Text;

namespace VergilHud.Core;

/// <summary>
/// Vigila la carpeta de historiales (sondeo cada segundo) e importa solo lo nuevo de cada archivo.
/// </summary>
public sealed class HandHistoryImporter : IDisposable
{
    readonly HudDatabase _db;
    readonly Dictionary<string, long> _offsets;
    readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    Timer? _timer;
    string _folder = "";
    int _busy;

    /// <summary>Se dispara (en un hilo de fondo) con las manos nuevas de cada archivo.</summary>
    public event Action<IReadOnlyList<Hand>>? HandsImported;
    public event Action<string>? ImportError;

    public bool IsRunning => _timer != null;

    public HandHistoryImporter(HudDatabase db)
    {
        _db = db;
        _offsets = db.GetFileOffsets();
    }

    public void Start(string folder)
    {
        Stop();
        _folder = folder;
        _timer = new Timer(_ => Poll(), null, 0, 1000);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (!Directory.Exists(_folder)) return;
            foreach (var f in Directory.EnumerateFiles(_folder, "*.txt", SearchOption.AllDirectories))
            {
                if (_timer == null) break;
                try { ImportFile(f); }
                catch (IOException) { /* archivo bloqueado; se reintenta en el siguiente ciclo */ }
                catch (Exception ex) { ImportError?.Invoke($"{Path.GetFileName(f)}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { ImportError?.Invoke(ex.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Marca todo lo que hay ya en la carpeta como importado: solo se leerán las manos nuevas a partir de ahora.</summary>
    public int SkipExisting(string folder)
    {
        int n = 0;
        if (!Directory.Exists(folder)) return 0;
        foreach (var f in Directory.EnumerateFiles(folder, "*.txt", SearchOption.AllDirectories))
        {
            try
            {
                var len = new FileInfo(f).Length;
                _offsets[f] = len;
                _db.SetFileOffset(f, len);
                n++;
            }
            catch (IOException) { }
        }
        return n;
    }

    sealed record Parsed(string Raw, Hand? Hand, List<PlayerHandFacts>? Facts, string? Error);

    public const int BatchSize = 1000;

    /// <summary>Lee un lote de manos y calcula sus hechos, repartido entre los núcleos. Una mano que falla no detiene el lote.</summary>
    static List<Parsed> ParseChunk(string[] blocks) =>
        blocks.AsParallel().AsOrdered().Select(b =>
        {
            try
            {
                if (PokerStarsParser.Parse(b) is not { } hand) throw new FormatException("No se pudo leer la cabecera de la mano");
                return new Parsed(b, hand, FactExtractor.Extract(hand), null);
            }
            catch (Exception ex) { return new Parsed(b, null, null, ex.Message); }
        }).ToList();
    readonly object _importLock = new();
    volatile bool _bulk;

    /// <summary>Importa lo que haya de nuevo en un archivo. Seguro frente a la vigilancia de la carpeta (nunca importan a la vez).</summary>
    public int ImportFile(string path)
    {
        lock (_importLock) return ImportFileCore(path);
    }

    int ImportFileCore(string path)
    {
        var length = new FileInfo(path).Length;
        long offset = _offsets.GetValueOrDefault(path);
        if (length < offset) offset = 0; // archivo reescrito
        if (length == offset) return 0;

        string text;
        int bom = 0; // PokerStars guarda los historiales en UTF-8 con BOM: hay que saltarlo, no dejarlo pegado a la 1.ª cabecera
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.Seek(offset, SeekOrigin.Begin);
            var bytes = new byte[length - offset];
            int read = 0;
            while (read < bytes.Length)
            {
                int n = fs.Read(bytes, read, bytes.Length - read);
                if (n == 0) break;
                read += n;
            }
            if (offset == 0 && read >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bom = 3;
            text = Encoding.UTF8.GetString(bytes, bom, read - bom);
        }

        var (blocks, consumed) = PokerStarsParser.SplitComplete(text);
        if (blocks.Count == 0 && consumed == 0 && text.Length > 300 && !_warned.Contains(path)
            && text.Contains("PokerStars", StringComparison.Ordinal) && !text.Contains("Hand #", StringComparison.Ordinal))
        {
            // Historial en otro idioma: no se parsea. Se avisa una vez y se salta el archivo.
            _warned.Add(path);
            ImportError?.Invoke($"{Path.GetFileName(path)}: parece estar en otro idioma. Cambia el idioma de los historiales a inglés en PokerStars.");
            _offsets[path] = length;
            _db.SetFileOffset(path, length);
            return 0;
        }

        // Leer y calcular los hechos de cada mano no depende de las demás: se reparte entre los núcleos. Una mano que falla
        // no detiene la importación: se registra y se sigue.
        // Transacciones de 1.000 manos: acotan la memoria y hacen que un fallo no pierda todo el archivo. Mientras se insertan
        // las de un lote, las del siguiente ya se están leyendo en los demás núcleos (la escritura en SQLite es de un solo hilo).
        var inserted = new List<Hand>();
        var chunks = blocks.Chunk(BatchSize).ToList();
        Task<List<Parsed>>? pending = chunks.Count > 0 ? Task.Run(() => ParseChunk(chunks[0])) : null;
        for (int i = 0; i < chunks.Count; i++)
        {
            var parsed = pending!.GetAwaiter().GetResult();
            if (i + 1 < chunks.Count)
            {
                var next = chunks[i + 1];
                pending = Task.Run(() => ParseChunk(next));
            }
            var items = new List<(Hand, List<PlayerHandFacts>)>(parsed.Count);
            foreach (var x in parsed)
            {
                if (x.Hand != null) items.Add((x.Hand, x.Facts!));
                else _db.LogImportError(path, offset, x.Error!, x.Raw);
            }
            if (items.Count > 0) inserted.AddRange(_db.InsertHands(items));
        }
        if (consumed > 0)
        {
            var newOffset = offset + bom + Encoding.UTF8.GetByteCount(text.AsSpan(0, consumed));
            _offsets[path] = newOffset;
            _db.SetFileOffset(path, newOffset);
        }
        if (inserted.Count > 0 && !_bulk) HandsImported?.Invoke(inserted);   // en una importación masiva no se avisa al HUD de manos antiguas
        return inserted.Count;
    }

    /// <summary>
    /// Importación masiva de una carpeta (historial antiguo): con progreso y cancelación. Mientras dura quita los índices
    /// secundarios para ir más rápido y los recrea al final aunque se cancele. Después, si se pide, calcula el EV de los
    /// all-in y las sesiones.
    /// </summary>
    public ImportSummary ImportFolder(string folder, IProgress<ImportProgress>? progress = null, CancellationToken ct = default,
        bool postProcess = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var files = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.txt", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        long hands = 0, failedBefore = _db.GetImportErrorCount();
        int done = 0;
        bool cancelled = false;

        void Report(string phase, string? file) => progress?.Report(new ImportProgress(phase, done, files.Count, hands, file,
            sw.Elapsed.TotalSeconds > 0 ? hands / sw.Elapsed.TotalSeconds : 0, sw.Elapsed));

        _db.DropSecondaryIndexes();
        _bulk = true;
        try
        {
            Report("Importando", null);
            foreach (var f in files)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                try { hands += ImportFile(f); }
                catch (IOException) { /* archivo bloqueado por PokerStars: se importará en la vigilancia normal */ }
                catch (Exception ex) { ImportError?.Invoke($"{Path.GetFileName(f)}: {ex.Message}"); }
                done++;
                Report("Importando", Path.GetFileName(f));
            }
        }
        finally
        {
            _bulk = false;
            _db.CreateSecondaryIndexes();
        }

        if (postProcess && !cancelled)
        {
            Report("Calculando EV de los all-in", null);
            AllInEvService.ProcessAll(_db, ct);
            Report("Detectando sesiones", null);
            SessionService.Rebuild(_db);
        }
        Report(cancelled ? "Cancelado" : "Terminado", null);
        return new ImportSummary(files.Count, done, hands, _db.GetImportErrorCount() - failedBefore, sw.Elapsed, cancelled);
    }

    public void Dispose() => Stop();
}

/// <param name="Phase">Importando, Calculando EV de los all-in, Detectando sesiones, Terminado o Cancelado.</param>
public sealed record ImportProgress(string Phase, int FilesDone, int FilesTotal, long HandsImported, string? CurrentFile,
    double HandsPerSecond, TimeSpan Elapsed);

public sealed record ImportSummary(int Files, int FilesDone, long Hands, long Errors, TimeSpan Elapsed, bool Cancelled)
{
    public double HandsPerSecond => Elapsed.TotalSeconds > 0 ? Hands / Elapsed.TotalSeconds : 0;
}
