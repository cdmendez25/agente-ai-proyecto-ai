using System;
using System.Collections.Generic;
using System.Linq;
using AgentsPoker.Core;
using AgentsPoker.Core.Agents;

/// <summary>
/// Validación diferencial (arquitectura §3): se juegan manos reales en el SDK y se reproducen en el simulador
/// con las MISMAS cartas, botón y acciones. Deben coincidir: ciegas, turnos, legalidad, claves del agente y neto.
/// Ejecutar: bash entrenar.sh validar-sim [manos]
/// </summary>
static class ValidarSimulador
{
    /// <summary>Agente que juega como "inner" y anota la clave que calcula el agente real en cada decisión.</summary>
    private sealed class Grabador : IPokerAgent
    {
        private readonly IPokerAgent _inner;
        private readonly AgenteHibridoV2.HandStrengthCalc _hs = new AgenteHibridoV2.HandStrengthCalc();
        private readonly List<(int Seat, int Key)> _log;
        public Grabador(IPokerAgent inner, List<(int, int)> log) { _inner = inner; _log = log; }
        public void Initialize(string n) => _inner.Initialize(n);
        public ActionType GetAction(PlayerObservation o)
        {
            _log.Add((o.Seat, AgenteHibridoV2.StateEncoder.EncodeFine(AgenteHibridoV2.StateEncoder.FromObservation(o, _hs))));
            return _inner.GetAction(o);
        }
    }

    public static int Ejecutar(int manosPorMesa)
    {
        var rng = new Random(5);
        int manos = 0, decisiones = 0, errCiegas = 0, errTurno = 0, errClave = 0, errLegal = 0, errFin = 0, errNeto = 0;
        var ejemplos = new List<string>();
        for (int n = 2; n <= 6; n++)
        {
            var log = new List<(int, int)>();
            var mesa = new PokerTable(GameConfig.Training(seed: 40 + n));
            var tipos = new Func<IPokerAgent>[] { () => new RandomAgent(n), () => new SimpleHeuristicAgent(), () => new AlwaysRaiseAgent(), () => new CallingStationAgent(), () => new RandomAgent(n + 10), () => new SimpleHeuristicAgent() };
            for (int s = 0; s < n; s++) mesa.Seat(new Grabador(tipos[s](), log), "J" + s);

            var deal = new LhDeal();
            var st = new LhState();
            for (int h = 0; h < manosPorMesa; h++)
            {
                for (int s = 0; s < n; s++) mesa.SetStack(s, 10000);   // stacks profundos, como en el entrenamiento
                log.Clear();
                var res = mesa.PlayHand();
                manos++;

                deal.SetCards(n, res.ButtonSeat, res.HoleCards, res.Board, rng);
                if (deal.Sb != res.SmallBlindSeat || deal.Bb != res.BigBlindSeat) { errCiegas++; continue; }
                st.Init(deal);
                int li = 0;
                bool roto = false;
                foreach (var a in res.Actions)
                {
                    if (a.Kind == ActionKind.SmallBlind || a.Kind == ActionKind.BigBlind) continue;
                    decisiones++;
                    if (st.Terminal || st.ToAct != a.Seat) { errTurno++; roto = true; Nota(ejemplos, $"turno: SDK {a.Seat}, sim {st.ToAct} ({a})"); break; }
                    int clave = st.Key(a.Seat);
                    if (li >= log.Count || log[li].Item1 != a.Seat || log[li].Item2 != clave)
                    {
                        errClave++;
                        Nota(ejemplos, $"clave: agente {(li < log.Count ? log[li].Item2 : -1)}, sim {clave} ({a})");
                    }
                    li++;
                    int act = a.Kind == ActionKind.Fold ? 0 : (a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise) ? 2 : 1;
                    st.LegalMask(out bool lf, out bool lr);
                    if ((act == 2 && !lr) || (act == 0 && !lf && a.Kind != ActionKind.Fold)) { errLegal++; Nota(ejemplos, $"legalidad: {a}"); }
                    st.Apply(act);
                }
                if (roto) continue;
                if (!st.Terminal) { errFin++; Nota(ejemplos, $"la mano no terminó en el simulador (mano {res.HandNumber})"); continue; }
                for (int s = 0; s < n; s++)
                    if (Math.Abs(st.NetChips(s) - res.Net[s]) > 5) { errNeto++; Nota(ejemplos, $"neto asiento {s}: SDK {res.Net[s]}, sim {st.NetChips(s):F1}"); break; }
            }
        }
        Console.WriteLine($"Validación simulador vs SDK: {manos:N0} manos, {decisiones:N0} decisiones (mesas de 2 a 6)");
        Console.WriteLine($"  ciegas distintas: {errCiegas}   turnos distintos: {errTurno}   claves distintas: {errClave}");
        Console.WriteLine($"  acciones ilegales: {errLegal}   manos sin terminar: {errFin}   netos distintos: {errNeto}");
        foreach (var e in ejemplos) Console.WriteLine("  ej: " + e);
        int total = errCiegas + errTurno + errClave + errLegal + errFin + errNeto;
        Console.WriteLine(total == 0 ? "  RESULTADO: OK, el simulador reproduce al SDK" : "  RESULTADO: HAY DIFERENCIAS");
        return total == 0 ? 0 : 1;
    }

    private static void Nota(List<string> l, string s) { if (l.Count < 8) l.Add(s); }
}
