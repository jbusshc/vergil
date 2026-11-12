using System.Text.Json;

namespace VergilHud.Core;

/// <summary>Las 169 manos de salida (13 parejas, 78 suited, 78 offsuit) en la cuadrícula 13×13: fila = carta alta, columna = baja.</summary>
public static class HandClasses
{
    public const int Count = 169;
    const string R = "23456789TJQKA";

    /// <summary>Nombre de cada casilla de la cuadrícula (AA, AKs, AKo…). Índice = fila*13 + columna, fila 0 = A.</summary>
    public static readonly string[] Names = new string[Count];
    /// <summary>Combinaciones concretas (carta, carta) de cada clase.</summary>
    public static readonly int[][][] Combos = new int[Count][][];
    /// <summary>Nº de pares de combinaciones sin cartas en común entre dos clases: peso de cada enfrentamiento.</summary>
    public static readonly double[] Overlap = new double[Count * Count];
    public static readonly double[] ComboCount = new double[Count];

    static readonly Dictionary<string, int> ByName = new();

    static HandClasses()
    {
        for (int row = 0; row < 13; row++)
            for (int col = 0; col < 13; col++)
            {
                int hi = 12 - row, lo = 12 - col;
                int i = row * 13 + col;
                string name = hi == lo ? $"{R[hi]}{R[hi]}" : hi > lo ? $"{R[hi]}{R[lo]}s" : $"{R[lo]}{R[hi]}o";
                Names[i] = name;
                ByName[name] = i;
                var list = new List<int[]>();
                for (int s1 = 0; s1 < 4; s1++)
                    for (int s2 = 0; s2 < 4; s2++)
                    {
                        if (hi == lo && s1 >= s2) continue;
                        bool suited = s1 == s2;
                        if (hi != lo && (hi > lo) != suited) continue;
                        // Para hi>lo (suited) la carta alta es hi; para offsuit la alta es la mayor de las dos
                        int a = Math.Max(hi, lo) * 4 + (hi >= lo ? s1 : s2), b = Math.Min(hi, lo) * 4 + (hi >= lo ? s2 : s1);
                        if (hi == lo) { a = hi * 4 + s1; b = hi * 4 + s2; }
                        list.Add(new[] { a, b });
                    }
                Combos[i] = list.ToArray();
                ComboCount[i] = list.Count;
            }
        for (int a = 0; a < Count; a++)
            for (int b = 0; b < Count; b++)
            {
                int n = 0;
                foreach (var x in Combos[a])
                    foreach (var y in Combos[b])
                        if (x[0] != y[0] && x[0] != y[1] && x[1] != y[0] && x[1] != y[1]) n++;
                Overlap[a * Count + b] = n;
            }
    }

    public static int IndexOf(string handName) => ByName.TryGetValue(handName, out var i) ? i : -1;

    /// <summary>Clase de unas cartas concretas ("Ah Kd" → AKo), o -1 si no son dos cartas válidas.</summary>
    public static int IndexOfCards(string? holeCards)
    {
        var n = Cards.HandName(holeCards ?? "");
        return n.Length == 0 ? -1 : IndexOf(n);
    }
}

/// <summary>Equity de cada mano de salida contra cada otra (promedio sobre combinaciones sin cartas repetidas y tableros). Es la base del solver.</summary>
public sealed class EquityMatrix
{
    public const int Version = 1;
    readonly float[] _e;

    EquityMatrix(float[] e) => _e = e;

    /// <summary>Equity (victoria + mitad de empates) de la clase <paramref name="a"/> contra <paramref name="b"/>.</summary>
    public double this[int a, int b] => _e[a * HandClasses.Count + b];

    /// <summary>Calcula la matriz por Monte Carlo: <paramref name="samples"/> tableros por pareja de clases, con semilla fija (reproducible).</summary>
    public static EquityMatrix Compute(int samples = 3000, int seed = 7, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        int n = HandClasses.Count;
        var e = new float[n * n];
        var pairs = new List<(int, int)>();
        for (int a = 0; a < n; a++)
            for (int b = a; b < n; b++) pairs.Add((a, b));
        int done = 0;
        Parallel.ForEach(pairs, new ParallelOptions { CancellationToken = ct }, p =>
        {
            var (a, b) = p;
            double v = a == b ? 0.5 : Pair(a, b, samples, seed * 1_000_003 + a * n + b);
            e[a * n + b] = (float)v;
            e[b * n + a] = (float)(1 - v);
            int d = Interlocked.Increment(ref done);
            if (d % 500 == 0) progress?.Report((double)d / pairs.Count);
        });
        progress?.Report(1);
        return new EquityMatrix(e);
    }

    static double Pair(int a, int b, int samples, int seed)
    {
        var rng = new Random(seed);
        var ca = HandClasses.Combos[a];
        var cb = HandClasses.Combos[b];
        Span<int> deck = stackalloc int[52];
        Span<int> h1 = stackalloc int[7];
        Span<int> h2 = stackalloc int[7];
        double score = 0;
        int counted = 0;
        for (int s = 0; s < samples; s++)
        {
            int[] x, y;
            int guard = 0;
            do
            {
                x = ca[rng.Next(ca.Length)];
                y = cb[rng.Next(cb.Length)];
            } while ((x[0] == y[0] || x[0] == y[1] || x[1] == y[0] || x[1] == y[1]) && ++guard < 1000);
            if (guard >= 1000) continue;

            int m = 0;
            for (int c = 0; c < 52; c++)
                if (c != x[0] && c != x[1] && c != y[0] && c != y[1]) deck[m++] = c;
            for (int k = 0; k < 5; k++)
            {
                int j = k + rng.Next(m - k);
                (deck[k], deck[j]) = (deck[j], deck[k]);
            }
            h1[0] = x[0]; h1[1] = x[1]; h2[0] = y[0]; h2[1] = y[1];
            for (int k = 0; k < 5; k++) { h1[2 + k] = deck[k]; h2[2 + k] = deck[k]; }
            int s1 = HandEvaluator.Evaluate(h1), s2 = HandEvaluator.Evaluate(h2);
            score += s1 > s2 ? 1 : s1 == s2 ? 0.5 : 0;
            counted++;
        }
        return counted == 0 ? 0.5 : score / counted;
    }

    public void Save(string path)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(Version);
        w.Write(_e.Length);
        foreach (var f in _e) w.Write(f);
    }

    public static EquityMatrix? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadInt32() != Version) return null;
            int len = r.ReadInt32();
            if (len != HandClasses.Count * HandClasses.Count) return null;
            var e = new float[len];
            for (int i = 0; i < len; i++) e[i] = r.ReadSingle();
            return new EquityMatrix(e);
        }
        catch { return null; }
    }
}

/// <summary>
/// Gráfico de rangos de una solución: frecuencia de la acción (0..1) por clase de mano, y la ventaja en bb de esa acción frente al fold
/// (para estimar lo que cuesta equivocarse en esa mano).
/// </summary>
public sealed class NashChart
{
    public string Mode { get; set; } = "hu";
    public double StackBb { get; set; }
    public double Ante { get; set; }
    public Dictionary<string, double[]> Freq { get; set; } = new();
    public Dictionary<string, double[]> Advantage { get; set; } = new();

    /// <summary>% de las manos (por combinaciones) que usa la acción de ese rol.</summary>
    public double RangePct(string role)
    {
        var f = Freq[role];
        double sum = 0;
        for (int i = 0; i < HandClasses.Count; i++) sum += f[i] * HandClasses.ComboCount[i];
        return sum / 1326.0;
    }
}

/// <summary>
/// Solver Nash de push/fold por fictitious play sobre la matriz de equity 169×169, con EV de fichas
/// (en Spin &amp; Go el ganador se lo lleva todo, así que el EV de fichas es el valor, DESIGN.md §18.1).
/// </summary>
public static class NashSolver
{
    static double Ov(int a, int b) => HandClasses.Overlap[a * HandClasses.Count + b];

    /// <summary>Heads-up: el SB empuja o se retira, el BB paga o se retira. Roles: «push» (SB) y «call» (BB).</summary>
    public static NashChart SolveHeadsUp(EquityMatrix m, double stackBb, double ante = 0, int iterations = 3000)
    {
        const int N = HandClasses.Count;
        double S = stackBb, foldSb = -(0.5 + ante), foldBb = -(1 + ante), winBlinds = 1 + ante;
        var push = new double[N];
        var call = new double[N];
        Array.Fill(push, 0.5);
        Array.Fill(call, 0.5);
        var pushAdv = new double[N];
        var callAdv = new double[N];

        for (int t = 0; t < iterations; t++)
        {
            var brPush = new double[N];
            var brCall = new double[N];
            for (int h = 0; h < N; h++)
            {
                double tot = 0, ev = 0;
                for (int b = 0; b < N; b++)
                {
                    double w = Ov(h, b);
                    if (w == 0) continue;
                    tot += w;
                    ev += w * (call[b] * (2 * m[h, b] - 1) * S + (1 - call[b]) * winBlinds);
                }
                pushAdv[h] = tot > 0 ? ev / tot - foldSb : 0;
                brPush[h] = pushAdv[h] > 0 ? 1 : 0;
            }
            for (int b = 0; b < N; b++)
            {
                double tot = 0, ev = 0;
                for (int h = 0; h < N; h++)
                {
                    double w = Ov(h, b) * push[h];
                    if (w == 0) continue;
                    tot += w;
                    ev += w * (1 - 2 * m[h, b]) * S;
                }
                callAdv[b] = tot > 0 ? (ev - foldBb * tot) / tot : 0;
                brCall[b] = callAdv[b] > 0 ? 1 : 0;
            }
            double k = 1.0 / (t + 2);
            for (int i = 0; i < N; i++)
            {
                push[i] += (brPush[i] - push[i]) * k;
                call[i] += (brCall[i] - call[i]) * k;
            }
        }
        return new NashChart
        {
            Mode = "hu", StackBb = stackBb, Ante = ante,
            Freq = { ["push"] = push, ["call"] = call },
            Advantage = { ["push"] = pushAdv, ["call"] = callAdv },
        };
    }

    /// <summary>
    /// 3-handed (BTN, SB, BB) con stacks iguales. Aproximación: cuando el BTN empuja, SB y BB responden cada uno como si fuera heads-up
    /// contra el BTN (si paga el SB, el BB se retira); no se modelan los botes de tres jugadores con dos all-in a la vez.
    /// Roles: «btn_push», «sb_call» (vs BTN), «bb_call_btn» (BTN empuja, SB se retira), «sb_push» (BTN retirado) y «bb_call_sb».
    /// </summary>
    public static NashChart SolveThreeHanded(EquityMatrix m, double stackBb, int iterations = 2000)
    {
        const int N = HandClasses.Count;
        double S = stackBb;
        var btn = new double[N]; var sbCall = new double[N]; var bbCallBtn = new double[N]; var sbPush = new double[N]; var bbCallSb = new double[N];
        foreach (var a in new[] { btn, sbCall, bbCallBtn, sbPush, bbCallSb }) Array.Fill(a, 0.5);
        var advBtn = new double[N]; var advSbCall = new double[N]; var advBbBtn = new double[N]; var advSbPush = new double[N]; var advBbSb = new double[N];

        for (int t = 0; t < iterations; t++)
        {
            var nBtn = new double[N]; var nSbCall = new double[N]; var nBbBtn = new double[N]; var nSbPush = new double[N]; var nBbSb = new double[N];
            for (int h = 0; h < N; h++)
            {
                // BTN: empujar frente a retirarse (0)
                double tot = 0, evSbCall = 0, pSb = 0, evBbPart = 0;
                for (int o = 0; o < N; o++)
                {
                    double w = Ov(h, o);
                    if (w == 0) continue;
                    tot += w;
                    pSb += w * sbCall[o];
                    evSbCall += w * sbCall[o] * (2 * m[h, o] - 1) * S;
                    evBbPart += w * (bbCallBtn[o] * (2 * m[h, o] - 1) * S + (1 - bbCallBtn[o]) * 1.5);
                }
                double evBtn = tot > 0 ? evSbCall / tot + (1 - pSb / tot) * (evBbPart / tot) : 0;
                advBtn[h] = evBtn;
                nBtn[h] = evBtn > 0 ? 1 : 0;

                // SB empuja con el BTN retirado: heads-up contra el BB
                double tot2 = 0, ev2 = 0;
                for (int b = 0; b < N; b++)
                {
                    double w = Ov(h, b);
                    if (w == 0) continue;
                    tot2 += w;
                    ev2 += w * (bbCallSb[b] * (2 * m[h, b] - 1) * S + (1 - bbCallSb[b]) * 1.0);
                }
                advSbPush[h] = tot2 > 0 ? ev2 / tot2 + 0.5 : 0;
                nSbPush[h] = advSbPush[h] > 0 ? 1 : 0;
            }
            for (int s = 0; s < N; s++)
            {
                // SB paga el empuje del BTN (si no, pierde 0,5)
                double tot = 0, ev = 0;
                for (int h = 0; h < N; h++)
                {
                    double w = Ov(h, s) * btn[h];
                    if (w == 0) continue;
                    tot += w;
                    ev += w * (1 - 2 * m[h, s]) * S;
                }
                advSbCall[s] = tot > 0 ? ev / tot + 0.5 : 0;
                nSbCall[s] = advSbCall[s] > 0 ? 1 : 0;

                // BB paga el empuje del BTN (SB retirado): pierde 1 si se retira
                double tot2 = 0, ev2 = 0;
                for (int h = 0; h < N; h++)
                {
                    double w = Ov(h, s) * btn[h];
                    if (w == 0) continue;
                    tot2 += w;
                    ev2 += w * (1 - 2 * m[h, s]) * S;
                }
                advBbBtn[s] = tot2 > 0 ? ev2 / tot2 + 1.0 : 0;
                nBbBtn[s] = advBbBtn[s] > 0 ? 1 : 0;

                // BB paga el empuje del SB (BTN retirado)
                double tot3 = 0, ev3 = 0;
                for (int h = 0; h < N; h++)
                {
                    double w = Ov(h, s) * sbPush[h];
                    if (w == 0) continue;
                    tot3 += w;
                    ev3 += w * (1 - 2 * m[h, s]) * S;
                }
                advBbSb[s] = tot3 > 0 ? ev3 / tot3 + 1.0 : 0;
                nBbSb[s] = advBbSb[s] > 0 ? 1 : 0;
            }
            double k = 1.0 / (t + 2);
            for (int i = 0; i < N; i++)
            {
                btn[i] += (nBtn[i] - btn[i]) * k;
                sbCall[i] += (nSbCall[i] - sbCall[i]) * k;
                bbCallBtn[i] += (nBbBtn[i] - bbCallBtn[i]) * k;
                sbPush[i] += (nSbPush[i] - sbPush[i]) * k;
                bbCallSb[i] += (nBbSb[i] - bbCallSb[i]) * k;
            }
        }
        return new NashChart
        {
            Mode = "3h", StackBb = stackBb,
            Freq = { ["btn_push"] = btn, ["sb_call"] = sbCall, ["bb_call_btn"] = bbCallBtn, ["sb_push"] = sbPush, ["bb_call_sb"] = bbCallSb },
            Advantage = { ["btn_push"] = advBtn, ["sb_call"] = advSbCall, ["bb_call_btn"] = advBbBtn, ["sb_push"] = advSbPush, ["bb_call_sb"] = advBbSb },
        };
    }
}

/// <summary>Matriz y gráficos Nash en disco: se calculan la primera vez y después se leen (DESIGN.md §19.5).</summary>
public sealed class NashLibrary
{
    readonly string _dir;
    readonly object _lock = new();
    EquityMatrix? _matrix;
    Dictionary<string, NashChart>? _charts;

    public NashLibrary(string dir) => _dir = dir;

    string MatrixPath => Path.Combine(_dir, $"nash-equity-v{EquityMatrix.Version}.bin");
    string ChartsPath => Path.Combine(_dir, "nash-charts-v1.json");

    public bool MatrixReady => _matrix != null || File.Exists(MatrixPath);

    public EquityMatrix GetMatrix(IProgress<double>? progress = null)
    {
        lock (_lock)
        {
            if (_matrix != null) return _matrix;
            _matrix = EquityMatrix.TryLoad(MatrixPath);
            if (_matrix == null)
            {
                _matrix = EquityMatrix.Compute(progress: progress);
                try { Directory.CreateDirectory(_dir); _matrix.Save(MatrixPath); } catch { /* sin disco: se recalcula la próxima vez */ }
            }
            return _matrix;
        }
    }

    static string Key(string mode, double stack, double ante) => $"{mode}|{stack:0.0}|{ante:0.##}";

    /// <summary>Stack redondeado a medio ciego grande y limitado a 1–25 bb.</summary>
    public static double Quantize(double stackBb) => Math.Clamp(Math.Round(stackBb * 2) / 2, 1, 25);

    void LoadCharts()
    {
        if (_charts != null) return;
        _charts = new();
        try
        {
            if (File.Exists(ChartsPath))
                foreach (var c in JsonSerializer.Deserialize<List<NashChart>>(File.ReadAllText(ChartsPath)) ?? new())
                    _charts[Key(c.Mode, c.StackBb, c.Ante)] = c;
        }
        catch { _charts = new(); }
    }

    void SaveCharts()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(ChartsPath, JsonSerializer.Serialize(_charts!.Values.ToList()));
        }
        catch { /* solo lectura */ }
    }

    public NashChart Get(string mode, double stackBb, double ante = 0)
    {
        stackBb = Quantize(stackBb);
        lock (_lock)
        {
            LoadCharts();
            var key = Key(mode, stackBb, ante);
            if (_charts!.TryGetValue(key, out var c)) return c;
            var m = GetMatrix();
            c = mode == "3h" ? NashSolver.SolveThreeHanded(m, stackBb) : NashSolver.SolveHeadsUp(m, stackBb, ante);
            _charts[key] = c;
            SaveCharts();
            return c;
        }
    }

    /// <summary>Calcula de una vez todos los stacks de 1 a 25 bb (cada 0,5) para HU y 3-handed, en paralelo.</summary>
    public void BuildAll(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var m = GetMatrix();
        var jobs = new List<(string Mode, double Stack)>();
        for (double s = 1; s <= 25; s += 0.5) { jobs.Add(("hu", s)); jobs.Add(("3h", s)); }
        int done = 0;
        var results = new System.Collections.Concurrent.ConcurrentBag<NashChart>();
        Parallel.ForEach(jobs, new ParallelOptions { CancellationToken = ct }, j =>
        {
            results.Add(j.Mode == "3h" ? NashSolver.SolveThreeHanded(m, j.Stack) : NashSolver.SolveHeadsUp(m, j.Stack));
            progress?.Report((double)Interlocked.Increment(ref done) / jobs.Count);
        });
        lock (_lock)
        {
            LoadCharts();
            foreach (var c in results) _charts![Key(c.Mode, c.StackBb, c.Ante)] = c;
            SaveCharts();
        }
    }
}
