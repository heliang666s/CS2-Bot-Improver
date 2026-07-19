using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using Common;
using CompetitiveBotCore;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace BotAI;

public record PatchInfo(string Name, nint Address, List<byte> OriginalBytes);

public static class BotOffsets
{
    public const int m_gameState = 0x5128;
    public const int m_isRoundOver = 0x08;
    public const int m_bombState = 0x0C;
    public const int m_plantedBombsite = 0x68;

}

[MinimumApiVersion(304)]
public class BotAI : BasePlugin
{
    public override string ModuleName => "Patches - Bot AI";
    public override string ModuleVersion => "1.8.6";
    public override string ModuleAuthor => "K4ryuu & Austin (updated by ed0ard & Misaka17032 & XBribo & AmagiReina)";
    public override string ModuleDescription =>
        "Improve and fix bots' behavior comprehensively";

    private readonly List<PatchInfo> _appliedPatches = [];
    private readonly bool _isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    private BotMatchProfile _profile = BotMatchProfile.Competitive;
    private IReadOnlyDictionary<string, (string signature, string patch, string expectedOriginal, int patchOffset)> _activePatchDefinitions =
        new Dictionary<string, (string signature, string patch, string expectedOriginal, int patchOffset)>();



    public override void Load(bool hotReload)
    {
        _profile = ProfilePolicy.Resolve(
            ProfileConfig.Load(ProfileConfig.DefaultPath(Server.GameDirectory)),
            IsEntertainmentMode());
        var allPatchDefinitions = _isLinux ? LinuxPatchDefinitions.All : WindowsPatchDefinitions.All;
        _activePatchDefinitions = _profile == BotMatchProfile.Competitive
            ? allPatchDefinitions.Where(pair => IsCompetitivePatchAllowed(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value)
            : allPatchDefinitions;

        Logger.LogInformation("Bot AI Patches loading with profile {Profile} ({Count}/{Total} patches)",
            _profile, _activePatchDefinitions.Count, allPatchDefinitions.Count);

        foreach (var name in _activePatchDefinitions.Keys)
        {
            if (ApplyPatch(name, _isLinux)) Logger.LogInformation($"{name}: applied.");
            else Logger.LogError($"{name}: FAILED.");
        }

        AddCommand("bot_improver_profile", "Set the Bot Improver profile (restart/reload required)",
            (caller, info) => SetProfileCommand(caller, info));

        RegisterEventHandler<EventPlayerSpawn>((@event, info) =>
        {
            var player = @event.Userid;
            if (player?.IsValid != true || !player.IsBot) return HookResult.Continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn?.IsValid != true
                || player.Team <= CsTeam.Spectator
                || !pawn.BotAllowActive)
                return HookResult.Continue;

            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;

            if (gameRules == null || gameRules.BombPlanted) return HookResult.Continue;

            UpdateBotBombState(pawn, player.PlayerName);
            return HookResult.Continue;
        });

        Logger.LogInformation($"Applied {_appliedPatches.Count}/{_activePatchDefinitions.Count} patches.");
    }

    public override void Unload(bool hotReload)
    {
        Logger.LogInformation("Bot AI Patches unloading...");
        foreach (var patch in _appliedPatches) RestorePatch(patch);
        _appliedPatches.Clear();
        Logger.LogInformation("All patches restored.");
    }

    // ── Patch machinery ───────────────────────────────────────────────────────

    private bool ApplyPatch(string name, bool linux = false)
    {
        try
        {
            if (!_activePatchDefinitions.TryGetValue(name, out var def)) return false;

            nint sigAddr = NativeAPI.FindSignature(GameUtils.GetModulePath("server"), def.signature);
            if (sigAddr == 0) { Logger.LogError($"'{name}': signature not found."); return false; }

            nint addr = sigAddr + def.patchOffset;
            var patchBytes = ParseHex(def.patch);
            if (patchBytes.Count == 0 || !IsValid(addr)) return false;

            var origBytes = new List<byte>();
            for (int i = 0; i < patchBytes.Count; i++)
                origBytes.Add(Marshal.ReadByte(addr, i));

            if (!ValidateOrig(name, origBytes, def.expectedOriginal))
            {
                Logger.LogError($"'{name}': byte mismatch. Expected [{def.expectedOriginal}] " +
                                $"got [{string.Join(" ", origBytes.Select(b => $"{b:X2}"))}].");
                return false;
            }

            if (!MemoryPatch.SetMemAccess(addr, patchBytes.Count)) return false;
            for (int i = 0; i < patchBytes.Count; i++) Marshal.WriteByte(addr, i, patchBytes[i]);

            _appliedPatches.Add(new PatchInfo(name, addr, origBytes));
            Logger.LogInformation($"'{name}' patched at 0x{addr:X} ({patchBytes.Count} bytes).");
            return true;
        }
        catch (Exception ex) { Logger.LogError($"'{name}': {ex.Message}"); return false; }
    }

    private void SetProfileCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (info.ArgCount < 2)
        {
            caller?.PrintToConsole($"[BotImprover] profile = {_profile.ToString().ToLowerInvariant()}");
            return;
        }

        var value = info.GetArg(1);
        var profile = ProfilePolicy.Parse(value);
        var path = ProfileConfig.DefaultPath(Server.GameDirectory);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"// Bot Improver profile\nbot_improver_profile {profile.ToString().ToLowerInvariant()}\n");
            caller?.PrintToConsole($"[BotImprover] profile saved as {profile}. Restart/reload plugins to apply.");
            Logger.LogInformation("Bot Improver profile changed to {Profile}; restart/reload required", profile);
        }
        catch (Exception ex)
        {
            caller?.PrintToConsole($"[BotImprover] failed to save profile: {ex.Message}");
            Logger.LogError(ex, "Failed to save Bot Improver profile");
        }
    }

    private static bool IsCompetitivePatchAllowed(string name)
        => name is "HasVisitedEnemySpawn"
            or "GameState_Reset"
            or "EscapeFromBomb_OnEnter_NoEquipKnife"
            or "EscapeFromBomb_OnUpdate_NoEquipKnife"
            or "EscapeFromFlames_OnEnter_NoEquipKnife"
            or "PlantBombLookAtPriorityLow"
            or "DefuseBombLookAtPriorityLow"
            or "TBot_BombsiteSearch_UseKnownPlantedSite";

    private static bool IsEntertainmentMode()
    {
        bool teammatesAreEnemies = ConVar.Find("mp_teammates_are_enemies")?.StringValue is "1" or "true";
        bool noSpread = ConVar.Find("weapon_accuracy_nospread")?.StringValue is "1" or "true";
        bool unlimitedMoney = ConVar.Find("mp_maxmoney")?.StringValue == "0";
        return teammatesAreEnemies || noSpread || unlimitedMoney;
    }

    private void RestorePatch(PatchInfo p)
    {
        try
        {
            if (!IsValid(p.Address)) return;
            if (!MemoryPatch.SetMemAccess(p.Address, p.OriginalBytes.Count)) return;
            for (int i = 0; i < p.OriginalBytes.Count; i++)
                Marshal.WriteByte(p.Address, i, p.OriginalBytes[i]);
        }
        catch (Exception ex) { Logger.LogError($"Restore '{p.Name}': {ex.Message}"); }
    }

    private bool ValidateOrig(string name, List<byte> actual, string expectedHex)
    {
        try
        {
            var tokens = expectedHex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (actual.Count != tokens.Length) return false;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i] == "?") continue;
                if (actual[i] != Convert.ToByte(tokens[i], 16)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static bool IsValid(nint addr)
    {
        if (addr == nint.Zero) return false;
        try { Marshal.ReadByte(addr); return true; }
        catch { return false; }
    }

    private static List<byte> ParseHex(string hex) =>
        [.. hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
               .Where(t => t != "?")
               .Select(t => Convert.ToByte(t, 16))];

    private bool UpdateBotBombState(CCSPlayerPawn pawn, string playerName)
    {
        try
        {
            if (pawn?.Bot?.Handle is not { } handle || handle == nint.Zero) return false;
            if (!IsValid(handle)) return false;

            nint gsPtr = handle + BotOffsets.m_gameState;
            if (!IsValid(gsPtr)) return false;
            if (Marshal.ReadByte(gsPtr + BotOffsets.m_isRoundOver) != 0) return true;

            nint bombAddr = gsPtr + BotOffsets.m_bombState;
            if (!IsValid(bombAddr)) return false;
            if (!MemoryPatch.SetMemAccess(bombAddr, sizeof(int))) return false;
            if (Marshal.ReadInt32(bombAddr) != 0) Marshal.WriteInt32(bombAddr, 0);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"UpdateBotBombState({playerName}): {ex.Message}");
            return false;
        }
    }
}
