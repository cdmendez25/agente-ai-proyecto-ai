using System;
using System.Collections.Generic;
using System.Diagnostics;
using AgentsPoker.Core;

/// <summary>
/// AgenteHibridov3: ES-MCCFR (estrategia base) + explotador (modelo de rivales, Monte Carlo, EV) + orquestador.
/// Arquitectura vigente: Arquitectura_Agente_Poker_Actualizada.pdf. Detalle matemático: DISENO_AGENTE.md.
/// Hoja de ruta por partes: HOJA_DE_RUTA.md (raíz del proyecto).
///
/// Estado:
///   [x] Parte 1  Seguridad: ActionValidator, FallbackPolicy (V0), TimeBudgetController
///   [x] Parte 2  Cartas y fuerza de mano: CardUtil, HandStrengthCalc (falta explicarla)
///   [ ] Parte 3  SdkObservationAdapter + StateEncoder        [ ] Parte 4  Simulador propio + validación
///   [ ] Parte 5  ES-MCCFR en Kuhn   [ ] Parte 6  ES-MCCFR real   [ ] Parte 7  CfrPolicy embebida (V1)
///   [ ] Parte 8  OpponentModel      [ ] Parte 9  RangeEstimator  [ ] Parte 10 MonteCarloEvaluator
///   [ ] Parte 11 ExpectedValueEstimator   [ ] Parte 12 Orquestador V3/V3b   [ ] Parte 13 Torneo T1/T2
///
/// Reglas del archivo (ver diseño 0.3 y 5.1): C# 9 / netstandard 2.1; sin estado static mutable;
/// no escribir en este archivo las subcadenas que vigila el detector del workbench (lista en el diseño 0.3).
/// </summary>
public sealed class AgenteHibridoV3 : IPokerAgent, IHandObserver
{
    // =====================================================================================
    // Configuración (diseño 5.3 y apéndice 10). Instancia: el laboratorio puede sustituirla.
    // =====================================================================================
    public sealed class Params
    {
        // Orquestación (F10)
        public double LambdaMax = 0.4, LambdaMaxNoCfr = 0.6, Cmin = 0.15, ZA = 1.28, Kc = 15;
        // Monte Carlo (F8)
        public int McMaxSims = 4000, McMinSims = 200;
        // Tiempo, en ms locales de .NET (4.5)
        public double TskipMs = 60, McDeadlineMs = 150, ThardMs = 250, MonoFactor = 1.0;
        // Torneo (F11)
        public double SigmaHand = 150;
        // CFR (F6)
        public double PurifyTau = 0.0;
        /// <summary>0 = mezcla lineal (V3), 1 = ajuste por EV (V3b). Por defecto V3b: mejor en la comparación de 1 000 mesas.</summary>
        public int MixMode = 1;
        public bool UseCfr = true, UseExploiter = true, UseTournament = true, AdvancedTournament = false;
        /// <summary>null = semilla no determinista (torneo). Fijarla en el laboratorio para reproducir.</summary>
        public int? Seed = null;
        public bool ExploiterPlus = false;   // mejoras del punto 2 (2a y luego 2b)
        public bool RangePost = false; 

        // FallbackPolicy (F1). Valores iniciales [PRUEBA].
        /// <summary>Percentil preflop máximo para abrir subiendo, según el número de rivales activos (índice 1..5).</summary>
        // Explotador (Parte D). Valores iniciales [PRUEBA].
        public int ExpMaxSims = 1500, ExpMinHands = 5;
        public double ExpDeadlineMs = 60, ExpZ = 1.28, ExpMarginFrac = 0.05, Chi = 0.5;

        public double[] PreOpenRaise = { 0, 0.45, 0.25, 0.18, 0.14, 0.12 };
        public double PreCallFactor = 1.6;     // pagar sin subida previa: pct <= open * factor
        public double PreFacingRaiseRaise = 1.0 / 3, PreFacingRaiseCall = 0.9;
        public double PreFacing3BetRaise = 1.0 / 6, PreFacing3BetCall = 0.5;
        public double PreCompleteSbPct = 0.60; // la SB completa barato con pct <= 0,60
        public double PostRaise = 0.70, PostRaiseVsRaise = 0.80, PostBet = 0.55, PostCallMargin = 0.03;
        public double DrawEqStrongFlop = 0.32, DrawEqStrongTurn = 0.18, DrawEqWeakFlop = 0.16, DrawEqWeakTurn = 0.09;
    }

    public Params Config { get; set; } = new Params();

    // =====================================================================================
    // Cartas (diseño 2.2.2 y 3.3). Público: lo reutiliza el entrenador (Entrenamiento/).
    // =====================================================================================
    public static class CardUtil
    {
        public const int NumClasses = 169, NumCombos = 1326;
        private const string RankChars = "23456789TJQKA";

        /// <summary>Clase canónica preflop 0..168: 0..12 parejas, 13..90 suited, 91..168 offsuit.</summary>
        public static int Canonical169(Card a, Card b)
        {
            int r1 = a.Rank, r2 = b.Rank;
            if (r1 < r2) { int t = r1; r1 = r2; r2 = t; }
            if (r1 == r2) return r1 - 2;
            int idx78 = (r1 - 2) * (r1 - 3) / 2 + (r2 - 2);
            return SuitOf(a) == SuitOf(b) ? 13 + idx78 : 91 + idx78;
        }

        /// <summary>Palo 0..3 a partir de Card.Index = (Rank-2)*4 + Suit.</summary>
        public static int SuitOf(Card c) => c.Index & 3;

        /// <summary>Índice de combinación 0..1325 para dos cartas distintas (índices 0..51).</summary>
        public static int ComboIndex(int c1, int c2)
        {
            if (c1 > c2) { int t = c1; c1 = c2; c2 = t; }
            return c2 * (c2 - 1) / 2 + c1;
        }

        public static readonly byte[] ComboC1 = BuildCombo(true);
        public static readonly byte[] ComboC2 = BuildCombo(false);

        private static byte[] BuildCombo(bool first)
        {
            var arr = new byte[NumCombos];
            for (int c2 = 1; c2 < 52; c2++)
                for (int c1 = 0; c1 < c2; c1++)
                    arr[c2 * (c2 - 1) / 2 + c1] = (byte)(first ? c1 : c2);
            return arr;
        }

        /// <summary>Nombre legible de una clase: "AA", "AKs", "72o".</summary>
        public static string ClassName(int cls)
        {
            if (cls < 13) { char r = RankChars[cls]; return new string(new[] { r, r }); }
            bool suited = cls < 91;
            int idx78 = suited ? cls - 13 : cls - 91;
            int r1 = 3;                                  // mayor r1 con (r1-2)(r1-3)/2 <= idx78
            while ((r1 - 1) * (r1 - 2) / 2 <= idx78) r1++;
            int r2 = idx78 - (r1 - 2) * (r1 - 3) / 2 + 2;
            return new string(new[] { RankChars[r1 - 2], RankChars[r2 - 2], suited ? 's' : 'o' });
        }

        /// <summary>
        /// Percentil acumulado de combinaciones por clase (menor = mejor), ordenado por equity contra una mano aleatoria.
        /// GENERADO por el laboratorio: bash entrenar.sh calibrar-preflop  (no editar a mano).
        /// </summary>
        public static readonly double[] PreflopPct =
        {
            // <PREFLOP-PCT-BEGIN>
            0.4811, 0.3560, 0.2519, 0.1448, 0.0739, 0.0392, 0.0317, 0.0271, 0.0226, 0.0181,
            0.0136, 0.0090, 0.0045, 0.9457, 0.9155, 0.8582, 0.8944, 0.8341, 0.7738, 0.8914,
            0.8371, 0.7858, 0.7255, 0.8703, 0.8130, 0.7707, 0.7014, 0.6531, 0.8100, 0.7888,
            0.7376, 0.6682, 0.6169, 0.5656, 0.7496, 0.7225, 0.6983, 0.6410, 0.5777, 0.5264,
            0.4615, 0.6652, 0.6380, 0.6048, 0.5807, 0.5324, 0.4646, 0.3922, 0.3424, 0.5928,
            0.5535, 0.5294, 0.4962, 0.4676, 0.4072, 0.3484, 0.2911, 0.2353, 0.4842, 0.4495,
            0.4103, 0.3952, 0.3590, 0.3454, 0.2670, 0.2021, 0.1719, 0.1373, 0.3891, 0.3514,
            0.3213, 0.2881, 0.2549, 0.2323, 0.1870, 0.1403, 0.1131, 0.0980, 0.0694, 0.2474,
            0.1900, 0.1750, 0.1478, 0.1599, 0.1161, 0.1011, 0.0950, 0.0573, 0.0452, 0.0422,
            0.0347, 1.0000, 0.9910, 0.9548, 0.9729, 0.9336, 0.8793, 0.9819, 0.9427, 0.8884,
            0.8220, 0.9638, 0.9246, 0.8673, 0.8069, 0.7587, 0.9125, 0.9035, 0.8462, 0.7828,
            0.7195, 0.6621, 0.8552, 0.8311, 0.7979, 0.7345, 0.6772, 0.6139, 0.5505, 0.7677,
            0.7466, 0.7104, 0.6953, 0.6259, 0.5626, 0.5053, 0.4465, 0.6863, 0.6501, 0.6350,
            0.6018, 0.5747, 0.5143, 0.4284, 0.3861, 0.3092, 0.5897, 0.5415, 0.5234, 0.4932,
            0.4585, 0.4193, 0.3771, 0.3002, 0.2443, 0.1991, 0.4766, 0.4374, 0.4042, 0.3680,
            0.3394, 0.3183, 0.2760, 0.2202, 0.1569, 0.1342, 0.1101, 0.3303, 0.2851, 0.2640,
            0.2112, 0.2293, 0.1840, 0.1689, 0.1252, 0.0920, 0.0830, 0.0664, 0.0543,
            // <PREFLOP-PCT-END>
        };
    }

    // =====================================================================================
    // Fuerza de mano (diseño 2.2.2). Público: lo reutiliza el entrenador.
    // Buffers de instancia: no es thread-safe (un objeto por agente / por hilo del entrenador).
    // =====================================================================================
    public sealed class HandStrengthCalc
    {
        private readonly bool[] _dead = new bool[52];
        private readonly Card[] _deck = new Card[52];
        private readonly int[] _suitCount = new int[4];

        /// <summary>
        /// HS exacta: fracción de manos rivales (todas las combinaciones del mazo restante) a las que gano,
        /// contando los empates como 1/2. Board de 3 a 5 cartas.
        /// </summary>
        public float Hs(Card h0, Card h1, IReadOnlyList<Card> board)
        {
            Array.Clear(_dead, 0, 52);
            _dead[h0.Index] = true;
            _dead[h1.Index] = true;
            for (int i = 0; i < board.Count; i++) _dead[board[i].Index] = true;
            int n = 0;
            for (int i = 0; i < 52; i++) if (!_dead[i]) _deck[n++] = new Card(i);

            int mine = HandEvaluator.Evaluate(h0, h1, board);
            int win = 0, tie = 0, tot = 0;
            for (int i = 0; i < n - 1; i++)
            {
                Card a = _deck[i];
                for (int j = i + 1; j < n; j++)
                {
                    int v = HandEvaluator.Evaluate(a, _deck[j], board);
                    if (mine > v) win++;
                    else if (mine == v) tie++;
                    tot++;
                }
            }
            return tot == 0 ? 0.5f : (float)((win + 0.5 * tie) / tot);
        }

        /// <summary>Clase de proyecto: 0 = ninguno, 1 = débil (gutshot), 2 = fuerte (flush draw u OESD / doble gutshot).
        /// Solo en flop y turn; en river o preflop devuelve 0.</summary>
        public int DrawClass(Card h0, Card h1, IReadOnlyList<Card> board)
        {
            int nb = board.Count;
            if (nb < 3 || nb > 4) return 0;

            // Flush draw: exactamente 4 de un palo, al menos una carta propia de ese palo, sin color hecho.
            Array.Clear(_suitCount, 0, 4);
            _suitCount[CardUtil.SuitOf(h0)]++;
            _suitCount[CardUtil.SuitOf(h1)]++;
            for (int i = 0; i < nb; i++) _suitCount[CardUtil.SuitOf(board[i])]++;
            bool flushMade = false, fd = false;
            for (int s = 0; s < 4; s++)
            {
                if (_suitCount[s] >= 5) flushMade = true;
                else if (_suitCount[s] == 4 && (CardUtil.SuitOf(h0) == s || CardUtil.SuitOf(h1) == s)) fd = true;
            }
            if (flushMade) fd = false;

            // Proyecto de escalera: rangos que completan escalera usando al menos una carta propia.
            int maskBoard = 0;
            for (int i = 0; i < nb; i++) maskBoard |= RankBit(board[i].Rank);
            int maskAll = maskBoard | RankBit(h0.Rank) | RankBit(h1.Rank);
            int outs = 0;
            if (!IsStraight(maskAll))
            {
                for (int r = 2; r <= 14; r++)
                {
                    int bit = RankBit(r);
                    if ((maskAll & bit) == bit) continue;
                    if (IsStraight(maskAll | bit) && !IsStraight(maskBoard | bit)) outs++;
                }
            }
            if (fd || outs >= 2) return 2;
            return outs == 1 ? 1 : 0;
        }

        /// <summary>Bit del rango (posición r); el as también ocupa la posición 1 (escalera A-5).</summary>
        private static int RankBit(int r) => r == 14 ? (1 << 14) | (1 << 1) : 1 << r;

        private static bool IsStraight(int mask)
        {
            for (int low = 1; low <= 10; low++)
                if (((mask >> low) & 0x1F) == 0x1F) return true;
            return false;
        }
    }

    // =====================================================================================
    // StateEncoder (Parte A): situación observable -> número de situación (clave) 0..NumKeys-1.
    // Público: el entrenador usa EXACTAMENTE estas funciones para que entrenar y jugar coincidan.
    // Rasgos: calle, bucket de mano, rivales que hablan después de mí, rivales activos,
    //         apuestas en la calle y si me enfrento a una apuesta.
    // =====================================================================================
    public static class StateEncoder
    {
        public const int Version = 1;

        /// <summary>Límites superiores de los 12 buckets preflop (percentil de PreflopPct: menor = mejor mano).</summary>
        public static readonly double[] PreflopBins = { 0.02, 0.05, 0.08, 0.12, 0.17, 0.23, 0.30, 0.40, 0.50, 0.62, 0.78, 1.01 };
        /// <summary>Límites superiores de los 6 buckets de HS (fuerza de la mano tras el flop).</summary>
        public static readonly double[] HsBins = { 0.35, 0.50, 0.62, 0.74, 0.86, 1.01 };

        // Tamaño de cada dimensión por calle: bucket, después de mí (3), rivales (3), apuestas, enfrentando (2).
        private static readonly int[] NBucket = { 12, 12, 12, 6 };
        private static readonly int[] NBets = { 4, 5, 5, 5 };
        private static readonly int[] Offset = BuildOffsets();
        public static readonly int NumKeys = Offset[4];

        private static int[] BuildOffsets()
        {
            var off = new int[5];
            for (int s = 0; s < 4; s++) off[s + 1] = off[s] + NBucket[s] * 3 * 3 * NBets[s] * 2;
            return off;
        }

        public static int PreflopBucket(double pct)
        {
            for (int i = 0; i < PreflopBins.Length; i++) if (pct <= PreflopBins[i]) return i;
            return PreflopBins.Length - 1;
        }

        public static int HsBucket(double hs)
        {
            for (int i = 0; i < HsBins.Length; i++) if (hs <= HsBins[i]) return i;
            return HsBins.Length - 1;
        }

        /// <summary>Bucket postflop: flop y turn = bin de HS x 2 + (proyecto fuerte ? 1 : 0); river = bin de HS.</summary>
        public static int PostflopBucket(int street, double hs, int drawClass)
        {
            int b = HsBucket(hs);
            return street == 3 ? b : b * 2 + (drawClass == 2 ? 1 : 0);
        }

        /// <summary>Junta los rasgos en un solo número (como un número de varias cifras con bases distintas).</summary>
        public static int Encode(int street, int bucket, int after, int opponents, int bets, int facing)
        {
            int betIdx = street == 0 ? bets - 1 : bets;                // preflop la BB ya cuenta como apuesta 1
            if (betIdx < 0) betIdx = 0;
            if (betIdx >= NBets[street]) betIdx = NBets[street] - 1;
            if (after > 2) after = 2;
            int opp = opponents < 1 ? 0 : (opponents > 3 ? 2 : opponents - 1);
            int x = bucket;
            x = x * 3 + after;
            x = x * 3 + opp;
            x = x * NBets[street] + betIdx;
            x = x * 2 + (facing != 0 ? 1 : 0);
            return Offset[street] + x;
        }

        /// <summary>Acciones legales "de diseño" de una clave: FOLD solo si me enfrento a apuesta; RAISE si quedan apuestas.</summary>
        public static void LegalMask(int key, out bool fold, out bool raise)
        {
            int s = StreetOf(key);
            int x = key - Offset[s];
            int facing = x % 2; x /= 2;
            int betIdx = x % NBets[s];
            int bets = s == 0 ? betIdx + 1 : betIdx;
            fold = facing == 1;
            raise = bets < 4;
        }

        public static int StreetOf(int key)
        {
            int s = 0;
            while (s < 3 && key >= Offset[s + 1]) s++;
            return s;
        }

        /// <summary>Clave a partir de lo que ve el agente en el SDK (SdkObservationAdapter + StateEncoder).</summary>
        public static int FromObservation(PlayerObservation o, HandStrengthCalc hs) => FromObservation(o, hs, out _);

        /// <summary>Igual, y además devuelve la fuerza 0..1 de la mano (preflop: 1 - percentil; después: HS).</summary>
        public static int FromObservation(PlayerObservation o, HandStrengthCalc hs, out double strength)
        {
            int street = (int)o.Street;
            if (street < 0 || street > 3) street = 3;
            int bucket;
            if (street == 0)
            {
                double pct = CardUtil.PreflopPct[CardUtil.Canonical169(o.HoleCards[0], o.HoleCards[1])];
                strength = 1 - pct;
                bucket = PreflopBucket(pct);
            }
            else
            {
                float h = hs.Hs(o.HoleCards[0], o.HoleCards[1], o.Board);
                strength = h;
                bucket = PostflopBucket(street, h, hs.DrawClass(o.HoleCards[0], o.HoleCards[1], o.Board));
            }

            int n = o.Players.Count;
            int start = street == 0 ? (o.BigBlindSeat + 1) % n : (o.ButtonSeat + 1) % n;
            int myOrder = (o.Seat - start + n) % n;
            int after = 0;
            for (int j = 0; j < n; j++)
            {
                var pl = o.Players[j];
                if (j == o.Seat || !pl.IsActive || pl.IsAllIn) continue;
                if ((j - start + n) % n > myOrder) after++;
            }
            return Encode(street, bucket, after, o.ActivePlayerCount - 1, o.BetsThisStreet, o.ToCall > 0 ? 1 : 0);
        }

        /// <summary>Huella de las constantes de la abstracción: la política guardada solo se usa si coincide.</summary>
        public static uint ConstantsHash()
        {
            uint h = 2166136261;
            void Mix(long v) { for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (8 * i)); h *= 16777619; } }
            Mix(Version); Mix(NumKeys);
            foreach (var d in PreflopBins) Mix(BitConverter.DoubleToInt64Bits(d));
            foreach (var d in HsBins) Mix(BitConverter.DoubleToInt64Bits(d));
            return h;
        }
    }

    // =====================================================================================
    // Política heurística de seguridad (diseño 5.2, V0b). Sin estado salvo buffers.
    // =====================================================================================
    private sealed class FallbackPolicy
    {
        /// <summary>Escribe en pi[3] (FOLD, CALL, RAISE) una distribución one-hot. No aplica la máscara (lo hace el validador).</summary>
        public void Policy(PlayerObservation o, HandStrengthCalc hs, Params p, double[] pi)
        {
            pi[0] = pi[1] = pi[2] = 0;
            int m = Math.Max(1, Math.Min(5, o.ActivePlayerCount - 1));
            int act;
            if (o.Street == Street.Preflop) act = Preflop(o, p, m);
            else act = Postflop(o, hs, p, m);
            pi[act] = 1;
        }

        private static int Preflop(PlayerObservation o, Params p, int m)
        {
            double pct = CardUtil.PreflopPct[CardUtil.Canonical169(o.HoleCards[0], o.HoleCards[1])];
            double open = p.PreOpenRaise[m];
            int bets = o.BetsThisStreet;     // la BB cuenta como 1
            bool free = o.ToCall == 0;

            if (bets <= 1)
            {
                if (pct <= open) return 2;
                if (free) return 1;
                if (pct <= open * p.PreCallFactor) return 1;
                if (o.ToCall <= o.SmallBlind && pct <= p.PreCompleteSbPct) return 1;
                return 0;
            }
            if (bets == 2)
            {
                if (pct <= open * p.PreFacingRaiseRaise) return 2;
                if (free || pct <= open * p.PreFacingRaiseCall) return 1;
                return 0;
            }
            if (pct <= open * p.PreFacing3BetRaise) return 2;
            if (free || pct <= open * p.PreFacing3BetCall) return 1;
            return 0;
        }

        private static int Postflop(PlayerObservation o, HandStrengthCalc hs, Params p, int m)
        {
            Card h0 = o.HoleCards[0], h1 = o.HoleCards[1];
            double h = hs.Hs(h0, h1, o.Board);
            int draw = hs.DrawClass(h0, h1, o.Board);
            double made = Math.Pow(h, m);    // aproximación de la equity contra m manos aleatorias
            double eq = made;
            bool flop = o.Street == Street.Flop;
            if (draw == 2) eq = Math.Max(eq, flop ? p.DrawEqStrongFlop : p.DrawEqStrongTurn);
            else if (draw == 1) eq = Math.Max(eq, flop ? p.DrawEqWeakFlop : p.DrawEqWeakTurn);

            double raiseThr = o.BetsThisStreet >= 2 ? p.PostRaiseVsRaise : p.PostRaise;
            if (o.CanRaise && made >= raiseThr) return 2;
            if (o.ToCall == 0) return (o.CanRaise && made >= p.PostBet) ? 2 : 1;
            return eq >= o.PotOdds + p.PostCallMargin ? 1 : 0;
        }
    }

    // =====================================================================================
    // Validación de acciones (diseño 4.6 SelectLegalAction y 5.4).
    // =====================================================================================
    private static class ActionValidator
    {
        /// <summary>Anula las acciones ilegales o absurdas: FOLD si es gratis pasar, RAISE si no se puede subir.</summary>
        public static void Mask(double[] pi, PlayerObservation o)
        {
            for (int a = 0; a < 3; a++)
                if (double.IsNaN(pi[a]) || pi[a] < 0) pi[a] = 0;
            if (o.ToCall == 0) pi[0] = 0;
            if (!o.CanRaise) pi[2] = 0;
        }

        /// <summary>
        /// Acción segura (arquitectura §13, paso 1): se fija ANTES de pensar. Nunca es ilegal ni un fold gratis:
        /// si pasar es gratis, CHECK (CALL); si hay que pagar, FOLD.
        /// </summary>
        public static ActionType SafeAction(PlayerObservation o) => o.ToCall == 0 ? ActionType.CALL : ActionType.FOLD;

        /// <summary>Muestrea una acción legal. Nunca FOLD con ToCall == 0; RAISE solo si CanRaise.</summary>
        public static ActionType Sample(double[] pi, PlayerObservation o, Random rng)
        {
            Mask(pi, o);
            double s = pi[0] + pi[1] + pi[2];
            if (!(s > 0)) return o.ToCall == 0 ? ActionType.CALL : ActionType.FOLD;
            double u = rng.NextDouble() * s;
            if (u < pi[0]) return ActionType.FOLD;
            if (u < pi[0] + pi[1]) return ActionType.CALL;
            return ActionType.RAISE;
        }
    }

    // =====================================================================================
    // Política CFR guardada dentro del archivo (Parte C). La escribe el laboratorio:
    //   bash entrenar.sh entrenar-cfr <segundos>
    // Formato: Header "v=..;k=..;h=..;c=..;it=.." + base64 con 2 bytes por clave:
    //   byte0 = P(FOLD)*255, byte1 = P(CALL)*255, P(RAISE) = resto. (255,255) = situación no aprendida.
    //   c = suma de control FNV-1a de los bytes (integridad).
    // =====================================================================================
    private static class PolicyData
    {
        // <POLICY-BEGIN>
        internal const string Header = "v=1;k=3564;h=978714086;c=3750387577;it=62292219";
        internal static readonly string[] Blob =
        {
            "AAD/////AAD//wAA//8A/wAA/////wAA//8AAP//AP8AAP////8Aaf//AC3//wD///8AAP//AAD//wAA//8A////AAD//wBB//8A",
            "AP//AP///wIB//8AAf//AAD//wD/////////////////////////AAD//wAA//8AAP//AP///wAD//8AHP//AAD//wD/AAD/////",
            "AAD//wDW//8A/wAI/////wAA//8AAP//AP8A//////8Ayf//ACD//wD///8AAP//AAD//wAA//8C/f//AAr//wAA//8AAP//AP//",
            "/wBC//8smf//AAD//wD/////////////////////////AAD//wAA//8AAP//AP///wCF//8AAP//AAD//wD/AAD/////AAD//wDx",
            "//8A/wCt/////wAA//8A5P//AP8A2P////8W2P//AOL//wD///8AAP//AAH//wD///8A////APn//wAA//8AAP//AP///wzi//8A",
            "2f//AOL//wD/////////////////////////AAD//wAD//8AAP//AP///wAA//8AAP//AAH//wD/AAD/////AP3//wH+//8A/wDy",
            "/////wAB//8A+v//AP8Awf/////+AP//0yv//wD///8AAP//AAL//wD///8L9P//AP///wBX//8A/P//AP///32C///ENP//4xj/",
            "/wD/////////////////////////AAD//wAA//8A7f//AP///wAB//8AAP//AAD//wD/AAD/////AP///wD///8A/wD//////wAC",
            "//+Sbf//AP8Avv/////1B///+AD///gH//8AAP//ABH//wD///8L9P//OMb//wD5//8A/v//AP////sE///+Af///wD//wD/////",
            "////////////////////AAD//wAy//8A////AP///wAA//8AAP//7QD//wD/ACf/////AP///wD///8A/wD9/////wDn////AP//",
            "AP8AsP/////aAP///gH///oF//8AAP//AN///wD///8I9///ol3//wD///8A////AP///+oU////AP///wD//xvk////////////",
            "////////////AAD//wEI//8A////AP///wAA///hAf///gH//wD/AEj/////AP///wP8//8A/wD//////wD9////AP//AP8A+v//",
            "///lFv///QD///sE//8AAP//APT//wD///8B/v//gH///wX6//8A////AP////oF////AP///wD//wD/////////////////////",
            "////AAD//yIG//8A////AP///8sA///+AP///gD//wD/AIv/////AP///wD///8G+QD//////wD/////AP//AP8A+v//////AP//",
            "/wD///4B//8AAP//APj//wD///9Osf//8A////8A//8A5///AP////sD////AP///wD//wD/////////////////////////AAD/",
            "/xAL//8A/v//AP////8A////AP///wD//wD/AP3/////AP///wD+//8A/wD//////wD/////AP//AP8A/f/////3CP///wD///oF",
            "//8AAP//ANj//wD///+Mc///+wT///8A//8A+v//AP////YJ////AP///wD//wD/////////////////////////AAD//0Fc//8E",
            "+v//AP////8A////AP///wD//wD/AP//////AP///wL9//8C/QD//////wH+////AP//AP8A/f/////3CP///wD///wD//8ABf//",
            "AOb//wD///+dYv///gH///8A//8B+///AP////0C////AP///wD//wD/////////////////////////AAD//wD4//8B/v//AP//",
            "//8A////AP///wD//wD/AP//////AP///+4R//8A/wD//////wD/////AP//AP8A////////AP///wD///4B//8Awf//AP///wD/",
            "///kG////wD///8A//8FuP//Af7///8A////AP///wD//wD/////////////////////////AgP//wP8//8b5P//AP////8A////",
            "AP///wD//wD/AP//////apX///4B//8C/QD//////wX6////AP//AP8A///////8A////wD///8A///+Af//APz//wHv//8A////",
            "/wD///8A//82iP//dYr///8A////AP///wD//1Gu/////////////////////////wD//xwR//8n0P//Av3///8A////AP///wD/",
            "/wD/AOX/////jzX///4A////AP//2yQA/f/////9Af///wD///sD///bJADk//////oF////AP///wD///0CAN7/////g2T///8A",
            "////AP//mWYA/P/////2AP///wD///8A////AAA8/////+Id///+Af///QD///sE//////////////////////////8A///////3",
            "AP///wD///8A////AAD///////4B////AP///wD///4BAAD/////AL7//wD///8A////AP8AAP////8A4f//AAv//wD7//8A/wAA",
            "/////wCH//8AA///AQv//wD/AC//////AAr//wCm//8A////AP8AGf////8AFv//AGX//wD///8A/wCO/////wDH//8A9///Agj/",
            "/wD///////////////////////////8A+P////8A8v//APr//wD///8A/wDr/////wDy//8AhP//AAT//wD/AL//////AeT//+gW",
            "////AP//9gkAsf////9Rov///wD///8A///+AQCS/////71A////AP///wD///EOAPT/////AP7///4B////AP//xDsA//////+B",
            "MP///wD///8A///+AQC7/////2Oc////AP///gD///sE//////////////////////////8A//////+9Hf///wD///8A////AAD/",
            "//////wB////AP///gD///QLAAD/////AIz//wD///8A////AP8AGP////8Axf//AAH//wC1//8A/wCb/////wA0//8AJP//AAD/",
            "/wD/AAT/////AGH//wD0//8A////AP8Aq/////8AF///AI7//wD///8A/wCw/////wA5//8AXv//AAT//wD/////////////////",
            "//////////8A//////8A6v//AP3//wD+//8A/wD1/////wDT//8A1P//AAD//wD/AJ//////AP7//0m2///+Af//8A8Af/////9L",
            "s////wD///8A//+oVwCp/////3CP////AP///gD///wDAOL/////AP7///8A////AP//rVIAt/////9Ms////wD///8A//8V6gD+",
            "/////7dH////AP///gD///cI//////////////////////////8A//////+Pbv///wD///8A///5BgD//////+0S////AP///wD/",
            "//INAAD/////AAf//wDx//8A////AP8AeP////8A4v//AAL//wBU//8A/wAA/////wCX//8B/P//AAH//wL9AAD/////AAj//wBc",
            "//8A////AP8Aj/////8AtP//AAz//wDt//8A/wC6/////wBj//8BCv//AQ///wD///////////////////////////8A/v////8A",
            "xv//AP///wD///8A/wD3/////wDg//8Aj///AAT//wD/AAD/////AP///wD///8A////AP8AFP////8A2f//T0n//+cI//8A/wAd",
            "/////wDi///bHP///gD//wH+AJ3/////AP///wL9///sE///+wQAOf////8A/v//eB7///oF//8A/wDD/////wD6//9Jqv//9wj/",
            "/wH+//////////////////////////8A+f////8A////+gX///4B//8A/wD6/////wD+///TFv///gD//wD/AAD/////AAf//wDd",
            "//8A////AP8AAP////8A1f//AAD//wCn//8A/wAB/////wBS//8B+f//Awj//wD/AAD/////AAD//wCV//8A////AP8AKv////8A",
            "ZP//AAz//wDR//8A/wAA/////wCc//8AC///AEj//wD///////////////////////////8A4v////8AM///APr//wD7//8A/wD8",
            "/////wBY//8AAP//AH///wD/AAD/////AP///wD///8Z5v//AP8AAP////8Ai///N6D//8o0//8A/wAA/////wD+///JMf//+QT/",
            "/wT7AP//////AP///wD///8X6P///AMAIf////8AIv//Auv///sE//8A/wAc/////wDu//+vNv//8Ar//wD/////////////////",
            "//////////8AnP////8Aef//n2D///IN//90iwAp/////wBn//9Msv//5xj//wD/AAD/////AAD//wD///8A+f//AP8AAP////8A",
            "rv//AAH//wAB//8A/wAA/////wAP//8AZv//BgP//wH+AAD/////AAD//wD///8A/f//AP8AHf////8ACf//AAD//wAD//8A/wDa",
            "/////wCI//8AL///AAH//wD///////////////////////////8ATP////8AAP//ACn//wCy//8A/wCd/////wAK//8As///AQP/",
            "/wD/AAD/////AAD//wBH//8AwP//AP8AAP////8AAP//AAD//wAA//8A/wAA/////wAA//8AAP//AAH//wD/AGL/////AAD//wA6",
            "//8Aqf//AP8AaP////8AAP//AAD//wAA//8A/wAU/////wAC//8ADf//AAD//wD///////////////////////////8Ar/////8A",
            "AP//AAL//wAA//8A/wCY/////wAA//8AAP//AAD//wD/AAD/////AAD//wAA//8AAP//AP8AAP////8AAP//AAD//wAA//8A/wAA",
            "/////wAE//8ABf//ABb//wD/ABb/////AAD//wAA//8AAP//AP8Anf////8AAv//AAD//wAA//8A/wDM/////wAa//8AAP//ABD/",
            "/wD///////////////////////////8AEP////8AAP//AA3//wAA//8A/wBX/////wAH//8AKf//AQD//wD/AAT/////5AD///8A",
            "////AP//Av0AD//////6AP///wD///8A///+AQAd/////7AA////AP///wD///8AAP///////wD///8A//8fkf//SrUAZP//////",
            "AP///wD///8A///XKACa//////4A////AP///gD//+Id//////////////////////////8A////////AP///wD//29K//9KtQD/",
            "//////8A////AP///wD///4BAJD/////AP///wD///8A////AP8Av/////8A1f//apX//wD///8A/wAA/////wB9//8Bsf//HNn/",
            "/wD/APn/////AOv//wD///8A////AP8Al/////8Al///AOn//wD///8A/wCe/////wDe//8Gpv//Cu///wD/////////////////",
            "//////////8AYf////8A////AP///wD///8A/wCg/////wAH//8H+P//e3///wD/ACP/////3gD///8A////AP//Av0A4//////6",
            "AP///wD///4B////AACv/////6AD////AP///wD///8AANP//////gD///8A////AP//OsUAuP//////AP///wD///8A///NMgCx",
            "//////8A////AP///gD///0C//////////////////////////8A////////AP///wD///8A////AAD///////8A////AP///wD/",
            "/9olAKX/////AP7//wD///8A////AP8ABf////8A4P//AP///wD///8A/wAo/////wB7//85xf//A+v//wH+AP//////AP///wD/",
            "//8A////AP8A+v////8AZ///ANb//wD///8A/wCM/////wCb//8Brf//APr//wD///////////////////////////8A6f////8A",
            "////AP///wD///8A/wD3/////wAA//8A4v//A/j//wD/AP//////EO////0C////AP//4xwA//////9yUf///wD///8A///+AQD5",
            "/////05a////AP///gD///8AAFj/////AMn///8A////AP//4R4A////////AP///wD//8Y1///TLAD///////4B////AP///gH/",
            "/+Ee//////////////////////////8A////////AP///wD//+YM//85xgD///////8A////AP///AD///gHAP//////APj//wD/",
            "//8A////AP8ACf////8Azv//APL//wD///8A/wAB/////wCB//8AFv//Dt3//wD/AP//////APb//wD///8A////AP8A//////8A",
            "Af//ANn//wD///8A/wC9/////wBi//8AwP//APr//wD///////////////////////////8AwP////8A/v//AP///wD///8A/wD3",
            "/////wAF//8Auf//A+j//wD/AP//////AP///4N8////AP//+AcA+/////8F+v///wD///8A///4BwDs/////wDs////AP///QD/",
            "//oFAOD/////AP////8A///+Af//+wQA///////eAf///QD///8A////AAD6//////4B///6A///+QH//0yz////////////////",
            "//////////8A//////8O8f///wD///8A///uEQD//////9Ua////AP///wD///oFAP//////AP///wD///8A////AP8AAP////8A",
            "5v//AJ///wD///8A/wAE/////wGF//8JKf//E+z//wH+AP//////AP///wD///8A////AP8A/v////8AAv//ALv//wD///8A/wDU",
            "/////wAi//8AA///BqP//wH+//////////////////////////8AyP////8A////AP///wD///8A/wD9/////wAB//8BaP//APj/",
            "/wP8AEP/////AP///yvU///+Af//vEMACP////8A////sk3///8A////AAAK/////wD/////AP///wD///UKAN3/////AP////8A",
            "////AP//7xAA2P////8A////wT3///8A///9AgD//////9Ip///sEv///QL//9km//////////////////////////8A/f////8A",
            "5///q1T///8A///+AQDB/////wDi//+uUf///wD///oFAAD/////AP///wD///8A////AP8AAP////8A9f//AAj//wD+//8A/wAC",
            "/////wDG//8DB///AcH//wH+AP//////AP///wD///8A////AP8A//////8AAP//AND//wD///8A/wAI/////wA8//8BA///AoH/",
            "/wD///////////////////////////8Auv////8A/v//AKr//wD///8A/wBZ/////wAA//8ATv//AIf//wD/AAD/////AAD//wAA",
            "//8AAP//AP8AAP////8AO///AAD//wAA//8A/wAA/////wAK//8AAv//AAD//wD/ADP/////AAD//wAA//8AAP//AP8ABP////8A",
            "AP//AAD//wAA//8A/wAJ/////wA1//8AAP//AAP//wD///////////////////////////8AWv////8AAP//AAD//wAA//8A/wA4",
            "/////wAD//8AAP//AAD//wD/AAD/////ANr//wBv//8A////AP8AAP////8AGv//AAD//wAA//8A/wAB/////wBB//8BAv//AAL/",
            "/wD/AKD/////AAD//wAz//8A+f//AP8AvP////8AAP//AAD//wAA//8A/wAO/////wDb//8AA///AAH//wD/////////////////",
            "//////////8AFf////8AAP//AAD//wAA//8A/wDx/////wAA//8AF///AA7//wD/AAD//////wD///8A////AP///wAAAP/////+",
            "AP//rwD///4A////AAAA/////1kA///qAP//+gD///8AAHb//////wD///8A///+AP///wAA7f//////AP//1gD///8A////AADN",
            "/////8IA///qAP//+gD///8A//////////////////////////8A///////+AP//9AD///8A////AAD///////8A///4AP///wD/",
            "//8AAL3/////2yT///8A///+AP///wAAav/////ZAP//ngD///8A////AAAA/////0wS///tAP///wD///8AAP///////wD///8A",
            "///+AP///wAAkf/////5AP//vwD//+EA////AACp/////7oA///FAP//8gD///8A//////////////////////////8Auv/////z",
            "AP//8wD///sA////AADi/////9AA///zAP///gD///8AAP//////HuD///8A///+AP///wAA4/////+qPP//gQD///8A////AADx",
            "/////znG///fAP///wD///YJAP//////U6r///8A///zAP///wAA///////iAP//yQD///4A////AAD//////8EA//+wAP//1AD/",
            "//8A//////////////////////////8A//////9vev//4QD///oA////AAD9/////50H///zAP///wD///8AAP//////COP///4B",
            "////AP///wAA//////8S6v//oAD///4A////AAD//////0an///qAP///wD///cIAP//////Buz///4B///9AP///wAA///////Q",
            "AP//uwD///AA////AAD+/////+AA//+OAP//7AD///8A//////////////////////////8A//////8szP//zQD///kA////AADm",
            "/////5pO//+gG////QH///8AADX/////AP///y3S////AP///gEAbf////8A////g1P///sA////AADy/////wD////iAf//mlD/",
            "/2CfAOb/////AP///9wj///wD////wAA//////8A3///Y17//8I9///zDAD//////z91///ZAP//UFT//9ol////////////////",
            "//////////8A//////8A////xS3//+8C///9AgD//////wDp//8oxv//3AD//+YZAAD/////AJX//wDi//8A+f//AP8AAP////8A",
            "4P//AB7//wBi//8A/wAA/////wAP//8A8///ALP//wD/ADr/////AAD//wA7//8A8v//AP8AF/////8AAP//ALX//wCi//8A/wAF",
            "/////wB8//8Arf//AD3//wD///////////////////////////8AJf////8AAP//AD3//wCu//8A/wAB/////wAC//8AZf//AP//",
            "/wD/",
        };
        // <POLICY-END>
    }

    private static class CfrPolicy
    {
        public static readonly bool IsValid;
        public static readonly string Info;
        private static readonly byte[] Bytes;

        static CfrPolicy()
        {
            try
            {
                if (string.IsNullOrEmpty(PolicyData.Header)) { Info = "sin política entrenada"; return; }
                var campos = new Dictionary<string, string>();
                foreach (var par in PolicyData.Header.Split(';'))
                {
                    int i = par.IndexOf('=');
                    if (i > 0) campos[par.Substring(0, i)] = par.Substring(i + 1);
                }
                if (campos["v"] != StateEncoder.Version.ToString()) { Info = "versión distinta"; return; }
                if (campos["k"] != StateEncoder.NumKeys.ToString()) { Info = "número de claves distinto"; return; }
                if (campos["h"] != StateEncoder.ConstantsHash().ToString()) { Info = "huella de abstracción distinta"; return; }
                var bytes = Convert.FromBase64String(string.Concat(PolicyData.Blob));
                if (bytes.Length != 2 * StateEncoder.NumKeys) { Info = "tamaño incorrecto"; return; }
                if (campos["c"] != Checksum(bytes).ToString()) { Info = "suma de control incorrecta"; return; }
                Bytes = bytes;
                IsValid = true;
                Info = "ok " + PolicyData.Header;
            }
            catch (Exception e)
            {
                Info = "error al cargar: " + e.GetType().Name;
            }
        }

        public static uint Checksum(byte[] b)
        {
            uint h = 2166136261;
            for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= 16777619; }
            return h;
        }

        /// <summary>Escribe en pi la distribución CFR de la clave. false si no hay política o no sirve en este estado.</summary>
        public static bool TryGet(int key, PlayerObservation o, double[] pi)
        {
            if (!IsValid || key < 0 || key >= StateEncoder.NumKeys) return false;
            int q0 = Bytes[2 * key], q1 = Bytes[2 * key + 1];
            if (q0 == 255 && q1 == 255) return false;
            pi[0] = q0 / 255.0;
            pi[1] = q1 / 255.0;
            pi[2] = Math.Max(0, 1 - pi[0] - pi[1]);
            ActionValidator.Mask(pi, o);
            return pi[0] + pi[1] + pi[2] > 1e-9;
        }
    }

    /// <summary>Información de la política embebida (para el laboratorio).</summary>
    public static string PolicyInfo => CfrPolicy.Info;

    // =====================================================================================
    // Control del tiempo (arquitectura §11 y §12, TimeBudgetController).
    // El reloj arranca al entrar en GetAction; cada módulo pregunta si aún queda presupuesto.
    // =====================================================================================
    private sealed class TimeBudgetController
    {
        private readonly Stopwatch _sw = new Stopwatch();

        /// <summary>Multiplicador para simular una máquina más lenta (Arena en Mono). 1 = tiempo real local.</summary>
        public double Scale = 1.0;

        public void Start() => _sw.Restart();

        public double ElapsedMs => _sw.Elapsed.TotalMilliseconds;

        /// <summary>true si todavía no se ha llegado a limiteMs (comparando con el tiempo escalado).</summary>
        public bool HasTime(double limiteMs) => ElapsedMs * Scale < limiteMs;
    }

    // =====================================================================================
    // Telemetría de la última decisión (lectura desde el laboratorio).
    // =====================================================================================
    public struct DecisionTrace
    {
        public int HandNumber;
        public Street Street;
        public double PiFold, PiCall, PiRaise;
        public ActionType Action;
        public double Ms;
        public bool Fallback;
        public string Source;   // "fallback-policy" | "safe-action" | "cfr", más "+v3" / "+v3b" si el explotador actuó
        public int Key;
        public double Lambda, EvC, EvR;
        public string Mode;
        public int Sims;
    }

    /// <summary>
    /// V3b: parte de la política base y sube o baja cada acción según su ventaja de EV, de forma limitada.
    /// π(a) ∝ max(π_base(a), 0.02) · exp(clamp(β · (EV(a) − EV(π_base)) / escala, −3, 3)), con β = λ/(1−λ).
    /// </summary>
    private void MixV3b(PlayerObservation o, double lambda)
    {
        double[] ev = { _exp.EvF, _exp.EvC, double.IsNegativeInfinity(_exp.EvR) ? 0 : _exp.EvR };
        double evBase = _pi[0] * ev[0] + _pi[1] * ev[1] + _pi[2] * ev[2];
        double escala = Math.Max(0.1 * o.BetSize, _exp.SeC + _exp.SeR);
        double beta = lambda / (1 - lambda);
        double s = 0;
        for (int i = 0; i < 3; i++)
        {
            double x = Math.Max(-3, Math.Min(3, beta * (ev[i] - evBase) / escala));
            _pi[i] = Math.Max(_pi[i], 0.02) * Math.Exp(x);
        }
        ActionValidator.Mask(_pi, o);
        for (int i = 0; i < 3; i++) s += _pi[i];
        if (s > 0) for (int i = 0; i < 3; i++) _pi[i] /= s;
    }

    /// <summary>Pone a 0 las probabilidades menores que tau y renormaliza (reduce ruido de poco entrenamiento).</summary>
    private static void Purify(double[] pi, double tau)
    {
        double s = 0;
        for (int a = 0; a < 3; a++) { if (pi[a] < tau) pi[a] = 0; s += pi[a]; }
        if (s <= 0) return;
        for (int a = 0; a < 3; a++) pi[a] /= s;
    }

    public DecisionTrace LastTrace { get; private set; }

    // =====================================================================================
    // OpponentModel (Parte D): estadísticas por rival (por NOMBRE), con prior para muestras pequeñas.
    // Media con prior = (eventos + p0*k0) / (oportunidades + k0): con pocas manos se queda cerca de p0.
    // =====================================================================================
    private sealed class OppStats
    {
        public int Hands, VpipN, VpipX, PfrN, PfrX, AfN, AfX, FtbN, FtbX;
        public int FtrN, FtrX; //fold ante subida preflop
        public double Vpip => (VpipX + 0.35 * 10) / (VpipN + 10.0);
        public double Pfr => (PfrX + 0.15 * 10) / (PfrN + 10.0);
        public double Afq => (AfX + 0.35 * 10) / (AfN + 10.0);
        public double Ftb => (FtbX + 0.40 * 8) / (FtbN + 8.0);
        public double Ftr => (FtrX + 0.45 * 8) / (FtrN + 8.0);
    }

    private sealed class OpponentModel
    {
        private readonly Dictionary<string, OppStats> _stats = new Dictionary<string, OppStats>();
        private readonly bool[] _hadDecision = new bool[16], _voluntary = new bool[16], _raised = new bool[16], _facedOnStreet = new bool[16];

        public OppStats Get(string name) => name != null && _stats.TryGetValue(name, out var s) ? s : null;

        public void ObserveHand(HandSummary s)
        {
            int n = s.PlayerNames.Count;
            if (n > 16) return;
            for (int i = 0; i < n; i++) { _hadDecision[i] = _voluntary[i] = _raised[i] = _facedOnStreet[i] = false; }
            for (int seat = 0; seat < n; seat++)
            {
                if (seat == s.MySeat || s.StacksBefore[seat] == 0) continue;
                GetOrAdd(s.PlayerNames[seat]).Hands++;
            }

            Street calle = Street.Preflop;
            int maxCommit = 0;
            int preRaises = 0;
            foreach (var a in s.Actions)
            {
                if (a.Street != calle) { calle = a.Street; maxCommit = 0; for (int i = 0; i < n; i++) _facedOnStreet[i] = false; }
                int antes = a.CommittedThisStreet - a.Amount;
                bool debia = maxCommit > antes;                       // ¿tenía que pagar fichas al actuar?
                if (a.CommittedThisStreet > maxCommit) maxCommit = a.CommittedThisStreet;
                if (a.Kind == ActionKind.SmallBlind || a.Kind == ActionKind.BigBlind) continue;

                bool enfrentaSubida = a.Street == Street.Preflop && preRaises >= 1;
                if (a.Street == Street.Preflop && a.Kind == ActionKind.Raise) preRaises++;
                
                
                if (a.Seat == s.MySeat || a.Seat < 0 || a.Seat >= n) continue;
                var st = GetOrAdd(a.PlayerName);
                if (a.Street == Street.Preflop)
                {
                    _hadDecision[a.Seat] = true;
                    if (a.Kind == ActionKind.Call || a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise) _voluntary[a.Seat] = true;
                    if (a.Kind == ActionKind.Raise) _raised[a.Seat] = true;
                    if (enfrentaSubida) { st.FtrN++; if (a.Kind == ActionKind.Fold) st.FtrX++; }
                }
                else
                {
                    if (a.Kind != ActionKind.Check) { st.AfN++; if (a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise) st.AfX++; }
                    if (debia && !_facedOnStreet[a.Seat])
                    {
                        _facedOnStreet[a.Seat] = true;
                        st.FtbN++;
                        if (a.Kind == ActionKind.Fold) st.FtbX++;
                    }
                }
            }
            for (int seat = 0; seat < n; seat++)
            {
                if (seat == s.MySeat || !_hadDecision[seat]) continue;
                var st = GetOrAdd(s.PlayerNames[seat]);
                st.VpipN++; st.PfrN++;
                if (_voluntary[seat]) st.VpipX++;
                if (_raised[seat]) st.PfrX++;
            }
        }

        private OppStats GetOrAdd(string name)
        {
            if (!_stats.TryGetValue(name ?? "?", out var st)) { st = new OppStats(); _stats[name ?? "?"] = st; }
            return st;
        }
    }

    // =====================================================================================
    // Explotador (Parte D): rangos simples + Monte Carlo emparejado + EV por acción + incertidumbre MEDIDA.
    // En cada escenario simulado se calcula el resultado de CALL y de RAISE con las MISMAS cartas
    // (escenarios emparejados): así la varianza de la diferencia incluye la covarianza real (arquitectura §8).
    // =====================================================================================
    private sealed class Exploiter
    {
        private const int MaxOpp = 6;
        private readonly int[] _deck = new int[52];
        private readonly Card[] _board = new Card[5];
        private readonly int[] _oppSeat = new int[MaxOpp];
        private readonly double[] _oppT = new double[MaxOpp], _oppFtb = new double[MaxOpp], _oppC = new double[MaxOpp], _oppR = new double[MaxOpp];
        private readonly bool[] _oppBehind = new bool[MaxOpp];
        private int[] _vMe = new int[0];
        private int[,] _vOpp = new int[0, 0];
        private double[,] _sOpp = new double[0, 0];
        private double[] _xc = new double[0], _xr = new double[0], _tmp = new double[0];
        private readonly int[] _oppAgg = new int[MaxOpp], _oppCal = new int[MaxOpp];
        private readonly double[] _oppAfq = new double[MaxOpp];

        public bool Valid;
        public double EvF, EvC, EvR, SeC, SeR, Confidence, Lambda, Lcb, DeltaEv;
        public int Sims;
        public readonly double[] PiExp = new double[3];

        /// <summary>Calcula EV por acción, π_EXP y λ. base = política CFR (o de respaldo); kappa = factor del torneo.</summary>
        public void Evaluate(PlayerObservation o, OpponentModel model, double[] basePi, Random rng, TimeBudgetController budget, Params p, double kappa)
        {
            Valid = false; Lambda = 0; Sims = 0;
            int n = o.Players.Count, me = o.Seat;
            int nb = o.Board.Count;
            double B = o.BetSize, P = o.Pot, C = o.ToCall, R = o.RaiseCost;
            bool canFold = o.ToCall > 0, canRaise = o.CanRaise;
            if (o.ActionHistory == null || o.ActionHistory.Count == 0) return;

            // 1) Rivales activos, su rango simple y su confianza.
            int k = 0; double confSum = 0; int conInfo = 0;
            int start = o.Street == Street.Preflop ? (o.BigBlindSeat + 1) % n : (o.ButtonSeat + 1) % n;
            int myOrder = (me - start + n) % n;
            int myAfterRaise = o.MyCommittedThisStreet + o.RaiseCost;
            for (int j = 0; j < n && k < MaxOpp; j++)
            {
                var pl = o.Players[j];
                if (j == me || !pl.IsActive) continue;
                var st = model.Get(pl.Name);
                int hands = st == null ? 0 : st.Hands;
                if (hands >= p.ExpMinHands) conInfo++;
                confSum += hands / (hands + p.Kc);
                bool raised = false, called = false;
                foreach (var a in o.ActionHistory)
                {
                    if (a.Seat != j || a.Street != Street.Preflop) continue;
                    if (a.Kind == ActionKind.Raise) raised = true;
                    else if (a.Kind == ActionKind.Call) called = true;
                }
                double vpip = st == null ? 0.35 : st.Vpip, pfr = st == null ? 0.15 : st.Pfr;
                _oppSeat[k] = j;
                _oppT[k] = raised ? pfr : (called ? vpip : 1.01);      // umbral de percentil preflop de su rango
                _oppFtb[k] = st == null ? 0.40 : (p.ExploiterPlus && o.Street == Street.Preflop ? st.Ftr : st.Ftb);
                int agg = 0, cal = 0;
                foreach (var a in o.ActionHistory)
                {
                    if (a.Seat != j || a.Street == Street.Preflop) continue;
                    if (a.Kind == ActionKind.Bet || a.Kind == ActionKind.Raise) agg++;
                    else if (a.Kind == ActionKind.Call) cal++;
                }
                _oppAgg[k] = agg; _oppCal[k] = cal;
                _oppAfq[k] = st == null ? 0.35 : st.Afq;
                _oppBehind[k] = !pl.IsAllIn && (j - start + n) % n > myOrder;
                _oppC[k] = Math.Max(0, Math.Min(pl.Stack, o.CurrentBet - pl.CommittedThisStreet));
                _oppR[k] = Math.Max(0, Math.Min(pl.Stack, myAfterRaise - pl.CommittedThisStreet));
                k++;
            }
            if (k == 0 || conInfo == 0) return;                          // nadie con datos: no hay nada que explotar
            Confidence = confSum / k;

            // 2) Monte Carlo: guardar valores de cada escenario.
            int maxN = p.ExpMaxSims;
            if (_vMe.Length < maxN) { _vMe = new int[maxN]; _vOpp = new int[maxN, MaxOpp]; _sOpp = new double[maxN, MaxOpp]; _xc = new double[maxN]; _xr = new double[maxN]; _tmp = new double[maxN]; }
            int nd = 0;
            var used = new bool[52];
            used[o.HoleCards[0].Index] = used[o.HoleCards[1].Index] = true;
            for (int i = 0; i < nb; i++) { used[o.Board[i].Index] = true; _board[i] = o.Board[i]; }
            for (int c = 0; c < 52; c++) if (!used[c]) _deck[nd++] = c;
            Card h0 = o.HoleCards[0], h1 = o.HoleCards[1];

            int N = 0;
            while (N < maxN && budget.HasTime(p.ExpDeadlineMs))
            {
                int pos = 0;
                for (int r = 0; r < k; r++)
                {
                    int c1 = 0, c2 = 0;
                    for (int tries = 0; tries < 4; tries++)
                    {
                        Swap(rng, pos, nd); Swap(rng, pos + 1, nd);
                        c1 = _deck[pos]; c2 = _deck[pos + 1]; pos += 2;
                        double pct = CardUtil.PreflopPct[CardUtil.Canonical169(new Card(c1), new Card(c2))];
                        double acc = _oppT[r] > 1 ? 1 : Math.Max(0.05, 1 / (1 + Math.Exp((pct - _oppT[r]) / 0.08)));

                        if (p.RangePost && nb > 0 && (_oppAgg[r] > 0 || _oppCal[r] > 0))
                        {
                            double sc = (7463 - HandEvaluator.ToEquivalenceClass(
                                HandEvaluator.Evaluate(new Card(c1), new Card(c2), o.Board))) / 7462.0;
                            double w = Math.Max(0.2, 1 - _oppAfq[r]);                 // agresor habitual = señal débil
                            double like = _oppAgg[r] > 0
                                ? 0.15 + 0.85 * sc * sc                               // apostó o subió: sesga a manos fuertes
                                : 0.35 + 0.65 * (1 - Math.Abs(sc - 0.55) / 0.55);     // solo pagó: sesga a manos medias
                            acc = Math.Max(0.03, acc * ((1 - w) + w * like));
                        }
                        
                        if (rng.NextDouble() < acc) break;
                        if (pos + 2 * (k - r) + (5 - nb) + 2 > nd) break;
                    }
                    var a1 = new Card(c1); var a2 = new Card(c2);
                    _sOpp[N, r] = nb == 0 ? 1 - CardUtil.PreflopPct[CardUtil.Canonical169(a1, a2)]
                                          : (7463 - HandEvaluator.ToEquivalenceClass(HandEvaluator.Evaluate(a1, a2, o.Board))) / 7462.0;
                    _deck[pos - 2] = c1; _deck[pos - 1] = c2;
                    _oppCards1[r] = a1; _oppCards2[r] = a2;
                }
                for (int i = nb; i < 5; i++) { Swap(rng, pos, nd); _board[i] = new Card(_deck[pos++]); }
                _vMe[N] = HandEvaluator.Evaluate(h0, h1, _board);
                for (int r = 0; r < k; r++) _vOpp[N, r] = HandEvaluator.Evaluate(_oppCards1[r], _oppCards2[r], _board);
                N++;
            }
            Sims = N;
            if (N < 100) return;

            // 3) Umbral de continuación de cada rival: se retira el FTB más débil de su rango simulado.
            var theta = new double[k];
            for (int r = 0; r < k; r++)
            {
                for (int s = 0; s < N; s++) _tmp[s] = _sOpp[s, r];
                Array.Sort(_tmp, 0, N);
                theta[r] = _tmp[Math.Min(N - 1, (int)(_oppFtb[r] * N))];
            }

            // 4) Resultado de CALL y RAISE en cada escenario (emparejados).
            double bFut = o.Street == Street.Preflop ? 100 : o.Street == Street.Flop ? 80 : o.Street == Street.Turn ? 40 : 0;
            double sC = 0, sR = 0;
            for (int s = 0; s < N; s++)
            {
                // CALL: los que ya actuaron siguen; los que hablan después continúan si su mano supera el umbral.
                double extraC = 0; int kC = 0; bool loseC = false; int tieC = 0;
                // RAISE: todos deben responder.
                double extraR = 0; int kR = 0; bool loseR = false; int tieR = 0;
                for (int r = 0; r < k; r++)
                {
                    bool cont = _sOpp[s, r] >= theta[r];
                    int v = _vOpp[s, r];
                    if (!_oppBehind[r] || cont)
                    {
                        kC++; if (_oppBehind[r]) extraC += _oppC[r];
                        if (v > _vMe[s]) loseC = true; else if (v == _vMe[s]) tieC++;
                    }
                    if (cont)
                    {
                        kR++; extraR += _oppR[r];
                        if (v > _vMe[s]) loseR = true; else if (v == _vMe[s]) tieR++;
                    }
                }
                double shareC = loseC ? 0 : 1.0 / (1 + tieC);
                _xc[s] = shareC * (P + C + extraC) - C + p.Chi * bFut * ((kC + 1) * shareC - 1);
                if (kR == 0) _xr[s] = P;                                  // todos se retiran ante mi subida
                else
                {
                    double shareR = loseR ? 0 : 1.0 / (1 + tieR);
                    _xr[s] = shareR * (P + R + extraR) - R + p.Chi * bFut * ((kR + 1) * shareR - 1);
                }
                sC += _xc[s]; sR += _xr[s];
            }
            EvF = 0; EvC = sC / N; EvR = canRaise ? sR / N : double.NegativeInfinity;
            SeC = StdErr(_xc, N, EvC); SeR = canRaise ? StdErr(_xr, N, EvR) : 0;

            // 5) π_EXP = softmax(EV / T); T crece con la incertidumbre (estimación + modelo).
            double T = Math.Max(0.1 * B, 0.5 * (SeC + SeR)) + (1 - Confidence) * 0.5 * B;
            double mx = Math.Max(canFold ? 0 : double.NegativeInfinity, Math.Max(EvC, EvR));
            double e0 = canFold ? Math.Exp((0 - mx) / T) : 0, e1 = Math.Exp((EvC - mx) / T), e2 = canRaise ? Math.Exp((EvR - mx) / T) : 0;
            double z = e0 + e1 + e2;
            PiExp[0] = e0 / z; PiExp[1] = e1 / z; PiExp[2] = e2 / z;

            // 6) ΔEV = EV(π_EXP) − EV(π_base) escenario a escenario: su varianza ya incluye la covarianza.
            double d0 = PiExp[0] - basePi[0], d1 = PiExp[1] - basePi[1], d2 = PiExp[2] - basePi[2];
            double sumD = 0, sumD2 = 0;
            for (int s = 0; s < N; s++)
            {
                double ds = d1 * _xc[s] + (canRaise ? d2 * _xr[s] : 0);       // FOLD vale 0 en todos los escenarios
                sumD += ds; sumD2 += ds * ds;
            }
            DeltaEv = sumD / N;
            double varD = Math.Max(0, sumD2 / N - DeltaEv * DeltaEv);
            Lcb = DeltaEv - p.ExpZ * Math.Sqrt(varD / N);

            // 7) λ: solo crece si el límite inferior supera un margen mínimo y hay confianza en el modelo.
            double margin = p.ExpMarginFrac * B;
            if (Lcb > margin && Confidence >= p.Cmin && kappa > 0)
            {
                double x = Lcb - margin;
                Lambda = Math.Min(p.LambdaMax, p.LambdaMax * Confidence * (x / (x + 0.1 * B)) * kappa);
            }
            Valid = true;
        }

        private readonly Card[] _oppCards1 = new Card[MaxOpp], _oppCards2 = new Card[MaxOpp];

        private void Swap(Random rng, int pos, int nd)
        {
            int j = pos + rng.Next(nd - pos);
            int t = _deck[pos]; _deck[pos] = _deck[j]; _deck[j] = t;
        }

        private static double StdErr(double[] x, int n, double mean)
        {
            double s = 0;
            for (int i = 0; i < n; i++) { double d = x[i] - mean; s += d * d; }
            return Math.Sqrt(s / Math.Max(1, n - 1) / n);
        }
    }

    // =====================================================================================
    // TournamentContext T1 (Parte E): modos de riesgo según la clasificación (arquitectura §10).
    // =====================================================================================
    private sealed class TournamentContext
    {
        public string Mode = "normal";
        public double Kappa = 1, Alpha = 0;

        public void Evaluate(PlayerObservation o, Params p)
        {
            Mode = "normal"; Kappa = 1; Alpha = 0;
            var m = o.Match;
            int n = o.Players.Count;
            var chips = new List<int>();
            int mine = o.Players[o.Seat].Stack + o.Players[o.Seat].CommittedThisHand;
            for (int j = 0; j < n; j++)
            {
                var pl = o.Players[j];
                int c = pl.Stack + pl.CommittedThisHand;
                if (c > 0 || j == o.Seat) chips.Add(c);
            }
            chips.Sort((a, b) => b.CompareTo(a));
            int alive = chips.Count;
            int q = m.IsFinalTable ? 1 : (m.QualifyingSpots > 0 ? m.QualifyingSpots : Math.Max(1, Math.Min(3, alive - 1)));
            int h = m.HasHandLimit ? Math.Max(1, m.HandsRemaining) : 30;
            int rank = 1;
            for (int j = 0; j < n; j++)
                if (j != o.Seat && o.Players[j].Stack + o.Players[j].CommittedThisHand >= mine) rank++;
            double scale = p.SigmaHand * Math.Sqrt(2.0 * h);

            if (rank <= q)
            {
                double z = alive > q ? (mine - chips[q]) / scale : double.PositiveInfinity;
                if (z >= (m.IsFinalTable ? 2.5 : 2.0)) { Mode = "proteger-ventaja"; Kappa = 0; Alpha = 0.4; }
            }
            else
            {
                double u = (chips[q - 1] - mine) / scale;
                if (h <= 3) { Mode = "critica"; Alpha = -0.5; }
                else if (u >= 1.0 || h <= 8) { Mode = "recuperacion"; Alpha = -0.25; }
            }
            if (m.IsFinalTable) Mode = "final-" + Mode;
        }

        /// <summary>Ajusta el perfil de riesgo: α > 0 = más prudente (más fold, menos raise); α < 0 = más agresivo.</summary>
        public void Apply(double[] pi, double strength)
        {
            if (Alpha == 0) return;
            bool fuerte = strength >= 0.85;
            if (!(Alpha > 0 && fuerte)) pi[0] *= 1 + Alpha;
            pi[2] *= 1 - Alpha;
            if (Alpha > 0 && strength < 0.5) pi[2] = 0;                   // protegiendo ventaja: sin faroles
            double s = pi[0] + pi[1] + pi[2];
            if (s > 0) for (int a = 0; a < 3; a++) pi[a] /= s;
        }
    }

    // =====================================================================================
    // Estado de instancia (nada static mutable).
    // =====================================================================================
    private Random _rng;
    private readonly TimeBudgetController _budget = new TimeBudgetController();
    private readonly HandStrengthCalc _hs = new HandStrengthCalc();
    private readonly FallbackPolicy _fallback = new FallbackPolicy();
    private readonly double[] _pi = new double[3];
    private readonly OpponentModel _model = new OpponentModel();
    private readonly Exploiter _exp = new Exploiter();
    private readonly TournamentContext _tour = new TournamentContext();

    public AgenteHibridoV3() { }

    public void Initialize(string agentName)
    {
        // Config puede haberse asignado después del constructor (inicializador de objeto): la semilla se lee aquí.
        _rng = Config.Seed.HasValue
            ? new Random(Config.Seed.Value)
            : new Random(unchecked(Environment.TickCount * 31 + (agentName == null ? 0 : agentName.GetHashCode())));
        WarmUp();
    }

    /// <summary>Secuencia de decisión (arquitectura §13). Hoy solo están los pasos 1, 8 y 9; el resto llega por partes.</summary>
    public ActionType GetAction(PlayerObservation o)
    {
        // Paso 1: antes de pensar, fijar una acción segura que nunca está mal.
        ActionType safe = ActionType.CALL;
        try { safe = ActionValidator.SafeAction(o); } catch { }
        _budget.Start();
        _budget.Scale = Config.MonoFactor;

        try
        {
            if (_rng == null) _rng = new Random(Config.Seed ?? Environment.TickCount);

            // Paso 2: codificar el estado y pedir la política CFR; si no hay, la de respaldo (V0).
            string source = "fallback-policy";
            int key = StateEncoder.FromObservation(o, _hs, out double strength);
            bool cfr = false;
            if (Config.UseCfr)
            {
                cfr = CfrPolicy.TryGet(key, o, _pi);
                if (cfr && Config.PurifyTau > 0) Purify(_pi, Config.PurifyTau);
            }
            if (cfr) source = "cfr";
            else _fallback.Policy(o, _hs, Config, _pi);
            ActionValidator.Mask(_pi, o);
            double pf = _pi[0], pc = _pi[1], pr = _pi[2];

            // Paso 7a: contexto de torneo (decide cuánto se permite explotar: kappa).
            if (Config.UseTournament) _tour.Evaluate(o, Config);
            double kappa = Config.UseTournament ? _tour.Kappa : 1;

            // Pasos 3-6: explotador (rangos + Monte Carlo + EV + ΔEV) y mezcla V3 / V3b.
            double lambda = 0;
            if (Config.UseExploiter && _budget.HasTime(Config.TskipMs))
            {
                _exp.Evaluate(o, _model, _pi, _rng, _budget, Config, kappa);
                if (_exp.Valid && _exp.Lambda > 0)
                {
                    lambda = _exp.Lambda;
                    if (Config.MixMode == 0)
                    {
                        for (int i = 0; i < 3; i++) _pi[i] = (1 - lambda) * _pi[i] + lambda * _exp.PiExp[i];   // V3
                        source += "+v3";
                    }
                    else
                    {
                        MixV3b(o, lambda);
                        source += "+v3b";
                    }
                }
            }

            // Paso 7b: ajuste de riesgo del torneo.
            if (Config.UseTournament) _tour.Apply(_pi, strength);

            // Paso 8: filtrar por acciones legales, renormalizar y elegir.
            ActionType a = ActionValidator.Sample(_pi, o, _rng);

            LastTrace = new DecisionTrace
            {
                HandNumber = o.HandNumber, Street = o.Street, PiFold = pf, PiCall = pc, PiRaise = pr,
                Action = a, Ms = _budget.ElapsedMs, Fallback = false, Source = source, Key = key,
                Lambda = lambda, Mode = Config.UseTournament ? _tour.Mode : "off",
                EvC = _exp.Valid ? _exp.EvC : 0, EvR = _exp.Valid ? _exp.EvR : 0, Sims = _exp.Sims,
            };
            return a;   // Paso 9: devolver antes del límite.
        }
        catch
        {
            // Paso 9 (fallo controlado): la acción segura del paso 1.
            LastTrace = new DecisionTrace { Action = safe, Fallback = true, Source = "safe-action", Ms = _budget.ElapsedMs };
            return safe;
        }
    }

    public void OnHandEnd(HandSummary summary)
    {
        try
        {
            _model.ObserveHand(summary);
        }
        catch
        {
            // Nunca propagar: una excepción aquí cuenta como error del agente.
        }
    }

    /// <summary>Calentamiento JIT y de las tablas perezosas del evaluador (Initialize no está cronometrado).</summary>
    private void WarmUp()
    {
        try
        {
            var board = new[] { new Card(0), new Card(17), new Card(34) };
            _hs.Hs(new Card(51), new Card(47), board);
            _hs.DrawClass(new Card(51), new Card(47), board);
        }
        catch
        {
            // El calentamiento es opcional.
        }
    }
}