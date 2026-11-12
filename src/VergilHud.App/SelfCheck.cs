using System.Diagnostics;
using System.IO;
using System.Text;
using VergilHud.Core;
using Path = System.IO.Path;

namespace VergilHud.App;

/// <summary>--selfcheck &lt;hud.db&gt; &lt;salida.txt&gt;: ejecuta todos los análisis sobre una COPIA de la base de datos y anota resultados y errores.</summary>
static class SelfCheck
{
    public static void Run(string dbCopy, string outFile)
    {
        var sb = new StringBuilder();
        var tmp = Path.Combine(Path.GetTempPath(), "vergil-selfcheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        void Step(string name, Func<string> f)
        {
            var sw = Stopwatch.StartNew();
            try { var r = f(); sb.AppendLine($"[OK ] {name} ({sw.ElapsedMilliseconds} ms): {r}"); }
            catch (Exception ex) { sb.AppendLine($"[ERR] {name}: {ex}"); }
        }
        try
        {
            using var svc = new AppServices(new AppSettings(), dbCopy, Path.Combine(tmp, "profiles.json"));
            var hero = svc.Db.GetHeroName();
            sb.AppendLine($"Versión de esquema: {svc.Db.SchemaVersion} · manos: {svc.Db.GetHandCount()} · héroe: {hero ?? "(ninguno)"}");
            Step("Detector de leaks", () => { if (hero == null) return "sin héroe"; var l = svc.Leaks.Detect(hero, svc.LeakBaselines); return $"{l.Count} leaks" + (l.Count > 0 ? ": " + LeakDetector.Describe(l[0]) : ""); });
            Step("Estudio (sincronización)", () => $"{svc.Study.SyncAutomatic()} manos nuevas, {svc.Study.GetStats().Total} en cola");
            Step("Equilibrio de apuestas", () => { var b = BalanceReport.Build(svc.Db); return $"{b.Hands} manos, {b.Rows.Count} filas, {b.Findings.Count} hallazgos"; });
            Step("Población (todos)", () => $"{PopulationReport.Build(svc.Db, svc.Engine, null).Count} spots");
            Step("Arquetipos", () => string.Join(", ", new[] { "cash", "spin", "mtt", "all" }.Select(k => k + "=" + (svc.Archetypes.Ensure(k, true)?.Clusters.Count.ToString() ?? "sin modelo"))));
            Step("Bankroll", () => { var s = svc.Bankroll.Summarize(); return $"saldo {s.Balance:0.00}, cash {s.CashResult:0.00}, torneos {s.TournamentResult:0.00}"; });
            Step("Tilt (historial)", () => $"{(hero == null ? 0 : TiltService.ScoreHistory(svc.Db, svc.Engine, hero).Count)} sesiones");
            Step("Correlaciones", () => $"{CorrelationReport.Build(svc.Db, svc.Journal).Messages.Count} mensajes");
            Step("ICM", () => $"{IcmAnalysis.ScanHero(svc.Db, new[] { 0.5, 0.3, 0.2 }).Count} all-in analizados");
            Step("Nash (matriz y desviaciones)", () =>
            {
                var lib = new NashLibrary(tmp);
                var r = NashDeviations.Analyze(svc.Db, lib);
                return $"{r.Spots} spots, {r.Deviations.Count} desviaciones, coste {r.TotalCostBb:0.0} bb";
            });
            Step("Informe semanal", () => { var p = WeeklyReport.Generate(svc.Db, svc.Engine, svc.Study, svc.Leaks, svc.LeakBaselines, tmp, DateTime.Now); return new FileInfo(p).Length + " bytes"; });
            Step("Manos propuestas", () => $"{DailyPicks.Get(svc.Db, svc.Study, hero == null ? Array.Empty<Leak>() : svc.Leaks.Detect(hero, svc.LeakBaselines), svc.Leaks, null).Count} manos");
        }
        catch (Exception ex) { sb.AppendLine("[ERR] General: " + ex); }
        File.WriteAllText(outFile, sb.ToString());
        try { Directory.Delete(tmp, true); } catch { }
    }
}
