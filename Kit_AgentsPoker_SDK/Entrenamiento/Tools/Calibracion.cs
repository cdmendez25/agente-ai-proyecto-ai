using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AgentsPoker.Core;

/// <summary>
/// Calibraciones del laboratorio (diseño 5.6 "Calibrators"). No se entrega.
/// </summary>
static class Calibracion
{
    /// <summary>
    /// Tabla PreflopPct (diseño 3.3): equity de cada una de las 169 clases contra UNA mano aleatoria (Monte Carlo),
    /// orden descendente y percentil ACUMULADO de combinaciones (AA = 6/1326 ... peor clase = 1.0).
    /// </summary>
    public static void PreflopPct(int simsPorClase, bool escribirEnAgente)
    {
        var rng = new Random(2026);
        var equity = new double[169];
        var combos = new int[169];
        var board = new Card[5];
        var mazo = new int[52];
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int cls = 0; cls < 169; cls++)
        {
            (Card a, Card b) = Representante(cls);
            if (AgenteHibrido.CardUtil.Canonical169(a, b) != cls)
                throw new InvalidOperationException($"Representante incoherente para la clase {cls}");
            combos[cls] = cls < 13 ? 6 : (cls < 91 ? 4 : 12);

            int n = 0;
            for (int i = 0; i < 52; i++) if (i != a.Index && i != b.Index) mazo[n++] = i;

            double puntos = 0;
            for (int s = 0; s < simsPorClase; s++)
            {
                for (int k = 0; k < 7; k++)      // Fisher-Yates parcial: 2 cartas del rival + 5 del board
                {
                    int j = k + rng.Next(n - k);
                    int t = mazo[k]; mazo[k] = mazo[j]; mazo[j] = t;
                }
                for (int k = 0; k < 5; k++) board[k] = new Card(mazo[2 + k]);
                int yo = HandEvaluator.Evaluate(a, b, board);
                int el = HandEvaluator.Evaluate(new Card(mazo[0]), new Card(mazo[1]), board);
                puntos += yo > el ? 1.0 : (yo == el ? 0.5 : 0.0);
            }
            equity[cls] = puntos / simsPorClase;
        }

        var orden = Enumerable.Range(0, 169).OrderByDescending(c => equity[c]).ToArray();
        var pct = new double[169];
        int acumulado = 0;
        foreach (int c in orden)
        {
            acumulado += combos[c];
            pct[c] = acumulado / 1326.0;
        }

        Console.WriteLine($"PreflopPct: {simsPorClase:N0} simulaciones por clase en {sw.Elapsed.TotalSeconds:F1} s");
        Console.WriteLine("Top 12: " + string.Join(", ", orden.Take(12).Select(c => $"{AgenteHibrido.CardUtil.ClassName(c)} {equity[c]:F3}")));
        Console.WriteLine("Últimas 5: " + string.Join(", ", orden.Skip(164).Select(c => $"{AgenteHibrido.CardUtil.ClassName(c)} {equity[c]:F3}")));

        var sb = new StringBuilder();
        for (int i = 0; i < 169; i++)
        {
            if (i % 10 == 0) sb.Append("            ");
            sb.Append(pct[i].ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(i % 10 == 9 || i == 168 ? "\n" : " ");
        }
        string bloque = sb.ToString();

        if (!escribirEnAgente) { Console.WriteLine(bloque); return; }
        string ruta = BuscarAgente();
        string texto = File.ReadAllText(ruta);
        const string ini = "// <PREFLOP-PCT-BEGIN>", fin = "            // <PREFLOP-PCT-END>";
        int i0 = texto.IndexOf(ini, StringComparison.Ordinal), i1 = texto.IndexOf(fin, StringComparison.Ordinal);
        if (i0 < 0 || i1 < 0) throw new InvalidOperationException("No se encontraron los marcadores PREFLOP-PCT en " + ruta);
        int desde = texto.IndexOf('\n', i0) + 1;
        texto = texto.Substring(0, desde) + bloque + texto.Substring(i1);
        File.WriteAllText(ruta, texto);
        Console.WriteLine("Tabla escrita en " + ruta);
    }

    /// <summary>Dos cartas concretas de la clase (palos fijos).</summary>
    private static (Card, Card) Representante(int cls)
    {
        if (cls < 13) return (new Card(cls + 2, Suit.Clubs), new Card(cls + 2, Suit.Diamonds));
        bool suited = cls < 91;
        int idx78 = suited ? cls - 13 : cls - 91;
        int r1 = 3;
        while ((r1 - 1) * (r1 - 2) / 2 <= idx78) r1++;
        int r2 = idx78 - (r1 - 2) * (r1 - 3) / 2 + 2;
        return (new Card(r1, Suit.Spades), new Card(r2, suited ? Suit.Spades : Suit.Hearts));
    }

    public static string BuscarAgente()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string p = Path.Combine(dir.FullName, "Agents", "AgenteHibrido.cs");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        string local = Path.Combine(Directory.GetCurrentDirectory(), "Agents", "AgenteHibrido.cs");
        if (File.Exists(local)) return local;
        throw new FileNotFoundException("No se encontró Agents/AgenteHibrido.cs");
    }
}
