namespace CompetitiveBotCore;

public enum CtEcoTactic
{
    FourPlusOneGamble,
    AggressivePack,
    MidTrap,
    CloseRangeTrap,
}

public enum CtEcoRole
{
    Crossfire,
    Information,
    PackEntry,
    PackSupport,
    MidControl,
    CloseTrap,
}

public sealed record CtEcoPlanningContext(
    string MapName,
    bool MapKnown,
    BuyPhase BuyPhase,
    BuyPhase OpponentBuyPhase,
    MatchPressure Pressure,
    IReadOnlyList<CtBotSnapshot> Bots,
    CtEcoTactic? PreviousTactic,
    CtGambleSite PreviousAttackSite,
    bool HasFlash,
    bool HasMolotov);

public sealed record CtEcoAssignment(
    int Slot,
    CtEcoRole Role,
    CtGambleSite Site,
    int PointIndex,
    bool IsEntry,
    bool KeepBackdoor);

public sealed record CtEcoPlan(
    CtEcoTactic Tactic,
    CtGambleSite Site,
    IReadOnlyList<CtEcoAssignment> Assignments,
    string Reason);

public static class CtEcoTacticalPlanner
{
    public static CtEcoPlan Plan(CtEcoPlanningContext context)
    {
        var alive = context.Bots
            .Where(bot => bot.Alive)
            .OrderByDescending(bot => bot.Aggression)
            .ThenByDescending(bot => bot.Teamwork)
            .ThenBy(bot => bot.Slot)
            .ToArray();
        if (alive.Length == 0)
        {
            return new CtEcoPlan(
                CtEcoTactic.FourPlusOneGamble,
                CtGambleSite.None,
                Array.Empty<CtEcoAssignment>(),
                "no-alive-bots");
        }

        CtEcoTactic tactic = ChooseTactic(context, alive);
        CtGambleSite site = ChooseSite(context);
        var assignments = tactic switch
        {
            CtEcoTactic.AggressivePack => BuildPackAssignments(alive, site),
            CtEcoTactic.MidTrap => BuildMidAssignments(alive, site),
            CtEcoTactic.CloseRangeTrap => BuildCloseAssignments(alive, site),
            _ => BuildFourPlusOneAssignments(alive, site),
        };

        return new CtEcoPlan(tactic, site, assignments, context.MapKnown
            ? "map-profile-tactical-plan"
            : "unknown-map-safe-four-plus-one");
    }

    private static CtEcoTactic ChooseTactic(
        CtEcoPlanningContext context,
        IReadOnlyList<CtBotSnapshot> alive)
    {
        // Unknown maps must never invent an entry route. Four-plus-one only
        // needs a resolved site anchor and independent nearby firing points.
        if (!context.MapKnown)
            return CtEcoTactic.FourPlusOneGamble;

        var candidates = new List<CtEcoTactic>(4);
        if (context.HasFlash
            && context.OpponentBuyPhase is BuyPhase.Eco or BuyPhase.Save
            && alive.Count >= 3)
            candidates.Add(CtEcoTactic.AggressivePack);
        if (alive.Count >= 4 && context.HasFlash)
            candidates.Add(CtEcoTactic.MidTrap);
        if (alive.Count <= 3 || (!context.HasFlash && !context.HasMolotov))
            candidates.Add(CtEcoTactic.CloseRangeTrap);
        candidates.Add(CtEcoTactic.FourPlusOneGamble);

        foreach (var candidate in candidates)
        {
            if (candidate != context.PreviousTactic)
                return candidate;
        }

        return CtEcoTactic.FourPlusOneGamble;
    }

    private static CtGambleSite ChooseSite(CtEcoPlanningContext context)
        => context.PreviousAttackSite switch
        {
            CtGambleSite.A => CtGambleSite.B,
            CtGambleSite.B => CtGambleSite.A,
            _ => CtGambleSite.A,
        };

    private static IReadOnlyList<CtEcoAssignment> BuildFourPlusOneAssignments(
        IReadOnlyList<CtBotSnapshot> bots,
        CtGambleSite site)
    {
        var assignments = new List<CtEcoAssignment>(bots.Count);
        int crossfireCount = Math.Min(4, Math.Max(0, bots.Count - 1));
        for (int i = 0; i < crossfireCount; i++)
        {
            assignments.Add(new CtEcoAssignment(
                bots[i].Slot,
                CtEcoRole.Crossfire,
                site,
                i,
                IsEntry: false,
                KeepBackdoor: i == 0));
        }

        for (int i = crossfireCount; i < bots.Count; i++)
        {
            assignments.Add(new CtEcoAssignment(
                bots[i].Slot,
                CtEcoRole.Information,
                site == CtGambleSite.A ? CtGambleSite.B : CtGambleSite.A,
                0,
                IsEntry: false,
                KeepBackdoor: true));
        }

        return assignments;
    }

    private static IReadOnlyList<CtEcoAssignment> BuildPackAssignments(
        IReadOnlyList<CtBotSnapshot> bots,
        CtGambleSite site)
        => bots.Select((bot, index) => new CtEcoAssignment(
                bot.Slot,
                index == 0 ? CtEcoRole.PackEntry : CtEcoRole.PackSupport,
                site,
                index,
                IsEntry: index == 0,
                KeepBackdoor: index == bots.Count - 1))
            .ToArray();

    private static IReadOnlyList<CtEcoAssignment> BuildMidAssignments(
        IReadOnlyList<CtBotSnapshot> bots,
        CtGambleSite site)
        => bots.Select((bot, index) => new CtEcoAssignment(
                bot.Slot,
                CtEcoRole.MidControl,
                site,
                index,
                IsEntry: index == 0,
                KeepBackdoor: index == bots.Count - 1))
            .ToArray();

    private static IReadOnlyList<CtEcoAssignment> BuildCloseAssignments(
        IReadOnlyList<CtBotSnapshot> bots,
        CtGambleSite site)
        => bots.Select((bot, index) => new CtEcoAssignment(
                bot.Slot,
                CtEcoRole.CloseTrap,
                site,
                index,
                IsEntry: false,
                KeepBackdoor: index == bots.Count - 1))
            .ToArray();
}

public static class CtMapProfileCatalog
{
    private readonly record struct MapTacticalProfile(
        float LaneWidth,
        float EntryDepth,
        float MidDepth,
        float CloseDepth,
        float BackdoorShift,
        float EntryAnchorFraction,
        float MidAnchorBias,
        float CloseAnchorFraction);

    private static readonly IReadOnlyDictionary<string, MapTacticalProfile> Profiles
        = new Dictionary<string, MapTacticalProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["de_ancient"] = new(1.00f, 460f, 620f, 190f, 280f, 0.42f, 0.08f, 0.18f),
            ["de_anubis"] = new(1.10f, 500f, 680f, 210f, 300f, 0.45f, 0.10f, 0.20f),
            ["de_dust2"] = new(1.25f, 560f, 760f, 230f, 340f, 0.48f, 0.06f, 0.22f),
            ["de_inferno"] = new(0.78f, 360f, 460f, 150f, 220f, 0.34f, 0.12f, 0.16f),
            ["de_mirage"] = new(1.00f, 440f, 600f, 180f, 270f, 0.40f, 0.09f, 0.18f),
            ["de_nuke"] = new(0.72f, 360f, 500f, 150f, 220f, 0.32f, 0.11f, 0.15f),
            ["de_overpass"] = new(1.18f, 540f, 760f, 220f, 360f, 0.46f, 0.07f, 0.21f),
            ["de_vertigo"] = new(0.68f, 330f, 440f, 140f, 200f, 0.30f, 0.13f, 0.14f),
        };

    private static readonly (float X, float Y)[] CrossfireOffsets =
    {
        (-150f, -80f),
        (130f, -70f),
        (-110f, 120f),
        (120f, 130f),
    };

    public static bool IsKnown(string? mapName)
        => mapName is not null && Profiles.ContainsKey(mapName);

    public static bool TryGetAnchorProfile(
        string? mapName,
        out (float EntryFraction, float MidBias, float CloseFraction) profile)
    {
        if (mapName is not null && Profiles.TryGetValue(mapName, out var configured))
        {
            profile = (
                configured.EntryAnchorFraction,
                configured.MidAnchorBias,
                configured.CloseAnchorFraction);
            return true;
        }

        profile = default;
        return false;
    }

    public static (float X, float Y) GetTacticalOffset(
        string? mapName,
        CtEcoAssignment assignment,
        CtEcoTactic tactic)
    {
        MapTacticalProfile profile = Profiles.TryGetValue(
            mapName ?? string.Empty,
            out var configured)
            ? configured
            : new MapTacticalProfile(1f, 420f, 560f, 180f, 260f, 0.4f, 0.1f, 0.18f);
        (float X, float Y)[] points = assignment.Role switch
        {
            CtEcoRole.PackEntry => [(-profile.EntryDepth, -40f)],
            CtEcoRole.PackSupport =>
            [(-profile.EntryDepth * 0.62f, 160f),
                (-profile.EntryDepth * 0.10f, 220f),
                (profile.EntryDepth * 0.52f, 160f),
                (profile.EntryDepth * 0.72f, -20f)],
            CtEcoRole.MidControl =>
            [(-profile.MidDepth, 0f),
                (-profile.MidDepth * 0.5f, 80f),
                (0f, 0f),
                (profile.MidDepth * 0.5f, 80f),
                (profile.MidDepth, 0f)],
            CtEcoRole.CloseTrap =>
            [(-profile.CloseDepth, -100f),
                (0f, -130f),
                (profile.CloseDepth, -100f),
                (0f, 80f)],
            CtEcoRole.Information => [(0f, 0f)],
            _ => CrossfireOffsets,
        };
        var point = points[Math.Abs(assignment.PointIndex) % points.Length];
        float mapScale = profile.LaneWidth;
        if (assignment.IsEntry)
            mapScale *= 1.10f;
        if (assignment.KeepBackdoor)
        {
            point.X *= 0.65f;
            point.Y += profile.BackdoorShift;
        }

        float tacticScale = tactic switch
        {
            CtEcoTactic.CloseRangeTrap => 0.75f,
            CtEcoTactic.MidTrap => 0.90f,
            CtEcoTactic.AggressivePack => 1.05f,
            _ => 1f,
        };
        return (point.X * mapScale * tacticScale, point.Y * mapScale * tacticScale);
    }
}

public enum CtThreatEventKind
{
    Sound,
    AttackUtility,
    CtDamage,
    MultipleEnemies,
    CtDeath,
    BombFound,
}

public sealed record CtThreatEvent(
    CtThreatEventKind Kind,
    CtGambleSite Site,
    float RecordedAt,
    int Count = 1);

public enum CtThreatLevel
{
    None,
    Low,
    Medium,
    High,
    Confirmed,
}

public readonly record struct CtThreatEvaluation(
    CtThreatLevel SiteA,
    CtThreatLevel SiteB,
    CtThreatLevel Mid,
    CtGambleSite ConfirmedSite,
    float LastEventAt)
{
    public CtThreatLevel ForSite(CtGambleSite site)
        => site == CtGambleSite.B ? SiteB : SiteA;
}

public static class CtThreatPlanner
{
    public static CtThreatEvaluation Evaluate(
        IReadOnlyList<CtThreatEvent> events,
        float now,
        MatchPressure pressure,
        CtThreatEvaluation? previous = null)
    {
        float a = Score(events, CtGambleSite.A, now);
        float b = Score(events, CtGambleSite.B, now);
        float mid = Score(events, CtGambleSite.None, now);
        CtThreatLevel levelA = ToLevel(a, pressure);
        CtThreatLevel levelB = ToLevel(b, pressure);
        CtThreatLevel levelMid = ToLevel(mid, pressure);

        if (previous is { } old)
        {
            levelA = ApplyHysteresis(levelA, old.SiteA, a);
            levelB = ApplyHysteresis(levelB, old.SiteB, b);
            levelMid = ApplyHysteresis(levelMid, old.Mid, mid);
        }

        CtGambleSite confirmed = levelA >= CtThreatLevel.High && levelA >= levelB
            ? CtGambleSite.A
            : levelB >= CtThreatLevel.High
                ? CtGambleSite.B
                : CtGambleSite.None;
        return new(levelA, levelB, levelMid, confirmed,
            events.Count == 0 ? -1f : events.Max(@event => @event.RecordedAt));
    }

    private static float Score(
        IReadOnlyList<CtThreatEvent> events,
        CtGambleSite site,
        float now)
    {
        float score = 0f;
        foreach (var @event in events)
        {
            if (@event.Site != site)
                continue;
            float age = Math.Max(0f, now - @event.RecordedAt);
            if (age > 12f)
                continue;
            float decay = Math.Max(0.35f, 1f - age / 12f);
            float weight = @event.Kind switch
            {
                CtThreatEventKind.Sound => 0.25f,
                CtThreatEventKind.AttackUtility => 0.75f,
                CtThreatEventKind.CtDamage => 0.90f,
                CtThreatEventKind.MultipleEnemies => 1.20f,
                CtThreatEventKind.CtDeath => 1.50f,
                CtThreatEventKind.BombFound => 3.00f,
                _ => 0f,
            };
            score += weight * Math.Max(1, @event.Count) * decay;
        }

        return score;
    }

    private static CtThreatLevel ToLevel(float score, MatchPressure pressure)
    {
        // Pressure lowers the evidence threshold, but cannot manufacture a
        // threat when the round contains no sound, utility, damage or death.
        if (score <= 0f)
            return CtThreatLevel.None;

        // A must-win CT side should commit on weaker evidence. Subtracting
        // here made Elimination the least responsive pressure level.
        float offset = pressure == MatchPressure.Elimination ? 0.25f : 0f;
        return (score + offset) switch
        {
            >= 3.0f => CtThreatLevel.Confirmed,
            >= 1.5f => CtThreatLevel.High,
            >= 0.65f => CtThreatLevel.Medium,
            >= 0.20f => CtThreatLevel.Low,
            _ => CtThreatLevel.None,
        };
    }

    private static CtThreatLevel ApplyHysteresis(
        CtThreatLevel current,
        CtThreatLevel previous,
        float score)
    {
        if (current >= previous || previous == CtThreatLevel.None)
            return current;

        float keepThreshold = previous switch
        {
            CtThreatLevel.Confirmed => 1.8f,
            CtThreatLevel.High => 1.0f,
            CtThreatLevel.Medium => 0.45f,
            _ => 0.10f,
        };
        return score >= keepThreshold ? previous : current;
    }
}

public enum TPostPlantRole
{
    BombGuard,
    Crossfire,
    DefuseInterceptor,
    OuterRetreat,
}

public enum TPostPlantWatchTarget
{
    Bomb,
    DefuseRoute,
    OuterRoute,
}

public sealed record TPostPlantBotSnapshot(
    int Slot,
    bool Alive,
    int Health,
    float DistanceToBomb,
    float DistanceToSafeTarget = -1f);

public sealed record TPostPlantAssignment(
    int Slot,
    TPostPlantRole Role,
    int FormationIndex,
    TPostPlantWatchTarget WatchTarget = TPostPlantWatchTarget.Bomb);

public sealed record TPostPlantPlan(
    IReadOnlyList<TPostPlantAssignment> Assignments,
    bool ShouldRetreat,
    string Reason);

public static class TPostPlantPlanner
{
    public static TPostPlantPlan Plan(
        CtGambleSite bombSite,
        int bombRemainingSeconds,
        IReadOnlyList<TPostPlantBotSnapshot> bots)
    {
        var alive = bots.Where(bot => bot.Alive).ToArray();
        if (alive.Length == 0)
            return new(Array.Empty<TPostPlantAssignment>(), true, "no-alive-ts");

        var ordered = alive
            .OrderBy(bot => bot.DistanceToBomb)
            .ThenByDescending(bot => bot.Health)
            .ThenBy(bot => bot.Slot)
            .ToArray();
        var assignments = new List<TPostPlantAssignment>(ordered.Length)
        {
            new(
                ordered[0].Slot,
                TPostPlantRole.BombGuard,
                0,
                TPostPlantWatchTarget.Bomb),
        };

        if (ordered.Length > 1)
            assignments.Add(new(
                ordered[1].Slot,
                TPostPlantRole.DefuseInterceptor,
                1,
                TPostPlantWatchTarget.DefuseRoute));
        for (int index = 2; index < ordered.Length; index++)
        {
            assignments.Add(new(
                ordered[index].Slot,
                index == ordered.Length - 1
                    ? TPostPlantRole.OuterRetreat
                    : TPostPlantRole.Crossfire,
                index,
                index == ordered.Length - 1
                    ? TPostPlantWatchTarget.OuterRoute
                    : TPostPlantWatchTarget.DefuseRoute));
        }

        var safeDistances = alive
            .Select(bot => bot.DistanceToSafeTarget)
            .Where(distance => distance >= 0f && float.IsFinite(distance))
            .ToArray();
        return new(
            assignments,
            safeDistances.Length > 0
                && ShouldRetreat(
                    etaSeconds: safeDistances.Max() / 250f,
                    safetyBufferSeconds: 3f,
                    bombRemainingSeconds),
            bombSite == CtGambleSite.None ? "site-unknown" : "independent-postplant-roles");
    }

    public static bool ShouldRetreat(
        float etaSeconds,
        float safetyBufferSeconds,
        int bombRemainingSeconds)
        => etaSeconds + safetyBufferSeconds >= Math.Max(0, bombRemainingSeconds);
}
