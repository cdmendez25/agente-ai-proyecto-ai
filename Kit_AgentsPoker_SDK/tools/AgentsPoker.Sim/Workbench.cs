using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using AgentsPoker.Core;

namespace AgentsPoker.Sim
{
    /// <summary>Per-agent accumulated statistics across matches.</summary>
    public sealed class AgentStats
    {
        public string Name;
        public int Tables, Wins, Qualified, RankSum;
        public long StackSum;
        public long Decisions;
        public double DecisionMsSum, DecisionMsMax;
        public int Timeouts, Errors;
    }

    /// <summary>Measures decision time and forwards end-of-hand info (if the inner agent wants it).</summary>
    public sealed class TimedAgent : IPokerAgent, IHandObserver
    {
        private readonly IPokerAgent _inner;
        private readonly AgentStats _stats;
        private readonly object _lock = new object();
        public TimedAgent(IPokerAgent inner, AgentStats stats) { _inner = inner; _stats = stats; }
        public void Initialize(string agentName) => _inner.Initialize(agentName);
        public ActionType GetAction(PlayerObservation o)
        {
            var sw = Stopwatch.StartNew();
            try { return _inner.GetAction(o); }
            finally
            {
                double ms = sw.Elapsed.TotalMilliseconds;
                lock (_lock)
                {
                    _stats.Decisions++;
                    _stats.DecisionMsSum += ms;
                    if (ms > _stats.DecisionMsMax) _stats.DecisionMsMax = ms;
                }
            }
        }
        public void OnHandEnd(HandSummary s) { if (_inner is IHandObserver h) h.OnHandEnd(s); }
    }

    public sealed class RunOptions
    {
        public int Hands = 30;
        public int Qualify = 3;
        public int? Seed;
        public int TimeoutMs = 1000;
        public int StartingStack = 10000;
        public bool Verbose;
        public int Matches = 100;
        public int Tables; // tournament: 0 = auto
        public bool Final; // match/bench: agents see MatchInfo.IsFinalTable = true (only 1st place counts)
    }

    public static class Workbench
    {
        public sealed class TableOutcome
        {
            public List<Standing> Standings;
            public List<AgentSpec> Seats;
        }

        /// <summary>Qualifiers of a table: at least 1, at most seats - 1; the final table always crowns 1.</summary>
        public static int QualifiersFor(int seats, int qualify, bool final)
            => final ? MatchInfo.FinalTableQualifyingSpots : Math.Max(1, Math.Min(qualify, seats - 1));

        /// <summary>Context of a single practice table (match / bench): standalone, final only with --final.</summary>
        public static MatchInfo PracticeMatch(int seats, RunOptions o)
            => MatchInfo.Standalone(o.Hands, QualifiersFor(seats, o.Qualify, o.Final), o.Final, seats);

        public static TableOutcome PlayTable(IReadOnlyList<AgentSpec> seats, RunOptions o, int? seed, Dictionary<string, AgentStats> stats, bool verbose, MatchInfo info = null)
        {
            var cfg = GameConfig.Tournament(seed);
            cfg.AgentTimeoutMs = o.TimeoutMs;
            cfg.StartingStack = o.StartingStack;
            var table = new PokerTable(cfg);
            foreach (var s in seats)
            {
                var st = GetStats(stats, s.Name);
                IPokerAgent agent;
                try { agent = s.Create(); }
                catch (Exception e) { throw new AgentLoadException($"{s.Name}: el constructor lanzó {e.GetType().Name}: {e.Message}"); }
                table.Seat(new TimedAgent(agent, st), s.Name);
            }
            var match = new TableMatch(table, info ?? PracticeMatch(seats.Count, o));
            HandResult r;
            while ((r = match.PlayNext()) != null)
            {
                foreach (var e in r.Errors)
                {
                    var st = GetStats(stats, seats[e.Seat].Name);
                    if (e.IsTimeout) st.Timeouts++; else st.Errors++;
                    if (verbose || o.Verbose) Console.WriteLine("  !! " + e);
                }
                if (verbose) PrintHand(table, r);
            }
            var standings = match.GetStandings();
            foreach (var s in standings)
            {
                var st = GetStats(stats, seats[s.Seat].Name);
                st.Tables++;
                st.StackSum += s.Stack;
                st.RankSum += s.Rank;
                if (s.Rank == 1) st.Wins++;
                if (s.Qualifies) st.Qualified++;
            }
            return new TableOutcome { Standings = standings, Seats = seats.ToList() };
        }

        private static AgentStats GetStats(Dictionary<string, AgentStats> stats, string name)
        {
            if (!stats.TryGetValue(name, out var s)) stats[name] = s = new AgentStats { Name = name };
            return s;
        }

        public static void PrintHand(PokerTable t, HandResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Mano {r.HandNumber} | {Describe(r.Match)} | (botón {t.GetName(r.ButtonSeat)}) ===");
            sb.AppendLine("  [vista espectador: todas las cartas]");
            for (int i = 0; i < t.SeatCount; i++)
                if (r.HoleCards[i] != null) sb.AppendLine($"  {t.GetName(i),-14} {r.HoleCards[i][0]} {r.HoleCards[i][1]}   stack {r.StacksBefore[i]}");
            Street? last = null;
            foreach (var a in r.Actions)
            {
                if (last != a.Street && a.Street != Street.Preflop)
                {
                    int count = a.Street == Street.Flop ? 3 : a.Street == Street.Turn ? 4 : 5;
                    sb.AppendLine($"  -- {a.Street}: {string.Join(" ", r.Board.Take(count))}");
                }
                last = a.Street;
                sb.AppendLine("    " + a);
            }
            sb.AppendLine("  Board: " + string.Join(" ", r.Board));
            if (r.WentToShowdown)
            {
                sb.AppendLine("  SHOWDOWN (cartas reveladas a TODOS los agentes vía IHandObserver.OnHandEnd):");
                for (int i = 0; i < t.SeatCount; i++)
                    if (r.HandValues[i] != 0)
                        sb.AppendLine($"    {t.GetName(i),-14} {r.HoleCards[i][0]} {r.HoleCards[i][1]}  {HandEvaluator.DescribeDetailed(r.HandValues[i])}");
            }
            else sb.AppendLine("  Sin showdown: el ganador NO muestra sus cartas.");
            foreach (var p in r.Pots)
                sb.AppendLine($"  Pot {p.Amount} -> {string.Join(", ", p.WinnerSeats.Select(w => t.GetName(w)))}{(p.IsUncontested ? " (sin disputa)" : "")}");
            sb.AppendLine("  Neto: " + string.Join("  ", Enumerable.Range(0, t.SeatCount).Select(i => $"{t.GetName(i)} {r.Net[i]:+#;-#;0}")));
            Console.Write(sb.ToString());
        }

        /// <summary>One-liner of what the agents see in PlayerObservation.Match.</summary>
        public static string Describe(MatchInfo m)
            => (m.IsFinalTable ? "MESA FINAL" : m.IsTournament ? $"ronda {m.Round}/{m.TotalRounds} mesa {m.TableNumber}/{m.TablesInRound}" : "mesa de practica")
               + $" | quedan {(m.HasHandLimit ? m.HandsRemaining.ToString() : "?")} manos | clasifican {m.QualifyingSpots}";

        public static void PrintStats(IEnumerable<AgentStats> stats, int stack)
        {
            Console.WriteLine();
            Console.WriteLine($"{"Agente",-16}{"Mesas",6}{"Gana%",8}{"Clasif%",9}{"RankProm",9}{"StackProm",11}{"ms/dec",9}{"ms máx",9}{"Timeouts",9}{"Errores",8}");
            foreach (var s in stats.OrderByDescending(x => x.Tables == 0 ? 0 : x.Qualified / (double)x.Tables).ThenByDescending(x => x.StackSum / (double)Math.Max(1, x.Tables)))
            {
                double n = Math.Max(1, s.Tables);
                Console.WriteLine($"{s.Name,-16}{s.Tables,6}{100.0 * s.Wins / n,8:F1}{100.0 * s.Qualified / n,9:F1}{s.RankSum / n,9:F2}{s.StackSum / n,11:N0}" +
                                  $"{(s.Decisions == 0 ? 0 : s.DecisionMsSum / s.Decisions),9:F2}{s.DecisionMsMax,9:F1}{s.Timeouts,9}{s.Errors,8}");
            }
            Console.WriteLine($"(stack inicial {stack:N0}; Clasif% = quedó en el top N de su mesa)");
        }

        // ------------------------------------------------------------------ tournament (same rules as the Unity bracket)
        public static int[] Distribute(int n, int tables)
        {
            var sizes = new int[tables];
            for (int i = 0; i < tables; i++) sizes[i] = n / tables + (i < n % tables ? 1 : 0);
            return sizes;
        }

        /// <summary>One round of the bracket: players, tables, and whether it is the final.</summary>
        public readonly struct RoundPlan
        {
            public RoundPlan(int players, int tables, bool final) { Players = players; Tables = tables; IsFinal = final; }
            public int Players { get; }
            public int Tables { get; }
            public bool IsFinal { get; }
        }

        /// <summary>Max seats per tournament table (TDD: 6-max).</summary>
        public const int MaxSeatsPerTable = 6;

        public static int AutoTables(int players) => players <= MaxSeatsPerTable ? 1 : (int)Math.Ceiling(players / (double)MaxSeatsPerTable);

        /// <summary>Whole bracket known in advance (same rules as the loop below), so every table knows Round / TotalRounds.</summary>
        public static List<RoundPlan> PlanRounds(int players, int firstTables, int qualify)
        {
            var plan = new List<RoundPlan>();
            int tables = firstTables;
            while (true)
            {
                bool final = tables == 1 && players <= MaxSeatsPerTable;
                plan.Add(new RoundPlan(players, tables, final));
                if (final) return plan;
                int next = Distribute(players, tables).Sum(size => QualifiersFor(size, qualify, false));
                players = next;
                tables = AutoTables(players);
            }
        }

        public static void Tournament(List<AgentSpec> entrants, RunOptions o, Dictionary<string, AgentStats> stats)
        {
            int n = entrants.Count;
            if (n < 2) throw new AgentLoadException("El torneo necesita al menos 2 agentes.");
            int tables = o.Tables > 0 ? o.Tables : AutoTables(n);
            if (tables > n / 2) throw new AgentLoadException($"{tables} mesas para {n} agentes: alguna quedaría con menos de 2.");
            if (n > tables * MaxSeatsPerTable) throw new AgentLoadException($"{n} agentes no caben en {tables} mesas (máx 6).");
            var rng = o.Seed.HasValue ? new Random(o.Seed.Value) : new Random();
            var current = entrants.OrderBy(_ => rng.Next()).ToList(); // random draw for round 1
            int round = 1, seedBase = o.Seed ?? rng.Next();
            var plan = PlanRounds(n, tables, o.Qualify);
            Console.WriteLine($"Plan: {plan.Count} ronda(s): " + string.Join(" -> ", plan.Select(p => p.IsFinal ? $"final ({p.Players})" : $"{p.Players} en {p.Tables} mesa(s)")));
            while (true)
            {
                bool final = plan[round - 1].IsFinal;
                Console.WriteLine();
                Console.WriteLine(final ? "########## MESA FINAL ##########" : $"########## RONDA {round}: {current.Count} agentes, {tables} mesa(s) ({string.Join(" + ", Distribute(current.Count, tables))}) ##########");
                var buckets = Enumerable.Range(0, tables).Select(_ => new List<AgentSpec>()).ToList();
                for (int k = 0; k < current.Count; k++) buckets[k % tables].Add(current[k]);
                var qualifiersPerTable = new List<List<AgentSpec>>();
                for (int t = 0; t < tables; t++)
                {
                    var seats = buckets[t];
                    int q = QualifiersFor(seats.Count, o.Qualify, final);
                    var opts = new RunOptions { Hands = o.Hands, Qualify = q, TimeoutMs = o.TimeoutMs, StartingStack = o.StartingStack, Verbose = o.Verbose, Final = final };
                    var info = MatchInfo.Tournament(round, plan.Count, final, o.Hands, q, t + 1, tables, seats.Count);
                    var outcome = PlayTable(seats, opts, seedBase + round * 100 + t, stats, o.Verbose, info);
                    Console.WriteLine((final ? "  Mesa final" : $"  Mesa {t + 1}") + $"  [agentes ven: {Describe(info)}]:");
                    foreach (var s in outcome.Standings)
                        Console.WriteLine($"    #{s.Rank} {seats[s.Seat].Name,-16} {s.Stack,7:N0}{(s.Qualifies ? (final ? "  CAMPEÓN" : "  -> avanza") : "")}{(s.TiedOnStack ? "  [desempate: " + s.TieBreak + "]" : "")}");
                    qualifiersPerTable.Add(outcome.Standings.Where(s => s.Qualifies).Select(s => seats[s.Seat]).ToList());
                }
                if (final)
                {
                    Console.WriteLine();
                    Console.WriteLine($"*** CAMPEÓN: {qualifiersPerTable[0][0].Name} ***");
                    return;
                }
                var next = new List<AgentSpec>();
                for (int r = 0; r < 6; r++)
                    foreach (var q in qualifiersPerTable) if (r < q.Count) next.Add(q[r]);
                current = next;
                tables = AutoTables(current.Count);
                round++;
                if (plan[round - 1].Players != current.Count) throw new InvalidOperationException("Bracket plan out of sync.");
            }
        }
    }
}
