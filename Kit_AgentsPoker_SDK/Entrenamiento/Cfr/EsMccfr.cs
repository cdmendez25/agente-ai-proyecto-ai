using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// =====================================================================================================
// Entrenador External Sampling MCCFR (arquitectura §5) + exportación de la política (§6). No se entrega.
//
//   bash entrenar.sh entrenar-cfr <segundos> [hilos] [--nuevo]
//
// Por cada reparto aleatorio (azar muestreado UNA vez) se recorre el árbol una vez por cada jugador:
//   - nodo del jugador actualizado (traverser): se prueban TODAS sus acciones legales y se actualizan regrets;
//   - nodo de otro jugador: se acumula su estrategia promedio y se muestrea UNA acción según su estrategia actual.
// No hay factores 1/q: en External Sampling se cancelan.
// =====================================================================================================
sealed class EsMccfr
{
    public readonly int K = AgenteHibridoV2.StateEncoder.NumKeys;
    public readonly double[] R, S;
    public readonly long[] Visits;
    public readonly int KC = AgenteHibridoV2.StateEncoder.NumCoarseKeys;
    /// <summary>Clave gruesa de cada clave fina (se rellena al visitarla): para construir la tabla de respaldo.</summary>
    public readonly int[] FineToCoarse;
    public long Iterations;

    // Probabilidad de cada tamaño de mesa (índice = número de jugadores).
    private static readonly double[] SizeWeight = { 0, 0, 0.10, 0.10, 0.15, 0.20, 0.45 };

    public EsMccfr()
    {
        R = new double[K * 3];
        S = new double[K * 3];
        Visits = new long[K];
        FineToCoarse = new int[K];
        for (int i = 0; i < K; i++) FineToCoarse[i] = -1;
    }

    /// <summary>
    /// Linear CFR por descuentos (Brown y Sandholm 2019; usado en Pluribus): cada DiscountEvery repartos, mientras
    /// Iterations &lt;= DiscountUntil, se multiplican regrets y estrategia acumulada por k/(k+1) (k = número de periodos).
    /// Efecto: lo aprendido al principio (cuando la estrategia era mala) pesa menos y se converge antes.
    /// DiscountEvery = 0 desactiva el descuento (ES-MCCFR estándar).
    /// </summary>
    public long DiscountEvery = 2_000_000, DiscountUntil = 200_000_000;
    public int Discounts;

    public void Train(double seconds, int threads, int seed)
    {
        var sw = Stopwatch.StartNew();
        long start = Iterations;
        long next = DiscountEvery > 0 ? (Iterations / DiscountEvery + 1) * DiscountEvery : long.MaxValue;
        var tareas = Enumerable.Range(0, threads).Select(w => Task.Run(() => Worker(w, seed, sw, seconds))).ToArray();
        int tick = 0;
        while (!Task.WaitAll(tareas, 1_000))
        {
            long now = Interlocked.Read(ref Iterations);
            if (now >= next && now <= DiscountUntil)
            {
                double k = now / (double)DiscountEvery;
                double f = k / (k + 1);
                for (int i = 0; i < R.Length; i++) { R[i] *= f; S[i] *= f; }
                Discounts++;
                while (next <= now) next += DiscountEvery;
            }
            if (++tick % 10 == 0)
            {
                long it = now - start;
                Console.Write($"\r  {sw.Elapsed.TotalSeconds:F0}/{seconds:F0} s   {it:N0} repartos   {it / sw.Elapsed.TotalSeconds:N0}/s   descuentos {Discounts}   ");
            }
        }
        Console.WriteLine($"\r  Entrenamiento: {Iterations - start:N0} repartos en {sw.Elapsed.TotalSeconds:F0} s ({(Iterations - start) / sw.Elapsed.TotalSeconds:N0}/s, {threads} hilos, {Discounts} descuentos lineales)      ");
    }

    private void Worker(int w, int seed, Stopwatch sw, double seconds)
    {
        var rng = new Random(seed * 7919 + w * 104729 + (int)(Iterations % 100000));
        var deal = new LhDeal();
        var pool = new LhState[96];
        for (int i = 0; i < pool.Length; i++) pool[i] = new LhState();
        long local = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            int n = SampleSize(rng);
            deal.NewRandom(n, rng.Next(n), rng);          // azar: se muestrea una vez
            for (int i = 0; i < n; i++)                   // un recorrido por jugador actualizado
            {
                pool[0].Init(deal);
                Traverse(pool, 0, i, rng);
            }
            if (++local % 256 == 0) Interlocked.Add(ref Iterations, 256);
        }
        Interlocked.Add(ref Iterations, local % 256);
    }

    private static int SampleSize(Random rng)
    {
        double u = rng.NextDouble();
        for (int n = 2; n <= 6; n++) { u -= SizeWeight[n]; if (u <= 0) return n; }
        return 6;
    }

    /// <summary>Recorrido External Sampling. Devuelve la utilidad (en ciegas grandes) para el traverser i.</summary>
    private double Traverse(LhState[] pool, int d, int i, Random rng)
    {
        var g = pool[d];
        if (g.Terminal) return g.Utility(i);

        int p = g.ToAct;
        var f = g.Feat(p);
        int k = AgenteHibridoV2.StateEncoder.EncodeFine(f);
        if (FineToCoarse[k] < 0) FineToCoarse[k] = AgenteHibridoV2.StateEncoder.EncodeCoarse(f);
        g.LegalMask(out bool lf, out bool lr);

        // Regret matching: estrategia actual proporcional a los regrets positivos.
        double r0 = lf ? Math.Max(R[3 * k], 0) : 0;
        double r1 = Math.Max(R[3 * k + 1], 0);
        double r2 = lr ? Math.Max(R[3 * k + 2], 0) : 0;
        double sum = r0 + r1 + r2, s0, s1, s2;
        if (sum > 0) { s0 = r0 / sum; s1 = r1 / sum; s2 = r2 / sum; }
        else
        {
            int nl = 1 + (lf ? 1 : 0) + (lr ? 1 : 0);
            s0 = lf ? 1.0 / nl : 0; s1 = 1.0 / nl; s2 = lr ? 1.0 / nl : 0;
        }

        if (p == i)
        {
            // Jugador actualizado: probar todas las acciones legales desde una copia del estado.
            double v0 = 0, v1, v2 = 0;
            var c = pool[d + 1];
            if (lf) { c.CopyFrom(g); c.Apply(0); v0 = Traverse(pool, d + 1, i, rng); }
            c.CopyFrom(g); c.Apply(1); v1 = Traverse(pool, d + 1, i, rng);
            if (lr) { c.CopyFrom(g); c.Apply(2); v2 = Traverse(pool, d + 1, i, rng); }
            double node = s0 * v0 + s1 * v1 + s2 * v2;
            if (lf) R[3 * k] += v0 - node;
            R[3 * k + 1] += v1 - node;
            if (lr) R[3 * k + 2] += v2 - node;
            return node;
        }
        else
        {
            // Otro jugador: acumular su estrategia promedio y muestrear UNA acción.
            S[3 * k] += s0; S[3 * k + 1] += s1; S[3 * k + 2] += s2;
            Visits[k]++;
            double u = rng.NextDouble();
            int a = u < s0 ? 0 : (u < s0 + s1 ? 1 : 2);
            if (a == 2 && !lr) a = 1;
            g.Apply(a);                                   // en el sitio: este estado ya es una copia propia
            return Traverse(pool, d, i, rng);
        }
    }

    // ------------------------------------------------------------------ checkpoints
    public void Save(string path)
    {
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write(AgenteHibridoV2.StateEncoder.ConstantsHash()); bw.Write(K); bw.Write(Iterations);
        foreach (var x in R) bw.Write(x);
        foreach (var x in S) bw.Write(x);
        foreach (var x in Visits) bw.Write(x);
        foreach (var x in FineToCoarse) bw.Write(x);
    }

    public bool Load(string path)
    {
        if (!File.Exists(path)) return false;
        using var br = new BinaryReader(File.OpenRead(path));
        if (br.ReadUInt32() != AgenteHibridoV2.StateEncoder.ConstantsHash() || br.ReadInt32() != K) return false;
        Iterations = br.ReadInt64();
        for (int i = 0; i < R.Length; i++) R[i] = br.ReadDouble();
        for (int i = 0; i < S.Length; i++) S[i] = br.ReadDouble();
        for (int i = 0; i < Visits.Length; i++) Visits[i] = br.ReadInt64();
        for (int i = 0; i < FineToCoarse.Length; i++) FineToCoarse[i] = br.ReadInt32();
        return true;
    }

    // ------------------------------------------------------------------ exportación (arquitectura §6)
    /// <summary>Estrategia PROMEDIO cuantizada: 2 bytes por clave fina y luego 2 por clave gruesa (respaldo).
    /// La gruesa suma las estrategias acumuladas de todas las claves finas que caen en ella.</summary>
    public byte[] Quantize(long minVisits, out int conFina, out int conGruesa)
    {
        var b = new byte[2 * (K + KC)];
        var sc = new double[KC * 3];
        var vc = new long[KC];
        conFina = conGruesa = 0;
        for (int k = 0; k < K; k++)
        {
            if (Put(b, k, S[3 * k], S[3 * k + 1], S[3 * k + 2], Visits[k], minVisits)) conFina++;
            int c = FineToCoarse[k];
            if (c >= 0) { sc[3 * c] += S[3 * k]; sc[3 * c + 1] += S[3 * k + 1]; sc[3 * c + 2] += S[3 * k + 2]; vc[c] += Visits[k]; }
        }
        for (int c = 0; c < KC; c++)
            if (Put(b, K + c, sc[3 * c], sc[3 * c + 1], sc[3 * c + 2], vc[c], minVisits)) conGruesa++;
        return b;
    }

    private static bool Put(byte[] b, int idx, double s0, double s1, double s2, long visits, long minVisits)
    {
        double t = s0 + s1 + s2;
        if (visits < minVisits || t <= 0) { b[2 * idx] = 255; b[2 * idx + 1] = 255; return false; }
        int q0 = (int)Math.Round(255 * s0 / t), q1 = (int)Math.Round(255 * s1 / t);
        while (q0 + q1 > 255) { if (q0 >= q1) q0--; else q1--; }
        b[2 * idx] = (byte)q0; b[2 * idx + 1] = (byte)q1;
        return true;
    }

    public void ExportToAgent(string agentPath, long minVisits)
    {
        var bytes = Quantize(minVisits, out int con, out int conG);
        uint checksum = 2166136261;
        foreach (var x in bytes) { checksum ^= x; checksum *= 16777619; }
        string header = $"v={AgenteHibridoV2.StateEncoder.Version};k={K};kc={KC};h={AgenteHibridoV2.StateEncoder.ConstantsHash()};c={checksum};it={Iterations}";
        string b64 = Convert.ToBase64String(bytes);
        var sb = new StringBuilder();
        sb.AppendLine($"        internal const string Header = \"{header}\";");
        sb.AppendLine("        internal static readonly string[] Blob =");
        sb.AppendLine("        {");
        for (int i = 0; i < b64.Length; i += 100)
            sb.AppendLine($"            \"{b64.Substring(i, Math.Min(100, b64.Length - i))}\",");
        sb.AppendLine("        };");

        string texto = File.ReadAllText(agentPath);
        const string ini = "// <POLICY-BEGIN>", fin = "        // <POLICY-END>";
        int i0 = texto.IndexOf(ini, StringComparison.Ordinal), i1 = texto.IndexOf(fin, StringComparison.Ordinal);
        if (i0 < 0 || i1 < 0) throw new InvalidOperationException("No se encontraron los marcadores POLICY en " + agentPath);
        int desde = texto.IndexOf('\n', i0) + 1;
        File.WriteAllText(agentPath, texto.Substring(0, desde) + sb + texto.Substring(i1));
        Console.WriteLine($"  Política exportada: fina {con:N0}/{K:N0} claves ({100.0 * con / K:F1} %), gruesa {conG:N0}/{KC:N0}; {bytes.Length:N0} bytes -> {b64.Length:N0} caracteres base64");
        Console.WriteLine($"  Cabecera: {header}");
    }

    /// <summary>Cuánto cambió la estrategia promedio respecto de una copia anterior (L1 medio ponderado por visitas).</summary>
    public static double Delta(double[] sOld, EsMccfr t)
    {
        double num = 0, den = 0;
        for (int k = 0; k < t.K; k++)
        {
            double a = sOld[3 * k] + sOld[3 * k + 1] + sOld[3 * k + 2];
            double b = t.S[3 * k] + t.S[3 * k + 1] + t.S[3 * k + 2];
            if (a <= 0 || b <= 0) continue;
            double l1 = 0;
            for (int j = 0; j < 3; j++) l1 += Math.Abs(sOld[3 * k + j] / a - t.S[3 * k + j] / b);
            num += t.Visits[k] * l1; den += t.Visits[k];
        }
        return den > 0 ? num / den : double.NaN;
    }
}
