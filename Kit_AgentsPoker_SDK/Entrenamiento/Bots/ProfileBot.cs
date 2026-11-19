using System;
using AgentsPoker.Core;

/// <summary>
/// Bot de laboratorio con un ESTILO de juego fijo (diseño §7.3, pool P-Perfiles). No se entrega.
/// Sirve para comprobar si el explotador aprovecha estilos distintos a los bots del kit, mientras no haya
/// agentes reales de compañeros.
///
/// Preflop: sube si su mano está en el top "Pfr"; paga si está en el top "Vpip"; si no, se retira (o pasa si es gratis).
/// Postflop: fuerza = HS^rivales; sube si fuerza >= RaiseThr (o farolea con prob. Bluff); si le apuestan paga si
/// fuerza >= CallThr; si no, se retira.
/// </summary>
sealed class ProfileBot : IPokerAgent
{
    private readonly double _vpip, _pfr, _raiseThr, _callThr, _bluff;
    private readonly Random _rng;
    private readonly AgenteHibrido.HandStrengthCalc _hs = new AgenteHibrido.HandStrengthCalc();

    public ProfileBot(string perfil, int seed)
    {
        _rng = new Random(seed);
        switch (perfil)
        {
            case "TightPasivo":   _vpip = 0.15; _pfr = 0.05; _raiseThr = 0.85; _callThr = 0.45; _bluff = 0.00; break;
            case "LoosePasivo":   _vpip = 0.60; _pfr = 0.05; _raiseThr = 0.90; _callThr = 0.05; _bluff = 0.00; break; // casi calling station
            case "TightAgresivo": _vpip = 0.20; _pfr = 0.15; _raiseThr = 0.60; _callThr = 0.35; _bluff = 0.05; break;
            case "LooseAgresivo": _vpip = 0.60; _pfr = 0.35; _raiseThr = 0.40; _callThr = 0.20; _bluff = 0.25; break; // maníaco
            case "Folder":        _vpip = 0.30; _pfr = 0.10; _raiseThr = 0.80; _callThr = 0.70; _bluff = 0.02; break; // se retira mucho
            default: throw new ArgumentException("Perfil desconocido: " + perfil);
        }
    }

    public void Initialize(string agentName) { }

    public ActionType GetAction(PlayerObservation o)
    {
        bool free = o.ToCall == 0;
        if (o.Street == Street.Preflop)
        {
            double pct = AgenteHibrido.CardUtil.PreflopPct[AgenteHibrido.CardUtil.Canonical169(o.HoleCards[0], o.HoleCards[1])];
            if (pct <= _pfr && o.CanRaise) return ActionType.RAISE;
            if (pct <= _vpip || free) return ActionType.CALL;
            return ActionType.FOLD;
        }
        int m = Math.Max(1, o.ActivePlayerCount - 1);
        double fuerza = Math.Pow(_hs.Hs(o.HoleCards[0], o.HoleCards[1], o.Board), m);
        if (o.CanRaise && (fuerza >= _raiseThr || _rng.NextDouble() < _bluff)) return ActionType.RAISE;
        if (free || fuerza >= _callThr) return ActionType.CALL;
        return ActionType.FOLD;
    }
}
