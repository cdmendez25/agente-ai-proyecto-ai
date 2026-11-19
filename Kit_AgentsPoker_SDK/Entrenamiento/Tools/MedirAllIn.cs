using System;
using System.Collections.Generic;
using System.Linq;
using AgentsPoker.Core;
using AgentsPoker.Core.Agents;

/// <summary>
/// Pendiente de verificación de la arquitectura (§3 y §15): ¿con qué frecuencia hay all-in en mesas reales
/// de 30 manos con 10 000 fichas? Decide si el simulador de entrenamiento puede simplificar all-in y botes laterales.
/// Ejecutar: bash entrenar.sh medir-allin [mesas]
/// </summary>
static class MedirAllIn
{
    public static void Ejecutar(int mesas)
    {
        var escenarios = new List<(string Nombre, Func<IPokerAgent>[] Agentes)>
        {
            ("Heuristic x6", Enumerable.Repeat<Func<IPokerAgent>>(() => new SimpleHeuristicAgent(), 6).ToArray()),
            ("Mixto (2 Heur, Station, Raiser, Random, AgenteHibrido)", new Func<IPokerAgent>[]
                { () => new SimpleHeuristicAgent(), () => new SimpleHeuristicAgent(), () => new CallingStationAgent(),
                  () => new AlwaysRaiseAgent(), () => new RandomAgent(), () => new AgenteHibrido() }),
            ("Peor caso (Raiser x3, Station x3)", new Func<IPokerAgent>[]
                { () => new AlwaysRaiseAgent(), () => new AlwaysRaiseAgent(), () => new AlwaysRaiseAgent(),
                  () => new CallingStationAgent(), () => new CallingStationAgent(), () => new CallingStationAgent() }),
        };

        var rng = new Random(11);
        foreach (var (nombre, agentes) in escenarios)
        {
            long manos = 0, manosConAllIn = 0, manosConBoteLateral = 0;
            int mesasConAllIn = 0, maxComprometido = 0, minStack = int.MaxValue;
            for (int m = 0; m < mesas; m++)
            {
                var mesa = new PokerTable(GameConfig.Training(seed: rng.Next()));
                for (int i = 0; i < agentes.Length; i++) mesa.Seat(agentes[i](), "A" + i);
                var partida = new TableMatch(mesa, MatchInfo.Standalone(handsToPlay: 30, qualifyingSpots: 3, isFinalTable: false, playersAtStart: agentes.Length));
                bool huboAllIn = false;
                HandResult r;
                while ((r = partida.PlayNext()) != null)
                {
                    manos++;
                    if (r.Actions.Any(a => a.IsAllIn)) { manosConAllIn++; huboAllIn = true; }
                    if (r.Pots.Count(p => !p.IsUncontested) > 1) manosConBoteLateral++;
                    for (int s = 0; s < r.StacksBefore.Count; s++)
                    {
                        int puesto = r.StacksBefore[s] - r.StacksAfter[s] + r.Won[s];   // fichas comprometidas en la mano
                        if (puesto > maxComprometido) maxComprometido = puesto;
                        if (r.StacksAfter[s] > 0 && r.StacksAfter[s] < minStack) minStack = r.StacksAfter[s];
                    }
                }
                if (huboAllIn) mesasConAllIn++;
            }
            Console.WriteLine($"{nombre}: {mesas:N0} mesas, {manos:N0} manos");
            Console.WriteLine($"   manos con algún all-in: {manosConAllIn:N0} ({100.0 * manosConAllIn / manos:F4} %)" +
                              $"   mesas con algún all-in: {mesasConAllIn:N0} ({100.0 * mesasConAllIn / mesas:F2} %)");
            Console.WriteLine($"   manos con bote lateral disputado: {manosConBoteLateral:N0}" +
                              $"   máximo comprometido por un jugador en una mano: {maxComprometido:N0}   stack vivo mínimo visto: {minStack:N0}");
        }
    }
}
