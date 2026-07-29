namespace CompetitiveTacticalApi;

public interface ICompetitiveRoundPlanApi
{
    int AbiVersion { get; }

    bool TryGetRoundPlan(
        int roundKey,
        out CompetitiveRoundPlanSnapshot plan);

    IReadOnlyList<CompetitiveRoundPlanSnapshot> GetCandidatePlans(
        int roundKey);

    bool CommitSelectedPlan(int roundKey, string planId);

    bool TryClaimUtilityRequest(string requestId, int slot);

    void ReportUtilityResult(TacticalUtilityResult result);

    IReadOnlyList<TacticalUtilityResult> GetRecentUtilityResults(int roundKey);

    void ReportTacticalEvent(TacticalEventSnapshot tacticalEvent);

    bool TryGetRoundStage(int roundKey, out string stage);

    void ReportRoundStage(int roundKey, string stage);
}

public interface ICompetitiveLineupCatalogApi
{
    int AbiVersion { get; }

    bool TryGetCoverage(
        string mapName,
        string utilityType,
        string targetSite,
        out LineupCoverageSnapshot coverage);

    MapCapabilitySnapshot GetMapCapabilities(string mapName);
}

public sealed record CompetitiveRoundPlanSnapshot(
    int RoundKey,
    long Version,
    string PlanId,
    string Side,
    string Tactic,
    string PrimarySite,
    string FallbackSite,
    string Stage,
    IReadOnlyList<TacticalRoleAssignmentSnapshot> RoleAssignments,
    IReadOnlyList<TacticalUtilityRequirementSnapshot> TeamUtilityRequirements,
    IReadOnlyList<TacticalUtilityRequirementSnapshot> PlayerUtilityRequirements,
    IReadOnlyList<TacticalUtilityRequestSnapshot> UtilityRequests,
    int PlanSeed,
    bool IsFallback)
{
    public IReadOnlyList<TacticalUtilityRequestSnapshot> RequiredLineupRequests
        => UtilityRequests;
}

public sealed record TacticalRoleAssignmentSnapshot(
    int Slot,
    string Role,
    int Group,
    string Site,
    bool IsCarrierSupport,
    bool IsHumanDependent);

public sealed record TacticalUtilityRequirementSnapshot(
    int? Slot,
    string UtilityType,
    int RequiredCount,
    int OptionalCount,
    string Stage,
    string TargetSite,
    string RequestPrefix = "");

public sealed record TacticalUtilityRequestSnapshot(
    string RequestId,
    int Slot,
    string UtilityType,
    string Stage,
    string TargetSite,
    bool Required);

public enum TacticalUtilityOutcome
{
    Issued,
    Confirmed,
    Failed,
    TimedOut,
}

public sealed record TacticalUtilityResult(
    int RoundKey,
    string RequestId,
    int Slot,
    TacticalUtilityOutcome Outcome,
    string Reason);

public sealed record TacticalEventSnapshot(
    int RoundKey,
    int Slot,
    string EventType,
    string Site,
    float X,
    float Y,
    float Z,
    float RecordedAt,
    float Confidence);

public sealed record LineupCoverageSnapshot(
    string MapName,
    string UtilityType,
    string TargetSite,
    int CandidateCount,
    bool HasReachableCandidate,
    bool HasRequiredType);

public sealed record MapCapabilitySnapshot(
    string MapName,
    int BombSiteCount,
    bool HasNav,
    bool HasTwoReachableGroups,
    int CandidateLineupCount,
    int SupportedUtilityTypeCount,
    string Tier);

public sealed class CompetitiveRoundPlanStore : ICompetitiveRoundPlanApi
{
    private const int MaxRetainedRounds = 4;
    private readonly object _gate = new();
    private readonly Dictionary<int, CompetitiveRoundPlanSnapshot[]> _candidates = new();
    private readonly Dictionary<int, CompetitiveRoundPlanSnapshot> _selected = new();
    private readonly Dictionary<int, long> _publishedVersions = new();
    private readonly Dictionary<int, string> _roundStages = new();
    private readonly Dictionary<string, (int RoundKey, int Slot)> _claimedRequests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _completedRequestRounds = new(StringComparer.Ordinal);
    private readonly List<TacticalUtilityResult> _utilityResults = new();
    private readonly Dictionary<int, TacticalUtilityResult[]> _utilityResultSnapshots = new();
    private readonly List<TacticalEventSnapshot> _events = new();

    public int AbiVersion => 2;

    public int StoredRoundCount
    {
        get
        {
            lock (_gate)
                return _candidates.Count;
        }
    }

    public void PublishCandidates(
        int roundKey,
        IEnumerable<CompetitiveRoundPlanSnapshot> candidates)
    {
        var filtered = candidates
            .Where(candidate => candidate.RoundKey == roundKey)
            .GroupBy(candidate => candidate.PlanId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        lock (_gate)
        {
            long previousVersion = _publishedVersions.GetValueOrDefault(roundKey);
            long version = previousVersion == long.MaxValue
                ? 1
                : previousVersion + 1;
            _publishedVersions[roundKey] = version;
            var normalized = filtered
                .Select(candidate => candidate with { Version = version })
                .ToArray();
            _candidates[roundKey] = normalized;
            _selected.Remove(roundKey);
            _roundStages[roundKey] = normalized.FirstOrDefault()?.Stage ?? "Stage";
            ClearRoundState(roundKey);
            TrimRetainedRounds();
        }
    }

    public bool TryGetRoundPlan(
        int roundKey,
        out CompetitiveRoundPlanSnapshot plan)
    {
        lock (_gate)
            return _selected.TryGetValue(roundKey, out plan!);
    }

    public IReadOnlyList<CompetitiveRoundPlanSnapshot> GetCandidatePlans(
        int roundKey)
    {
        lock (_gate)
        {
            return _candidates.TryGetValue(roundKey, out var candidates)
                ? candidates
                : Array.Empty<CompetitiveRoundPlanSnapshot>();
        }
    }

    public bool CommitSelectedPlan(int roundKey, string planId)
    {
        if (string.IsNullOrWhiteSpace(planId))
            return false;

        lock (_gate)
        {
            if (!_candidates.TryGetValue(roundKey, out var candidates))
                return false;

            var plan = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.PlanId, planId, StringComparison.Ordinal));
            if (plan is null)
                return false;

            _selected[roundKey] = plan;
            ClearRoundState(roundKey);
            return true;
        }
    }

    public bool TryClaimUtilityRequest(string requestId, int slot)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return false;

        lock (_gate)
        {
            if (_completedRequests.Contains(requestId))
                return false;

            var plan = _selected.Values.FirstOrDefault(candidate =>
                candidate.UtilityRequests.Any(request =>
                    string.Equals(request.RequestId, requestId, StringComparison.Ordinal)));
            if (plan is null)
                return false;

            var request = plan.UtilityRequests.First(candidate =>
                string.Equals(candidate.RequestId, requestId, StringComparison.Ordinal));
            if (request.Slot != slot)
                return false;

            if (_claimedRequests.TryGetValue(requestId, out var claim))
                return claim.Slot == slot && claim.RoundKey == plan.RoundKey;

            _claimedRequests[requestId] = (plan.RoundKey, slot);
            return true;
        }
    }

    public void ReportUtilityResult(TacticalUtilityResult result)
    {
        lock (_gate)
        {
            _utilityResults.Add(result);
            if (_utilityResults.Count > 128)
                _utilityResults.RemoveRange(0, _utilityResults.Count - 128);
            _utilityResultSnapshots[result.RoundKey] = _utilityResults
                .Where(entry => entry.RoundKey == result.RoundKey)
                .ToArray();

            switch (result.Outcome)
            {
                case TacticalUtilityOutcome.Confirmed:
                    _claimedRequests.Remove(result.RequestId);
                    _completedRequests.Add(result.RequestId);
                    _completedRequestRounds[result.RequestId] = result.RoundKey;
                    break;
                case TacticalUtilityOutcome.Failed:
                case TacticalUtilityOutcome.TimedOut:
                    _claimedRequests.Remove(result.RequestId);
                    break;
            }
        }
    }

    public IReadOnlyList<TacticalUtilityResult> GetRecentUtilityResults(int roundKey)
    {
        lock (_gate)
        {
            if (_utilityResultSnapshots.TryGetValue(roundKey, out var snapshot))
                return snapshot;

            snapshot = _utilityResults
                .Where(result => result.RoundKey == roundKey)
                .ToArray();
            _utilityResultSnapshots[roundKey] = snapshot;
            return snapshot;
        }
    }

    public void ReportTacticalEvent(TacticalEventSnapshot tacticalEvent)
    {
        lock (_gate)
        {
            _events.Add(tacticalEvent);
            if (_events.Count > 128)
                _events.RemoveRange(0, _events.Count - 128);
        }
    }

    public bool TryGetRoundStage(int roundKey, out string stage)
    {
        lock (_gate)
            return _roundStages.TryGetValue(roundKey, out stage!);
    }

    public void ReportRoundStage(int roundKey, string stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
            return;

        lock (_gate)
            _roundStages[roundKey] = stage;
    }

    public IReadOnlyList<TacticalEventSnapshot> GetRecentEvents(int roundKey)
    {
        lock (_gate)
            return _events
                .Where(tacticalEvent => tacticalEvent.RoundKey == roundKey)
                .ToArray();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _candidates.Clear();
            _selected.Clear();
            _publishedVersions.Clear();
            _roundStages.Clear();
            _claimedRequests.Clear();
            _completedRequests.Clear();
            _completedRequestRounds.Clear();
            _utilityResults.Clear();
            _utilityResultSnapshots.Clear();
            _events.Clear();
        }
    }

    private void ClearRoundState(int roundKey)
    {
        foreach (var requestId in _claimedRequests
                     .Where(entry => entry.Value.RoundKey == roundKey)
                     .Select(entry => entry.Key)
                     .ToArray())
            _claimedRequests.Remove(requestId);

        foreach (var requestId in _completedRequestRounds
                     .Where(entry => entry.Value == roundKey)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _completedRequestRounds.Remove(requestId);
            _completedRequests.Remove(requestId);
        }

        _utilityResults.RemoveAll(result => result.RoundKey == roundKey);
        _utilityResultSnapshots.Remove(roundKey);
    }

    private void TrimRetainedRounds()
    {
        if (_candidates.Count <= MaxRetainedRounds)
            return;

        var retained = _candidates.Keys
            .OrderByDescending(roundKey => roundKey)
            .Take(MaxRetainedRounds)
            .ToHashSet();
        var staleRounds = _candidates.Keys
            .Where(roundKey => !retained.Contains(roundKey))
            .ToArray();

        foreach (int roundKey in staleRounds)
        {
            _candidates.Remove(roundKey);
            _selected.Remove(roundKey);
            _publishedVersions.Remove(roundKey);
            _roundStages.Remove(roundKey);
            var staleRequestIds = _claimedRequests
                .Where(entry => entry.Value.RoundKey == roundKey)
                .Select(entry => entry.Key)
                .ToArray();
            foreach (string requestId in staleRequestIds)
                _claimedRequests.Remove(requestId);
        }

        foreach (var entry in _completedRequestRounds
                     .Where(entry => !retained.Contains(entry.Value))
                     .ToArray())
        {
            _completedRequestRounds.Remove(entry.Key);
            _completedRequests.Remove(entry.Key);
        }

        _utilityResults.RemoveAll(result => !retained.Contains(result.RoundKey));
        foreach (int roundKey in _utilityResultSnapshots.Keys
                     .Where(roundKey => !retained.Contains(roundKey))
                     .ToArray())
            _utilityResultSnapshots.Remove(roundKey);
        _events.RemoveAll(entry => !retained.Contains(entry.RoundKey));
    }
}
