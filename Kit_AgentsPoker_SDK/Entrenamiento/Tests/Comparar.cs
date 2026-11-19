using System;
using System.Collections.Generic;
using System.Linq;
using AgentsPoker.Core;
using AgentsPoker.Core.Agents;

/// <summary>
/// Comparación emparejada de versiones (arquitectura §14): mismas semillas, mismas cartas, mismos asientos y
/// MISMOS rivales (también el aleatorio, que ahora lleva semilla) para todas las versiones.
///
///   bash entrenar.sh comparar [mesas] [versiones separadas por coma] [mesas a jugar: números de pool separados por coma]
///   ej.: bash entrenar.sh comparar 1000 BASE,V3-ER 1,2,3
///
/// La columna "Δ vs 1ª" es la diferencia de Clasif% frente a la PRIMERA versión de la lista (± IC95 % pareado).
/// </summary>
static class Comparar
{
    /// <summary>Variantes construidas sobre AgenteHibridoV2 (laboratorio de abstracción CFR).</summary>
    static readonly Dictionary<string, Func<AgenteHibridoV2.Params>> Versiones = new Dictionary<string, Func<AgenteHibridoV2.Params>>
    {
        ["V0"]  = () => new AgenteHibridoV2.Params { UseCfr = false, UseExploiter = false, UseTournament = false },
        ["V1"]  = () => new AgenteHibridoV2.Params { UseCfr = true,  UseExploiter = false, UseTournament = false },
        ["V2"]  = () => new AgenteHibridoV2.Params { UseCfr = false, UseExploiter = true,  UseTournament = false },
        ["V3b"] = () => new AgenteHibridoV2.Params { UseCfr = true,  UseExploiter = true,  UseTournament = false, MixMode = 1 },
        ["V4"]  = () => new AgenteHibridoV2.Params { UseCfr = true,  UseExploiter = true,  UseTournament = true,  MixMode = 0 },
        ["V4b"] = () => new AgenteHibridoV2.Params { UseCfr = true,  UseExploiter = true,  UseTournament = true,  MixMode = 1 },
        ["V4b-P05"] = () => new AgenteHibridoV2.Params { UseCfr = true, UseExploiter = true, UseTournament = true, MixMode = 1, PurifyTau = 0.05 },
        ["V4b-P10"] = () => new AgenteHibridoV2.Params { UseCfr = true, UseExploiter = true, UseTournament = true, MixMode = 1, PurifyTau = 0.10 },
        ["V3b-L6"] = () => new AgenteHibridoV2.Params { UseCfr = true, UseExploiter = true, UseTournament = false, MixMode = 1, LambdaMax = 0.6 },
    };

    /// <summary>Agentes que no son variantes de V2: el entregable (BASE) y el AgenteHibridoV3 del compañero.</summary>
    static Func<IPokerAgent> Especial(string v, int t)
    {
        switch (v)
        {
            case "BASE":    return () => new AgenteHibrido { Config = new AgenteHibrido.Params { Seed = 77 + t } };
            // V4b sin T2 (la entrega anterior a añadir T2 en la mesa final)
            case "BASE-NOT2": return () => new AgenteHibrido { Config = new AgenteHibrido.Params { Seed = 77 + t, AdvancedTournament = false } };
            case "BASE-T0": return () => new AgenteHibrido { Config = new AgenteHibrido.Params { Seed = 77 + t, UseTournament = false } };
            case "BASE-X0": return () => new AgenteHibrido { Config = new AgenteHibrido.Params { Seed = 77 + t, UseExploiter = false } };
            case "BASE-T2": return () => new AgenteHibrido { Config = new AgenteHibrido.Params { Seed = 77 + t, AdvancedTournament = true, T2OnlyFinal = false } };
            case "BASE-T2F": return () => new AgenteHibrido { Config = new AgenteHibrido.Params { Seed = 77 + t, AdvancedTournament = true, T2OnlyFinal = false, T2BetaLate = 2.0, T2BetaEarly = 1.0 } };
            case "V3":      return () => new AgenteHibridoV3 { Config = new AgenteHibridoV3.Params { Seed = 77 + t } };
            case "V3-E":    return () => new AgenteHibridoV3 { Config = new AgenteHibridoV3.Params { Seed = 77 + t, ExploiterPlus = true } };
            case "V3-R":    return () => new AgenteHibridoV3 { Config = new AgenteHibridoV3.Params { Seed = 77 + t, RangePost = true } };
            case "V3-ER":   return () => new AgenteHibridoV3 { Config = new AgenteHibridoV3.Params { Seed = 77 + t, ExploiterPlus = true, RangePost = true } };
            default:        return null;
        }
    }

    /// <summary>Mesas de prueba. Cada rival recibe el número de mesa t para que los aleatorios también sean reproducibles.</summary>
    static readonly (string Nombre, Func<int, IPokerAgent>[] Rivales, int Q, bool Final)[] Pools =
    {
        /* 1 */ ("Heuristic x5", Enumerable.Repeat<Func<int, IPokerAgent>>(t => new SimpleHeuristicAgent(), 5).ToArray(), 3, false),
        /* 2 */ ("Mixto (2 Heur, Station, Raiser, Random)", new Func<int, IPokerAgent>[] { t => new SimpleHeuristicAgent(), t => new SimpleHeuristicAgent(), t => new CallingStationAgent(), t => new AlwaysRaiseAgent(), t => new RandomAgent(5000 + t) }, 3, false),
        /* 3 */ ("Heads-up vs Heuristic (final)", new Func<int, IPokerAgent>[] { t => new SimpleHeuristicAgent() }, 1, true),
        /* 4 */ ("3 jugadores (Heuristic x2)", new Func<int, IPokerAgent>[] { t => new SimpleHeuristicAgent(), t => new SimpleHeuristicAgent() }, 2, false),
        /* 5 */ ("4 jugadores (Heur, Heur, Station, Raiser)", new Func<int, IPokerAgent>[] { t => new SimpleHeuristicAgent(), t => new SimpleHeuristicAgent(), t => new CallingStationAgent(), t => new AlwaysRaiseAgent() }, 3, false),
        /* 6 */ ("Perfiles (5 estilos de jugador)", new Func<int, IPokerAgent>[] { t => new ProfileBot("TightPasivo", 6000 + t), t => new ProfileBot("LoosePasivo", 7000 + t), t => new ProfileBot("TightAgresivo", 8000 + t), t => new ProfileBot("LooseAgresivo", 9000 + t), t => new ProfileBot("Folder", 9500 + t) }, 3, false),
        /* 7 */ ("Mesa final de 6 (Heuristic x5)", Enumerable.Repeat<Func<int, IPokerAgent>>(t => new SimpleHeuristicAgent(), 5).ToArray(), 1, true),
    };

    public static void Ejecutar(int mesas, string[] nombres, int[] pools = null)
    {
        Console.WriteLine($"Políticas: AgenteHibrido = {AgenteHibrido.PolicyInfo}");
        Console.WriteLine($"           AgenteHibridoV2 = {AgenteHibridoV2.PolicyInfo}");
        for (int pi = 0; pi < Pools.Length; pi++)
        {
            if (pools != null && !pools.Contains(pi + 1)) continue;
            var pool = Pools[pi];
            Console.WriteLine($"\n=== [{pi + 1}] {pool.Nombre}: {mesas} mesas emparejadas, {(pool.Final ? "MESA FINAL, " : "")}top {pool.Q} clasifica");
            Console.WriteLine($"{"Versión",-9}{"Clasif%",9}{"±95%",7}{"Gana%",8}{"Stack",9}{"Δ vs 1ª",10}{"±95%",7}{"%CFR",7}{"%λ>0",7}{"ms máx",8}");
            bool[] baseQ = null;
            foreach (var v in nombres)
            {
                var q = new bool[mesas];
                int gana = 0; long stack = 0; long dec = 0, decCfr = 0, decMix = 0; double msMax = 0;
                for (int t = 0; t < mesas; t++)
                {
                    var rng = new Random(1000 + t);                         // misma semilla para todas las versiones
                    var asientos = new List<(string, Func<IPokerAgent>)>();
                    int tt = t;
                    asientos.Add(("Yo", Especial(v, tt) ?? (() => new AgenteHibridoV2 { Config = Conf(v, tt) })));
                    for (int r = 0; r < pool.Rivales.Length; r++) { var f = pool.Rivales[r]; asientos.Add(("R" + r, () => f(tt))); }
                    asientos = asientos.OrderBy(_ => rng.Next()).ToList();
                    var mesa = new PokerTable(GameConfig.Training(seed: rng.Next()));
                    var espia = new Espia();
                    foreach (var a in asientos)
                    {
                        var ag = a.Item2();
                        if (a.Item1 == "Yo") { espia.Inner = ag; ag = espia; }
                        mesa.Seat(ag, a.Item1);
                    }
                    var partida = new TableMatch(mesa, MatchInfo.Standalone(handsToPlay: 30, qualifyingSpots: pool.Q, isFinalTable: pool.Final, playersAtStart: asientos.Count));
                    partida.PlayAll();
                    var mio = partida.GetStandings().First(s => s.Name == "Yo");
                    q[t] = mio.Qualifies;
                    if (mio.Rank == 1) gana++;
                    stack += mio.Stack;
                    dec += espia.Dec; decCfr += espia.Cfr; decMix += espia.Mix;
                    msMax = Math.Max(msMax, espia.MsMax);
                }
                double p = q.Count(x => x) / (double)mesas;
                string delta = "", deltaCi = "";
                if (baseQ == null) baseQ = q;
                else
                {
                    var d = Enumerable.Range(0, mesas).Select(i => (q[i] ? 1.0 : 0) - (baseQ[i] ? 1.0 : 0)).ToArray();
                    double md = d.Average(), sd = Math.Sqrt(d.Select(x => (x - md) * (x - md)).Sum() / Math.Max(1, mesas - 1));
                    delta = $"{100 * md:+0.0;-0.0}"; deltaCi = $"{100 * 1.96 * sd / Math.Sqrt(mesas):F1}";
                }
                Console.WriteLine($"{v,-9}{100 * p,9:F1}{100 * 1.96 * Math.Sqrt(p * (1 - p) / mesas),7:F1}{100.0 * gana / mesas,8:F1}{stack / mesas,9:N0}{delta,10}{deltaCi,7}" +
                                  $"{100.0 * decCfr / Math.Max(1, dec),7:F0}{100.0 * decMix / Math.Max(1, dec),7:F1}{msMax,8:F1}");
            }
        }
    }

    static AgenteHibridoV2.Params Conf(string v, int t)
    {
        if (!Versiones.TryGetValue(v, out var f)) throw new ArgumentException($"Versión desconocida: {v}");
        var p = f();
        p.Seed = 77 + t;
        return p;
    }

    /// <summary>Envuelve al agente para contar de dónde sale cada decisión y medir tiempos (sirve para las 3 clases).</summary>
    sealed class Espia : IPokerAgent, IHandObserver
    {
        public IPokerAgent Inner;
        public long Dec, Cfr, Mix; public double MsMax;
        public void Initialize(string n) => Inner.Initialize(n);
        public ActionType GetAction(PlayerObservation o)
        {
            var r = Inner.GetAction(o);
            Dec++;
            string src = null; double lambda = 0, ms = 0;
            if (Inner is AgenteHibrido a) { src = a.LastTrace.Source; lambda = a.LastTrace.Lambda; ms = a.LastTrace.Ms; }
            else if (Inner is AgenteHibridoV2 b) { src = b.LastTrace.Source; lambda = b.LastTrace.Lambda; ms = b.LastTrace.Ms; }
            else if (Inner is AgenteHibridoV3 c) { src = c.LastTrace.Source; lambda = c.LastTrace.Lambda; ms = c.LastTrace.Ms; }
            if (src != null && src.StartsWith("cfr")) Cfr++;
            if (lambda > 0) Mix++;
            if (ms > MsMax) MsMax = ms;
            return r;
        }
        public void OnHandEnd(HandSummary s) { if (Inner is IHandObserver h) h.OnHandEnd(s); }
    }
}
