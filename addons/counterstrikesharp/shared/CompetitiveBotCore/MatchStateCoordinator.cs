namespace CompetitiveBotCore;

/// <summary>
/// The immutable match input consumed by economy and tactical planners.
/// Both sides are projected from one score pair so a consumer cannot
/// accidentally maintain a second, differently rotated scoreboard.
/// </summary>
public readonly record struct MatchStateSnapshot(
    int RoundKey,
    int RoundsPlayed,
    RoundScoreSnapshot Score,
    MatchFormatSnapshot Format,
    bool ScoreIsAuthoritative,
    TeamSide Side,
    MatchPressureResult Pressure)
{
    public bool IsOvertime => RoundSchedule.IsOvertimeRound(
        RoundsPlayed,
        Format.MaxRounds);

    public int TeamScore => Side == TeamSide.Terrorist
        ? Score.Terrorist
        : Score.CounterTerrorist;

    public int OpponentScore => Side == TeamSide.Terrorist
        ? Score.CounterTerrorist
        : Score.Terrorist;
}

/// <summary>
/// Owns score transitions at the plugin boundary. The CounterStrikeSharp
/// team_score event is authoritative; round-end events are only a bounded
/// fallback if a server build drops the score event. A fallback event is
/// accepted once per round and is replaced wholesale as soon as an authoritative
/// score arrives.
/// </summary>
public sealed class MatchStateCoordinator
{
    public static MatchStateCoordinator Shared { get; } = new(
        new MatchFormatSnapshot(24, OvertimeEnabled: true, OvertimeMaxRounds: 6));

    private MatchFormatSnapshot _format;
    private RoundScoreSnapshot _score;
    private int _roundKey;
    private int _roundsPlayed;
    private bool _scoreIsAuthoritative;
    private bool _authoritativeSeenForRound;
    private bool _fallbackApplied;

    public MatchStateCoordinator(MatchFormatSnapshot format)
    {
        _format = NormalizeFormat(format);
    }

    public RoundScoreSnapshot Score => _score;
    public bool ScoreIsAuthoritative => _scoreIsAuthoritative;
    public int RoundKey => _roundKey;
    public int RoundsPlayed => _roundsPlayed;
    public MatchFormatSnapshot Format => _format;

    public void UpdateFormat(MatchFormatSnapshot format)
        => _format = NormalizeFormat(format);

    public void BeginRound(int roundKey, int roundsPlayed)
    {
        _roundKey = Math.Max(0, roundKey);
        _roundsPlayed = Math.Max(0, roundsPlayed);
        _fallbackApplied = false;
        _authoritativeSeenForRound = false;
        _scoreIsAuthoritative = false;
    }

    /// <summary>
    /// Applies Counter-Strike's team_score event. Team 2 is Terrorist and
    /// team 3 is Counter-Terrorist; the event carries one side at a time, so
    /// the other side is deliberately preserved from the same shared pair.
    /// </summary>
    public bool ApplyTeamScoreEvent(int teamId, int score)
    {
        if (score < 0)
            return false;

        _score = teamId switch
        {
            2 => _score with { Terrorist = score },
            3 => _score with { CounterTerrorist = score },
            _ => _score,
        };
        if (teamId is not (2 or 3))
            return false;

        _scoreIsAuthoritative = true;
        _authoritativeSeenForRound = true;
        _fallbackApplied = false;
        return true;
    }

    public bool ApplyTeamScoreSnapshot(
        int terroristScore,
        int counterTerroristScore)
    {
        if (terroristScore < 0 || counterTerroristScore < 0)
            return false;

        _score = new RoundScoreSnapshot(
            terroristScore,
            counterTerroristScore);
        _scoreIsAuthoritative = true;
        _authoritativeSeenForRound = true;
        _fallbackApplied = false;
        return true;
    }

    public bool RestoreHotReloadState(
        int terroristScore,
        int counterTerroristScore,
        int roundKey,
        int roundsPlayed)
    {
        if (terroristScore < 0 || counterTerroristScore < 0)
            return false;

        _score = new RoundScoreSnapshot(
            terroristScore,
            counterTerroristScore);
        _roundKey = Math.Max(0, roundKey);
        _roundsPlayed = Math.Max(0, roundsPlayed);
        _scoreIsAuthoritative = false;
        _authoritativeSeenForRound = false;
        _fallbackApplied = false;
        return true;
    }

    public void ApplyRoundWinner(TeamSide winner)
    {
        if (_fallbackApplied || _authoritativeSeenForRound)
            return;

        _score = winner == TeamSide.Terrorist
            ? _score with { Terrorist = _score.Terrorist + 1 }
            : _score with { CounterTerrorist = _score.CounterTerrorist + 1 };
        _fallbackApplied = true;
        _scoreIsAuthoritative = false;
    }

    public void ResetForMapOrHotReload()
    {
        _score = default;
        _roundKey = 0;
        _roundsPlayed = 0;
        _scoreIsAuthoritative = false;
        _authoritativeSeenForRound = false;
        _fallbackApplied = false;
    }

    public MatchStateSnapshot Capture(TeamSide side, bool economySwing = false)
    {
        int teamScore = side == TeamSide.Terrorist
            ? _score.Terrorist
            : _score.CounterTerrorist;
        int opponentScore = side == TeamSide.Terrorist
            ? _score.CounterTerrorist
            : _score.Terrorist;
        var pressure = MatchPressurePolicy.Evaluate(
            teamScore,
            opponentScore,
            _roundsPlayed,
            _format,
            economySwing);

        return new MatchStateSnapshot(
            _roundKey,
            _roundsPlayed,
            _score,
            _format,
            _scoreIsAuthoritative,
            side,
            pressure);
    }

    private static MatchFormatSnapshot NormalizeFormat(MatchFormatSnapshot format)
        => new(
            format.MaxRounds > 0 ? format.MaxRounds : 24,
            format.OvertimeEnabled,
            format.OvertimeMaxRounds > 0 ? format.OvertimeMaxRounds : 6);
}
