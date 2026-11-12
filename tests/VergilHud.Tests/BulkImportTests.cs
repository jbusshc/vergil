using System.Text;
using System.Text.RegularExpressions;
using VergilHud.Core;
using Xunit;

namespace VergilHud.Tests;

[Collection("Rendimiento")]
public class BulkImportTests
{
    static readonly string[] Templates =
    {
        "cases/cash_01_fold_to_bb", "cases/cash_03_open_steal_btn", "cases/cash_04_utg_open_call_cbet_fold", "cases/cash_14_barrels_showdown_win",
        "cases/cash_16_preflop_allin_showdown", "cases/cash_25_raise_cbet", "cases/cash_29_turn_fold_to_barrel", "cases/cash_30_turn_check_raise",
    };

    /// <summary>Un archivo con n manos distintas (ids únicos) hechas a partir de los fixtures.</summary>
    static string BigFile(string dir, string name, int n, long firstId, DateTime t0)
    {
        var path = Path.Combine(dir, name);
        var sb = new StringBuilder(n * 700);
        var texts = Templates.Select(Fixtures.Text).ToArray();
        for (int i = 0; i < n; i++)
        {
            var id = firstId + i;
            var t = t0.AddMinutes(i / 3);
            sb.Append(Regex.Replace(texts[i % texts.Length], @"Hand #\d+:", $"Hand #{id}:", RegexOptions.None)
                .Replace("2026/03/01 20:00:00", t.ToString("yyyy/MM/dd H:mm:ss")));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"vergil-bulk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    static List<string> Indexes(HudDatabase db) => db.Query(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'ix_%' ORDER BY name";
        using var r = cmd.ExecuteReader();
        var l = new List<string>();
        while (r.Read()) l.Add(r.GetString(0));
        return l;
    });

    [Fact]
    public void Bulk_import_reports_progress_and_finishes_with_everything_in_place()
    {
        var dir = TempDir();
        try
        {
            BigFile(dir, "a.txt", 400, 5_000_000, new DateTime(2026, 3, 1, 10, 0, 0));
            BigFile(dir, "b.txt", 300, 6_000_000, new DateTime(2026, 3, 2, 10, 0, 0));
            using var db = new HudDatabase(":memory:");
            using var imp = new HandHistoryImporter(db);
            var events = new List<ImportProgress>();
            var sum = imp.ImportFolder(dir, new SyncProgress<ImportProgress>(events.Add));

            Assert.Equal(700, db.GetHandCount());
            Assert.Equal(700, sum.Hands);
            Assert.Equal(2, sum.Files);
            Assert.Equal(2, sum.FilesDone);
            Assert.False(sum.Cancelled);
            Assert.Equal(0, sum.Errors);
            Assert.Equal("Importando", events[0].Phase);
            Assert.Contains(events, e => e.Phase == "Calculando EV de los all-in");
            Assert.Contains(events, e => e.Phase == "Detectando sesiones");
            Assert.Equal("Terminado", events[^1].Phase);
            var importing = events.Where(e => e.Phase == "Importando").ToList();
            Assert.Equal(importing.OrderBy(e => e.FilesDone).Select(e => e.FilesDone), importing.Select(e => e.FilesDone));   // monótono
            Assert.Equal(new[] { "a.txt", "b.txt" }, importing.Where(e => e.CurrentFile != null).Select(e => e.CurrentFile!).ToArray());

            // Los índices vuelven y las sesiones y el EV están calculados
            Assert.Equal(HandStore.Indexes.Select(i => i.Name).OrderBy(x => x).ToList(), Indexes(db).Where(i => HandStore.Indexes.Any(h => h.Name == i)).ToList());
            Assert.NotEmpty(SessionService.GetSessions(db, Period.All));
            Assert.True(db.Query(c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM allin_ev";
                return (long)cmd.ExecuteScalar()! > 0;
            }));

            // Volver a importar la carpeta no duplica nada
            Assert.Equal(0, imp.ImportFolder(dir, null, default, postProcess: false).Hands);
            Assert.Equal(700, db.GetHandCount());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Cancelling_stops_between_files_and_still_restores_the_indexes()
    {
        var dir = TempDir();
        try
        {
            for (int i = 0; i < 4; i++) BigFile(dir, $"f{i}.txt", 100, 7_000_000 + i * 1000, new DateTime(2026, 3, 1).AddDays(i));
            using var db = new HudDatabase(":memory:");
            using var imp = new HandHistoryImporter(db);
            using var cts = new CancellationTokenSource();
            var sum = imp.ImportFolder(dir, new SyncProgress<ImportProgress>(p => { if (p.FilesDone >= 1) cts.Cancel(); }), cts.Token);

            Assert.True(sum.Cancelled);
            Assert.InRange(sum.FilesDone, 1, 2);
            Assert.InRange(db.GetHandCount(), 100, 200);
            Assert.Equal(HandStore.Indexes.Length, Indexes(db).Count(i => HandStore.Indexes.Any(h => h.Name == i)));
            Assert.Empty(SessionService.GetSessions(db, Period.All));      // cancelado: no se hace el postproceso

            // Se puede reanudar: solo entra lo que faltaba
            var rest = imp.ImportFolder(dir, null, default, postProcess: false);
            Assert.Equal(400, db.GetHandCount());
            Assert.Equal(400 - sum.Hands, rest.Hands);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Indexes_missing_after_an_interrupted_import_are_recreated_on_open()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vergil-idx-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new HudDatabase(path)) db.DropSecondaryIndexes();   // simula un cierre brusco a mitad de importación
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using var again = new HudDatabase(path);
            Assert.Equal(HandStore.Indexes.Length, Indexes(again).Count(i => HandStore.Indexes.Any(h => h.Name == i)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }

    [Fact]
    public void A_bad_hand_is_logged_and_the_rest_are_imported()
    {
        var dir = TempDir();
        try
        {
            var good = Fixtures.Text("cases/cash_01_fold_to_bb");
            // Una "mano" con cabecera válida pero sin asientos ni ciegas: se lee, pero otra con el cuerpo roto no debe parar nada
            File.WriteAllText(Path.Combine(dir, "mix.txt"), good + "PokerStars Hand #99:  Hold'em No Limit ($1/$2 USD)\n*** SUMMARY ***\nTotal pot $0 | Rake $0\n\n\n\n"
                + Regex.Replace(Fixtures.Text("cases/cash_03_open_steal_btn"), @"Hand #\d+:", "Hand #100:"));
            using var db = new HudDatabase(":memory:");
            using var imp = new HandHistoryImporter(db);
            var sum = imp.ImportFolder(dir, null, default, postProcess: false);
            Assert.True(db.GetHandCount() >= 2);       // las manos buenas entran sí o sí
            Assert.Equal(sum.Hands, db.GetHandCount());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Import_throughput_has_not_regressed()
    {
        var dir = TempDir();
        try
        {
            BigFile(dir, "big.txt", 20_000, 9_000_000, new DateTime(2026, 1, 1));
            using var db = new HudDatabase(":memory:");
            using var imp = new HandHistoryImporter(db);
            var sum = imp.ImportFolder(dir, null, default, postProcess: false);
            Assert.Equal(20_000, db.GetHandCount());
            Console.WriteLine($"Importación masiva: {sum.HandsPerSecond:N0} manos/s ({sum.Elapsed.TotalSeconds:0.0} s para 20.000 manos)");
            // Guarda de regresión, no una promesa: en Release se miden 2.000-3.300 manos/s según la carga de la máquina.
            Assert.True(sum.HandsPerSecond >= 1200, $"{sum.HandsPerSecond:N0} manos/s");
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>IProgress que llama en el mismo hilo (Progress&lt;T&gt; es asíncrono y desordena las pruebas).</summary>
    sealed class SyncProgress<T> : IProgress<T>
    {
        readonly Action<T> _a;
        public SyncProgress(Action<T> a) => _a = a;
        public void Report(T value) => _a(value);
    }
}
