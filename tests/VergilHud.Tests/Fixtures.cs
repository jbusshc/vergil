using VergilHud.Core;

namespace VergilHud.Tests;

/// <summary>Acceso a los historiales de ejemplo (copiados junto al ensamblado de pruebas).</summary>
static class Fixtures
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>Ruta relativa sin extensión, p. ej. "cases/cash_01_fold_to_bb".</summary>
    public static string Text(string name) => File.ReadAllText(Path.Combine(Root, name + ".txt"));

    public static IEnumerable<string> CaseNames(string prefix = "") =>
        Directory.GetFiles(Path.Combine(Root, "cases"), prefix + "*.txt")
            .Select(f => "cases/" + Path.GetFileNameWithoutExtension(f)).OrderBy(x => x, StringComparer.Ordinal);

    /// <summary>Todas las manos de todos los fixtures (casos + manos reales anonimizadas).</summary>
    public static IEnumerable<Hand> AllHands()
    {
        foreach (var f in Directory.GetFiles(Root, "*.txt", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
            foreach (var h in PokerStarsParser.ParseAll(File.ReadAllText(f)))
                yield return h;
    }
}
