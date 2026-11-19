using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AgentsPoker.Core;
using AgentsPoker.Core.Agents;

/// <summary>
/// Pruebas unitarias del agente (criterios de "terminado" de F1/F2 en DISENO_AGENTE.md, sección 6).
/// Ejecutar: bash entrenar.sh pruebas
/// </summary>
static class PruebasAgente
{
    private static int _ok, _fail;

    public static int Ejecutar()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("Pruebas de AgenteHibrido");
        Canonical169();
        Combos();
        HsRiverExacta();
        HsOrden();
        Draws();
        TiempoHs();
        Legalidad();
        Console.WriteLine($"\nResultado: {_ok} OK, {_fail} FALLAN");
        return _fail == 0 ? 0 : 1;
    }

    private static void Check(bool cond, string nombre)
    {
        if (cond) _ok++; else { _fail++; Console.WriteLine("  FALLA: " + nombre); }
    }

    private static List<Card> C(string s) => Card.ParseMany(s).ToList();

    // ---------- CardUtil ----------
    private static void Canonical169()
    {
        var cuenta = new int[169];
        bool rango = true;
        for (int i = 0; i < 52; i++)
            for (int j = i + 1; j < 52; j++)
            {
                int c1 = AgenteHibrido.CardUtil.Canonical169(new Card(i), new Card(j));
                int c2 = AgenteHibrido.CardUtil.Canonical169(new Card(j), new Card(i));
                if (c1 < 0 || c1 >= 169 || c1 != c2) rango = false;
                else cuenta[c1]++;
            }
        Check(rango, "Canonical169 en rango y simétrica");
        bool combos = true;
        for (int c = 0; c < 169; c++) if (cuenta[c] != (c < 13 ? 6 : c < 91 ? 4 : 12)) combos = false;
        Check(combos, "Canonical169: 6/4/12 combinaciones por clase");
        var nombres = Enumerable.Range(0, 169).Select(AgenteHibrido.CardUtil.ClassName).ToList();
        Check(nombres.Distinct().Count() == 169, "ClassName únicos");
        Check(nombres[0] == "22" && nombres[12] == "AA" && nombres[13] == "32s" && nombres[90] == "AKs" && nombres[168] == "AKo",
              $"ClassName casos: {nombres[0]} {nombres[12]} {nombres[13]} {nombres[90]} {nombres[168]}");
        var ajd = C("As Jd");
        Check(AgenteHibrido.CardUtil.Canonical169(ajd[0], ajd[1]) == 166, "AJo = 166 (ejemplo 9.1 del diseño)");
        var pct = AgenteHibrido.CardUtil.PreflopPct;
        Check(pct.Length == 169, "PreflopPct tiene 169 valores");
        Check(pct[12] < pct[11] && pct[12] < 0.01 && pct.Max() > 0.999, "PreflopPct calibrada (AA la mejor, máximo 1.0)");
    }

    private static void Combos()
    {
        var vistos = new bool[1326];
        bool ok = true;
        for (int i = 0; i < 52; i++)
            for (int j = i + 1; j < 52; j++)
            {
                int k = AgenteHibrido.CardUtil.ComboIndex(j, i);
                if (k < 0 || k >= 1326 || vistos[k] || AgenteHibrido.CardUtil.ComboC1[k] != i || AgenteHibrido.CardUtil.ComboC2[k] != j) ok = false;
                else vistos[k] = true;
            }
        Check(ok && vistos.All(v => v), "ComboIndex biyectivo y coherente con ComboC1/ComboC2");
    }

    // ---------- HandStrengthCalc ----------
    private static void HsRiverExacta()
    {
        var hs = new AgenteHibrido.HandStrengthCalc();
        var rng = new Random(7);
        var deck = new Deck();
        bool ok = true;
        for (int t = 0; t < 20; t++)
        {
            deck.Shuffle(rng);
            Card h0 = deck.Draw(), h1 = deck.Draw();
            var board = new List<Card> { deck.Draw(), deck.Draw(), deck.Draw(), deck.Draw(), deck.Draw() };
            // Enumeración manual independiente (evaluador de 7 cartas con lista)
            var usadas = new HashSet<int> { h0.Index, h1.Index };
            foreach (var c in board) usadas.Add(c.Index);
            var resto = Enumerable.Range(0, 52).Where(i => !usadas.Contains(i)).Select(i => new Card(i)).ToList();
            int mio = HandEvaluator.Evaluate(new List<Card>(board) { h0, h1 });
            double gan = 0; int tot = 0;
            for (int i = 0; i < resto.Count; i++)
                for (int j = i + 1; j < resto.Count; j++)
                {
                    int v = HandEvaluator.Evaluate(new List<Card>(board) { resto[i], resto[j] });
                    gan += mio > v ? 1 : mio == v ? 0.5 : 0;
                    tot++;
                }
            double esperado = gan / tot;
            if (Math.Abs(hs.Hs(h0, h1, board) - esperado) > 1e-5) ok = false;
            if (tot != 990) ok = false;
        }
        Check(ok, "HS en river = enumeración manual (20 manos, 990 rivales)");
    }

    private static void HsOrden()
    {
        var hs = new AgenteHibrido.HandStrengthCalc();
        var board = C("Kd 8s 3c");
        var aa = C("Ah Ac");
        var bad = C("7h 2d");
        float hAA = hs.Hs(aa[0], aa[1], board), h72 = hs.Hs(bad[0], bad[1], board);
        Check(hAA > 0.85 && h72 < 0.35 && hAA > h72, $"HS(AA)={hAA:F3} > HS(72o)={h72:F3} en Kd8s3c");
        var nuts = C("Kh Kc");
        Check(hs.Hs(nuts[0], nuts[1], board) > 0.99, "Trío de K en board K83 ≈ nuts");
    }

    private static void Draws()
    {
        var hs = new AgenteHibrido.HandStrengthCalc();
        void Caso(string mano, string board, int esperado)
        {
            var m = C(mano);
            int d = hs.DrawClass(m[0], m[1], C(board));
            Check(d == esperado, $"DrawClass({mano} | {board}) = {d}, esperado {esperado}");
        }
        Caso("Ah Kh", "Qh 7h 2c", 2);   // flush draw
        Caso("9c 8d", "7s 6h 2c", 2);   // OESD
        Caso("9c 7d", "Jh 8s 5c", 2);   // doble gutshot (T o 6)
        Caso("9c 8d", "7s 5h 2c", 1);   // gutshot (6)
        Caso("Ac 2d", "3s 4h Kc", 1);   // gutshot a la rueda (5)
        Caso("9c 8d", "Ks 3h 2c", 0);   // nada
        Caso("Ah Kh", "Qh 7h 2h", 0);   // color hecho: no es proyecto
        Caso("Kc Kd", "6s 7h 8d 9c", 0);// el proyecto de escalera es solo del board
        Caso("Th 9h", "8h 7c 2h 3d", 2);// turn: flush draw + OESD
        Caso("Ah Kh", "Qh 7h 2c 3s 9d", 0); // river: siempre 0
    }

    private static void TiempoHs()
    {
        var hs = new AgenteHibrido.HandStrengthCalc();
        var rng = new Random(3);
        var deck = new Deck();
        var sw = Stopwatch.StartNew();
        const int n = 2000;
        for (int t = 0; t < n; t++)
        {
            deck.Shuffle(rng);
            Card h0 = deck.Draw(), h1 = deck.Draw();
            var board = new[] { deck.Draw(), deck.Draw(), deck.Draw() };
            hs.Hs(h0, h1, board);
        }
        double ms = sw.Elapsed.TotalMilliseconds / n;
        Console.WriteLine($"  HS en flop: {ms * 1000:F0} µs por llamada");
        Check(ms < 0.3, $"HS en flop < 0,3 ms (medido {ms:F3} ms)");
    }

    // ---------- Legalidad en partidas reales ----------
    private sealed class Verificador : IPokerAgent, IHandObserver
    {
        private readonly AgenteHibrido _a;
        public int FoldGratis, RaiseIlegal, Decisiones;
        public Verificador(AgenteHibrido a) { _a = a; }
        public void Initialize(string n) => _a.Initialize(n);
        public ActionType GetAction(PlayerObservation o)
        {
            var r = _a.GetAction(o);
            Decisiones++;
            if (r == ActionType.FOLD && o.ToCall == 0) FoldGratis++;
            if (r == ActionType.RAISE && !o.CanRaise) RaiseIlegal++;
            return r;
        }
        public void OnHandEnd(HandSummary s) => _a.OnHandEnd(s);
    }

    private static void Legalidad()
    {
        int errores = 0, foldGratis = 0, raiseIlegal = 0, decisiones = 0;
        for (int n = 2; n <= 6; n++)
        {
            var mesa = new PokerTable(GameConfig.Training(seed: 100 + n));
            var v = new Verificador(new AgenteHibrido { Config = new AgenteHibrido.Params { Seed = n } });
            mesa.Seat(v, "Yo");
            var pool = new Func<IPokerAgent>[] { () => new SimpleHeuristicAgent(), () => new AlwaysRaiseAgent(), () => new CallingStationAgent(), () => new RandomAgent(n), () => new CheckFoldAgent() };
            for (int k = 1; k < n; k++) mesa.Seat(pool[(k - 1) % pool.Length](), "R" + k);
            for (int h = 0; h < 2000; h++)
            {
                if (!mesa.CanPlayHand) mesa.ResetStacks();
                for (int s = 0; s < mesa.SeatCount; s++) if (mesa.GetStack(s) == 0) mesa.SetStack(s, 10000);
                var res = mesa.PlayHand();
                errores += res.Errors.Count(e => e.Seat == 0);
            }
            foldGratis += v.FoldGratis; raiseIlegal += v.RaiseIlegal; decisiones += v.Decisiones;
        }
        Console.WriteLine($"  Legalidad: {decisiones:N0} decisiones en mesas de 2 a 6 jugadores");
        Check(errores == 0, $"0 excepciones/timeouts del agente (hubo {errores})");
        Check(foldGratis == 0, $"Nunca FOLD con ToCall == 0 (hubo {foldGratis})");
        Check(raiseIlegal == 0, $"Nunca RAISE con CanRaise == false (hubo {raiseIlegal})");
    }
}
