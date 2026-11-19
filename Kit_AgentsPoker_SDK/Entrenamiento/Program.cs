using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AgentsPoker.Core;
using AgentsPoker.Core.Agents;

// =====================================================================================================
// LABORATORIO DE ENTRENAMIENTO (no se entrega: edítalo a tu gusto)
//
//   .\entrenar.cmd          cada prueba corre 30 s          (macOS/Linux: bash entrenar.sh)
//   .\entrenar.cmd 300      cada prueba corre 300 s: más manos = resultados más precisos
//   .\entrenar.cmd 30 --final   la prueba B simula la MESA FINAL (o.Match.IsFinalTable = true, solo gana el 1.o)
//
// Compila todos los .cs de Agents/ con las reglas de la Arena (C# 9, .NET Standard 2.1).
// Para DEPURAR: abre la carpeta del SDK en VS Code / Visual Studio, pon un breakpoint en tu agente
// (Agents/MiAgente.cs) y ejecuta este proyecto con F5.
// =====================================================================================================

// 0) SUBCOMANDOS del proyecto (ver DISENO_AGENTE.md, secciones 5.6 y 6):
//      bash entrenar.sh calibrar-preflop [simsPorClase]   genera CardUtil.PreflopPct y lo escribe en Agents/AgenteHibrido.cs
//      bash entrenar.sh pruebas                           pruebas unitarias del agente (CardUtil, HS, draws, legalidad)
if (args.Length > 0 && args[0] == "calibrar-preflop")
{
    int sims = args.Length > 1 ? int.Parse(args[1]) : 100_000;
    Calibracion.PreflopPct(sims, escribirEnAgente: true);
    return;
}
if (args.Length > 0 && args[0] == "entrenar-cfr")
{
    // bash entrenar.sh entrenar-cfr <segundos> [hilos] [--nuevo]   (continúa desde el checkpoint si existe)
    var nums = args.Skip(1).Where(a => !a.StartsWith("--")).ToArray();
    double seg = nums.Length > 0 ? double.Parse(nums[0], System.Globalization.CultureInfo.InvariantCulture) : 600;
    int hilos = nums.Length > 1 ? int.Parse(nums[1]) : Math.Max(1, Environment.ProcessorCount - 1);
    string ckpt = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Calibracion.BuscarAgente())!, "..", "Entrenamiento", "cfr_checkpoint.bin");
    var t = new EsMccfr();
    if (!args.Contains("--nuevo") && t.Load(ckpt)) Console.WriteLine($"  Checkpoint cargado: {t.Iterations:N0} repartos previos");
    var antes = (double[])t.S.Clone();
    t.Train(seg, hilos, seed: (int)(t.Iterations % 1_000_000) + 1);
    Console.WriteLine($"  Estabilidad δ (cambio medio de la estrategia promedio en esta sesión): {EsMccfr.Delta(antes, t):F4}");
    t.Save(ckpt);
    // El entrenador usa la abstracción v2: exporta a Agents/AgenteHibridoV2.cs (la v1 entregable no se toca).
    t.ExportToAgent(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Calibracion.BuscarAgente())!, "AgenteHibridoV2.cs"), minVisits: 50);
    return;
}
if (args.Length > 0 && args[0] == "comparar")
{
    // bash entrenar.sh comparar [mesas] [BASE,V3-ER,...] [pools: 1,2,3,4,5,6,7]
    int mesasC = args.Length > 1 ? int.Parse(args[1]) : 500;
    var vers = (args.Length > 2 ? args[2] : "BASE,V4b").Split(',');
    int[] poolsC = args.Length > 3 ? args[3].Split(',').Select(int.Parse).ToArray() : null;
    Comparar.Ejecutar(mesasC, vers, poolsC);
    return;
}
if (args.Length > 0 && args[0] == "validar-sim")
{
    Environment.ExitCode = ValidarSimulador.Ejecutar(args.Length > 1 ? int.Parse(args[1]) : 4000);
    return;
}
if (args.Length > 0 && args[0] == "medir-allin")
{
    MedirAllIn.Ejecutar(args.Length > 1 ? int.Parse(args[1]) : 2000);
    return;
}
if (args.Length > 0 && args[0] == "pruebas")
{
    Environment.ExitCode = PruebasAgente.Ejecutar();
    return;
}

// 1) TU AGENTE: AgenteHibrido. Con --plantilla se mide la plantilla MiAgente (línea base V0).
Func<IPokerAgent> crearMiAgente = args.Contains("--plantilla")
    ? () => new MiAgente()
    : () => new AgenteHibrido();

// 2) RIVALES de entrenamiento (de 1 a 5): agentes base del SDK y/o clases de Agents/ (p. ej. () => new Rival()).
//    El Jefe Final del torneo ("Archimago") es SimpleHeuristicAgent: conviene ganarle.
var rivales = new List<(string Nombre, Func<IPokerAgent> Crear)>
{
    ("Heuristic", () => new SimpleHeuristicAgent()),
    ("Heuristic2", () => new SimpleHeuristicAgent()),
    ("Rival", () => new Rival()),
    ("Station", () => new CallingStationAgent()),
    ("Raiser", () => new AlwaysRaiseAgent()),
};

Console.OutputEncoding = System.Text.Encoding.UTF8;
// --final: la prueba B simula la MESA FINAL (PlayerObservation.Match.IsFinalTable = true, solo gana el 1.o).
bool mesaFinal = args.Any(a => a.Equals("--final", StringComparison.OrdinalIgnoreCase));
var numeros = args.Where(a => !a.StartsWith("--")).ToArray();
double segundos = numeros.Length > 0 ? double.Parse(numeros[0], System.Globalization.CultureInfo.InvariantCulture) : 30;
Console.WriteLine($"AgentsPoker.Core v{typeof(IPokerAgent).Assembly.GetName().Version.ToString(3)} | rivales: {string.Join(", ", rivales.Select(r => r.Nombre))} | {segundos} s por prueba");

// -----------------------------------------------------------------------------------------------------
// A) LARGO PLAZO: manos seguidas en modo Training (sin timeout = máxima velocidad).
//    bb/100 = ciegas grandes ganadas cada 100 manos. > 0 significa que ganas fichas a la larga.
//    El "±" es el margen de error (95 %): si es mayor que el resultado, aún no sabes si ganas. Corre más tiempo.
// -----------------------------------------------------------------------------------------------------
{
    var cfg = GameConfig.Training(seed: 1);
    var mesa = new PokerTable(cfg);
    var yo = new Cronometro(crearMiAgente());
    mesa.Seat(yo, "Yo");
    foreach (var r in rivales) mesa.Seat(r.Crear(), r.Nombre);

    long manos = 0;
    double sumaBb = 0, sumaBb2 = 0;
    int errores = 0;
    var reloj = Stopwatch.StartNew();
    while (reloj.Elapsed.TotalSeconds < segundos)
    {
        for (int s = 0; s < mesa.SeatCount; s++)
            if (mesa.GetStack(s) == 0) mesa.SetStack(s, cfg.StartingStack); // recompra: nadie queda fuera
        var res = mesa.PlayHand();
        double bb = res.Net[0] / (double)cfg.BigBlind;
        sumaBb += bb;
        sumaBb2 += bb * bb;
        manos++;
        foreach (var e in res.Errors)
            if (e.Seat == 0 && errores++ < 10) Console.WriteLine("  ERROR " + e);
    }
    double media = sumaBb / manos;
    double margen = 1.96 * Math.Sqrt(Math.Max(0, sumaBb2 / manos - media * media) / manos);
    Console.WriteLine($"\nA) Largo plazo: {manos:N0} manos ({manos / reloj.Elapsed.TotalSeconds:N0} manos/s)");
    Console.WriteLine($"   Resultado: {media * 100:+0.0;-0.0} ± {margen * 100:0.0} bb/100");
    Console.WriteLine($"   Decisión: {yo.TotalMs / Math.Max(1, yo.Decisiones):F2} ms prom, {yo.MaxMs:F1} ms máx   Errores: {errores}");
    Console.WriteLine($"   Latencia GetAction: p50 {yo.Percentil(50):F3} ms   p95 {yo.Percentil(95):F3} ms   p99 {yo.Percentil(99):F3} ms   máx {yo.MaxMs:F2} ms   |  OnHandEnd máx {yo.MaxMsHandEnd:F2} ms");
    Console.WriteLine($"   Folds gratis (FOLD con ToCall == 0): {yo.FoldsGratis}{(yo.FoldsGratis > 0 ? "   <-- ERROR: debe ser 0" : "")}");
    if (yo.MaxMs > 300) Console.WriteLine("   ¡Cuidado! La Arena (Unity/Mono) es más lenta que .NET en tu PC: apunta a < 300 ms máx aquí.");
}

// -----------------------------------------------------------------------------------------------------
// B) FORMATO TORNEO: mesas de 30 manos, stack 10 000, top 3 clasifica, asientos al azar.
//    Esto es lo que cuenta en el torneo: Clasif% = % de mesas en las que quedas en el top 3.
//    Como en la Arena, cada mesa crea una instancia NUEVA de cada agente (lo aprendido con OnHandEnd se reinicia).
//    Tu agente recibe el contexto en o.Match (HandsRemaining, QualifyingSpots, IsFinalTable); con --final es la final.
// -----------------------------------------------------------------------------------------------------
{
    var rng = new Random(7);
    int mesas = 0, clasifica = 0, gana = 0;
    long rankSuma = 0, stackSuma = 0;
    int clasificanPorMesa = mesaFinal ? MatchInfo.FinalTableQualifyingSpots : Math.Min(3, rivales.Count);
    var reloj = Stopwatch.StartNew();
    while (reloj.Elapsed.TotalSeconds < segundos)
    {
        var asientos = new List<(string Nombre, Func<IPokerAgent> Crear)> { ("Yo", crearMiAgente) };
        asientos.AddRange(rivales);
        asientos = asientos.OrderBy(_ => rng.Next()).ToList();

        var mesa = new PokerTable(GameConfig.Training(seed: rng.Next()));
        foreach (var a in asientos) mesa.Seat(a.Crear(), a.Nombre);
        // Contexto que ve tu agente en o.Match (mesa de práctica, o la final con --final).
        var contexto = MatchInfo.Standalone(handsToPlay: 30, qualifyingSpots: clasificanPorMesa, isFinalTable: mesaFinal, playersAtStart: asientos.Count);
        var partida = new TableMatch(mesa, contexto);
        partida.PlayAll();

        var yo = partida.GetStandings().First(s => s.Name == "Yo");
        mesas++;
        if (yo.Qualifies) clasifica++;
        if (yo.Rank == 1) gana++;
        rankSuma += yo.Rank;
        stackSuma += yo.Stack;
    }
    double p = clasifica / (double)mesas;
    Console.WriteLine($"\nB) Formato torneo{(mesaFinal ? " (MESA FINAL: Match.IsFinalTable = true)" : "")}: {mesas:N0} mesas de 30 manos, {1 + rivales.Count} jugadores, top {clasificanPorMesa} clasifica");
    Console.WriteLine($"   Clasif% {100 * p:F1} ± {100 * 1.96 * Math.Sqrt(p * (1 - p) / mesas):F1}   Gana% {100.0 * gana / mesas:F1}" +
                      $"   Puesto prom {rankSuma / (double)mesas:F2}   Stack prom {stackSuma / mesas:N0}");
}

Console.WriteLine("\nSiguiente paso, con el timeout real del torneo (1000 ms):  .\\run.cmd bench --agent MiAgente --builtin Heuristic*5");

/// <summary>Mide el tiempo de cada decisión y reenvía OnHandEnd si tu agente lo implementa.</summary>
sealed class Cronometro : IPokerAgent, IHandObserver
{
    private readonly IPokerAgent _agente;
    public double MaxMs, TotalMs;
    public long Decisiones, FoldsGratis;

    public Cronometro(IPokerAgent agente) { _agente = agente; }

    public void Initialize(string agentName) => _agente.Initialize(agentName);

    public ActionType GetAction(PlayerObservation o)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var a = _agente.GetAction(o);
            if (a == ActionType.FOLD && o.ToCall == 0) FoldsGratis++;
            return a;
        }
        finally
        {
            double ms = sw.Elapsed.TotalMilliseconds;
            TotalMs += ms;
            Decisiones++;
            if (ms > MaxMs) MaxMs = ms;
            if (Muestras.Count < 2_000_000) Muestras.Add(ms);
        }
    }

    public void OnHandEnd(HandSummary s)
    {
        var sw = Stopwatch.StartNew();
        if (_agente is IHandObserver h) h.OnHandEnd(s);
        double ms = sw.Elapsed.TotalMilliseconds;
        if (ms > MaxMsHandEnd) MaxMsHandEnd = ms;
    }

    /// <summary>Tiempos de cada decisión (arquitectura §11: controlar percentiles, no solo promedios).</summary>
    public readonly List<double> Muestras = new List<double>();
    public double MaxMsHandEnd;

    public double Percentil(double p)
    {
        if (Muestras.Count == 0) return 0;
        var orden = Muestras.OrderBy(x => x).ToList();
        int i = (int)Math.Min(orden.Count - 1, Math.Ceiling(p / 100.0 * orden.Count) - 1);
        return orden[Math.Max(0, i)];
    }
}
