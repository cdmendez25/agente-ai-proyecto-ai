using System;
using System.Collections.Generic;
using System.Linq;
using AgentsPoker.Core;
using AgentsPoker.Core.Agents;

/// <summary>
/// Comparación emparejada de versiones (arquitectura §14): mismas semillas, mismas cartas y mismos asientos
/// para todas las versiones. Ejecutar: bash entrenar.sh comparar [mesas] [versiones separadas por coma]
/// </summary>
static class Comparar
{
    static readonly Dictionary<string, Func<AgenteHibridoV2.Params>> Versiones = new Dictionary<string, Func<AgenteHibridoV2.Params>>
    {
        ["V0"]  = () => new AgenteHibridoV2.Params { UseCfr = false, UseExploiter = false, UseTournament = false },
        ["V1"]  = () => new AgenteHibridoV2.Params { UseCfr = true,  UseExploiter = false, UseTournament = false },
        ["V2"]  = () => new AgenteHibridoV2.Params { UseCfr = false, UseExploiter = true,  UseTournament = false },
        ["V3"]  = () => new AgenteHibridoV2.Params { UseCfr = true,  UseExploiter = true,  UseTournament = false, MixMode = 0 },
        ["V3b"] = () => new AgenteHibridoV2.Params { UseCfr = true,  UseExploiter = true,  UseTournament = false, MixMode = 1 },
        ["V4"]  = () => new AgenteHibridoV2.Params { UseCfr = true,  UseExploiter = true,  UseTournament = true,  MixMode = 0 },
        ["V4b"] = () => new AgenteHibridoV2.Params { UseCfr = true,  UseExploiter = true,  UseTournament = true,  MixMode = 1 },
        ["V4b-P05"] = () => new AgenteHibridoV2.Params { UseCfr = true, UseExploiter = true, UseTournament = true, MixMode = 1, PurifyTau = 0.05 },
        ["V4b-P10"] = () => new AgenteHibridoV2.Params { UseCfr = true, UseExploiter = true, UseTournament = true, MixMode = 1, PurifyTau = 0.10 },
        ["V3b-L6"] = () => new AgenteHibridoV2.Params { UseCfr = true, UseExploiter = true, UseTournament = false, MixMode = 1, LambdaMax = 0.6 },
    };

    static readonly (string Nombre, Func<IPokerAgent>[] Rivales, int Q)[] Pools =
    {
        ("Heuristic x5", Enumerable.Repeat<Func<IPokerAgent>>(() => new SimpleHeuristicAgent(), 5).ToArray(), 3),
        ("Mixto (2 Heur, Station, Raiser, Random)", new Func<IPokerAgent>[] { () => new SimpleHeuristicAgent(), () => new SimpleHeuristicAgent(), () => new CallingStationAgent(), () => new AlwaysRaiseAgent(), () => new RandomAgent() }, 3),
        ("Heads-up vs Heuristic", new Func<IPokerAgent>[] { () => new SimpleHeuristicAgent() }, 1),
    };

    public static void Ejecutar(int mesas, string[] nombres)
    {
        Console.WriteLine($"Política CFR embebida: {AgenteHibridoV2.PolicyInfo}");
        foreach (var pool in Pools)
        {
            Console.WriteLine($"\n=== {pool.Nombre}: {mesas} mesas emparejadas, top {pool.Q} clasifica");
            Console.WriteLine($"{"Versión",-8}{"Clasif%",9}{"±95%",7}{"Gana%",8}{"Stack",9}{"Δ vs V0",10}{"±95%",7}{"%CFR",7}{"%λ>0",7}{"ms máx",8}");
            bool[] baseQ = null;
            foreach (var v in nombres)
            {
                var q = new bool[mesas];
                int gana = 0; long stack = 0; long dec = 0, decCfr = 0, decMix = 0; double msMax = 0;
                for (int t = 0; t < mesas; t++)
                {
                    var rng = new Random(1000 + t);                         // misma semilla para todas las versiones
                    var asientos = new List<(string, Func<IPokerAgent>)>();
                    AgenteHibridoV2 yo = null;
                    // "BASE" = la V4b entregable (Agents/AgenteHibrido.cs, abstracción v1).
                    var tt = t;
                    
                    asientos.Add(("Yo",
                                v == "BASE" ? (Func<IPokerAgent>)(() => new AgenteHibrido { Config = new AgenteHibrido.Params { Seed = 77 + tt } })
                                : v == "V3"   ? (Func<IPokerAgent>)(() => new AgenteHibridoV3 { Config = new AgenteHibridoV3.Params { Seed = 77 + tt } })
                                : v == "V3-E" ? (Func<IPokerAgent>)(() => new AgenteHibridoV3 { Config = new AgenteHibridoV3.Params { Seed = 77 + tt, ExploiterPlus = true } })
                                : v == "V3-R"  ? (Func<IPokerAgent>)(() => new AgenteHibridoV3 { Config = new AgenteHibridoV3.Params { Seed = 77 + tt, RangePost = true } })
                                : v == "V3-ER" ? (Func<IPokerAgent>)(() => new AgenteHibridoV3 { Config = new AgenteHibridoV3.Params { Seed = 77 + tt, ExploiterPlus = true, RangePost = true } })
                                :               (Func<IPokerAgent>)(() => yo = new AgenteHibridoV2 { Config = Conf(v, tt) })));

                    for (int r = 0; r < pool.Rivales.Length; r++) asientos.Add(("R" + r, pool.Rivales[r]));
                    asientos = asientos.OrderBy(_ => rng.Next()).ToList();
                    var mesa = new PokerTable(GameConfig.Training(seed: rng.Next()));
                    var espia = new Espia();
                    foreach (var a in asientos)
                    {
                        var ag = a.Item2();
                        if (a.Item1 == "Yo") { espia.Inner = ag; espia.A = yo; ag = espia; }
                        mesa.Seat(ag, a.Item1);
                    }
                    var partida = new TableMatch(mesa, MatchInfo.Standalone(handsToPlay: 30, qualifyingSpots: pool.Q, isFinalTable: pool.Q == 1, playersAtStart: asientos.Count));
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
                Console.WriteLine($"{v,-8}{100 * p,9:F1}{100 * 1.96 * Math.Sqrt(p * (1 - p) / mesas),7:F1}{100.0 * gana / mesas,8:F1}{stack / mesas,9:N0}{delta,10}{deltaCi,7}" +
                                  $"{100.0 * decCfr / Math.Max(1, dec),7:F0}{100.0 * decMix / Math.Max(1, dec),7:F1}{msMax,8:F1}");
            }
        }
    }

    static AgenteHibridoV2.Params Conf(string v, int t)
    {
        var p = Versiones[v]();
        p.Seed = 77 + t;
        return p;
    }

    /// <summary>Envuelve al agente para contar de dónde sale cada decisión y medir tiempos.</summary>
    sealed class Espia : IPokerAgent, IHandObserver
    {
        public AgenteHibridoV2 A;      // null para BASE
        public IPokerAgent Inner;
        public long Dec, Cfr, Mix; public double MsMax;
        public void Initialize(string n) => Inner.Initialize(n);
        public ActionType GetAction(PlayerObservation o)
        {
            var r = Inner.GetAction(o);
            Dec++;

            if (A == null && Inner is AgenteHibridoV3 c3) {
                var t3 = c3.LastTrace;
                if (t3.Source != null && t3.Source.StartsWith("cfr")) Cfr++;
                if (t3.Lambda > 0) Mix++;
                if (t3.Ms > MsMax) MsMax = t3.Ms;
                return r;
            }

            if (A == null) { if (Inner is AgenteHibrido b) { if (b.LastTrace.Source != null && b.LastTrace.Source.StartsWith("cfr")) Cfr++; if (b.LastTrace.Lambda > 0) Mix++; if (b.LastTrace.Ms > MsMax) MsMax = b.LastTrace.Ms; } return r; }
            var tr = A.LastTrace;
            if (tr.Source != null && tr.Source.StartsWith("cfr")) Cfr++;
            if (tr.Lambda > 0) Mix++;
            if (tr.Ms > MsMax) MsMax = tr.Ms;
            return r;
        }
        public void OnHandEnd(HandSummary s) { if (Inner is IHandObserver h) h.OnHandEnd(s); }
    }
}
