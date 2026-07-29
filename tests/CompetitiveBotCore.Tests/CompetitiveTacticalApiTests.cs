using CompetitiveTacticalApi;

namespace CompetitiveBotCore.Tests;

public sealed class CompetitiveTacticalApiTests
{
    [Fact]
    public void StorePublishesAndCommitsOneRoundPlan()
    {
        var store = new CompetitiveRoundPlanStore();
        var plan = CreatePlan("r1:DefaultSplit:A");
        store.PublishCandidates(1, [plan]);

        Assert.Contains(plan, store.GetCandidatePlans(1));
        Assert.True(store.CommitSelectedPlan(1, plan.PlanId));
        Assert.True(store.TryGetRoundPlan(1, out var selected));
        Assert.Equal(plan.PlanId, selected.PlanId);
    }

    [Fact]
    public void RepublishingTheSameRoundAdvancesThePlanVersion()
    {
        var store = new CompetitiveRoundPlanStore();
        var plan = CreatePlan("r1:DefaultSplit:A");

        store.PublishCandidates(1, [plan]);
        var first = Assert.Single(store.GetCandidatePlans(1));

        store.PublishCandidates(
            1,
            [plan with
            {
                RoleAssignments =
                [
                    new TacticalRoleAssignmentSnapshot(
                        11,
                        "CarrierSupport",
                        0,
                        "A",
                        true,
                        true),
                ],
            }]);
        var second = Assert.Single(store.GetCandidatePlans(1));

        Assert.True(second.Version > first.Version);
    }

    [Fact]
    public void RoundStageCanBeReportedAndReadByUtilityConsumers()
    {
        var store = new CompetitiveRoundPlanStore();
        store.PublishCandidates(5, [CreatePlanForRound(5, "r5:FastExecute:A")]);

        store.ReportRoundStage(5, "Execute");

        Assert.True(store.TryGetRoundStage(5, out var stage));
        Assert.Equal("Execute", stage);
        Assert.Equal(2, store.AbiVersion);
    }

    [Fact]
    public void UtilityClaimIsIdempotentForOwnerAndExclusiveAcrossBots()
    {
        var store = new CompetitiveRoundPlanStore();
        var plan = CreatePlan("r2:FastExecute:A") with
        {
            UtilityRequests =
            [
                new TacticalUtilityRequestSnapshot(
                    "r2-smoke-1",
                    11,
                    "smoke",
                    "execute",
                    "A",
                    true),
            ],
        };
        store.PublishCandidates(2, [plan]);
        Assert.True(store.CommitSelectedPlan(2, plan.PlanId));

        Assert.True(store.TryClaimUtilityRequest("r2-smoke-1", 11));
        Assert.True(store.TryClaimUtilityRequest("r2-smoke-1", 11));
        Assert.False(store.TryClaimUtilityRequest("r2-smoke-1", 12));
    }

    [Fact]
    public void FailedUtilityResultReleasesRequestForRetry()
    {
        var store = new CompetitiveRoundPlanStore();
        var plan = CreatePlan("r3:ContactExplode:A") with
        {
            UtilityRequests =
            [
                new TacticalUtilityRequestSnapshot(
                    "r3-flash-1",
                    13,
                    "flash",
                    "contact",
                    "A",
                    true),
            ],
        };
        store.PublishCandidates(3, [plan]);
        Assert.True(store.CommitSelectedPlan(3, plan.PlanId));
        Assert.True(store.TryClaimUtilityRequest("r3-flash-1", 13));

        store.ReportUtilityResult(new TacticalUtilityResult(
            3,
            "r3-flash-1",
            13,
            TacticalUtilityOutcome.Failed,
            "engine-timeout"));

        Assert.True(store.TryClaimUtilityRequest("r3-flash-1", 13));
    }

    [Fact]
    public void FailedUtilityResultsRemainVisibleForRuntimeReplan()
    {
        var store = new CompetitiveRoundPlanStore();
        var plan = CreatePlan("r4:FastExecute:A") with
        {
            UtilityRequests =
            [
                new TacticalUtilityRequestSnapshot(
                    "r4-smoke-1",
                    14,
                    "smoke",
                    "execute",
                    "A",
                    true),
            ],
        };
        store.PublishCandidates(4, [plan]);
        Assert.True(store.CommitSelectedPlan(4, plan.PlanId));
        Assert.True(store.TryClaimUtilityRequest("r4-smoke-1", 14));

        store.ReportUtilityResult(new TacticalUtilityResult(
            4,
            "r4-smoke-1",
            14,
            TacticalUtilityOutcome.Failed,
            "no-reachable-lineup"));

        var results = store.GetRecentUtilityResults(4);
        var result = Assert.Single(results);
        Assert.Equal(TacticalUtilityOutcome.Failed, result.Outcome);
        Assert.Equal("r4-smoke-1", result.RequestId);
    }

    [Fact]
    public void TimedOutUtilityResultReleasesRequestForRetry()
    {
        var store = new CompetitiveRoundPlanStore();
        var plan = CreatePlan("r4:ContactExplode:A") with
        {
            UtilityRequests =
            [
                new TacticalUtilityRequestSnapshot(
                    "r4-flash-timeout",
                    15,
                    "flash",
                    "contact",
                    "A",
                    true),
            ],
        };
        store.PublishCandidates(4, [plan]);
        Assert.True(store.CommitSelectedPlan(4, plan.PlanId));
        Assert.True(store.TryClaimUtilityRequest("r4-flash-timeout", 15));

        store.ReportUtilityResult(new TacticalUtilityResult(
            4,
            "r4-flash-timeout",
            15,
            TacticalUtilityOutcome.TimedOut,
            "grenade-thrown-confirmation-timeout"));

        Assert.True(store.TryClaimUtilityRequest("r4-flash-timeout", 15));
        Assert.Contains(
            store.GetRecentUtilityResults(4),
            result => result.RequestId == "r4-flash-timeout"
                && result.Outcome == TacticalUtilityOutcome.TimedOut);
    }

    [Fact]
    public void StoreBoundsRetainedRoundPlans()
    {
        var store = new CompetitiveRoundPlanStore();
        for (int round = 1; round <= 8; round++)
        {
            var plan = CreatePlanForRound(round, $"r{round}:DefaultSplit:A");
            store.PublishCandidates(round, [plan]);
        }

        Assert.True(store.StoredRoundCount <= 4);
        Assert.Empty(store.GetCandidatePlans(1));
        Assert.NotEmpty(store.GetCandidatePlans(8));
    }

    [Fact]
    public void UtilityResultsReuseSnapshotUntilANewResultArrives()
    {
        var store = new CompetitiveRoundPlanStore();
        store.ReportUtilityResult(new TacticalUtilityResult(
            1,
            "r1-smoke-1",
            7,
            TacticalUtilityOutcome.Failed,
            "test"));
        var first = store.GetRecentUtilityResults(1);
        var second = store.GetRecentUtilityResults(1);

        Assert.Same(first, second);

        store.ReportUtilityResult(new TacticalUtilityResult(
            1,
            "r1-smoke-2",
            7,
            TacticalUtilityOutcome.Failed,
            "test"));

        Assert.NotSame(first, store.GetRecentUtilityResults(1));
    }

    private static CompetitiveRoundPlanSnapshot CreatePlan(string planId)
        => CreatePlanForRound(
            planId.StartsWith("r1", StringComparison.Ordinal) ? 1
                : planId.StartsWith("r2", StringComparison.Ordinal) ? 2
                : planId.StartsWith("r3", StringComparison.Ordinal) ? 3 : 4,
            planId);

    private static CompetitiveRoundPlanSnapshot CreatePlanForRound(
        int roundKey,
        string planId)
        => new(
            RoundKey: roundKey,
            Version: 1,
            PlanId: planId,
            Side: "T",
            Tactic: "DefaultSplit",
            PrimarySite: "A",
            FallbackSite: "B",
            Stage: "stage",
            RoleAssignments: Array.Empty<TacticalRoleAssignmentSnapshot>(),
            TeamUtilityRequirements: Array.Empty<TacticalUtilityRequirementSnapshot>(),
            PlayerUtilityRequirements: Array.Empty<TacticalUtilityRequirementSnapshot>(),
            UtilityRequests: Array.Empty<TacticalUtilityRequestSnapshot>(),
            PlanSeed: 1,
            IsFallback: false);
}
