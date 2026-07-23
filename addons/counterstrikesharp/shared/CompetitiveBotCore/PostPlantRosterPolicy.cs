namespace CompetitiveBotCore;

public readonly record struct PostPlantPlayerSnapshot(
    TeamSide Side,
    bool Alive,
    bool HasDefuser);

public readonly record struct PostPlantRosterSummary(
    int AliveTerrorists,
    int AliveCounterTerrorists,
    int CtDefusers);

public static class PostPlantRosterPolicy
{
    public static PostPlantRosterSummary Summarize(
        IEnumerable<PostPlantPlayerSnapshot> players)
    {
        var roster = players.ToArray();
        return new(
            roster.Count(player => player.Side == TeamSide.Terrorist && player.Alive),
            roster.Count(player => player.Side == TeamSide.CounterTerrorist && player.Alive),
            roster.Count(player => player.Side == TeamSide.CounterTerrorist
                && player.Alive
                && player.HasDefuser));
    }
}
