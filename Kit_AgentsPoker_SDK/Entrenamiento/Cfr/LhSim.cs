using System;
using System.Collections.Generic;
using AgentsPoker.Core;

// =====================================================================================================
// Simulador propio de Limit Hold'em para entrenar ES-MCCFR (arquitectura §3). No se entrega.
//
// Tratamiento de stacks (arquitectura §3/§15): en entrenamiento todos los stacks son profundos (>= 480,
// el máximo que se puede poner en una mano), así que el all-in es IMPOSIBLE dentro del simulador.
// Medido con `bash entrenar.sh medir-allin`: 0 all-in en pools realistas. En la competición el motor real
// resuelve cualquier all-in; la clave del agente no usa el stack, y el validador final filtra por CanRaise.
// =====================================================================================================

/// <summary>Reparto de una mano (azar presampleado) + cachés de buckets por jugador y calle.</summary>
sealed class LhDeal
{
    public int N, Button, Sb, Bb;
    public readonly Card[][] Hole = new Card[6][];
    public readonly Card[] Board = new Card[5];
    private readonly Card[][] _boardAt = { null, new Card[3], new Card[4], new Card[5] };
    public readonly int[] HandValue = new int[6];
    public readonly int[] Cls169 = new int[6];
    private readonly int[,] _bucket = new int[6, 4];
    private readonly bool[] _streetDone = new bool[4];

    private readonly int[] _deck = new int[52];
    private readonly int[] _comboVal = new int[1326];
    private readonly int[] _sorted = new int[1326];
    private readonly bool[] _dead = new bool[52];
    private readonly AgenteHibridoV2.HandStrengthCalc _hs = new AgenteHibridoV2.HandStrengthCalc();

    public LhDeal()
    {
        for (int i = 0; i < 6; i++) Hole[i] = new Card[2];
    }

    /// <summary>Reparto aleatorio: n jugadores, botón dado.</summary>
    public void NewRandom(int n, int button, Random rng)
    {
        for (int i = 0; i < 52; i++) _deck[i] = i;
        int need = 2 * n + 5;
        for (int k = 0; k < need; k++)
        {
            int j = k + rng.Next(52 - k);
            int t = _deck[k]; _deck[k] = _deck[j]; _deck[j] = t;
        }
        for (int p = 0; p < n; p++) { Hole[p][0] = new Card(_deck[2 * p]); Hole[p][1] = new Card(_deck[2 * p + 1]); }
        for (int k = 0; k < 5; k++) Board[k] = new Card(_deck[2 * n + k]);
        Finish(n, button);
    }

    /// <summary>Reparto fijado (validación contra el SDK). Si el board es incompleto se rellena con cartas no usadas.</summary>
    public void SetCards(int n, int button, IReadOnlyList<IReadOnlyList<Card>> holes, IReadOnlyList<Card> board, Random rng)
    {
        var used = new bool[52];
        for (int p = 0; p < n; p++)
        {
            Hole[p][0] = holes[p][0]; Hole[p][1] = holes[p][1];
            used[holes[p][0].Index] = used[holes[p][1].Index] = true;
        }
        for (int k = 0; k < board.Count; k++) { Board[k] = board[k]; used[board[k].Index] = true; }
        for (int k = board.Count; k < 5; k++)
        {
            int c;
            do c = rng.Next(52); while (used[c]);
            used[c] = true;
            Board[k] = new Card(c);
        }
        Finish(n, button);
    }

    private void Finish(int n, int button)
    {
        N = n; Button = button;
        if (n == 2) { Sb = button; Bb = (button + 1) % n; }       // heads-up: el botón pone la ciega pequeña
        else { Sb = (button + 1) % n; Bb = (button + 2) % n; }
        for (int s = 1; s <= 3; s++) for (int k = 0; k < _boardAt[s].Length; k++) _boardAt[s][k] = Board[k];
        for (int p = 0; p < n; p++)
        {
            HandValue[p] = HandEvaluator.Evaluate(Hole[p][0], Hole[p][1], Board);
            Cls169[p] = AgenteHibridoV2.CardUtil.Canonical169(Hole[p][0], Hole[p][1]);
        }
        Array.Clear(_streetDone, 0, 4);
    }

    /// <summary>Bucket de la mano del jugador en la calle (perezoso: se calcula solo si se llega a esa calle).</summary>
    public int GetBucket(int p, int street)
    {
        if (!_streetDone[street]) ComputeStreet(street);
        return _bucket[p, street];
    }

    private void ComputeStreet(int s)
    {
        if (s == 0)
        {
            for (int p = 0; p < N; p++)
                _bucket[p, 0] = AgenteHibridoV2.StateEncoder.PreflopBucket(
                    AgenteHibridoV2.CardUtil.PreflopPct[AgenteHibridoV2.CardUtil.Canonical169(Hole[p][0], Hole[p][1])]);
            _streetDone[0] = true;
            return;
        }
        Card[] b = _boardAt[s];
        int nb = b.Length;
        Array.Clear(_dead, 0, 52);
        for (int k = 0; k < nb; k++) _dead[b[k].Index] = true;

        // Valor de TODAS las manos rivales posibles sobre este board: una sola vez para todos los jugadores.
        int m = 0;
        for (int c2 = 1; c2 < 52; c2++)
        {
            if (_dead[c2]) continue;
            var card2 = new Card(c2);
            for (int c1 = 0; c1 < c2; c1++)
            {
                if (_dead[c1]) continue;
                int v = HandEvaluator.Evaluate(new Card(c1), card2, b);
                _comboVal[c2 * (c2 - 1) / 2 + c1] = v;
                _sorted[m++] = v;
            }
        }
        Array.Sort(_sorted, 0, m);
        int remaining = 52 - nb - 2;
        int tot = remaining * (remaining - 1) / 2;

        for (int p = 0; p < N; p++)
        {
            Card h0 = Hole[p][0], h1 = Hole[p][1];
            int mine = HandEvaluator.Evaluate(h0, h1, b);
            int less = LowerBound(_sorted, m, mine);
            int eq = LowerBound(_sorted, m, mine + 1) - less;
            // Quitar las combinaciones que usan mis cartas (el rival no puede tenerlas).
            for (int x = 0; x < 52; x++)
            {
                if (_dead[x]) continue;
                if (x != h0.Index) Remove(AgenteHibridoV2.CardUtil.ComboIndex(h0.Index, x), mine, ref less, ref eq);
                if (x != h0.Index && x != h1.Index) Remove(AgenteHibridoV2.CardUtil.ComboIndex(h1.Index, x), mine, ref less, ref eq);
            }
            float hs = tot == 0 ? 0.5f : (float)((less + 0.5 * eq) / tot);   // misma fórmula que HandStrengthCalc.Hs
            int draw = _hs.DrawClass(h0, h1, b);
            _bucket[p, s] = AgenteHibridoV2.StateEncoder.PostflopBucket(s, hs, draw);
        }
        _streetDone[s] = true;
    }

    private void Remove(int combo, int mine, ref int less, ref int eq)
    {
        int v = _comboVal[combo];
        if (v < mine) less--;
        else if (v == mine) eq--;
    }

    private static int LowerBound(int[] a, int n, int value)
    {
        int lo = 0, hi = n;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (a[mid] < value) lo = mid + 1; else hi = mid; }
        return lo;
    }
}

/// <summary>Estado de la mano. Operaciones del PDF §3: Init (CreateInitialState), CopyFrom (CloneState),
/// LegalMask (GetLegalActions), Apply (ApplyAction), Terminal (IsTerminal), Utility (EvaluateTerminalUtility),
/// Key (GetInformationState), ToAct (GetCurrentPlayer). El azar se presamplea en LhDeal (SampleChanceEvent).</summary>
sealed class LhState
{
    public LhDeal D;
    public int Street, ToAct, Bets, CurrentBet, NumActive;
    public bool Terminal;
    public readonly int[] CommStreet = new int[6], CommHand = new int[6];
    public readonly bool[] Folded = new bool[6], Acted = new bool[6], AggThis = new bool[6];
    public readonly int[] LastAggr = new int[4];   // último Bet/Raise de cada calle (-1 = nadie)

    private int Inc => Street < 2 ? 20 : 40;

    public void Init(LhDeal d)
    {
        D = d;
        int n = d.N;
        Street = 0; Terminal = false; NumActive = n;
        for (int i = 0; i < 6; i++) { CommStreet[i] = CommHand[i] = 0; Folded[i] = Acted[i] = AggThis[i] = false; }
        for (int s = 0; s < 4; s++) LastAggr[s] = -1;
        CommStreet[d.Sb] = CommHand[d.Sb] = 10;
        CommStreet[d.Bb] = CommHand[d.Bb] = 20;
        CurrentBet = 20;
        Bets = 1;                       // la BB cuenta como la apuesta #1
        ToAct = (d.Bb + 1) % n;          // heads-up: es el botón (SB)
    }

    public void CopyFrom(LhState o)
    {
        D = o.D; Street = o.Street; ToAct = o.ToAct; Bets = o.Bets; CurrentBet = o.CurrentBet;
        NumActive = o.NumActive; Terminal = o.Terminal;
        Array.Copy(o.CommStreet, CommStreet, 6); Array.Copy(o.CommHand, CommHand, 6);
        Array.Copy(o.Folded, Folded, 6); Array.Copy(o.Acted, Acted, 6);
        Array.Copy(o.AggThis, AggThis, 6); Array.Copy(o.LastAggr, LastAggr, 4);
    }

    /// <summary>FOLD solo si debo fichas (nunca fold gratis); CALL siempre; RAISE si quedan apuestas en la calle.</summary>
    public void LegalMask(out bool fold, out bool raise)
    {
        fold = CurrentBet > CommStreet[ToAct];
        raise = Bets < 4;
    }

    /// <summary>a: 0 = FOLD, 1 = CALL/CHECK, 2 = BET/RAISE.</summary>
    public void Apply(int a)
    {
        int p = ToAct;
        if (a == 0)
        {
            Folded[p] = true;
            NumActive--;
            if (NumActive == 1) { Terminal = true; return; }
        }
        else if (a == 1)
        {
            int owed = CurrentBet - CommStreet[p];
            CommStreet[p] += owed; CommHand[p] += owed;
        }
        else
        {
            int newBet = CurrentBet + Inc;
            int put = newBet - CommStreet[p];
            CommStreet[p] += put; CommHand[p] += put;
            CurrentBet = newBet;
            Bets++;
            LastAggr[Street] = p;
            AggThis[p] = true;
            for (int j = 0; j < D.N; j++) Acted[j] = false;   // los demás deben volver a hablar
        }
        Acted[p] = true;

        if (RoundDone()) NextStreet();
        else ToAct = NextActive(p);
    }

    private bool RoundDone()
    {
        for (int j = 0; j < D.N; j++)
            if (!Folded[j] && (!Acted[j] || CommStreet[j] != CurrentBet)) return false;
        return true;
    }

    private void NextStreet()
    {
        Street++;
        if (Street == 4) { Terminal = true; return; }
        for (int j = 0; j < D.N; j++) { CommStreet[j] = 0; Acted[j] = false; AggThis[j] = false; }
        CurrentBet = 0; Bets = 0;
        ToAct = NextActive(D.Button);   // heads-up: la BB habla primero después del flop
    }

    private int NextActive(int p)
    {
        int j = (p + 1) % D.N;
        while (Folded[j]) j = (j + 1) % D.N;
        return j;
    }

    /// <summary>Ganancia neta del jugador en ciegas grandes (fichas ganadas − fichas puestas) / 20.</summary>
    public double Utility(int seat) => NetChips(seat) / 20.0;

    public double NetChips(int seat)
    {
        int pot = 0;
        for (int j = 0; j < D.N; j++) pot += CommHand[j];
        if (Folded[seat]) return -CommHand[seat];
        if (NumActive == 1) return pot - CommHand[seat];
        int best = int.MinValue, k = 0;
        for (int j = 0; j < D.N; j++)
        {
            if (Folded[j]) continue;
            if (D.HandValue[j] > best) { best = D.HandValue[j]; k = 1; }
            else if (D.HandValue[j] == best) k++;
        }
        return D.HandValue[seat] == best ? (double)pot / k - CommHand[seat] : -CommHand[seat];
    }

    /// <summary>Rasgos del jugador: MISMOS que StateEncoder.FromObservation en el agente.</summary>
    public AgenteHibridoV2.StateEncoder.Features Feat(int seat)
    {
        int n = D.N;
        var f = new AgenteHibridoV2.StateEncoder.Features();
        f.Street = Street;
        f.Cls169 = D.Cls169[seat];
        f.Bucket = D.GetBucket(seat, Street);
        int start = Street == 0 ? (D.Bb + 1) % n : (D.Button + 1) % n;
        int myOrder = (seat - start + n) % n;
        for (int j = 0; j < n; j++)
            if (j != seat && !Folded[j] && (j - start + n) % n > myOrder) f.After++;
        f.Opp = NumActive - 1;
        f.Bets = Bets;
        f.Facing = CurrentBet > CommStreet[seat] ? 1 : 0;
        f.IAgg = AggThis[seat] ? 1 : 0;
        int prev = Street == 0 ? -1 : LastAggr[Street - 1];
        f.Prev = prev < 0 ? 0 : (prev == seat ? 1 : 2);
        int pot = 0;
        for (int j = 0; j < n; j++) pot += CommHand[j];
        f.PotBin = pot >= AgenteHibridoV2.StateEncoder.PotBigBets * Inc ? 1 : 0;
        return f;
    }

    public int Key(int seat) => AgenteHibridoV2.StateEncoder.EncodeFine(Feat(seat));
}
