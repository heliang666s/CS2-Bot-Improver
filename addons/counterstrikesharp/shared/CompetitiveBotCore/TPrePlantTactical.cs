namespace CompetitiveBotCore;

public enum TPrePlantStage
{
    Stage,
    Probe,
    Execute,
    Split,
    Fake,
    Rotate,
}

public sealed record TPrePlantContext(
    bool BombPlanted,
    bool HasReliableSite,
    bool HasBombCarrier,
    bool CarrierIsHuman,
    bool ProbeComplete,
    bool ContactConfirmed,
    bool SiteBlocked,
    bool AggressivePack,
    int AliveT,
    int AliveCt);

public sealed record TPrePlantDecision(
    TPrePlantStage Stage,
    bool SplitRoutes,
    bool UsesSharedTarget,
    bool ShouldRotate,
    string Reason);

public static class TPrePlantTacticalPolicy
{
    public static bool ShouldMarkRouteBlocked(
        bool hasReliableSite,
        bool routeTargetsReady,
        TPrePlantStage stage = TPrePlantStage.Probe)
        => stage is not TPrePlantStage.Stage
            && hasReliableSite
            && !routeTargetsReady;

    public static IReadOnlyDictionary<int, CtGambleSite> AssignSites(
        IReadOnlyList<int> botSlots,
        TPrePlantDecision decision,
        CtGambleSite confirmedSite,
        IReadOnlyList<CtGambleSite> availableSites)
    {
        var sites = availableSites
            .Where(site => site is CtGambleSite.A or CtGambleSite.B)
            .Distinct()
            .OrderBy(site => site)
            .ToArray();
        if (sites.Length == 0)
            return botSlots.ToDictionary(slot => slot, _ => CtGambleSite.None);

        CtGambleSite target = sites
            .FirstOrDefault(site => site is CtGambleSite.A or CtGambleSite.B);
        if (confirmedSite is CtGambleSite.A or CtGambleSite.B
            && sites.Contains(confirmedSite))
        {
            target = confirmedSite;
        }

        if (decision.ShouldRotate)
        {
            CtGambleSite opposite = confirmedSite switch
            {
                CtGambleSite.A => CtGambleSite.B,
                CtGambleSite.B => CtGambleSite.A,
                _ => target,
            };
            target = sites.Contains(opposite) ? opposite : target;
        }

        return botSlots
            .Select((slot, index) => new
            {
                Slot = slot,
                Site = !decision.ShouldRotate
                    && decision.SplitRoutes
                    && sites.Length > 1
                    ? sites[index % sites.Length]
                    : target,
            })
            .ToDictionary(entry => entry.Slot, entry => entry.Site);
    }

    public static TPrePlantDecision Resolve(TPrePlantContext context)
    {
        if (context.BombPlanted)
            return Decision(TPrePlantStage.Rotate, true, false, true, "bomb-planted");
        if (context.SiteBlocked)
            return Decision(TPrePlantStage.Rotate, true, false, true, "site-blocked-or-contact-lost");
        if (!context.HasReliableSite || !context.HasBombCarrier)
            return Decision(TPrePlantStage.Stage, true, false, false, "stage-for-site-or-bomb");
        if (!context.ProbeComplete)
            return Decision(TPrePlantStage.Probe, true, false, false, "probe-before-commit");
        if (context.AggressivePack && !context.ContactConfirmed)
            return Decision(TPrePlantStage.Fake, false, true, false, "explicit-aggressive-pack-fake");
        if (context.ContactConfirmed)
            return Decision(TPrePlantStage.Execute, false, context.AggressivePack, false, "confirmed-contact-execute");
        if (context.CarrierIsHuman)
            return Decision(TPrePlantStage.Split, true, false, false, "human-carrier-support-split");

        return Decision(TPrePlantStage.Split, true, false, false, "default-preplant-split");
    }

    private static TPrePlantDecision Decision(
        TPrePlantStage stage,
        bool splitRoutes,
        bool sharedTarget,
        bool rotate,
        string reason)
        => new(stage, splitRoutes, sharedTarget, rotate, reason);
}
