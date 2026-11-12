namespace VergilHud.Core;

/// <summary>Lo que el asistente de primer arranque averigua de la carpeta de historiales.</summary>
public sealed record FirstRunReport(
    string? Folder,
    bool FolderExists,
    int Files,
    DateTime? LatestWriteUtc,
    int HandsFound,
    bool LooksNonEnglish,
    IReadOnlyDictionary<string, int> FormatsSeen,
    string? Hero,
    IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0;
}

public static class FirstRunCheck
{
    /// <summary>Revisa la carpeta de historiales (solo lectura) y detecta idioma, formatos jugados y nick.</summary>
    public static FirstRunReport Analyze(string? folder, int maxFilesToSample = 8)
    {
        var problems = new List<string>();
        var formats = new Dictionary<string, int>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            problems.Add("No se encuentra la carpeta de historiales. En PokerStars activa el guardado del historial de manos.");
            return new FirstRunReport(folder, false, 0, null, 0, false, formats, null, problems);
        }

        var files = new DirectoryInfo(folder).EnumerateFiles("*.txt", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        if (files.Count == 0)
        {
            problems.Add("La carpeta existe pero aún no hay historiales. Juega unas manos con el guardado activado.");
            return new FirstRunReport(folder, true, 0, null, 0, false, formats, null, problems);
        }

        int hands = 0;
        bool nonEnglish = false;
        string? hero = null;
        foreach (var f in files.Take(maxFilesToSample))
        {
            string text;
            try
            {
                using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buf = new byte[Math.Min(fs.Length, 256 * 1024)];
                int n = fs.Read(buf, 0, buf.Length);
                int skip = n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF ? 3 : 0;
                text = System.Text.Encoding.UTF8.GetString(buf, skip, n - skip);
            }
            catch (IOException) { continue; }

            var (blocks, _) = PokerStarsParser.SplitComplete(text);
            if (blocks.Count == 0)
            {
                if (text.Contains("PokerStars", StringComparison.Ordinal) && !text.Contains("Hand #", StringComparison.Ordinal))
                    nonEnglish = true;
                continue;
            }
            hands += blocks.Count;
            foreach (var b in blocks.Take(1))
                if (PokerStarsParser.Parse(b) is { } h)
                {
                    formats[h.Format] = formats.GetValueOrDefault(h.Format) + 1;
                    hero ??= h.Hero;
                }
        }

        if (nonEnglish)
            problems.Add("Algunos historiales parecen estar en otro idioma. Cambia el idioma de los historiales a inglés en PokerStars.");
        if (hands == 0 && !nonEnglish)
            problems.Add("No se ha podido leer ninguna mano completa en los historiales.");

        return new FirstRunReport(folder, true, files.Count, files[0].LastWriteTimeUtc, hands, nonEnglish, formats, hero, problems);
    }
}
