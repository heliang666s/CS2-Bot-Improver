using System.Globalization;
using System.Text.RegularExpressions;

namespace CompetitiveBotCore.Tests;

public sealed class BotProfileConfigTests
{
    [Theory]
    [MemberData(nameof(TemplateBaselines))]
    public void BotProfilesKeepDifficultyOrderAndImproveLowAgainstItsBaseline(
        string template,
        float oldLowReactionTime,
        float oldLowAttackDelay)
    {
        BotProfileTemplate high = ReadTemplate("High", template);
        BotProfileTemplate medium = ReadTemplate("Medium", template);
        BotProfileTemplate low = ReadTemplate("Low", template);

        Assert.True(
            high.ReactionTime < medium.ReactionTime
                && medium.ReactionTime < low.ReactionTime,
            $"{template} ReactionTime must satisfy High < Medium < Low");
        Assert.True(
            high.AttackDelay <= medium.AttackDelay
                && medium.AttackDelay <= low.AttackDelay,
            $"{template} AttackDelay must satisfy High <= Medium <= Low");

        Assert.True(
            low.ReactionTime < oldLowReactionTime,
            $"{template} Low ReactionTime must improve on the old Easy baseline");
        Assert.True(
            low.AttackDelay <= oldLowAttackDelay,
            $"{template} Low AttackDelay must not regress from the old Easy baseline");
    }

    public static IEnumerable<object[]> TemplateBaselines()
    {
        yield return ["Default", 0.20f, 0.05f];
        yield return ["ProTop", 0.16f, 0.04f];
        yield return ["ProFast", 0.14f, 0.03f];
        yield return ["ProPrecise", 0.17f, 0.05f];
        yield return ["ProSlow", 0.22f, 0.07f];
        yield return ["ProSteady", 0.19f, 0.05f];
        yield return ["RankRifler", 0.10f, 0.01f];
        yield return ["RankDuelist", 0.10f, 0f];
        yield return ["RankOthers", 0.10f, 0f];
    }

    private static BotProfileTemplate ReadTemplate(string profile, string template)
    {
        string path = FindWorkspaceFile(Path.Combine(
            "overrides",
            profile,
            "botprofile.db"));
        bool inTemplate = false;
        float? reactionTime = null;
        float? attackDelay = null;
        string startPattern = template == "Default"
            ? "^\\s*Default\\s*$"
            : $"^\\s*Template\\s+{Regex.Escape(template)}(?:\\s|$)";
        Regex start = new(
            startPattern,
            RegexOptions.CultureInvariant);

        foreach (string line in File.ReadLines(path))
        {
            if (!inTemplate)
            {
                if (start.IsMatch(line))
                    inTemplate = true;
                continue;
            }

            if (line.Trim().Equals("End", StringComparison.OrdinalIgnoreCase))
                break;

            if (TryReadFloat(line, "ReactionTime", out float reaction))
                reactionTime = reaction;
            if (TryReadFloat(line, "AttackDelay", out float attack))
                attackDelay = attack;
        }

        Assert.True(
            reactionTime.HasValue && attackDelay.HasValue,
            $"Could not parse {profile}/{template} from {path}");
        return new BotProfileTemplate(reactionTime!.Value, attackDelay!.Value);
    }

    private static bool TryReadFloat(string line, string key, out float value)
    {
        value = 0f;
        Match match = Regex.Match(
            line,
            $"^\\s*{Regex.Escape(key)}\\s*=\\s*(?<value>[-+]?\\d+(?:\\.\\d+)?)",
            RegexOptions.CultureInvariant);
        return match.Success
            && float.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static string FindWorkspaceFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find workspace file '{relativePath}' from '{AppContext.BaseDirectory}'.");
    }

    private readonly record struct BotProfileTemplate(
        float ReactionTime,
        float AttackDelay);
}
