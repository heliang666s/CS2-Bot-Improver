using CompetitiveBotCore;

namespace CompetitiveBotCore.Tests;

public sealed class MatchStatePolicyTests
{
    [Theory]
    [InlineData(2, TeamSide.Terrorist)]
    [InlineData(3, TeamSide.CounterTerrorist)]
    public void TeamScoreRecoveryUsesManagerEntitiesAndMapsBothTeams(
        int teamNum,
        TeamSide expectedSide)
    {
        Assert.Equal("cs_team_manager", TeamScoreEntityPolicy.DesignerName);
        Assert.True(TeamScoreEntityPolicy.TryResolveSide(teamNum, out TeamSide actualSide));
        Assert.Equal(expectedSide, actualSide);
    }

    [Fact]
    public void AuthoritativeScoreIsSharedByBothTeamViews()
    {
        var state = new MatchStateCoordinator(
            new MatchFormatSnapshot(24, OvertimeEnabled: true, OvertimeMaxRounds: 6));

        state.BeginRound(roundKey: 21, roundsPlayed: 21);
        state.ApplyTeamScoreEvent(teamId: 2, score: 12);
        state.ApplyTeamScoreEvent(teamId: 3, score: 9);

        var terrorist = state.Capture(TeamSide.Terrorist);
        var counterTerrorist = state.Capture(TeamSide.CounterTerrorist);

        Assert.Equal(new RoundScoreSnapshot(12, 9), terrorist.Score);
        Assert.Equal(12, terrorist.TeamScore);
        Assert.Equal(9, terrorist.OpponentScore);
        Assert.Equal(9, counterTerrorist.TeamScore);
        Assert.Equal(12, counterTerrorist.OpponentScore);
        Assert.Equal(MatchPressure.Clinch, terrorist.Pressure.Level);
        Assert.Equal(MatchPressure.Elimination, counterTerrorist.Pressure.Level);
    }

    [Fact]
    public void TeamScoreEventMapsEngineTeamsIntoTheSharedAuthoritativeSnapshot()
    {
        var state = new MatchStateCoordinator(
            new MatchFormatSnapshot(24, OvertimeEnabled: true, OvertimeMaxRounds: 6));

        state.BeginRound(roundKey: 21, roundsPlayed: 21);
        Assert.True(state.ApplyTeamScoreEvent(teamId: 2, score: 12));
        Assert.True(state.ApplyTeamScoreEvent(teamId: 3, score: 9));

        var snapshot = state.Capture(TeamSide.Terrorist);
        Assert.True(snapshot.ScoreIsAuthoritative);
        Assert.Equal(new RoundScoreSnapshot(12, 9), snapshot.Score);
        Assert.Equal(MatchPressure.Clinch, snapshot.Pressure.Level);
    }

    [Fact]
    public void TeamScoreSnapshotRestoresBothSidesAfterAHotReload()
    {
        var state = new MatchStateCoordinator(
            new MatchFormatSnapshot(24, OvertimeEnabled: true, OvertimeMaxRounds: 6));

        Assert.True(state.RestoreHotReloadState(
            terroristScore: 12,
            counterTerroristScore: 9,
            roundKey: 29,
            roundsPlayed: 29));

        var snapshot = state.Capture(TeamSide.Terrorist);
        Assert.False(snapshot.ScoreIsAuthoritative);
        Assert.Equal(new RoundScoreSnapshot(12, 9), snapshot.Score);
        Assert.Equal(29, snapshot.RoundKey);
        Assert.Equal(29, snapshot.RoundsPlayed);
    }

    [Fact]
    public void NewRoundRequiresANewTeamScoreEventBeforeSuppressingFallback()
    {
        var state = new MatchStateCoordinator(
            new MatchFormatSnapshot(24, OvertimeEnabled: true, OvertimeMaxRounds: 6));

        state.BeginRound(roundKey: 5, roundsPlayed: 5);
        state.ApplyTeamScoreEvent(teamId: 2, score: 3);
        state.BeginRound(roundKey: 6, roundsPlayed: 6);

        Assert.False(state.ScoreIsAuthoritative);
        state.ApplyRoundWinner(TeamSide.CounterTerrorist);
        Assert.Equal(new RoundScoreSnapshot(3, 1), state.Score);
    }

    [Fact]
    public void MapResetAllowsTheNewMatchToStartAtZeroAfterAHotReload()
    {
        var state = new MatchStateCoordinator(
            new MatchFormatSnapshot(24, OvertimeEnabled: true, OvertimeMaxRounds: 6));

        state.BeginRound(roundKey: 18, roundsPlayed: 18);
        state.ApplyTeamScoreEvent(teamId: 2, score: 10);
        state.ApplyTeamScoreEvent(teamId: 3, score: 8);

        state.ResetForMapOrHotReload();
        state.BeginRound(roundKey: 0, roundsPlayed: 0);

        var snapshot = state.Capture(TeamSide.CounterTerrorist);
        Assert.Equal(new RoundScoreSnapshot(0, 0), snapshot.Score);
        Assert.False(snapshot.ScoreIsAuthoritative);
        Assert.Equal(MatchPressure.Normal, snapshot.Pressure.Level);
    }

    [Theory]
    [InlineData(12, 12, 24, MatchPressure.Normal, false)]
    [InlineData(14, 12, 26, MatchPressure.HalfClosing, false)]
    [InlineData(15, 12, 27, MatchPressure.Clinch, true)]
    [InlineData(15, 14, 29, MatchPressure.Clinch, true)]
    [InlineData(15, 15, 30, MatchPressure.Normal, false)]
    public void OvertimeSnapshotUsesTheCurrentOvertimeBlock(
        int terroristScore,
        int counterTerroristScore,
        int roundsPlayed,
        MatchPressure expectedPressure,
        bool expectedMustWin)
    {
        var state = new MatchStateCoordinator(
            new MatchFormatSnapshot(24, OvertimeEnabled: true, OvertimeMaxRounds: 6));

        state.BeginRound(roundsPlayed, roundsPlayed);
        state.ApplyTeamScoreEvent(teamId: 2, score: terroristScore);
        state.ApplyTeamScoreEvent(teamId: 3, score: counterTerroristScore);

        var snapshot = state.Capture(TeamSide.Terrorist);
        Assert.Equal(expectedPressure, snapshot.Pressure.Level);
        Assert.True(snapshot.IsOvertime);
        Assert.Equal(expectedMustWin, snapshot.Pressure.IsMustWin);
    }

    [Fact]
    public void HalfClosingRequiresAllInWithoutBecomingARealMatchPoint()
    {
        var pressure = MatchPressurePolicy.Evaluate(
            teamScore: 14,
            opponentScore: 12,
            roundsPlayed: 26,
            new MatchFormatSnapshot(24, OvertimeEnabled: true, OvertimeMaxRounds: 6),
            economySwing: false);

        Assert.Equal(MatchPressure.HalfClosing, pressure.Level);
        Assert.True(pressure.RequiresAllIn);
        Assert.False(pressure.IsMustWin);
        Assert.Equal(
            TeamBuyMode.MustWin,
            TeamBuyModePolicy.Resolve(
                pressure,
                BuyPhase.Eco,
                canReachFullBuy: false,
                forceSignal: false));
    }

    [Fact]
    public void EventFallbackIsAppliedOnceAndLaterAuthoritativeScoreWins()
    {
        var state = new MatchStateCoordinator(
            new MatchFormatSnapshot(24, OvertimeEnabled: true, OvertimeMaxRounds: 6));

        state.BeginRound(roundKey: 5, roundsPlayed: 5);
        state.ApplyRoundWinner(TeamSide.Terrorist);
        state.ApplyRoundWinner(TeamSide.Terrorist);
        Assert.Equal(1, state.Capture(TeamSide.Terrorist).TeamScore);

        state.ApplyTeamScoreEvent(teamId: 2, score: 6);
        state.ApplyTeamScoreEvent(teamId: 3, score: 5);
        Assert.Equal(6, state.Capture(TeamSide.Terrorist).TeamScore);
    }

    [Fact]
    public void AllInEcoPlanStillBuildsARealUtilityPackage()
    {
        var plan = BuyPlanner.BuildPlayerPlan(
            TeamSide.Terrorist,
            BuyPhase.Eco,
            money: 6000,
            designatedAwper: false,
            opponentEcoLikely: false,
            purchaseIntent: PurchaseIntent.AllIn);

        Assert.Equal("weapon_ak47", plan.PrimaryWeapon);
        Assert.Equal(ArmorLevel.Full, plan.ArmorLevel);
        Assert.Contains("smoke", plan.Utility);
        Assert.Contains("flash", plan.Utility);
        Assert.Contains("he", plan.Utility);
        Assert.Contains("molotov", plan.Utility);
    }

    [Theory]
    [InlineData(0.40f, 5f, FreezeBuyStage.TemporaryPlan)]
    [InlineData(3.50f, 5f, FreezeBuyStage.FinalCalibration)]
    [InlineData(4.30f, 5f, FreezeBuyStage.Execution)]
    [InlineData(5.00f, 5f, FreezeBuyStage.PostFreezeCheck)]
    public void FreezeWindowExposesAStableTwoPhaseExecutionBoundary(
        float elapsedSeconds,
        float freezeDurationSeconds,
        FreezeBuyStage expected)
    {
        Assert.Equal(
            expected,
            FreezeBuyPolicy.Resolve(elapsedSeconds, freezeDurationSeconds));
    }
}
