using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Core.Capabilities;
using RayTraceAPI;
using BotControllerApi;
using CompetitiveBotCore;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace BotState;

public class BotState : BasePlugin
{
    public override string ModuleName => "Smarter-Bot";
    public override string ModuleVersion => "1.8.0";
    public override string ModuleAuthor => "ed0ard & XBribo";
    public override string ModuleDescription => "Make bots smarter";

    private const int KnifeDefinitionIndex = 9001;
    private const float ExpandedSmokeLength = 500f;
    private const float NormalSmokeLength = 50f;
    private const float SmokeRestoreDelay = 1.0f;
    private const float TacticalContactHearingRange = 3000f;

    private BotMatchProfile _profile = BotMatchProfile.Competitive;
    private bool _isSmokeExpanded;
    private bool _isBombBeingDefused;
    private bool _isDefuseSmokeExpanded;
    private ConVar? _smokeVisibilityCvar;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _defuseSmokeTimer;

    private readonly Random _random = new Random();

    private readonly Dictionary<int, bool> _prevInAir = new();
    private readonly Dictionary<int, float> _lastForwardDir = new();
    private readonly Dictionary<int, float> _ladderExitTime = new();
    private readonly Dictionary<int, float> _lastLateralDir = new();
    private readonly Dictionary<int, float> _doorEventCooldown = new();

    private readonly Dictionary<int, float> _stuckStartTime = new();
    private readonly Dictionary<int, Vector> _stuckStartPos = new();
    private readonly Dictionary<int, bool> _stuckJumpDone = new();
    private readonly Dictionary<int, int> _stuckJumpCount = new();
    private readonly Dictionary<int, float> _stuckMaxSpeed = new();
    private readonly Dictionary<int, float> _idleStartTime = new();
    private readonly Dictionary<int, float> _lastRepathTime = new();
    private bool _isFreezeTime = false;

    private readonly HashSet<int> _hasFiredThisAttack = new();
    private readonly Dictionary<int, bool> _prevIsAttacking = new();

    private readonly Dictionary<int, bool> _cachedInAir = new();
    private readonly Dictionary<int, bool> _cachedNearLadder = new();

    // Flashbang avoidance via Ray-Trace
    private static readonly PluginCapability<CRayTraceInterface> RayTraceCap =
        new("raytrace:craytraceinterface");
    private CRayTraceInterface? _rayTrace;
    private Vector? _scratchEye;

    private readonly HashSet<int> _knifeLockedBotSlots = new();
    private object? _botController;
    private bool _eliminationHandled;

    private const float FlashFuseSeconds = 1.5f;        // CS2 flashbang fuse
    private const float FlashFovHorizDeg = 110f;        // bot horizontal cone (full angle)
    private const float FlashFovVertDeg = 90f;         // bot vertical cone (full angle)
    private readonly Dictionary<uint, float> _flashThrownAt = new();   // flash entindex -> server time first seen
    private readonly Dictionary<int, HashSet<uint>> _flashRolledByBot = new(); // bot idx  -> evaluated flashes

    // Per-(bot, flash) decision + sight window. Single source of truth consumed by OnPlayerBlind.
    private struct FlashDecision
    {
        public float FirstSeen;
        public float LastSeen;
        public float DetonateAt;
        public bool Avoided;
    }
    private readonly Dictionary<(int bot, uint flash), FlashDecision> _flashDecisions = new();
    private readonly HashSet<(int bot, uint flash)> _flashRejectLogged = new();

    // Debug logging (toggle with `css_botstate_flashdebug`)
    private bool _debugFlash = false;

    // Competitive CT tactical runtime. It only receives high-level events and
    // never writes a map coordinate or native aim/visibility state.
    private readonly CompetitiveTacticalRuntime _tacticalRuntime = new();
    private readonly Dictionary<int, CtTacticalDecision> _lastTacticalDecisions = new();
    private readonly Dictionary<int, float> _lastTacticalRepath = new();
    private readonly Dictionary<int, float> _lastTacticalGoalWrite = new();
    private readonly Dictionary<int, Vector> _lastTacticalPosition = new();
    private readonly Dictionary<int, float> _tacticalStuckSince = new();
    private readonly Dictionary<CtGambleSite, Vector> _ctGambleTargets = new();
    private readonly Dictionary<CtGambleSite, Vector> _ctEntryTargets = new();
    private readonly Dictionary<CtGambleSite, Vector> _ctCloseTargets = new();
    private Vector? _ctMidTarget;
    private Vector? _ctTacticalSpawn;
    private readonly Dictionary<int, Vector> _ctEcoTargets = new();
    private readonly Dictionary<int, CCSPlayerController> _competitiveBotPlayers = new();
    private readonly ConcurrentQueue<PlanningResult<CtEcoPlan>> _ctEcoPlanResults = new();
    private readonly LatestOnlyPlanningWorker<CtEcoPlanningContext, CtEcoPlan> _ctEcoPlanningWorker;
    private readonly ConcurrentQueue<PlanningResult<IReadOnlyList<CtTacticalDecision>>> _ctDecisionResults = new();
    private readonly LatestOnlyPlanningWorker<CtTacticalContext, IReadOnlyList<CtTacticalDecision>> _ctDecisionPlanningWorker;
    private long _ctEcoSnapshotValue;
    private long _ctEcoPlanValue;
    private long _roundSnapshotValue;
    private long _ctDecisionSnapshotValue;
    private long _ctDecisionPlanValue;
    private bool _ctDecisionDirty = true;
    private IReadOnlyList<CtTacticalDecision> _latestCtDecisions = Array.Empty<CtTacticalDecision>();
    private CtEcoTactic? _lastCtEcoTactic;
    private CtGambleSite _lastCtEcoSite;
    private readonly List<CtThreatEvent> _ctThreatEvents = new();
    private CtThreatEvaluation _ctThreatEvaluation;
    private float _nextCtThreatDecayAt;
    private readonly Dictionary<int, Vector> _tPostPlantTargets = new();
    private readonly Dictionary<int, Vector> _tPostPlantRetreatTargets = new();
    private readonly Dictionary<int, Vector> _tPostPlantWatchTargets = new();
    private readonly Dictionary<int, float> _lastTPostPlantLook = new();
    private float _nextTPostPlantTargetRefreshAt;
    private float _nextTPostPlantRetreatRefreshAt;
    private bool _tPostPlantActive;
    private bool _tPostPlantAssignmentsDirty;
    private float _bombPlantedAt;
    private float _bombBlowAt;
    private CPlantedC4? _cachedBombEntity;
    private Vector? _cachedBombOrigin;
    private bool _tPostPlantShouldRetreat;
    private CtGambleSite _tPostPlantSite;
    private TPostPlantAction? _lastTPostPlantAction;
    private string? _lastTPostPlantReason;
    private Vector? _ctRetreatTarget;
    private bool _ctGambleFallbackLogged;
    private readonly HashSet<int> _saveModeSlots = new();
    private bool _tacticalRolesInitialized;
    private bool _tacticalDebug;
    private float _nextTacticalTick;
    private int _tacticalRoundNumber;
    private int _tacticalRoundKey = -1;
    private RoundSnapshot? _latestRoundSnapshot;
    private readonly TacticalEconomyPhaseCache _tacticalEconomy = new();
    private int _terroristScore;
    private int _counterTerroristScore;

    private sealed record TacticalEconomyCheckpoint(
        int RoundKey,
        BuyPhase CtPhase,
        BuyPhase OpponentPhase);

    // CounterStrikeSharp hot reload normally keeps static plugin state alive,
    // which lets a new instance reuse the pre-purchase phase captured by the
    // previous instance. A first load in the middle of a round still falls
    // back to the current game state below.
    private static TacticalEconomyCheckpoint? _lastTacticalEconomyCheckpoint;
    private static float? _tacticalLiveStartedAt;

    public BotState()
    {
        _ctEcoPlanningWorker = new LatestOnlyPlanningWorker<CtEcoPlanningContext, CtEcoPlan>(
            CtEcoTacticalPlanner.Plan,
            result => _ctEcoPlanResults.Enqueue(result));
        _ctDecisionPlanningWorker = new LatestOnlyPlanningWorker<CtTacticalContext, IReadOnlyList<CtTacticalDecision>>(
            CtTacticalDecisionPlanner.Plan,
            result => _ctDecisionResults.Enqueue(result));
    }
    //---------------------------------------------------------------------------------------
    // Registers game events and the per-tick bot behavior listener
    public override void Load(bool hotReload)
    {
        _profile = ProfilePolicy.Resolve(
            ProfileConfig.Load(ProfileConfig.DefaultPath(Server.GameDirectory)),
            IsEntertainmentMode());
        _smokeVisibilityCvar = ConVar.Find("bot_max_visible_smoke_length");
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _terroristScore = 0;
            _counterTerroristScore = 0;
            _tacticalRoundNumber = 0;
            _tacticalRoundKey = -1;
            _latestRoundSnapshot = null;
            _competitiveBotPlayers.Clear();
            _ctEcoPlanningWorker.Reset(new PlanVersion(-1, 0, 0));
            _ctDecisionPlanningWorker.Reset(new PlanVersion(-1, 0, 0));
            while (_ctDecisionResults.TryDequeue(out var pendingDecision)) { }
            _latestCtDecisions = Array.Empty<CtTacticalDecision>();
            _tacticalRolesInitialized = false;
            _lastTacticalEconomyCheckpoint = null;
            _tacticalLiveStartedAt = null;
        });
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse);
        RegisterEventHandler<EventBombAbortdefuse>(OnBombAbortDefuse);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterEventHandler<EventBombExploded>(OnBombExploded);
        RegisterEventHandler<EventDoorOpen>(OnDoorOpen);
        RegisterEventHandler<EventDoorClose>(OnDoorClose);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventGrenadeThrown>(OnGrenadeThrown);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterListener<Listeners.OnTick>(OnTick);
    }

    // Resolves capabilities supplied by plugins after every plugin has loaded
    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try { _rayTrace = RayTraceCap.Get(); } catch { _rayTrace = null; }
        if (_rayTrace == null)
            Console.WriteLine("[Smarter-Bot] Ray-Trace not available");
        else
            _scratchEye = new Vector();

        try { _botController = BotControllerBridge.TryGet(); } catch { _botController = null; }
        if (_botController == null)
            Console.WriteLine("[Smarter-Bot] BotController API not available");

        if (hotReload && IsCompetitiveProfile())
        {
            // A reload can occur after RoundFreezeEnd or after the bomb plant
            // event. Rebuild from GameRules/entity state instead of assuming
            // the new plugin instance is in a live round.
            AddTimer(0.2f, SynchronizeTacticalAfterReload);
        }
    }

    [ConsoleCommand("css_botstate_flashdebug", "Toggle Smarter-Bot flashbang debug log")]
    [CommandHelper(minArgs: 0, usage: "[0|1]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnFlashDebugCmd(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string arg = cmd.GetArg(1);
            _debugFlash = arg == "1"
                       || arg.Equals("true", StringComparison.OrdinalIgnoreCase)
                       || arg.Equals("on", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            _debugFlash = !_debugFlash;
        }

        cmd.ReplyToCommand($"[Smarter-Bot] flash debug = {_debugFlash}");
        cmd.ReplyToCommand($"[Smarter-Bot] ray-trace loaded = {_rayTrace != null}");
        Console.WriteLine($"[Smarter-Bot] flash debug = {_debugFlash}, raytrace = {_rayTrace != null}");
    }

    [ConsoleCommand("css_bot_tactical_debug", "Toggle competitive CT tactical debug log")]
    [CommandHelper(minArgs: 0, usage: "[0|1]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnTacticalDebugCmd(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string arg = cmd.GetArg(1);
            _tacticalDebug = arg == "1"
                || arg.Equals("true", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("on", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            _tacticalDebug = !_tacticalDebug;
        }

        cmd.ReplyToCommand($"[Smarter-Bot] tactical debug = {_tacticalDebug}, profile = {_profile}");
        if (_tacticalDebug)
        {
            PrintTacticalSnapshot();
            PrintBotProfileSnapshot();
        }
    }

    // Server stdout + every connected human's console. Use only for debug-gated lines
    // so we don't spam non-debug runs.
    private static void BroadcastDebug(string msg)
    {
        Console.WriteLine(msg);
        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
            p.PrintToConsole(msg);
        }
    }

    private void PrintBotProfileSnapshot()
    {
        if (_botController == null)
        {
            BroadcastDebug("[Smarter-Bot/Profile] BotController unavailable");
            return;
        }

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
                     .Where(player => player.IsValid && player.IsBot
                         && (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist)))
        {
            if (!BotControllerBridge.TryGetProfile(_botController, player.Slot, out var profile))
                continue;

            BroadcastDebug(
                $"[Smarter-Bot/Profile] slot={player.Slot} side={player.Team} "
                + $"skill={profile.Skill:F2} aggression={profile.Aggression:F2} "
                + $"reaction={profile.ReactionTime:F3} teamwork={profile.Teamwork:F2} "
                + $"attackDelay={profile.AttackDelay:F3}");
        }
    }
    //---------------------------------------------------------------------------------------
    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo _)
    {
        if (IsCompetitiveProfile())
        {
            RecordCompetitiveHurt(@event);
            return HookResult.Continue;
        }

        try
        {
            var victim = @event.Userid;
            if (victim == null || !victim.IsValid || !victim.IsBot) return HookResult.Continue;

            if (!_isSmokeExpanded)
            {
                _isSmokeExpanded = true;
                SetSmokeVisibility(ExpandedSmokeLength);
                AddTimer(SmokeRestoreDelay, () =>
                {
                    SetSmokeVisibility(NormalSmokeLength);
                    _isSmokeExpanded = false;
                });
            }
        }
        catch
        {
            // Smoke visibility is an optional arcade/legacy enhancement.
        }
        return HookResult.Continue;
    }

    private bool IsCompetitiveProfile()
        => ProfilePolicy.IsCompetitive(_profile);

    private void RecordCompetitiveHurt(EventPlayerHurt @event)
    {
        var victim = @event.Userid;
        var attacker = @event.Attacker;
        if (victim == null || !victim.IsValid
            || attacker == null || !attacker.IsValid
            || attacker.Team != CsTeam.Terrorist
            || victim.Team != CsTeam.CounterTerrorist)
            return;

        if (attacker.PlayerPawn?.Value?.AbsOrigin is { } attackerOrigin)
            RecordCtThreat(CtThreatEventKind.CtDamage, ResolveContactSite(attackerOrigin));
        RecordTacticalContact(attacker, ContactConfidence.Medium);
    }

    private void RecordTacticalContact(
        CCSPlayerController source,
        ContactConfidence confidence,
        int? preferredListenerSlot = null)
    {
        if (!IsCompetitiveProfile() || source == null || !source.IsValid)
            return;

        var pawn = source.PlayerPawn?.Value;
        var origin = pawn?.IsValid == true ? pawn.AbsOrigin : null;
        if (origin == null)
            return;

        var contact = new CtContact(
            SourceSlot: source.Slot,
            RecordedAt: Server.CurrentTime,
            Confidence: confidence,
            X: origin.X,
            Y: origin.Y,
            Z: origin.Z,
            IsHistorical: false)
        {
            Site = ResolveContactSite(origin),
        };
        RecordCtThreat(
            confidence == ContactConfidence.Low
                ? CtThreatEventKind.Sound
                : confidence == ContactConfidence.Medium
                    ? CtThreatEventKind.CtDamage
                    : CtThreatEventKind.MultipleEnemies,
            contact.Site);

        var listeners = Utilities
            .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(player => player.IsValid
                && player.IsBot
                && !player.HasBeenControlledByPlayerThisRound
                && player.Team == CsTeam.CounterTerrorist
                && player.PawnIsAlive)
            .Select(player => new
            {
                Player = player,
                Origin = player.PlayerPawn?.Value?.AbsOrigin,
            })
            .Where(entry => entry.Origin is not null)
            .Select(entry => new
            {
                entry.Player,
                Origin = entry.Origin!,
                DistanceSquared = DistanceSquared(entry.Origin!, origin),
            })
            .ToList();

        if (preferredListenerSlot is int preferred)
        {
            listeners = listeners
                .Where(entry => entry.Player.Slot == preferred)
                .ToList();
        }
        else
        {
            float maxDistanceSquared = TacticalContactHearingRange * TacticalContactHearingRange;
            listeners = listeners
                .Where(entry => entry.DistanceSquared <= maxDistanceSquared)
                .OrderBy(entry => entry.DistanceSquared)
                .Take(5)
                .ToList();
        }

        foreach (var listener in listeners)
            _tacticalRuntime.RecordContactForBot(listener.Player.Slot, contact);
    }

    private void RecordCtThreat(CtThreatEventKind kind, CtGambleSite site, int count = 1)
    {
        if (!IsCompetitiveProfile())
            return;

        _ctThreatEvents.Add(new CtThreatEvent(kind, site, Server.CurrentTime, count));
        if (_ctThreatEvents.Count > 64)
            _ctThreatEvents.RemoveRange(0, _ctThreatEvents.Count - 64);
        _ctThreatEvaluation = CtThreatPlanner.Evaluate(
            _ctThreatEvents,
            Server.CurrentTime,
            ResolveCtMatchPressure(),
            _ctThreatEvaluation);
        _tacticalRuntime.SetThreat(_ctThreatEvaluation);
        _nextCtThreatDecayAt = Server.CurrentTime + 1f;
        InvalidateCtDecisionPlan();
    }

    private void RefreshCtThreatDecay(float now)
    {
        if (_ctThreatEvents.Count == 0 || now < _nextCtThreatDecayAt)
            return;

        var previous = _ctThreatEvaluation;
        var refreshed = CtThreatPlanner.Evaluate(
            _ctThreatEvents,
            now,
            ResolveCtMatchPressure(),
            previous);
        _nextCtThreatDecayAt = now + 1f;
        if (refreshed == previous)
            return;

        _ctThreatEvaluation = refreshed;
        _tacticalRuntime.SetThreat(refreshed);
        InvalidateCtDecisionPlan();
    }

    private void InvalidateCtDecisionPlan()
    {
        _ctDecisionDirty = true;
        _ctDecisionSnapshotValue++;
        _ctDecisionPlanValue++;
    }

    private static float DistanceSquared(Vector left, Vector right)
    {
        float dx = left.X - right.X;
        float dy = left.Y - right.Y;
        float dz = left.Z - right.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static bool IsEntertainmentMode()
    {
        bool teammatesAreEnemies = ConVar.Find("mp_teammates_are_enemies")?.StringValue is "1" or "true";
        bool noSpread = ConVar.Find("weapon_accuracy_nospread")?.StringValue is "1" or "true";
        bool unlimitedMoney = ConVar.Find("mp_maxmoney")?.StringValue == "0";
        return teammatesAreEnemies || noSpread || unlimitedMoney;
    }

    // Restores plugin-owned state before the plugin unloads
    public override void Unload(bool hotReload)
    {
        _ctEcoPlanningWorker.Dispose();
        _ctDecisionPlanningWorker.Dispose();
        ReleaseKnifeLocks();
        SetSmokeVisibility(NormalSmokeLength);
        _defuseSmokeTimer?.Kill();
    }
    // Spam smoke when an enemy is defusing the bomb
    private HookResult OnBombAbortDefuse(EventBombAbortdefuse @event, GameEventInfo info)
    {
        if (IsCompetitiveProfile())
            InvalidateCtDecisionPlan();
        StopDefuseSmoke();
        return HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        if (IsCompetitiveProfile())
            InvalidateCtDecisionPlan();
        _tPostPlantActive = false;
        _tPostPlantTargets.Clear();
        _tPostPlantRetreatTargets.Clear();
        _tPostPlantWatchTargets.Clear();
        _lastTPostPlantLook.Clear();
        _bombPlantedAt = 0f;
        _bombBlowAt = 0f;
        _cachedBombEntity = null;
        _cachedBombOrigin = null;
        _tPostPlantShouldRetreat = false;
        _tPostPlantAssignmentsDirty = false;
        StopDefuseSmoke();
        return HookResult.Continue;
    }

    private HookResult OnBombExploded(EventBombExploded @event, GameEventInfo info)
    {
        if (IsCompetitiveProfile())
            InvalidateCtDecisionPlan();
        _tPostPlantActive = false;
        _tPostPlantTargets.Clear();
        _tPostPlantRetreatTargets.Clear();
        _tPostPlantWatchTargets.Clear();
        _lastTPostPlantLook.Clear();
        _bombPlantedAt = 0f;
        _bombBlowAt = 0f;
        _cachedBombEntity = null;
        _cachedBombOrigin = null;
        _tPostPlantShouldRetreat = false;
        _tPostPlantAssignmentsDirty = false;
        StopDefuseSmoke();
        return HookResult.Continue;
    }

    private void StopDefuseSmoke()
    {
        _isBombBeingDefused = false;
        _defuseSmokeTimer?.Kill();
        _defuseSmokeTimer = null;
        if (_isDefuseSmokeExpanded)
        {
            _isDefuseSmokeExpanded = false;
            SetSmokeVisibility(NormalSmokeLength);
        }
    }

    private void StartDefuseSmokeCycle()
    {
        if (_defuseSmokeTimer != null) return;

        _defuseSmokeTimer = AddTimer(3.5f, () =>
        {
            _defuseSmokeTimer = null;
            if (!_isBombBeingDefused || IsCompetitiveProfile()) return;

            _isDefuseSmokeExpanded = true;
            SetSmokeVisibility(ExpandedSmokeLength);
            AddTimer(1.5f, () =>
            {
                _isDefuseSmokeExpanded = false;
                SetSmokeVisibility(NormalSmokeLength);
                if (_isBombBeingDefused && !IsCompetitiveProfile())
                    StartDefuseSmokeCycle();
            });
        });
    }

    private void SetSmokeVisibility(float value)
    {
        if (_smokeVisibilityCvar != null)
            _smokeVisibilityCvar.SetValue(value);
        else
            Server.ExecuteCommand($"bot_max_visible_smoke_length {value}");
    }
    //---------------------------------------------------------------------------------------
    private HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player is null || !player.IsValid || !player.IsBot)
            return HookResult.Continue;
        // In case the bot has been taken over
        bool isTakenOver = player.HasBeenControlledByPlayerThisRound;
        if (isTakenOver)
            return HookResult.Continue;

        int bidx = (int)player.Index;
        float origBlind = @event.BlindDuration;
        bool isImmune;

        // Match this blind event to the bot's most-recently-detonating tracked flash.
        // detonateAt should be ~now; allow a 250ms slack since CSS event dispatch and tick
        // boundaries don't line up exactly.
        float matchNow = Server.CurrentTime;
        (int bot, uint flash)? matchedKey = null;
        FlashDecision matched = default;
        float bestDelta = float.MaxValue;
        foreach (var kvp in _flashDecisions)
        {
            if (kvp.Key.bot != bidx) continue;
            float delta = Math.Abs(kvp.Value.DetonateAt - matchNow);
            if (delta < bestDelta && delta < 0.25f)
            {
                bestDelta = delta;
                matchedKey = kvp.Key;
                matched = kvp.Value;
            }
        }

        if (_rayTrace != null)
        {
            if (matchedKey.HasValue)
            {
                isImmune = matched.Avoided;
                _flashDecisions.Remove(matchedKey.Value);
            }
            else
            {
                // Bot never saw this flash through FOV+LOS — should be flashed normally
                isImmune = false;
            }
        }
        else
        {
            // Fail closed when raytrace is unavailable. A missing capability
            // cannot be treated as permission to ignore a flash.
            isImmune = false;
        }

        if (isImmune)
        {
            @event.BlindDuration = 0f;
            var pawn = player.PlayerPawn?.Value;
            if (pawn != null && pawn.IsValid)
            {
                ref float blindStartTime = ref pawn.BlindStartTime;
                blindStartTime = 0f;

                ref float blindUntilTime = ref pawn.BlindUntilTime;
                blindUntilTime = 0f;

                ref float flashDuration = ref pawn.FlashDuration;
                flashDuration = 0f;

                ref float flashMaxAlpha = ref pawn.FlashMaxAlpha;
                flashMaxAlpha = 0f;
            }
        }

        if (_debugFlash)
        {
            string detail;
            if (matchedKey.HasValue)
            {
                float visibleMs = (matched.LastSeen - matched.FirstSeen) * 1000f;
                detail = $"flash#{matchedKey.Value.flash} visible={visibleMs:F0}ms rolled={(matched.Avoided ? "AVOID" : "flash")}";
            }
            else
            {
                detail = "no tracked flash (out of FOV / occluded entire flight)";
            }
            BroadcastDebug(
                $"[Smarter-Bot/Flash] blind event bot={player.PlayerName} immune={isImmune} origDur={origBlind:F2}s ({detail})");
        }

        return HookResult.Continue;
    }
    //---------------------------------------------------------------------------------------
    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot)
            return HookResult.Continue;

        Server.NextFrame(() =>
        {
            if (player == null || !player.IsValid) return;
            ApplyBotState(player);
        });

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        _isFreezeTime = false;
        _tacticalLiveStartedAt = Server.CurrentTime;
        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot) continue;
            ApplyBotState(player);
        }

        if (IsCompetitiveProfile())
        {
            _tacticalRuntime.SetPhase(RoundPhase.Live, Server.CurrentTime);
            ConfigureTacticalEconomy();
            InitializeTacticalRoles();
        }
        return HookResult.Continue;
    }
    //---------------------------------------------------------------------------------------
    private void OnTick()
    {
        ProcessWeaponSwitchRequests();
        ProcessFlashbangAvoidance();

        if (IsCompetitiveProfile())
        {
            float now = Server.CurrentTime;
            if (now >= _nextTacticalTick)
            {
                _nextTacticalTick = now + 0.25f;
                ApplyReadyCtEcoPlan();
                ApplyCompetitiveTacticalActions(now);
            }
        }

        var statePlayers = IsCompetitiveProfile()
            ? _competitiveBotPlayers.Values
            : Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller");
        foreach (var player in statePlayers)
        {
            if (!player.IsValid || !player.IsBot)
                continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            var bot = pawn.Bot;
            if (bot == null)
                continue;
            // In case the bot has been taken over
            bool isTakenOver = player.HasBeenControlledByPlayerThisRound;
            if (isTakenOver) continue;

            int idx = (int)player.Index;
            float now = Server.CurrentTime;
            // Door Stuck Fix
            bool inDoorCooldown = _doorEventCooldown.TryGetValue(idx, out float doorCooldownEnd) && now < doorCooldownEnd;

            ref bool isSleeping = ref bot.IsSleeping;
            isSleeping = false;

            // Competitive bots keep the native AllowActive decision. The
            // tactical layer only changes it through ordinary event timing and
            // never turns the whole team into active hunters every tick.
            if (!IsCompetitiveProfile())
            {
                ref bool allowActive = ref bot.AllowActive;
                allowActive = true;
            }

            if (!IsCompetitiveProfile())
            {
                ref bool isRapidFiring = ref bot.IsRapidFiring;
                isRapidFiring = true;
            }

            if (!IsCompetitiveProfile())
            {
            ref float peripheralTimestamp = ref bot.PeripheralTimestamp;
            peripheralTimestamp = 0.0f;

            ref float fireWeaponTimestamp = ref bot.FireWeaponTimestamp;
            fireWeaponTimestamp = 0.0f;

            // Alert
            CountdownTimer alertTimer = bot.AlertTimer;
            ref float alertduration = ref alertTimer.Duration;
            alertduration = 600.0f;

            ref float alerttimestamp = ref alertTimer.Timestamp;
            alerttimestamp = now + alertduration;

            ref float alerttimescale = ref alertTimer.Timescale;
            alerttimescale = 1.0f;
            // Never ignore enemies
            CountdownTimer ignoreEnemiesTimer = bot.IgnoreEnemiesTimer;

            ref float ignoreEnemiesduration = ref ignoreEnemiesTimer.Duration;
            ignoreEnemiesduration = 0.0f;

            ref float ignoreEnemiestimestamp = ref ignoreEnemiesTimer.Timestamp;
            ignoreEnemiestimestamp = 0.0f;

            ref float ignoreEnemiestimescale = ref ignoreEnemiesTimer.Timescale;
            ignoreEnemiestimescale = 1.0f;

            // Never lookat (panic)
            CountdownTimer panicTimer = bot.PanicTimer;

            ref float panicduration = ref panicTimer.Duration;
            panicduration = 0.0f;

            ref float panictimestamp = ref panicTimer.Timestamp;
            panictimestamp = 0.0f;

            ref float panictimescale = ref panicTimer.Timescale;
            panictimescale = 1.0f;
            // Never be surprised
            CountdownTimer surpriseTimer = bot.SurpriseTimer;

            ref float surpriseDuration = ref surpriseTimer.Duration;
            surpriseDuration = 0.0f;

            ref float surpriseTimestamp = ref surpriseTimer.Timestamp;
            surpriseTimestamp = 0.0f;

            ref float surpriseTimescale = ref surpriseTimer.Timescale;
            surpriseTimescale = 1.0f;
            // Always dodge
            ref bool isEnemySniperVisible = ref bot.IsEnemySniperVisible;
            isEnemySniperVisible = true;

            CountdownTimer sawEnemySniperTimer = bot.SawEnemySniperTimer;

            ref float sawEnemySniperduration = ref sawEnemySniperTimer.Duration;
            sawEnemySniperduration = 600.0f;

            ref float sawEnemySniperTimestamp = ref sawEnemySniperTimer.Timestamp;
            sawEnemySniperTimestamp = now + sawEnemySniperduration;

            ref float sawEnemySniperTimescale = ref sawEnemySniperTimer.Timescale;
            sawEnemySniperTimescale = 1.0f;
            // Teammate Stuck Fix
            ref bool IsWaitingBehindFriend = ref bot.IsWaitingBehindFriend;
            IsWaitingBehindFriend = false;

            CountdownTimer politeTimer = bot.PoliteTimer;

            ref float politeTimerDuration = ref politeTimer.Duration;
            politeTimerDuration = 0.0f;

            ref float politeTimerTimestamp = ref politeTimer.Timestamp;
            politeTimerTimestamp = 0.0f;

            ref float politeTimerTimescale = ref politeTimer.Timescale;
            politeTimerTimescale = 1.0f;
            }

            // Sniper Peek
            bool curIsAttacking = bot.IsAttacking;

            if (curIsAttacking && _hasFiredThisAttack.Remove(idx))
            {
                string? wpn = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
                if (wpn == "weapon_awp" || wpn == "weapon_ssg08")
                {
                    _lastLateralDir.TryGetValue(idx, out float lastDir);
                    if (lastDir != 0f)
                    {
                        float yawS = pawn.EyeAngles.Y * MathF.PI / 180f;
                        float rx = -MathF.Sin(yawS), ry = MathF.Cos(yawS);
                        float injX = rx * (-lastDir) * 250f;
                        float injY = ry * (-lastDir) * 250f;
                        pawn.AbsVelocity.X += injX;
                        pawn.AbsVelocity.Y += injY;

                        ResetLookAroundForBot(player);
                    }
                }
            }
            // Avoid Confusion
            if (curIsAttacking)
            {
                ref bool eyeAnglesUnderPathFinderControl = ref bot.EyeAnglesUnderPathFinderControl;
                eyeAnglesUnderPathFinderControl = false;

                ref float inhibitLookAroundTimestamp = ref bot.InhibitLookAroundTimestamp;
                inhibitLookAroundTimestamp = 0f;
            }
            // Test Alert! Can cause a crash when bot_debug 1. It is also an
            // unfair forced-fire path, so keep it for legacy/arcade only.
            if (!IsCompetitiveProfile() && bot.IsAimingAtEnemy && !curIsAttacking)
            {
                bot.IsAttacking = true;
            }
            // Cancel Crouch After Attack
            if (_prevIsAttacking.TryGetValue(idx, out bool prevAttack))
            {
                if (prevAttack == true && curIsAttacking == false)
                {
                    ref bool isCrouching = ref bot.IsCrouching;
                    isCrouching = false;
                }
            }
            _prevIsAttacking[idx] = curIsAttacking;

            if (!curIsAttacking)
            {
                _hasFiredThisAttack.Remove(idx);
                float yawL2 = pawn.EyeAngles.Y * MathF.PI / 180f;
                float latX = -MathF.Sin(yawL2), latY = MathF.Cos(yawL2);
                float latSpd = pawn.AbsVelocity.X * latX + pawn.AbsVelocity.Y * latY;
                if (MathF.Abs(latSpd) > 10f)
                {
                    float newDir = latSpd > 0f ? 1f : -1f;
                    float prevDir = _lastLateralDir.GetValueOrDefault(idx);
                    _lastLateralDir[idx] = newDir;
                }
            }

            // Ladder Stuck Issue Fix
            var moveServices = pawn.MovementServices as CCSPlayer_MovementServices;
            var ladderNormal = moveServices?.LadderNormal;

            bool nearLadder = pawn.MoveType == MoveType_t.MOVETYPE_LADDER
                        || (ladderNormal != null
                            && (ladderNormal.X != 0f || ladderNormal.Y != 0f || ladderNormal.Z != 0f));

            if (nearLadder) _ladderExitTime[idx] = Server.CurrentTime;

            bool inLadderCooldown = nearLadder
                || (_ladderExitTime.TryGetValue(idx, out float exitTime)
                    && Server.CurrentTime - exitTime < 5.0f);

            bool inAir = !inLadderCooldown
                    && (pawn.GroundEntity == null || !pawn.GroundEntity.IsValid);

            _prevInAir.TryGetValue(idx, out bool prevInAir);
            // Door Stuck Issue Fix
            if (inDoorCooldown)
            {
                _prevInAir[idx] = inAir;
                continue;
            }
            // Jump Crouch Forward/Backward
            var angles = pawn.EyeAngles;
            float yawDir = angles.Y * MathF.PI / 180f;
            float fwdX = MathF.Cos(yawDir);
            float fwdY = MathF.Sin(yawDir);
            float currentFwd = pawn.AbsVelocity.X * fwdX + pawn.AbsVelocity.Y * fwdY;

            if (currentFwd >= 20f || currentFwd <= -20f)
            {
                _lastForwardDir[idx] = currentFwd > 0f ? 1f : -1f;
            }

            if (inAir)
            {
                if (!pawn.IsDefusing)
                {
                    ref bool isCrouching = ref bot.IsCrouching;
                    isCrouching = true;
                }
                if (!curIsAttacking)// Avoid Jump and Gun
                {
                    float targetSpeed;
                    if (currentFwd <= -20f)
                    {
                        targetSpeed = -215f;
                    }
                    else if (currentFwd >= 20f)
                    {
                        targetSpeed = 215f;
                    }
                    else
                    {
                        float lastDir = _lastForwardDir.TryGetValue(idx, out float dir) ? dir : 1f;
                        targetSpeed = lastDir > 0f ? 215f : -215f;
                    }
                    const float accel = 12f;
                    const float tickInterval = 0.015625f;
                    float delta = targetSpeed - currentFwd;
                    if (targetSpeed > 0)
                    {
                        if (delta > 0)
                        {
                            float addSpeed = delta * accel * tickInterval;

                            pawn.AbsVelocity.X += fwdX * addSpeed;
                            pawn.AbsVelocity.Y += fwdY * addSpeed;
                        }
                    }
                    else
                    {
                        if (delta < 0)
                        {
                            float addSpeed = delta * accel * tickInterval;

                            pawn.AbsVelocity.X += fwdX * addSpeed;
                            pawn.AbsVelocity.Y += fwdY * addSpeed;
                        }
                    }
                }
            }
            // Cancel Crouch
            if (prevInAir && !inAir)
            {
                ref bool isCrouching = ref bot.IsCrouching;
                isCrouching = false;
            }
            _prevInAir[idx] = inAir;
            // cache the parameters for counter-strafe
            _cachedInAir[idx] = inAir;
            _cachedNearLadder[idx] = nearLadder;
            // Normal Un-Stuck Process
            ref bool isStuck = ref bot.IsStuck;
            if (isStuck)
            {
                ref bool isRunning = ref bot.IsRunning;
                isRunning = true;

                ref float jumpTimestamp = ref bot.JumpTimestamp;
                jumpTimestamp = 0.0f;

                CountdownTimer stuckJumpTimer = bot.StuckJumpTimer;

                ref float stuckduration = ref stuckJumpTimer.Duration;
                stuckduration = 0.0f;

                ref float stucktimestamp = ref stuckJumpTimer.Timestamp;
                stucktimestamp = Server.CurrentTime;

                ref float stucktimescale = ref stuckJumpTimer.Timescale;
                stucktimescale = 1.0f;

                // Manual Stuck State
                float speed2D = MathF.Sqrt(
                    pawn.AbsVelocity.X * pawn.AbsVelocity.X +
                    pawn.AbsVelocity.Y * pawn.AbsVelocity.Y);

                var curPos = pawn.AbsOrigin!;

                if (!_stuckStartTime.ContainsKey(idx))
                {
                    _stuckStartTime[idx] = now;
                    _stuckStartPos[idx] = new Vector(curPos.X, curPos.Y, curPos.Z);
                    _stuckJumpDone[idx] = false;
                    _stuckMaxSpeed[idx] = 0f;
                }

                if (speed2D > _stuckMaxSpeed.GetValueOrDefault(idx))
                    _stuckMaxSpeed[idx] = speed2D;

                float elapsed = now - _stuckStartTime.GetValueOrDefault(idx);
                var sp = _stuckStartPos.GetValueOrDefault(idx, new Vector(curPos.X, curPos.Y, curPos.Z));
                float dist2D = MathF.Sqrt(
                    MathF.Pow(curPos.X - sp.X, 2) +
                    MathF.Pow(curPos.Y - sp.Y, 2));
                float maxSpd = _stuckMaxSpeed.GetValueOrDefault(idx);

                bool condA = elapsed >= 1.0f && maxSpd <= 10f;
                bool condB = elapsed >= 3.0f && maxSpd > 10f && dist2D < 75f;

                if ((condA || condB) && !_stuckJumpDone.GetValueOrDefault(idx))
                {
                    ref bool isCrouching = ref bot.IsCrouching;
                    isCrouching = false;

                    _stuckJumpDone[idx] = true;

                    int jumpCount = _stuckJumpCount.GetValueOrDefault(idx);
                    _stuckJumpCount[idx] = jumpCount + 1;

                    float sideSign = (jumpCount % 2 == 0) ? 1f : -1f;
                    float offsetRad = 30f * MathF.PI / 180f * sideSign;
                    float baseYaw = pawn.EyeAngles.Y * MathF.PI / 180f;
                    float backYaw = baseYaw + MathF.PI + offsetRad;

                    pawn.AbsVelocity.X = MathF.Cos(backYaw) * 100f;
                    pawn.AbsVelocity.Y = MathF.Sin(backYaw) * 100f;

                    CountdownTimer repathTimer = bot.RepathTimer;

                    ref float repathduration = ref repathTimer.Duration;
                    repathduration = 0.0f;

                    ref float repathtimestamp = ref repathTimer.Timestamp;
                    repathtimestamp = Server.CurrentTime;

                    ref float repathtimescale = ref repathTimer.Timescale;
                    repathtimescale = 1.0f;

                    // Reset
                    _stuckStartTime[idx] = now;
                    _stuckStartPos[idx] = new Vector(curPos.X, curPos.Y, curPos.Z);
                    _stuckMaxSpeed[idx] = 0f;
                }
            }
            else
            {
                // Clear
                _stuckStartTime.Remove(idx);
                _stuckStartPos.Remove(idx);
                _stuckJumpDone.Remove(idx);
                _stuckMaxSpeed.Remove(idx);

                // Idle repath: if speed < 5 for 5s, force a repath
                float speed2DIdle = MathF.Sqrt(
                    pawn.AbsVelocity.X * pawn.AbsVelocity.X +
                    pawn.AbsVelocity.Y * pawn.AbsVelocity.Y);

                if (speed2DIdle < 5f)
                {
                    if (!_idleStartTime.ContainsKey(idx))
                        _idleStartTime[idx] = now;

                    float idleElapsed = now - _idleStartTime[idx];
                    float lastRepath = _lastRepathTime.GetValueOrDefault(idx, -999f);

                    if (idleElapsed >= 5f && now - lastRepath >= 5f && !curIsAttacking && !pawn.IsDefusing)
                    {
                        ref bool isCrouching = ref bot.IsCrouching;
                        isCrouching = false;

                        _lastRepathTime[idx] = now;

                        CountdownTimer repathTimer = bot.RepathTimer;

                        ref float repathduration = ref repathTimer.Duration;
                        repathduration = 0.0f;

                        ref float repathtimestamp = ref repathTimer.Timestamp;
                        repathtimestamp = Server.CurrentTime;

                        ref float repathtimescale = ref repathTimer.Timescale;
                        repathtimescale = 1.0f;

                        ResetLookAroundForBot(player);
                    }
                }
                else
                {
                    _idleStartTime.Remove(idx);
                }
            }

            //Inferno Sewer Stuck Fix
            if (pawn.AbsOrigin != null)
            {
                Vector pos = pawn.AbsOrigin;
                bool isInferno = string.Equals(Server.MapName, "de_inferno", StringComparison.OrdinalIgnoreCase);
                float dx = pos.X - 285f;
                float dy = pos.Y - 450f;
                float dist = MathF.Sqrt(dx * dx + dy * dy);

                if (isInferno && dist < 50f)
                {
                    CountdownTimer repathTimer = bot.RepathTimer;

                    ref float repathduration = ref repathTimer.Duration;
                    repathduration = 0.0f;

                    ref float repathtimestamp = ref repathTimer.Timestamp;
                    repathtimestamp = Server.CurrentTime;

                    ref float repathtimescale = ref repathTimer.Timescale;
                    repathtimescale = 1.0f;
                }
            }
        }
    }

    private void ProcessWeaponSwitchRequests()
    {
        foreach (var request in BotWeaponSwitchQueue.Drain())
        {
            if (_botController == null)
            {
                Console.WriteLine(
                    $"[Smarter-Bot/Weapon] switch-deferred slot={request.Slot} weapon={request.Weapon} reason=BotController-unavailable");
                BotWeaponSwitchQueue.Requeue(request);
                continue;
            }

            int defIndex = request.Weapon switch
            {
                "weapon_ak47" => 7,
                "weapon_m4a1" => 16,
                "weapon_m4a1_silencer" => 60,
                "weapon_famas" => 10,
                "weapon_galilar" => 13,
                "weapon_awp" => 9,
                "weapon_mp9" => 34,
                "weapon_mac10" => 17,
                _ => 0,
            };
            if (defIndex == 0)
                continue;

            bool switched = false;
            try
            {
                switched = BotControllerBridge.SwitchBotWeapon(
                    _botController,
                    request.Slot,
                    defIndex);
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    $"[Smarter-Bot/Weapon] switch-error slot={request.Slot} weapon={request.Weapon} error={exception.Message}");
            }

            if (!switched)
            {
                if (BotWeaponSwitchQueue.TryRequeueFailed(request))
                {
                    Console.WriteLine(
                        $"[Smarter-Bot/Weapon] switch-failed slot={request.Slot} weapon={request.Weapon} def={defIndex} "
                        + $"attempt={request.Attempt + 1}/3; retry queued, original inventory preserved");
                }
                else
                {
                    Console.WriteLine(
                        $"[Smarter-Bot/Weapon] switch-failed slot={request.Slot} weapon={request.Weapon} def={defIndex} "
                        + $"attempt={request.Attempt}/3; retry limit reached, original inventory preserved");
                }
                continue;
            }

            if (string.IsNullOrEmpty(request.ReplaceWeapon))
                continue;

            // BuyExecutionTransaction removes the carried primary before the
            // replacement purchase. In that path the queue marker is still
            // useful for settlement/audit, but there is no old weapon left to
            // switch to or drop. Do not interpret that as a failed rollback.
            bool oldStillOwned = playerHasWeapon(request.Slot, request.ReplaceWeapon);
            if (!oldStillOwned)
            {
                Console.WriteLine(
                    $"[Smarter-Bot/Weapon] replacement-settled slot={request.Slot} "
                    + $"old={request.ReplaceWeapon} new={request.Weapon} old=pre-removed");
                continue;
            }

            int oldDefIndex = request.ReplaceWeapon switch
            {
                "weapon_ak47" => 7,
                "weapon_m4a1" => 16,
                "weapon_m4a1_silencer" => 60,
                "weapon_famas" => 10,
                "weapon_galilar" => 13,
                "weapon_awp" => 9,
                "weapon_mp9" => 34,
                "weapon_mac10" => 17,
                _ => 0,
            };
            var player = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
                .FirstOrDefault(candidate => candidate.IsValid && candidate.Slot == request.Slot);
            if (oldDefIndex == 0 || player == null)
            {
                if (player != null)
                    RollbackReplacementWeapon(player, request);
                continue;
            }

            bool oldSelected = false;
            try
            {
                oldSelected = BotControllerBridge.SwitchBotWeapon(
                    _botController,
                    request.Slot,
                    oldDefIndex);
            }
            catch
            {
                oldSelected = false;
            }

            if (!oldSelected)
            {
                var settlement = WeaponSwitchSettlementPolicy.Evaluate(
                    newWeaponSelected: switched,
                    oldWeaponSelected: false,
                    replacementReselected: false);
                if (settlement.ShouldRollbackReplacement)
                    RollbackReplacementWeapon(player, request);
                Console.WriteLine(
                    $"[Smarter-Bot/Weapon] old-switch-failed slot={request.Slot} old={request.ReplaceWeapon} "
                    + $"reason={settlement.Reason}; original weapon retained and replacement rolled back");
                continue;
            }

            player.DropActiveWeapon();
            bool restoredNew = false;
            try
            {
                restoredNew = BotControllerBridge.SwitchBotWeapon(
                    _botController,
                    request.Slot,
                    defIndex);
            }
            catch
            {
                restoredNew = false;
            }

            if (!restoredNew)
            {
                var settlement = WeaponSwitchSettlementPolicy.Evaluate(
                    newWeaponSelected: switched,
                    oldWeaponSelected: oldSelected,
                    replacementReselected: false);
                if (settlement.ShouldRollbackReplacement)
                    RollbackReplacementWeapon(player, request);
                if (settlement.ShouldRestoreOriginal)
                {
                    // The old weapon was already confirmed before the drop.
                    // Give it back without charging the bot if the final
                    // switch fails.
                    player.GiveNamedItem(request.ReplaceWeapon);
                    BotControllerBridge.SwitchBotWeapon(_botController, request.Slot, oldDefIndex);
                }
                Console.WriteLine(
                    $"[Smarter-Bot/Weapon] replacement-rollback slot={request.Slot} old={request.ReplaceWeapon} "
                    + $"new={request.Weapon} reason={settlement.Reason}");
            }
        }

        static bool playerHasWeapon(int slot, string weapon)
            => Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>(
                    "cs_player_controller")
                .FirstOrDefault(candidate => candidate.IsValid && candidate.Slot == slot)
                ?.PlayerPawn?.Value?.WeaponServices?.MyWeapons
                .Any(handle => handle.Value?.DesignerName == weapon) == true;
    }

    private static void RollbackReplacementWeapon(
        CCSPlayerController player,
        WeaponSwitchRequest request)
    {
        if (!player.IsValid)
            return;

        bool hasReplacement = player.PlayerPawn?.Value?.WeaponServices?.MyWeapons
            .Any(handle => handle.Value?.DesignerName == request.Weapon) == true;
        if (!hasReplacement)
            return;

        try
        {
            player.RemoveItemByDesignerName(request.Weapon);
        }
        catch
        {
            return;
        }

        int price = BuyPlanner.GetWeaponCost(request.Weapon);
        if (price <= 0 || player.InGameMoneyServices == null)
            return;

        int maxMoney = ConVar.Find("mp_maxmoney")?.GetPrimitiveValue<int>() ?? 16000;
        player.InGameMoneyServices.Account = Math.Min(
            maxMoney,
            player.InGameMoneyServices.Account + price);
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
    }

    private void SynchronizeTacticalAfterReload()
    {
        if (!IsCompetitiveProfile()) return;

        ClearSaveMode();
        _ctGambleTargets.Clear();
        _ctEcoTargets.Clear();
        _ctEcoSnapshotValue = 0;
        _ctEcoPlanValue = 0;
        _roundSnapshotValue = 0;
        _competitiveBotPlayers.Clear();
        _ctEcoPlanningWorker.Reset(new PlanVersion(_tacticalRoundKey, 0, 0));
        _ctDecisionSnapshotValue = 0;
        _ctDecisionPlanValue = 0;
        _ctDecisionDirty = true;
        _ctDecisionPlanningWorker.Reset(new PlanVersion(_tacticalRoundKey, 0, 0));
        while (_ctDecisionResults.TryDequeue(out _)) { }
        _latestCtDecisions = Array.Empty<CtTacticalDecision>();
        _lastCtEcoTactic = null;
        _lastCtEcoSite = CtGambleSite.None;
        while (_ctEcoPlanResults.TryDequeue(out _)) { }
        _ctThreatEvents.Clear();
        _ctThreatEvaluation = default;
        _nextCtThreatDecayAt = 0f;
        _ctRetreatTarget = null;
        _ctGambleFallbackLogged = false;
        _lastTacticalGoalWrite.Clear();
        _lastTacticalPosition.Clear();
        _tacticalStuckSince.Clear();
        _tPostPlantTargets.Clear();
        _tPostPlantRetreatTargets.Clear();
        _tPostPlantWatchTargets.Clear();
        _lastTPostPlantLook.Clear();
        _nextTPostPlantTargetRefreshAt = 0f;
        _nextTPostPlantRetreatRefreshAt = 0f;
        _tPostPlantActive = false;
        _tPostPlantAssignmentsDirty = false;
        _tPostPlantShouldRetreat = false;
        _bombPlantedAt = 0f;
        _bombBlowAt = 0f;
        _tPostPlantSite = CtGambleSite.None;
        _lastTPostPlantAction = null;
        _lastTPostPlantReason = null;
        var phase = ResolveTacticalRoundPhaseFromGameState();
        _tacticalRoundKey = ResolveTacticalRoundKey();
        _latestRoundSnapshot = null;
        _isFreezeTime = phase == RoundPhase.Freeze;
        _tacticalRuntime.Reset(CreateTacticalRoundContext(phase));
        if (phase == RoundPhase.Live)
        {
            _tacticalRuntime.SetPhase(
                RoundPhase.Live,
                _tacticalLiveStartedAt ?? Server.CurrentTime);
        }

        // A hot-reloaded instance has a fresh cache. Prefer the phase captured
        // during freeze time; the current account balances are post-purchase
        // and cannot distinguish an eco/force/pistol round from a full buy.
        if (!TryRestoreTacticalEconomyCheckpoint())
            ConfigureTacticalEconomy(captureRoundFlags: true);
        else
            ConfigureTacticalEconomy();
        if (!_isFreezeTime)
            InitializeTacticalRoles();
    }

    private int ResolveTacticalRoundKey()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
            return gameRules?.TotalRoundsPlayed ?? Math.Max(0, _tacticalRoundNumber - 1);
        }
        catch
        {
            return Math.Max(0, _tacticalRoundNumber - 1);
        }
    }

    private bool TryRestoreTacticalEconomyCheckpoint()
    {
        if (_lastTacticalEconomyCheckpoint is not { } checkpoint
            || checkpoint.RoundKey != _tacticalRoundKey)
            return false;

        _tacticalEconomy.Restore(checkpoint.CtPhase, checkpoint.OpponentPhase);
        return true;
    }

    private void CaptureTacticalEconomyCheckpoint()
    {
        if (!_tacticalEconomy.IsCaptured)
            return;

        _lastTacticalEconomyCheckpoint = new TacticalEconomyCheckpoint(
            _tacticalRoundKey,
            _tacticalEconomy.CtPhase,
            _tacticalEconomy.OpponentPhase);
    }

    private RoundPhase ResolveTacticalRoundPhaseFromGameState()
    {
        var rules = Utilities
            .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault();
        bool freezePeriod = rules?.GameRules?.FreezePeriod ?? _isFreezeTime;
        bool bombPlanted = Utilities
            .FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
            .Any(entity => entity.IsValid);

        return CompetitiveTacticalPolicy.ResolveRoundPhase(freezePeriod, bombPlanted);
    }

    private void InitializeTacticalRoles()
    {
        if (!IsCompetitiveProfile()) return;

        var bots = BuildCtBotSnapshots();
        if (bots.Count == 0) return;

        ConfigureTacticalEconomy();
        _tacticalRuntime.AssignCtRoles(bots);
        ResolveCtTacticalAnchors();
        QueueCtEcoPlan(bots);
        _tacticalRolesInitialized = true;
        _lastTacticalDecisions.Clear();
        _lastTacticalRepath.Clear();

        if (_tacticalDebug)
            PrintTacticalSnapshot();
    }

    private void QueueCtEcoPlan(IReadOnlyList<CtBotSnapshot> bots)
    {
        if (!IsCompetitiveProfile()
            || !IsEconomicCtPhase(_tacticalRuntime.Context.CtBuyPhase)
            || bots.Count == 0)
            return;

        var context = new CtEcoPlanningContext(
            Server.MapName,
            CtMapProfileCatalog.IsKnown(Server.MapName),
            _tacticalRuntime.Context.CtBuyPhase,
            _tacticalRuntime.Context.OpponentBuyPhase,
            (_latestRoundSnapshot?.Pressure.Level ?? ResolveCtMatchPressure()),
            bots.ToArray(),
            _lastCtEcoTactic,
            _lastCtEcoSite,
            HasTeamUtility("weapon_flashbang"),
            HasTeamUtility("weapon_molotov")
                || HasTeamUtility("weapon_incgrenade"));
        _ctEcoSnapshotValue++;
        _ctEcoPlanValue++;
        _ctEcoPlanningWorker.Submit(
            context,
            new PlanVersion(_tacticalRoundKey, _ctEcoSnapshotValue, _ctEcoPlanValue));
    }

    private void ApplyReadyCtEcoPlan()
    {
        while (_ctEcoPlanResults.TryDequeue(out var result))
        {
            if (result.Version.RoundKey != _tacticalRoundKey
                || result.Version.SnapshotValue != _ctEcoSnapshotValue
                || result.Version.PlanValue != _ctEcoPlanValue)
                continue;

            var plan = result.Result;
            if (!ResolveCtTacticalAnchors())
                continue;

            _ctEcoTargets.Clear();
            foreach (var assignment in plan.Assignments)
            {
                if (!TryResolveCtEcoAnchor(assignment, out var anchor))
                    continue;

                var rawOffset = CtMapProfileCatalog.GetTacticalOffset(
                    Server.MapName,
                    assignment,
                    plan.Tactic);
                var offset = OrientCtEcoOffset(assignment, anchor, rawOffset);
                var desired = new Vector(
                    anchor.X + offset.X,
                    anchor.Y + offset.Y,
                    anchor.Z);
                var navArea = CCSNavArea.GetClosestNavArea(desired, 900f);
                if (navArea == null)
                    continue;

                // Keep role separation even when several offsets resolve to
                // the same broad Nav area. The offset remains part of the
                // goal while Nav supplies a reachable Z/area anchor.
                _ctEcoTargets[assignment.Slot] = new Vector(
                    navArea.Center.X + offset.X * 0.25f,
                    navArea.Center.Y + offset.Y * 0.25f,
                    navArea.Center.Z);
            }

            _lastCtEcoTactic = plan.Tactic;
            _lastCtEcoSite = plan.Site;
            _tacticalRuntime.SetEcoPlan(plan);
            _tacticalRuntime.SetCtGambleSite(plan.Site);
            if (_tacticalDebug)
            {
                BroadcastDebug(
                    $"[Smarter-Bot/Tactical] eco-plan tactic={plan.Tactic} site={plan.Site} "
                    + $"assignments={plan.Assignments.Count} targets={_ctEcoTargets.Count} reason={plan.Reason}");
            }
        }
    }

    private void QueueCtDecisionPlan(float now)
    {
        if (!_tacticalRolesInitialized || !_ctDecisionDirty)
            return;

        var snapshot = _tacticalRuntime.CreatePlanningSnapshot(now);
        _ctDecisionSnapshotValue++;
        _ctDecisionPlanValue++;
        _ctDecisionPlanningWorker.Submit(
            snapshot,
            new PlanVersion(_tacticalRoundKey, _ctDecisionSnapshotValue, _ctDecisionPlanValue));
    }

    private void ApplyReadyCtDecisionPlan()
    {
        while (_ctDecisionResults.TryDequeue(out var result))
        {
            if (result.Version.RoundKey != _tacticalRoundKey
                || result.Version.SnapshotValue != _ctDecisionSnapshotValue
                || result.Version.PlanValue != _ctDecisionPlanValue)
                continue;

            _latestCtDecisions = result.Result;
            _ctDecisionDirty = false;
        }
    }

    private MatchPressureResult EvaluateCtMatchPressure()
    {
        int roundsPlayed = CurrentRoundsPlayed();
        return MatchPressurePolicy.Evaluate(
            _counterTerroristScore,
            _terroristScore,
            roundsPlayed,
            new MatchFormatSnapshot(
                ReadMaxRounds(),
                ReadOvertimeEnabled(),
                ReadOvertimeMaxRounds()),
            economySwing: _tacticalRuntime.Context.CtBuyPhase is BuyPhase.Eco or BuyPhase.Save);
    }

    private MatchPressure ResolveCtMatchPressure()
        => EvaluateCtMatchPressure().Level;

    private bool HasTeamUtility(string weaponName)
        => Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Any(player => player.IsValid
                && player.IsBot
                && player.Team == CsTeam.CounterTerrorist
                && player.PawnIsAlive
                && CountWeapon(player, weaponName) > 0);

    private bool ResolveCtTacticalAnchors()
    {
        _ctGambleTargets.Clear();
        _ctEntryTargets.Clear();
        _ctCloseTargets.Clear();
        _ctMidTarget = null;
        _ctTacticalSpawn = null;
        _ctRetreatTarget = null;

        foreach (var bombTarget in Utilities
                     .FindAllEntitiesByDesignerName<CBombTarget>("func_bomb_target"))
        {
            if (!bombTarget.IsValid || bombTarget.AbsOrigin is not { } origin)
                continue;

            var navArea = CCSNavArea.GetClosestNavArea(origin, 2500f);
            if (navArea == null)
                continue;

            CtGambleSite site = bombTarget.IsBombSiteB
                ? CtGambleSite.B
                : CtGambleSite.A;
            _ctGambleTargets.TryAdd(
                site,
                new Vector(navArea.Center.X, navArea.Center.Y, navArea.Center.Z));
        }

        _ctRetreatTarget = ResolveCtRetreatTarget();
        _ctTacticalSpawn = ResolveSpawnAnchor("info_player_terrorist");
        if (CtMapProfileCatalog.TryGetAnchorProfile(
                Server.MapName,
                out var anchorProfile)
            && _ctTacticalSpawn is { } terroristSpawn
            && _ctGambleTargets.TryGetValue(CtGambleSite.A, out var siteA)
            && _ctGambleTargets.TryGetValue(CtGambleSite.B, out var siteB))
        {
            foreach (CtGambleSite site in new[] { CtGambleSite.A, CtGambleSite.B })
            {
                Vector siteAnchor = site == CtGambleSite.A ? siteA : siteB;
                _ctEntryTargets[site] = ResolveNavAnchor(
                    Interpolate(siteAnchor, terroristSpawn, anchorProfile.EntryFraction),
                    siteAnchor);
                _ctCloseTargets[site] = ResolveNavAnchor(
                    Interpolate(siteAnchor, terroristSpawn, anchorProfile.CloseFraction),
                    siteAnchor);
            }

            Vector siteMidpoint = new(
                (siteA.X + siteB.X) * 0.5f,
                (siteA.Y + siteB.Y) * 0.5f,
                (siteA.Z + siteB.Z) * 0.5f);
            Vector? ctSpawn = _ctRetreatTarget;
            if (ctSpawn is { } counterTerroristSpawn)
            {
                Vector midCandidate = new(
                    siteMidpoint.X + (counterTerroristSpawn.X - terroristSpawn.X)
                        * anchorProfile.MidBias,
                    siteMidpoint.Y + (counterTerroristSpawn.Y - terroristSpawn.Y)
                        * anchorProfile.MidBias,
                    siteMidpoint.Z + (counterTerroristSpawn.Z - terroristSpawn.Z)
                        * anchorProfile.MidBias);
                _ctMidTarget = ResolveNavAnchor(midCandidate, siteMidpoint);
            }
        }
        return _ctGambleTargets.ContainsKey(CtGambleSite.A)
            && _ctGambleTargets.ContainsKey(CtGambleSite.B)
            && _ctRetreatTarget != null;
    }

    private static Vector? ResolveSpawnAnchor(string designerName)
    {
        var positions = Utilities
            .FindAllEntitiesByDesignerName<CBaseEntity>(designerName)
            .Where(entity => entity.IsValid && entity.AbsOrigin != null)
            .Select(entity => entity.AbsOrigin!)
            .ToArray();
        if (positions.Length == 0)
            return null;

        return new Vector(
            positions.Average(position => position.X),
            positions.Average(position => position.Y),
            positions.Average(position => position.Z));
    }

    private static Vector Interpolate(Vector from, Vector to, float fraction)
    {
        fraction = Math.Clamp(fraction, 0f, 1f);
        return new Vector(
            from.X + (to.X - from.X) * fraction,
            from.Y + (to.Y - from.Y) * fraction,
            from.Z + (to.Z - from.Z) * fraction);
    }

    private static Vector ResolveNavAnchor(Vector candidate, Vector fallback)
    {
        var navArea = CCSNavArea.GetClosestNavArea(candidate, 1800f)
            ?? CCSNavArea.GetClosestNavArea(fallback, 1800f);
        return navArea == null
            ? fallback
            : new Vector(navArea.Center.X, navArea.Center.Y, navArea.Center.Z);
    }

    private bool TryResolveCtEcoAnchor(
        CtEcoAssignment assignment,
        out Vector anchor)
    {
        if (assignment.Role == CtEcoRole.MidControl && _ctMidTarget is { } mid)
        {
            anchor = mid;
            return true;
        }

        if (assignment.Role is CtEcoRole.PackEntry or CtEcoRole.PackSupport
            && _ctEntryTargets.TryGetValue(assignment.Site, out var entry))
        {
            anchor = entry;
            return true;
        }

        if (assignment.Role == CtEcoRole.CloseTrap
            && _ctCloseTargets.TryGetValue(assignment.Site, out var close))
        {
            anchor = close;
            return true;
        }

        if (assignment.Role is CtEcoRole.Crossfire or CtEcoRole.Information
            && _ctGambleTargets.TryGetValue(assignment.Site, out var siteAnchor))
        {
            anchor = siteAnchor;
            return true;
        }

        anchor = default;
        return false;
    }

    private Vector OrientCtEcoOffset(
        CtEcoAssignment assignment,
        Vector anchor,
        (float X, float Y) offset)
    {
        Vector routeTarget = _ctTacticalSpawn ?? anchor;
        if (assignment.Role == CtEcoRole.MidControl && _ctTacticalSpawn is null)
            routeTarget = _ctRetreatTarget ?? anchor;

        float dx = routeTarget.X - anchor.X;
        float dy = routeTarget.Y - anchor.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1f)
            return new Vector(offset.X, offset.Y, 0f);

        float forwardX = dx / length;
        float forwardY = dy / length;
        float rightX = -forwardY;
        float rightY = forwardX;
        float along = -offset.X;
        return new Vector(
            forwardX * along + rightX * offset.Y,
            forwardY * along + rightY * offset.Y,
            0f);
    }

    private static Vector? ResolveCtRetreatTarget()
    {
        var spawnPositions = Utilities
            .FindAllEntitiesByDesignerName<CBaseEntity>("info_player_counterterrorist")
            .Where(entity => entity.IsValid && entity.AbsOrigin != null)
            .Select(entity => entity.AbsOrigin!)
            .ToArray();
        if (spawnPositions.Length == 0)
            return null;

        var center = new Vector(
            spawnPositions.Average(position => position.X),
            spawnPositions.Average(position => position.Y),
            spawnPositions.Average(position => position.Z));
        var navArea = CCSNavArea.GetClosestNavArea(center, 2500f);
        return navArea == null
            ? null
            : new Vector(navArea.Center.X, navArea.Center.Y, navArea.Center.Z);
    }

    private CtGambleSite ResolveContactSite(Vector origin)
    {
        if (_ctGambleTargets.Count < 2)
            ResolveCtTacticalAnchors();

        CtGambleSite nearestSite = CtGambleSite.None;
        float nearestDistance = float.MaxValue;
        foreach (var entry in _ctGambleTargets)
        {
            float dx = origin.X - entry.Value.X;
            float dy = origin.Y - entry.Value.Y;
            float dz = origin.Z - entry.Value.Z;
            float distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestSite = entry.Key;
            }
        }

        return nearestDistance <= 2400f ? nearestSite : CtGambleSite.None;
    }

    private static bool IsEconomicCtPhase(BuyPhase phase)
        => phase is BuyPhase.Pistol or BuyPhase.Eco or BuyPhase.HalfBuy or BuyPhase.ForceBuy;

    private bool TryApplyTacticalGoal(
        CCSPlayerPawn pawn,
        CCSBot bot,
        CtTacticalDecision decision,
        float now)
    {
        Vector? target = null;
        if (decision.ShouldMoveToRetreat)
        {
            target = _ctRetreatTarget;
        }
        else if (_ctEcoTargets.TryGetValue(decision.Slot, out var assignedTarget))
        {
            target = assignedTarget;
        }
        else if (decision.TargetSite != CtGambleSite.None)
        {
            _ctGambleTargets.TryGetValue(decision.TargetSite, out target);
        }

        if (target == null)
        {
            _ctGambleTargets.Clear();
            _ctRetreatTarget = null;
            ResolveCtTacticalAnchors();
            target = decision.ShouldMoveToRetreat
                ? _ctRetreatTarget
                : _ctEcoTargets.TryGetValue(decision.Slot, out var assignedTargetAfterReparse)
                    ? assignedTargetAfterReparse
                    : decision.TargetSite != CtGambleSite.None
                    && _ctGambleTargets.TryGetValue(decision.TargetSite, out var reparsedTarget)
                    ? reparsedTarget
                    : null;
            Console.WriteLine(
                $"[Smarter-Bot/Tactical] goal-reparse slot={decision.Slot} state={decision.State} "
                + $"target={decision.TargetSite} success={target != null}");
        }

        if (target == null || bot.Handle == nint.Zero)
            return false;

        if (pawn.AbsOrigin is { } origin)
        {
            if (_lastTacticalPosition.TryGetValue(decision.Slot, out var previous))
            {
                float dx = origin.X - previous.X;
                float dy = origin.Y - previous.Y;
                float dz = origin.Z - previous.Z;
                if (dx * dx + dy * dy + dz * dz < 24f * 24f)
                {
                    float stuckSince = _tacticalStuckSince.GetValueOrDefault(decision.Slot, now);
                    _tacticalStuckSince[decision.Slot] = stuckSince;
                    if (now - stuckSince >= 2.0f)
                    {
                        _lastTacticalGoalWrite.Remove(decision.Slot);
                        _tacticalStuckSince[decision.Slot] = now;
                        _ctEcoTargets.Remove(decision.Slot);
                        _ctGambleTargets.Clear();
                        _ctRetreatTarget = null;
                        ResolveCtTacticalAnchors();
                        Console.WriteLine(
                            $"[Smarter-Bot/Tactical] goal-stuck slot={decision.Slot} state={decision.State} "
                            + $"target={decision.TargetSite}; target reparsed, native fallback and repath");
                        return false;
                    }
                }
                else
                {
                    _tacticalStuckSince.Remove(decision.Slot);
                }
            }
            _lastTacticalPosition[decision.Slot] = new Vector(origin.X, origin.Y, origin.Z);
        }

        ref bool isRunning = ref bot.IsRunning;
        isRunning = true;

        float lastWrite = _lastTacticalGoalWrite.GetValueOrDefault(decision.Slot, -999f);
        if (now - lastWrite < 0.50f)
            return true;

        Schema.SetSchemaValue(bot.Handle, "CCSBot", "m_goalPosition", target);
        _lastTacticalGoalWrite[decision.Slot] = now;

        CountdownTimer repathTimer = bot.RepathTimer;
        ref float duration = ref repathTimer.Duration;
        duration = 0.0f;
        ref float timestamp = ref repathTimer.Timestamp;
        timestamp = now;
        ref float timescale = ref repathTimer.Timescale;
        timescale = 1.0f;
        return true;
    }

    private bool HasReachedRetreatTarget(CCSPlayerPawn pawn)
    {
        if (_ctRetreatTarget == null || pawn.AbsOrigin is not { } origin)
            return false;

        float dx = origin.X - _ctRetreatTarget.X;
        float dy = origin.Y - _ctRetreatTarget.Y;
        float dz = origin.Z - _ctRetreatTarget.Z;
        return dx * dx + dy * dy + dz * dz <= 180f * 180f;
    }

    private void InitializeTPostPlantTargets(CtGambleSite site)
    {
        _tPostPlantTargets.Clear();
        _tPostPlantWatchTargets.Clear();
        var bomb = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
            .FirstOrDefault(entity => entity.IsValid && entity.AbsOrigin != null);
        if (bomb?.AbsOrigin is not { } origin)
            return;
        _cachedBombEntity = bomb;
        _cachedBombOrigin = origin;

        var terrorists = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(player => player.IsValid
                && player.IsBot
                && player.PawnIsAlive
                && player.Team == CsTeam.Terrorist)
            .ToArray();
        var snapshots = terrorists
            .Where(player => player.PlayerPawn?.Value?.AbsOrigin is not null)
            .Select(player =>
            {
                var position = player.PlayerPawn!.Value!.AbsOrigin!;
                float distanceToSafeTarget = _tPostPlantRetreatTargets.TryGetValue(
                    player.Slot,
                    out var safeTarget)
                    ? MathF.Sqrt(DistanceSquared(position, safeTarget))
                    : -1f;
                return new TPostPlantBotSnapshot(
                    player.Slot,
                    Alive: true,
                    player.PlayerPawn.Value!.Health,
                    DistanceToBomb: MathF.Sqrt(DistanceSquared(position, origin)),
                    DistanceToSafeTarget: distanceToSafeTarget);
            })
            .ToArray();
        var plan = TPostPlantPlanner.Plan(site, ResolveBombSecondsRemaining(), snapshots);
        _tPostPlantShouldRetreat = plan.ShouldRetreat;

        foreach (var assignment in plan.Assignments)
        {
            Vector offset = assignment.WatchTarget switch
            {
                TPostPlantWatchTarget.Bomb => new Vector(0f, 180f, 0f),
                TPostPlantWatchTarget.DefuseRoute => new Vector(240f, 0f, 0f),
                TPostPlantWatchTarget.OuterRoute => new Vector(-420f, -260f, 0f),
                _ => assignment.FormationIndex % 2 == 0
                    ? new Vector(-240f, 120f, 0f)
                    : new Vector(240f, 120f, 0f),
            };
            float spread = assignment.FormationIndex * 65f;
            offset.X += spread;
            offset.Y -= spread * 0.35f;
            Vector desired = new(origin.X + offset.X, origin.Y + offset.Y, origin.Z + offset.Z);
            Vector watchTarget = assignment.WatchTarget switch
            {
                TPostPlantWatchTarget.Bomb => new Vector(origin.X, origin.Y, origin.Z + 64f),
                TPostPlantWatchTarget.DefuseRoute => new Vector(
                    origin.X + 320f,
                    origin.Y,
                    origin.Z + 64f),
                TPostPlantWatchTarget.OuterRoute => new Vector(
                    origin.X - 520f,
                    origin.Y - 320f,
                    origin.Z + 64f),
                _ => new Vector(origin.X, origin.Y, origin.Z + 64f),
            };
            _tPostPlantWatchTargets[assignment.Slot] = watchTarget;
            var navArea = CCSNavArea.GetClosestNavArea(desired, 1200f)
                ?? CCSNavArea.GetClosestNavArea(origin, 1200f);
            if (navArea != null)
            {
                _tPostPlantTargets[assignment.Slot] = new Vector(
                    navArea.Center.X + offset.X * 0.20f,
                    navArea.Center.Y + offset.Y * 0.20f,
                    navArea.Center.Z);
            }
        }
    }

    private void InitializeTPostPlantRetreatTargets()
    {
        _tPostPlantRetreatTargets.Clear();
        var bomb = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
            .FirstOrDefault(entity => entity.IsValid && entity.AbsOrigin != null);
        if (bomb?.AbsOrigin is not { } origin)
            return;
        _cachedBombEntity = bomb;
        _cachedBombOrigin = origin;

        var candidatePositions = Utilities
            .FindAllEntitiesByDesignerName<CBaseEntity>("info_player_terrorist")
            .Where(entity => entity.IsValid && entity.AbsOrigin != null)
            .Select(entity => entity.AbsOrigin!)
            .ToList();

        // Spawn points are the most reliable map-specific safe anchors. Keep
        // geometric candidates as a fallback for maps/custom modes that do
        // not expose the usual T spawn entities.
        for (int index = 0; index < 8; index++)
        {
            float angle = index * MathF.PI / 4f;
            candidatePositions.Add(new Vector(
                origin.X + MathF.Cos(angle) * 1400f,
                origin.Y + MathF.Sin(angle) * 1400f,
                origin.Z));
        }

        var candidates = candidatePositions
            .Select(position => CCSNavArea.GetClosestNavArea(position, 1800f))
            .Where(area => area != null)
            .Select(area => new Vector(area!.Center.X, area.Center.Y, area.Center.Z))
            .DistinctBy(position => (
                MathF.Round(position.X),
                MathF.Round(position.Y),
                MathF.Round(position.Z)))
            .OrderByDescending(position => DistanceSquared(position, origin))
            .ToArray();
        if (candidates.Length == 0)
            return;

        var terrorists = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(player => player.IsValid
                && player.IsBot
                && player.PawnIsAlive
                && player.Team == CsTeam.Terrorist)
            .ToArray();
        var participants = new List<TPostPlantRetreatParticipant>();
        foreach (var player in terrorists)
        {
            var pawn = player.PlayerPawn?.Value;
            if (pawn?.AbsOrigin is { } playerOrigin)
            {
                participants.Add(new TPostPlantRetreatParticipant(
                    player.Slot,
                    new RetreatPosition(playerOrigin.X, playerOrigin.Y, playerOrigin.Z),
                    pawn.Health));
            }
        }

        var assignments = TPostPlantRetreatPlanner.AssignTargets(
            participants,
            candidates
                .Select(position => new RetreatPosition(position.X, position.Y, position.Z))
                .ToArray(),
            new RetreatPosition(origin.X, origin.Y, origin.Z));
        foreach (var assignment in assignments)
        {
            var target = assignment.Value;
            _tPostPlantRetreatTargets[assignment.Key] = new Vector(target.X, target.Y, target.Z);
        }
    }

    private bool ResolveCtPostPlantPathViable()
    {
        // This method is called from the 250 ms post-plant execution tick.
        // Anchor/Nav discovery belongs to round/event initialization; never
        // rebuild it from the hot path when the cache is empty. An empty cache
        // is deliberately treated as unknown and lets the native bot remain
        // the final safe behavior.
        return _tPostPlantSite == CtGambleSite.None
            ? _ctGambleTargets.Count > 0
            : _ctGambleTargets.ContainsKey(_tPostPlantSite);
    }

    private void RunCompetitiveTPostPlantTick(float now)
    {
        var bomb = _cachedBombEntity;
        if (!_tPostPlantActive || (bomb?.IsValid != true && _cachedBombOrigin == null))
        {
            _tPostPlantActive = false;
            _tPostPlantTargets.Clear();
            _tPostPlantRetreatTargets.Clear();
            _tPostPlantWatchTargets.Clear();
            _lastTPostPlantLook.Clear();
            _tPostPlantAssignmentsDirty = false;
            _lastTPostPlantAction = null;
            _lastTPostPlantReason = null;
            return;
        }

        _tPostPlantActive = true;
        var aliveTerroristBots = _competitiveBotPlayers.Values
            .Where(player => player.IsValid
                && player.PawnIsAlive
                && player.Team == CsTeam.Terrorist)
            .ToArray();
        if ((_tPostPlantRetreatTargets.Count == 0
                || aliveTerroristBots.Any(player => !_tPostPlantRetreatTargets.ContainsKey(player.Slot)))
            && now >= _nextTPostPlantRetreatRefreshAt)
        {
            InitializeTPostPlantRetreatTargets();
            _nextTPostPlantRetreatRefreshAt = now + 1f;
        }
        if (_tPostPlantAssignmentsDirty
            || _tPostPlantTargets.Count == 0
            && _cachedBombOrigin != null
            && now >= _nextTPostPlantTargetRefreshAt)
        {
            InitializeTPostPlantTargets(_tPostPlantSite);
            _nextTPostPlantTargetRefreshAt = now + 1f;
            _tPostPlantAssignmentsDirty = false;
        }

        var players = _competitiveBotPlayers.Values
            .Where(player => player.IsValid && player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
            .ToArray();
        int aliveT = players.Count(player => player.Team == CsTeam.Terrorist && player.PawnIsAlive);
        int aliveCt = players.Count(player => player.Team == CsTeam.CounterTerrorist && player.PawnIsAlive);
        var ctPlayers = players.Where(player => player.Team == CsTeam.CounterTerrorist);
        int bombSeconds = ResolveBombSecondsRemaining(out bool bombTimerKnown);
        var decision = TPostPlantPolicy.Evaluate(new TPostPlantContext(
            BombPlanted: true,
            bombSeconds,
            aliveT,
            aliveCt,
            PathViable: _tPostPlantTargets.Count > 0,
            CtDefusers: ctPlayers.Count(player => player.PawnIsAlive && HasDefuser(player)),
            CtRetakePathViable: ResolveCtPostPlantPathViable(),
            RetreatPathViable: _tPostPlantRetreatTargets.Count > 0,
            BombTimerKnown: bombTimerKnown));

        // Refresh the ordering when the team first commits to leaving. A Bot
        // may have taken damage while holding the post-plant, so the health
        // snapshot captured immediately after planting can be stale.
        if (decision.Action == TPostPlantAction.RetreatFromBomb
            && _lastTPostPlantAction != TPostPlantAction.RetreatFromBomb)
        {
            InitializeTPostPlantRetreatTargets();
        }

        if (_lastTPostPlantAction != decision.Action
            || _lastTPostPlantReason != decision.Reason)
        {
            if (_tacticalDebug)
            {
                BroadcastDebug(
                    $"[Smarter-Bot/Tactical] t-postplant action={decision.Action} "
                    + $"reason={decision.Reason} bomb={bombSeconds} "
                    + $"timer-known={bombTimerKnown} alive={aliveT}v{aliveCt} "
                    + $"site-targets={_tPostPlantTargets.Count} "
                    + $"retreat-targets={_tPostPlantRetreatTargets.Count}");
            }

            _lastTPostPlantAction = decision.Action;
            _lastTPostPlantReason = decision.Reason;
        }

        foreach (var player in players.Where(player => player.Team == CsTeam.Terrorist && player.IsBot))
        {
            var pawn = player.PlayerPawn?.Value;
            var bot = pawn?.IsValid == true ? pawn.Bot : null;
            if (bot == null || player.HasBeenControlledByPlayerThisRound)
                continue;

            TPostPlantAction action = decision.Action;
            if (bombTimerKnown
                && pawn.AbsOrigin is { } currentOrigin
                && _tPostPlantRetreatTargets.TryGetValue(player.Slot, out var safeTarget)
                && TPostPlantPlanner.ShouldRetreat(
                    MathF.Sqrt(DistanceSquared(currentOrigin, safeTarget)) / 250f,
                    safetyBufferSeconds: 3f,
                    bombSeconds))
            {
                action = TPostPlantAction.RetreatFromBomb;
            }

            var targetKind = TPostPlantExecutionPolicy.TargetKind(action);
            if (targetKind == TPostPlantTargetKind.None)
            {
                // Repath/Hold must not reuse a stale site or retreat target.
                // Restore native control and force its next route evaluation.
                _lastTacticalGoalWrite.Remove(player.Slot);
                ref bool recoveryActive = ref bot.AllowActive;
                recoveryActive = true;
                ref bool recoveryRunning = ref bot.IsRunning;
                recoveryRunning = true;
                CountdownTimer recoveryRepath = bot.RepathTimer;
                ref float recoveryDuration = ref recoveryRepath.Duration;
                recoveryDuration = 0f;
                ref float recoveryTimestamp = ref recoveryRepath.Timestamp;
                recoveryTimestamp = now;
                ref float recoveryTimescale = ref recoveryRepath.Timescale;
                recoveryTimescale = 1f;
                continue;
            }

            var targetMap = targetKind == TPostPlantTargetKind.Retreat
                ? _tPostPlantRetreatTargets
                : _tPostPlantTargets;
            if (!targetMap.TryGetValue(player.Slot, out var target)
                || bot.Handle == nint.Zero)
            {
                // A missing/invalid Nav target must never stop the native bot.
                ref bool fallbackActive = ref bot.AllowActive;
                fallbackActive = true;
                ref bool fallbackRunning = ref bot.IsRunning;
                fallbackRunning = true;
                continue;
            }

            ref bool isRunning = ref bot.IsRunning;
            isRunning = true;
            ref bool allowActive = ref bot.AllowActive;
            allowActive = true;
            if (targetKind == TPostPlantTargetKind.Site
                && _tPostPlantWatchTargets.TryGetValue(player.Slot, out var watchTarget))
            {
                ApplyTPostPlantAttention(player, bot, watchTarget, now);
            }
            float lastWrite = _lastTacticalGoalWrite.GetValueOrDefault(player.Slot, -999f);
            if (now - lastWrite >= 0.35f)
            {
                Schema.SetSchemaValue(bot.Handle, "CCSBot", "m_goalPosition", target);
                _lastTacticalGoalWrite[player.Slot] = now;
                CountdownTimer repath = bot.RepathTimer;
                ref float duration = ref repath.Duration;
                duration = 0f;
                ref float timestamp = ref repath.Timestamp;
                timestamp = now;
                ref float timescale = ref repath.Timescale;
                timescale = 1f;
            }
        }
    }

    private void ApplyTPostPlantAttention(
        CCSPlayerController player,
        CCSBot bot,
        Vector target,
        float now)
    {
        float lastLook = _lastTPostPlantLook.GetValueOrDefault(player.Slot, -999f);
        if (now - lastLook < 0.75f)
            return;

        var pawn = player.PlayerPawn?.Value;
        if (pawn?.IsValid != true || pawn.AbsOrigin is not { } origin)
            return;

        double dx = target.X - origin.X;
        double dy = target.Y - origin.Y;
        double dz = target.Z - (origin.Z + pawn.ViewOffset.Z);
        double horizontal = Math.Sqrt(dx * dx + dy * dy);
        if (horizontal < 1.0 && Math.Abs(dz) < 1.0)
            return;

        float yaw = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);
        float pitch = (float)(-Math.Atan2(dz, Math.Max(1.0, horizontal)) * 180.0 / Math.PI);
        try
        {
            // EyeAngles is a native read path in CSS. Teleport's angle-only
            // overload gives the bot an actual attention direction without
            // inventing an unsupported CCSBot schema field; the current
            // origin and velocity are preserved by the nullable Vector3
            // overload. This is throttled and only runs for a post-plant
            // target, never in the ordinary tactical scan.
            pawn.Teleport(
                null,
                new System.Numerics.Vector3(pitch, yaw, 0f),
                null);
            ref bool underPathFinderControl = ref bot.EyeAnglesUnderPathFinderControl;
            underPathFinderControl = true;
            _lastTPostPlantLook[player.Slot] = now;
        }
        catch
        {
            // Invalid/unavailable native state must fall back to the bot's
            // normal look controller; movement remains independently valid.
        }
    }

    private void ApplyCompetitiveTacticalActions(float now)
    {
        if (!IsCompetitiveProfile()) return;
        RefreshCtThreatDecay(now);
        RunCompetitiveTPostPlantTick(now);
        if (!_tacticalRolesInitialized)
        {
            if (!_isFreezeTime) InitializeTacticalRoles();
            return;
        }

        ApplyReadyCtDecisionPlan();
        if (_latestCtDecisions.Count == 0)
        {
            QueueCtDecisionPlan(now);
            return;
        }

        // Decision calculation is worker-owned. The game thread only submits
        // an immutable snapshot, consumes the latest versioned result and
        // applies already-confirmed native actions below.
        QueueCtDecisionPlan(now);
        var decisions = _latestCtDecisions;
        int activeCount = decisions.Count(decision => decision.IsActive);

        foreach (var decision in decisions)
        {
            if (_tacticalDebug
                && (!_lastTacticalDecisions.TryGetValue(decision.Slot, out var previous)
                    || previous.State != decision.State
                    || previous.Role != decision.Role
                    || previous.IsActive != decision.IsActive))
            {
                var contact = _tacticalRuntime.Context.LastContact;
                BroadcastDebug(
                    $"[Smarter-Bot/Tactical] slot={decision.Slot} role={decision.Role} "
                    + $"state={decision.State} phase={_tacticalRuntime.Context.CtBuyPhase} "
                    + $"gamble={_tacticalRuntime.Context.SelectedGambleSite} target={decision.TargetSite} "
                    + $"contact={(contact?.Confidence.ToString() ?? "None")} "
                    + $"move={(decision.ShouldMoveToGambleSite ? "stack" : decision.ShouldMoveToRetreat ? "retreat" : "none")} "
                    + $"active={activeCount} reason={decision.Reason} t={now:F1}");
            }

            _lastTacticalDecisions[decision.Slot] = decision;
            if (!_competitiveBotPlayers.TryGetValue(decision.Slot, out var player))
                continue;

            var pawn = player.PlayerPawn?.Value;
            if (pawn?.IsValid != true)
                continue;

            var bot = pawn.Bot;
            if (bot == null) continue;

            bool hasTacticalGoal = decision.ShouldMoveToRetreat
                || decision.ShouldMoveToGambleSite
                || (decision.State is CtTacticalState.Rotate or CtTacticalState.Reinforce
                    && decision.TargetSite != CtGambleSite.None);
            if (hasTacticalGoal)
            {
                bool hasGoal = TryApplyTacticalGoal(pawn, bot, decision, now);
                ref bool goalAllowActive = ref bot.AllowActive;
                goalAllowActive = hasGoal
                    ? CtTacticalExecutionPolicy.ShouldAllowNativeActive(decision)
                    : true;

                if (!hasGoal)
                {
                    ref bool recoveredRunning = ref bot.IsRunning;
                    recoveredRunning = true;
                    _saveModeSlots.Remove(decision.Slot);
                    Console.WriteLine(
                        $"[Smarter-Bot/Tactical] goal-recovery slot={decision.Slot} state={decision.State} reason={decision.Reason}");
                    continue;
                }

                if (decision.State == CtTacticalState.Save)
                {
                    _saveModeSlots.Add(decision.Slot);
                    ref bool saveAllowActive = ref bot.AllowActive;
                    saveAllowActive = true;
                    ref bool saveIsRunning = ref bot.IsRunning;
                    saveIsRunning = true;
                }

                if (decision.State == CtTacticalState.Withdraw
                    && HasReachedRetreatTarget(pawn))
                {
                    ref bool withdrawAllowActive = ref bot.AllowActive;
                    withdrawAllowActive = true;
                    ref bool withdrawRunning = ref bot.IsRunning;
                    withdrawRunning = true;
                    _saveModeSlots.Remove(decision.Slot);
                }

                if (decision.State == CtTacticalState.Save && hasGoal)
                    continue;

                if (decision.State is CtTacticalState.Withdraw or CtTacticalState.Rotate
                    or CtTacticalState.Reinforce
                    or CtTacticalState.Hold)
                    continue;
            }

            if (decision.State == CtTacticalState.Save)
            {
                _saveModeSlots.Add(decision.Slot);
                ref bool allowActive = ref bot.AllowActive;
                allowActive = true;
                ref bool saveIsRunning = ref bot.IsRunning;
                saveIsRunning = true;
                continue;
            }

            if (_saveModeSlots.Remove(decision.Slot))
            {
                ref bool allowActive = ref bot.AllowActive;
                allowActive = true;
            }

            if (!decision.IsActive
                || decision.State is not (CtTacticalState.Rotate
                    or CtTacticalState.Reinforce
                    or CtTacticalState.Retake))
                continue;

            ref bool isRunning = ref bot.IsRunning;
            isRunning = true;

            float lastRepath = _lastTacticalRepath.GetValueOrDefault(decision.Slot, -999f);
            if (decision.ShouldRepath && now - lastRepath >= 0.35f)
            {
                _lastTacticalRepath[decision.Slot] = now;
                CountdownTimer repathTimer = bot.RepathTimer;

                ref float duration = ref repathTimer.Duration;
                duration = 0.0f;
                ref float timestamp = ref repathTimer.Timestamp;
                timestamp = now;
                ref float timescale = ref repathTimer.Timescale;
                timescale = 1.0f;
            }
        }
    }

    private List<CtBotSnapshot> BuildCtBotSnapshots()
    {
        var snapshots = new List<CtBotSnapshot>();
        var seenSlots = new HashSet<int>();
        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid
                || !CompetitiveTacticalPolicy.ShouldTrackCtBot(
                    player.IsBot,
                    player.HasBeenControlledByPlayerThisRound)
                || player.Team is not (CsTeam.CounterTerrorist or CsTeam.Terrorist))
                continue;

            _competitiveBotPlayers[player.Slot] = player;
            seenSlots.Add(player.Slot);
            if (player.Team != CsTeam.CounterTerrorist)
                continue;

            float aggression = 0.70f;
            float teamwork = 1.0f;
            bool isAwper = false;
            if (_botController != null
                && BotControllerBridge.TryGetProfile(_botController, player.Slot, out var profile))
            {
                aggression = Math.Clamp(profile.Aggression, 0f, 1f);
                teamwork = Math.Clamp(profile.Teamwork, 0f, 1f);
                isAwper = profile.WeaponPref?.Take(Math.Max(0, profile.WeaponPrefCount)).Contains((ushort)9) == true;
            }

            var pawn = player.PlayerPawn?.Value;
            if (pawn?.IsValid == true
                && pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName == "weapon_awp")
                isAwper = true;

            snapshots.Add(new CtBotSnapshot(
                Slot: player.Slot,
                Alive: player.PawnIsAlive,
                Aggression: aggression,
                Teamwork: teamwork,
                IsAwper: isAwper)
            {
                HasValuableWeapon = HasValuableWeapon(player),
            });
        }

        foreach (int slot in _competitiveBotPlayers
                     .Where(entry => !seenSlots.Contains(entry.Key)
                         || !entry.Value.IsValid)
                     .Select(entry => entry.Key)
                     .ToArray())
            _competitiveBotPlayers.Remove(slot);

        return snapshots;
    }

    private void ConfigureTacticalEconomy(bool captureRoundFlags = false)
    {
        if (!IsCompetitiveProfile()) return;

        SynchronizeCompetitiveScore();
        _ctThreatEvaluation = CtThreatPlanner.Evaluate(
            _ctThreatEvents,
            Server.CurrentTime,
            ResolveCtMatchPressure(),
            _ctThreatEvaluation);
        _tacticalRuntime.SetThreat(_ctThreatEvaluation);

        var players = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(player => player.IsValid
                && (player.Team == CsTeam.CounterTerrorist || player.Team == CsTeam.Terrorist))
            .ToList();
        var ct = players.Where(player => player.Team == CsTeam.CounterTerrorist).ToList();
        var terrorists = players.Where(player => player.Team == CsTeam.Terrorist).ToList();
        if (ct.Count == 0) return;

        int ctAlive = ct.Count(player => player.PawnIsAlive);
        int tAlive = terrorists.Count(player => player.PawnIsAlive);
        int[] ctMoney = ct.Select(player => player.InGameMoneyServices?.Account ?? 0).ToArray();
        int[] tMoney = terrorists.Select(player => player.InGameMoneyServices?.Account ?? 0).ToArray();
        bool opponentEcoLikely = tMoney.Length > 0
            && tMoney.Count(money => money < 1800) >= Math.Max(1, tMoney.Length - 1);
        // Account balances are post-purchase state after BotBuy runs and can
        // also be low in a normal loss streak. The round schedule is the only
        // source of truth for pistol rounds.
        bool pistolRound = IsTacticalPistolRound();
        bool forceBuySignal = !pistolRound
            && ctMoney.Count(money => money >= BuyPlanner.KevlarPrice + 1250)
                >= Math.Max(1, (int)Math.Ceiling(ctMoney.Length * 0.60d))
            && ctMoney.Any(money => money >= 1900);

        if (captureRoundFlags && ctMoney.Length > 0 && tMoney.Length > 0)
        {
            // Capture both phases before BotBuy's delayed purchase plan runs.
            // OnTick must never reclassify a full/force buy from post-purchase
            // balances.
            _tacticalEconomy.Capture(
                new TeamEconomySnapshot(
                    TeamSide.CounterTerrorist,
                    ctMoney,
                    pistolRound,
                    IsLastRound: false,
                    forceBuySignal,
                    opponentEcoLikely),
                new TeamEconomySnapshot(
                    TeamSide.Terrorist,
                    tMoney,
                    pistolRound,
                    IsLastRound: false,
                    ForceBuySignal: false,
                    OpponentEcoLikely: false));
        }

        var ctPhase = _tacticalEconomy.IsCaptured
            ? _tacticalEconomy.CtPhase
            : _tacticalRuntime.Context.CtBuyPhase;
        var tPhase = _tacticalEconomy.IsCaptured
            ? _tacticalEconomy.OpponentPhase
            : _tacticalRuntime.Context.OpponentBuyPhase;
        _tacticalRuntime.SetEconomy(ctPhase, tPhase, ctAlive, tAlive);
        _tacticalRuntime.SetTeamDefuser(
            ct.Where(player => player.PawnIsAlive).Any(HasDefuser));
        var pressure = EvaluateCtMatchPressure();
        bool pathViable = _ctGambleTargets.Count >= 2 || ResolveCtTacticalAnchors();
        // CCSNavArea gives us a usable anchor, but this repository has no
        // native route-query API to prove connectivity from each living CT to
        // that anchor. Keep that uncertainty explicit so the retake policy
        // does not turn an unverified anchor into an impossible path.
        bool pathKnown = _ctGambleTargets.Count == 0;
        int bombSeconds = ResolveBombSecondsRemaining(out bool bombTimerKnown);
        _tacticalRuntime.SetRetakeInfo(
            bombSeconds,
            AverageWeaponTier(ct),
            AverageWeaponTier(terrorists),
            CountTeamUtility(ct),
            CountTeamUtility(terrorists),
            pathViable,
            pressure.Level,
            bombTimerKnown,
            pathKnown);
        _latestRoundSnapshot = CaptureRoundSnapshot(ctPhase, pressure);
    }

    private RoundSnapshot CaptureRoundSnapshot(
        BuyPhase buyPhase,
        MatchPressureResult pressure)
    {
        int roundsPlayed = CurrentRoundsPlayed();
        int maxRounds = ReadMaxRounds();
        int bombSeconds = ResolveBombSecondsRemaining(out bool timerKnown);
        float blowAt = _bombBlowAt;
        return new RoundSnapshot(
            new SnapshotVersion(_tacticalRoundKey, ++_roundSnapshotValue),
            new MatchFormatSnapshot(maxRounds, ReadOvertimeEnabled(), ReadOvertimeMaxRounds()),
            pressure,
            _tacticalRoundNumber,
            roundsPlayed,
            _counterTerroristScore,
            _terroristScore,
            TeamSide.CounterTerrorist,
            _tacticalRuntime.Context.Phase,
            buyPhase,
            TeamBuyModePolicy.Resolve(
                pressure.Level,
                buyPhase,
                canReachFullBuy: buyPhase == BuyPhase.FullBuy,
                forceSignal: buyPhase == BuyPhase.ForceBuy),
            new BombSnapshot(
                _tPostPlantActive,
                _tPostPlantSite switch
                {
                    CtGambleSite.A => "A",
                    CtGambleSite.B => "B",
                    _ => null,
                },
                _bombPlantedAt,
                blowAt,
                timerKnown,
                bombSeconds),
            new TeamThreatSnapshot(
                (float)_ctThreatEvaluation.SiteA,
                (float)_ctThreatEvaluation.SiteB,
                (float)_ctThreatEvaluation.Mid,
                _ctThreatEvaluation.LastEventAt,
                _ctThreatEvaluation.ConfirmedSite != CtGambleSite.None),
            Server.CurrentTime);
    }

    private bool IsTacticalPistolRound()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;

            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            int overtimeMaxRounds = ConVar.Find("mp_overtime_maxrounds")?.GetPrimitiveValue<int>() ?? 6;
            int roundsPlayed = gameRules?.TotalRoundsPlayed
                ?? Math.Max(0, _tacticalRoundNumber - 1);

            return RoundSchedule.IsFirstRoundOfHalf(
                roundsPlayed,
                maxRounds,
                overtimeMaxRounds);
        }
        catch
        {
            return false;
        }
    }

    private int CurrentRoundsPlayed()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
            return Math.Max(0, gameRules?.TotalRoundsPlayed ?? Math.Max(0, _tacticalRoundNumber - 1));
        }
        catch
        {
            return Math.Max(0, _tacticalRoundNumber - 1);
        }
    }

    private int ReadMaxRounds()
    {
        try
        {
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            return maxRounds > 0 ? maxRounds : 24;
        }
        catch
        {
            return 24;
        }
    }

    private bool ReadOvertimeEnabled()
    {
        try
        {
            var cvar = ConVar.Find("mp_overtime_enable");
            return cvar == null || cvar.GetPrimitiveValue<int>() != 0;
        }
        catch
        {
            return true;
        }
    }

    private int ReadOvertimeMaxRounds()
    {
        try
        {
            int overtimeMaxRounds = ConVar.Find("mp_overtime_maxrounds")?.GetPrimitiveValue<int>() ?? 6;
            return overtimeMaxRounds > 0 ? overtimeMaxRounds : 6;
        }
        catch
        {
            return 6;
        }
    }

    private void SynchronizeCompetitiveScore()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
            if (RoundScoreReader.TryRead(gameRules, out var score))
            {
                _terroristScore = score.Terrorist;
                _counterTerroristScore = score.CounterTerrorist;
            }
        }
        catch
        {
            // Keep event-based counters as a fallback for CSS builds that do
            // not expose team score fields through GameRules.
        }
    }

    private static int AverageWeaponTier(IEnumerable<CCSPlayerController> players)
    {
        var tiers = players
            .Where(player => player.PawnIsAlive)
            .Select(player => CurrentWeaponTier(player))
            .ToArray();
        return tiers.Length == 0 ? 0 : (int)Math.Round(tiers.Average());
    }

    private static int CurrentWeaponTier(CCSPlayerController player)
    {
        string? primary = player.PlayerPawn?.Value?.WeaponServices?.MyWeapons
            .Select(handle => handle.Value?.DesignerName)
            .FirstOrDefault(BuyPlanner.IsPrimaryWeapon);
        return BuyPlanner.GetTier(ArmorLevel.Full, primary, null);
    }

    private static int CountTeamUtility(IEnumerable<CCSPlayerController> players)
        => players.Where(player => player.PawnIsAlive).Sum(player =>
            CountWeapon(player, "weapon_smokegrenade")
            + CountWeapon(player, "weapon_flashbang")
            + CountWeapon(player, "weapon_hegrenade")
            + CountWeapon(player, "weapon_molotov")
            + CountWeapon(player, "weapon_incgrenade"));

    private static int CountWeapon(CCSPlayerController player, string designerName)
        => player.PlayerPawn?.Value?.WeaponServices?.MyWeapons
            .Count(handle => handle.Value?.DesignerName == designerName) ?? 0;

    private int ResolveBombSecondsRemaining()
        => ResolveBombSecondsRemaining(out _);

    private int ResolveBombSecondsRemaining(out bool timerKnown)
    {
        timerKnown = false;
        if (_bombBlowAt > 0f)
        {
            timerKnown = true;
            return Math.Max(0, (int)MathF.Ceiling(_bombBlowAt - Server.CurrentTime));
        }

        // A hot reload can happen after the plant event. Resolve the entity
        // timer once, then cache blowAt; ordinary tactical ticks only subtract
        // Server.CurrentTime and never reflect-walk the bomb schema.
        if (_cachedBombEntity?.IsValid == true
            && BombTimerPolicy.TryResolveSeconds(
                _cachedBombEntity,
                Server.CurrentTime,
                out int entitySeconds))
        {
            _bombPlantedAt = _bombPlantedAt > 0f ? _bombPlantedAt : Server.CurrentTime;
            _bombBlowAt = Server.CurrentTime + entitySeconds;
            timerKnown = true;
            return Math.Max(0, entitySeconds);
        }

        if (_bombPlantedAt > 0f)
        {
            _bombBlowAt = _bombPlantedAt + ReadBombTimerSeconds();
            timerKnown = true;
            return Math.Max(0, (int)MathF.Ceiling(_bombBlowAt - Server.CurrentTime));
        }

        if (_cachedBombEntity?.IsValid == true)
        {
            _bombPlantedAt = Server.CurrentTime;
            _bombBlowAt = _bombPlantedAt + ReadBombTimerSeconds();
            timerKnown = true;
            return ReadBombTimerSeconds();
        }

        return 0;
    }

    private int ReadBombTimerSeconds()
    {
        try
        {
            int seconds = ConVar.Find("mp_c4timer")?.GetPrimitiveValue<int>() ?? 40;
            return seconds > 0 ? seconds : 40;
        }
        catch
        {
            return 40;
        }
    }

    private RoundContext CreateTacticalRoundContext(RoundPhase phase)
    {
        SynchronizeCompetitiveScore();
        int roundsPlayed = CurrentRoundsPlayed();
        int maxRounds = ReadMaxRounds();
        var pressure = EvaluateCtMatchPressure();
        return new(
            RoundNumber: _tacticalRoundNumber,
            Half: roundsPlayed < maxRounds / 2 ? 1 : roundsPlayed < maxRounds ? 2 : 3,
            TeamScore: _counterTerroristScore,
            OpponentScore: _terroristScore,
            IsLastRound: RoundSchedule.IsLastRegulationRound(
                _counterTerroristScore,
                _terroristScore,
                roundsPlayed,
                maxRounds),
            ConsecutiveLosses: 0,
            BombPlanted: phase is RoundPhase.BombPlanted or RoundPhase.Retake,
            KnownBombsite: null,
            AliveTeam: 5,
            AliveOpponent: 5,
            Phase: phase)
        {
            Pressure = pressure.Level,
        };
    }

    private void PrintTacticalSnapshot()
    {
        if (!_tacticalRolesInitialized)
        {
            BroadcastDebug("[Smarter-Bot/Tactical] roles are not initialized");
            return;
        }

        foreach (var entry in _tacticalRuntime.Roles.OrderBy(entry => entry.Key))
        {
            _lastTacticalDecisions.TryGetValue(entry.Key, out var decision);
            BroadcastDebug(
                $"[Smarter-Bot/Tactical] slot={entry.Key} role={entry.Value} "
                + $"state={(decision?.State.ToString() ?? "Setup")} "
                + $"phase={_tacticalRuntime.Context.CtBuyPhase} "
                + $"gamble={_tacticalRuntime.Context.SelectedGambleSite} "
                + $"target={(decision?.TargetSite.ToString() ?? "None")} "
                + $"active={(decision?.IsActive == true ? 1 : 0)}");
        }
    }
    //---------------------------------------------------------------------------------------
    // Clears per-round state and releases elimination knife locks
    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ClearSaveMode();
        _ctGambleTargets.Clear();
        _ctEcoTargets.Clear();
        _ctEcoSnapshotValue = 0;
        _ctEcoPlanValue = 0;
        _roundSnapshotValue = 0;
        _lastCtEcoTactic = null;
        _lastCtEcoSite = CtGambleSite.None;
        while (_ctEcoPlanResults.TryDequeue(out _)) { }
        _ctThreatEvents.Clear();
        _ctThreatEvaluation = default;
        _nextCtThreatDecayAt = 0f;
        _ctRetreatTarget = null;
        _tPostPlantShouldRetreat = false;
        _bombPlantedAt = 0f;
        _bombBlowAt = 0f;
        _cachedBombEntity = null;
        _cachedBombOrigin = null;
        _ctGambleFallbackLogged = false;
        _lastTacticalGoalWrite.Clear();
        ReleaseKnifeLocks();
        StopDefuseSmoke();
        _isSmokeExpanded = false;
        _eliminationHandled = false;
        _isFreezeTime = true;
        _tacticalRoundNumber++;
        _tacticalRoundKey = ResolveTacticalRoundKey();
        _competitiveBotPlayers.Clear();
        _ctEcoPlanningWorker.Reset(new PlanVersion(_tacticalRoundKey, 0, 0));
        _ctDecisionSnapshotValue = 0;
        _ctDecisionPlanValue = 0;
        _ctDecisionDirty = true;
        _ctDecisionPlanningWorker.Reset(new PlanVersion(_tacticalRoundKey, 0, 0));
        while (_ctDecisionResults.TryDequeue(out _)) { }
        _latestCtDecisions = Array.Empty<CtTacticalDecision>();
        _tacticalLiveStartedAt = null;
        _tacticalRolesInitialized = false;
        _nextTacticalTick = 0f;
        _lastTacticalDecisions.Clear();
        _lastTacticalRepath.Clear();
        _lastTacticalPosition.Clear();
        _tacticalStuckSince.Clear();
        _tPostPlantTargets.Clear();
        _tPostPlantRetreatTargets.Clear();
        _tPostPlantWatchTargets.Clear();
        _lastTPostPlantLook.Clear();
        _tPostPlantActive = false;
        _tPostPlantAssignmentsDirty = false;
        _nextTPostPlantTargetRefreshAt = 0f;
        _nextTPostPlantRetreatRefreshAt = 0f;
        _tPostPlantSite = CtGambleSite.None;
        _lastTPostPlantAction = null;
        _lastTPostPlantReason = null;
        _tacticalEconomy.Reset();
        if (IsCompetitiveProfile())
        {
            _tacticalRuntime.Reset(CreateTacticalRoundContext(RoundPhase.Freeze));
            ConfigureTacticalEconomy(captureRoundFlags: true);
            CaptureTacticalEconomyCheckpoint();
        }

        // Per-round transient state keyed by player index. Indices are reused by
        // later connections, so stale entries would leak last round's movement /
        // stuck / door state onto a different bot.
        _prevInAir.Clear();
        _lastForwardDir.Clear();
        _ladderExitTime.Clear();
        _lastLateralDir.Clear();
        _doorEventCooldown.Clear();
        _stuckStartTime.Clear();
        _stuckStartPos.Clear();
        _stuckJumpDone.Clear();
        _stuckJumpCount.Clear();
        _stuckMaxSpeed.Clear();
        _idleStartTime.Clear();
        _lastRepathTime.Clear();
        _hasFiredThisAttack.Clear();
        _prevIsAttacking.Clear();
        _cachedInAir.Clear();
        _cachedNearLadder.Clear();

        // Flash projectiles never survive a round transition; drop their tracking
        // so entity indices reused next round don't match stale decisions.
        _flashThrownAt.Clear();
        _flashRolledByBot.Clear();
        _flashDecisions.Clear();
        _flashRejectLogged.Clear();
        return HookResult.Continue;
    }

    [GameEventHandler]
    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (IsCompetitiveProfile())
        {
            TeamSide? winner = ResolveRoundWinner(@event);
            if (winner == TeamSide.Terrorist)
                _terroristScore++;
            else if (winner == TeamSide.CounterTerrorist)
                _counterTerroristScore++;

            _tacticalRuntime.SetPhase(RoundPhase.RoundEnd);
            _tacticalRolesInitialized = false;
            _competitiveBotPlayers.Clear();
            InvalidateCtDecisionPlan();
            _latestCtDecisions = Array.Empty<CtTacticalDecision>();
            while (_ctDecisionResults.TryDequeue(out _)) { }
            _ctGambleTargets.Clear();
            _ctEcoTargets.Clear();
            _ctRetreatTarget = null;
            _ctGambleFallbackLogged = false;
            _lastTacticalGoalWrite.Clear();
            _lastTacticalPosition.Clear();
            _tacticalStuckSince.Clear();
            _tPostPlantTargets.Clear();
            _tPostPlantRetreatTargets.Clear();
            _tPostPlantActive = false;
            _tPostPlantShouldRetreat = false;
            _nextTPostPlantTargetRefreshAt = 0f;
            _nextTPostPlantRetreatRefreshAt = 0f;
            _bombPlantedAt = 0f;
            _bombBlowAt = 0f;
            _cachedBombEntity = null;
            _cachedBombOrigin = null;
            _tPostPlantSite = CtGambleSite.None;
            _lastTPostPlantAction = null;
            _lastTPostPlantReason = null;
        }

        return HookResult.Continue;
    }

    private static TeamSide? ResolveRoundWinner(EventRoundEnd @event)
    {
        try
        {
            int winner = Convert.ToInt32(((dynamic)@event).Winner);
            return winner switch
            {
                (int)CsTeam.Terrorist => TeamSide.Terrorist,
                (int)CsTeam.CounterTerrorist => TeamSide.CounterTerrorist,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    // Detects elimination while explicitly excluding the current death victim
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        if (victim == null || !victim.IsValid)
            return HookResult.Continue;

        CsTeam victimTeam = (CsTeam)(int)victim.TeamNum;
        if (victimTeam != CsTeam.Terrorist &&
            victimTeam != CsTeam.CounterTerrorist)
            return HookResult.Continue;

        if (IsCompetitiveProfile())
        {
            InvalidateCtDecisionPlan();
            if (_tPostPlantActive && victimTeam == CsTeam.Terrorist)
            {
                // A BombGuard/Interceptor loss changes the minimum viable
                // post-plant formation. Reassign on the next tactical pass
                // from the alive snapshot instead of leaving a dead slot's
                // target in the cache.
                _tPostPlantAssignmentsDirty = true;
                _nextTPostPlantTargetRefreshAt = 0f;
            }
            if (_tacticalRolesInitialized)
            {
                var snapshots = BuildCtBotSnapshots();
                _tacticalRuntime.UpdateBotSnapshots(snapshots);
                ConfigureTacticalEconomy();
            }
        }

        if (CompetitiveTacticalPolicy.ShouldRecordCtDeath(
                _profile,
                victimTeam == CsTeam.CounterTerrorist && victim.IsBot,
                victim.HasBeenControlledByPlayerThisRound))
        {
            CtGambleSite deathSite = victim.PlayerPawn?.Value?.AbsOrigin is { } victimOrigin
                ? ResolveContactSite(victimOrigin)
                : CtGambleSite.None;
            _tacticalRuntime.RecordCtDeath(victim.Slot, Server.CurrentTime, deathSite);
            var attacker = @event.Attacker;
            if (attacker != null && attacker.IsValid && attacker.Team == CsTeam.Terrorist)
            {
                if (attacker.PlayerPawn?.Value?.AbsOrigin is { } attackerOrigin)
                    RecordCtThreat(CtThreatEventKind.CtDeath, ResolveContactSite(attackerOrigin));
                RecordTacticalContact(attacker, ContactConfidence.High);
            }
        }

        if (_eliminationHandled || _botController == null)
            return HookResult.Continue;

        HandleTeamElimination(victim.Slot, victimTeam);

        return HookResult.Continue;
    }

    // Locks every surviving Bot on the winning team to its knife slot
    private void HandleTeamElimination(int victimSlot, CsTeam victimTeam)
    {
        if (_eliminationHandled || _botController == null) return;

        var activePlayers = Utilities.GetPlayers()
            .Where(player => player.IsValid
                && !player.IsHLTV
                && ((int)player.TeamNum == (int)CsTeam.Terrorist
                    || (int)player.TeamNum == (int)CsTeam.CounterTerrorist))
            .ToList();

        bool victimTeamHasSurvivor = activePlayers.Any(player =>
            player.Slot != victimSlot
            && (int)player.TeamNum == (int)victimTeam
            && player.PawnIsAlive);
        if (victimTeamHasSurvivor) return;

        CsTeam winningTeam = victimTeam == CsTeam.Terrorist
            ? CsTeam.CounterTerrorist
            : CsTeam.Terrorist;
        var winningBots = activePlayers.Where(player =>
                player.IsBot
                && player.PawnIsAlive
                && !player.HasBeenControlledByPlayerThisRound
                && (int)player.TeamNum == (int)winningTeam)
            .ToList();

        bool winningTeamAlive = activePlayers.Any(player =>
            (int)player.TeamNum == (int)winningTeam && player.PawnIsAlive);
        if (!winningTeamAlive) return;

        _eliminationHandled = true;

        foreach (var bot in winningBots)
        {
            bool switched = BotControllerBridge.SwitchBotWeapon(
                _botController,
                bot.Slot, KnifeDefinitionIndex);
            bool locked = BotControllerBridge.LockKnife(
                _botController, bot.Slot);
            if (locked)
                _knifeLockedBotSlots.Add(bot.Slot);

            if (!switched || !locked)
            {
                Console.WriteLine(
                    $"[Smarter-Bot] Knife action failed for slot {bot.Slot}: switch={switched}, lock={locked}");
            }
        }

    }

    // Releases only Slot3 locks successfully applied by this plugin
    private void ReleaseKnifeLocks()
    {
        if (_botController != null)
        {
            foreach (int slot in _knifeLockedBotSlots)
            {
                if (BotControllerBridge.IsKnifeLocked(_botController, slot))
                    BotControllerBridge.UnlockWeapon(_botController, slot);
            }
        }

        _knifeLockedBotSlots.Clear();
    }

    // Isolates optional BotControllerApi types from the main plugin type
    private static class BotControllerBridge
    {
        // Resolves the optional BotController capability at runtime
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object? TryGet()
        {
            var capability =
                new PluginCapability<BotControllerApi.IBotControllerApi>(
                    "botcontroller:api");
            return capability.Get();
        }

        // Switches one Bot to its knife definition
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool SwitchBotWeapon(object api, int slot, int defIndex)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .SwitchBotWeapon(slot, defIndex);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool TryGetProfile(
            object api,
            int slot,
            out BotControllerApi.BotProfileData profile)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .GetBotProfile(slot, out profile);
        }

        // Applies the knife-slot weapon lock to one Bot
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool LockKnife(object api, int slot)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .Lock(slot, BotControllerApi.LockTarget.Slot3);
        }

        // Checks whether one Bot still has the knife-slot lock
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsKnifeLocked(object api, int slot)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .GetWeaponLock(slot) == BotControllerApi.LockTarget.Slot3;
        }

        // Releases the weapon lock from one Bot
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool UnlockWeapon(object api, int slot)
        {
            return ((BotControllerApi.IBotControllerApi)api)
                .Unlock(slot, BotControllerApi.LockKind.Weapon);
        }
    }

    private HookResult OnDoorOpen(EventDoorOpen @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot) return HookResult.Continue;

        int idx = (int)player.Index;
        _doorEventCooldown[idx] = Server.CurrentTime + 1.0f;
        return HookResult.Continue;
    }

    private HookResult OnDoorClose(EventDoorClose @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot) return HookResult.Continue;

        int idx = (int)player.Index;
        _doorEventCooldown[idx] = Server.CurrentTime + 1.0f;
        return HookResult.Continue;
    }

    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var shooter = @event.Userid;
        if (shooter == null || !shooter.IsValid) return HookResult.Continue;

        if (IsCompetitiveProfile() && shooter.Team == CsTeam.Terrorist)
        {
            if (shooter.PlayerPawn?.Value?.AbsOrigin is { } shooterOrigin)
                RecordCtThreat(CtThreatEventKind.Sound, ResolveContactSite(shooterOrigin));
            RecordTacticalContact(shooter, ContactConfidence.Low);
        }

        if (!shooter.IsBot) return HookResult.Continue;

        int idx = (int)shooter.Index;
        var pawn = shooter.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return HookResult.Continue;

        var bot = pawn.Bot;
        if (bot == null) return HookResult.Continue;
        // Sniper Peek
        _hasFiredThisAttack.Add(idx);

        // Counter-strafe on fire
        bool cachedInAir = _cachedInAir.GetValueOrDefault(idx, false);
        bool cachedNearLadder = _cachedNearLadder.GetValueOrDefault(idx, false);
        if (!cachedInAir && !cachedNearLadder)
        {
            string? wpnFire = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
            if (wpnFire != null)
            {
                float vx = pawn.AbsVelocity.X;
                float vy = pawn.AbsVelocity.Y;
                float speed2D = MathF.Sqrt(vx * vx + vy * vy);

                if (wpnFire is "weapon_glock" or "weapon_hkp2000" or "weapon_p250"
                            or "weapon_fiveseven" or "weapon_cz75a" or "weapon_tec9"
                            or "weapon_mac10" or "weapon_mp9")
                {
                    if (speed2D > 70f)
                    {
                        float scale = 70f / speed2D;
                        pawn.AbsVelocity.X = vx * scale;
                        pawn.AbsVelocity.Y = vy * scale;
                    }
                }
                else if (wpnFire is "weapon_usp_silencer" or "weapon_deagle"
                                or "weapon_ssg08" or "weapon_awp"
                                or "weapon_scar20" or "weapon_g3sg1"
                                or "weapon_galilar" or "weapon_ak47" or "weapon_sg556"
                                or "weapon_famas" or "weapon_m4a1" or "weapon_m4a1_silencer"
                                or "weapon_aug" or "weapon_m249" or "weapon_negev")
                {
                    if (speed2D > 0f)
                    {
                        pawn.AbsVelocity.X = 0f;
                        pawn.AbsVelocity.Y = 0f;
                    }
                }
                // Other weapons: no speed change
            }
        }

        if (pawn.IsDefusing || !bot.IsAttacking) return HookResult.Continue;
        // Random combat crouch
        double crouchChance = 0.0;
        string? wpn = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
        if (wpn != null)
        {
            if (wpn is "weapon_glock" or "weapon_hkp2000" or "weapon_p250" or "weapon_fiveseven")
                crouchChance = 0.20;

            else if (wpn is "weapon_usp_silencer" or "weapon_deagle")
                crouchChance = 0.30;

            else if (wpn is "weapon_elite" or "weapon_tec9" or "weapon_cz75a" or "weapon_revolver"
                    or "weapon_scar20" or "weapon_g3sg1")
                crouchChance = 0.10;

            else if (wpn is "weapon_mac10" or "weapon_mp9" or "weapon_bizon")
                crouchChance = 0.03;

            else if (wpn is "weapon_mp5sd" or "weapon_ump45" or "weapon_p90"
                    or "weapon_nova" or "weapon_xm1014" or "weapon_sawedoff" or "weapon_mag7"
                    or "weapon_ssg08" or "weapon_awp")
                crouchChance = 0.05;

            else if (wpn is "weapon_galilar" or "weapon_ak47" or "weapon_sg556"
                    or "weapon_famas" or "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_aug"
                    or "weapon_m249")
                crouchChance = 0.50;

            else if (wpn == "weapon_negev")
                crouchChance = 0.90;
        }

        ref bool isCrouching = ref bot.IsCrouching;
        isCrouching = _random.NextDouble() < crouchChance;

        CountdownTimer sneakTimer = bot.SneakTimer;

        ref float sneakduration = ref sneakTimer.Duration;
        sneakduration = 0.0f;

        ref float sneaktimestamp = ref sneakTimer.Timestamp;
        sneaktimestamp = 0.0f;

        ref float sneaktimescale = ref sneakTimer.Timescale;
        sneaktimescale = 1.0f;

        return HookResult.Continue;
    }

    private HookResult OnGrenadeThrown(EventGrenadeThrown @event, GameEventInfo info)
    {
        var thrower = @event.Userid;
        if (!IsCompetitiveProfile()
            || thrower == null
            || !thrower.IsValid
            || thrower.Team != CsTeam.Terrorist)
            return HookResult.Continue;

        if (thrower.PlayerPawn?.Value?.AbsOrigin is { } origin)
            RecordCtThreat(
                CtThreatEventKind.AttackUtility,
                ResolveContactSite(origin));
        return HookResult.Continue;
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        if (IsCompetitiveProfile())
        {
            _bombPlantedAt = Server.CurrentTime;
            _cachedBombEntity = Utilities
                .FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
                .FirstOrDefault(entity => entity.IsValid);
            _cachedBombOrigin = _cachedBombEntity?.AbsOrigin;
            _bombBlowAt = _cachedBombEntity?.IsValid == true
                && BombTimerPolicy.TryResolveSeconds(
                    _cachedBombEntity,
                    _bombPlantedAt,
                    out int entitySeconds)
                ? _bombPlantedAt + entitySeconds
                : _bombPlantedAt + ReadBombTimerSeconds();
            var plantedTarget = Utilities
                .FindAllEntitiesByDesignerName<CBombTarget>("func_bomb_target")
                .FirstOrDefault(target => target.IsValid && target.BombPlantedHere);
            if (plantedTarget != null)
            {
                _tPostPlantSite = plantedTarget.IsBombSiteB ? CtGambleSite.B : CtGambleSite.A;
                InitializeTPostPlantRetreatTargets();
                InitializeTPostPlantTargets(_tPostPlantSite);
                _tacticalRuntime.SetBomb(
                    _tPostPlantSite,
                    planted: true);
            }
            else
            {
                _tPostPlantSite = CtGambleSite.None;
                InitializeTPostPlantRetreatTargets();
                InitializeTPostPlantTargets(CtGambleSite.None);
                _tacticalRuntime.SetBomb(planted: true);
            }

            RecordCtThreat(CtThreatEventKind.BombFound, _tPostPlantSite);

            _tPostPlantActive = true;
            _tPostPlantAssignmentsDirty = true;
            _nextTPostPlantTargetRefreshAt = Server.CurrentTime + 1f;
            _nextTPostPlantRetreatRefreshAt = Server.CurrentTime + 1f;

            return HookResult.Continue;
        }

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot)
                continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            var bot = pawn.Bot;
            if (bot == null)
                continue;

            bool isTakenOver = player.HasBeenControlledByPlayerThisRound;
            if (isTakenOver) continue;

            CountdownTimer hurryTimer = bot.HurryTimer;

            ref float duration = ref hurryTimer.Duration;
            duration = 40.0f;

            ref float timestamp = ref hurryTimer.Timestamp;
            timestamp = Server.CurrentTime + duration;

            ref float timescale = ref hurryTimer.Timescale;
            timescale = 1.0f;

            ref bool isRunning = ref bot.IsRunning;
            isRunning = true;
        }
        return HookResult.Continue;
    }

    private HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
        ResetLookAroundForBot(@event.Userid);

        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot) return HookResult.Continue;

        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return HookResult.Continue;

        var bot = pawn.Bot;
        if (bot == null) return HookResult.Continue;

        bool isTakenOver = player.HasBeenControlledByPlayerThisRound;
        if (isTakenOver) return HookResult.Continue;

        bool hasLivingEnemies = Utilities
            .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Any(p => p.IsValid && p.PawnIsAlive
                && ((int)p.TeamNum == 2 || (int)p.TeamNum == 3)
                && (int)p.TeamNum != (int)player.TeamNum);
        var itemSvc = pawn.ItemServices?.Handle != nint.Zero
            ? new CCSPlayer_ItemServices(pawn.ItemServices!.Handle)
            : null;
        bool hasDefuser = itemSvc?.HasDefuser ?? false;

        // Keep a bounded fake-defuse chance in every profile when an enemy is
        // still alive. Competitive uses the same small human-like chance as
        // the legacy behavior; it is not a guaranteed interruption.
        if (DefuseDecisionPolicy.ShouldFakeDefuse(
                _profile,
                hasLivingEnemies,
                hasDefuser,
                _random.NextDouble()))
        {
            float yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
            float rx = -MathF.Sin(yaw);
            float ry = MathF.Cos(yaw);
            float side = _random.NextDouble() < 0.5 ? 1f : -1f;

            pawn.AbsVelocity.X += rx * side * 150f;
            pawn.AbsVelocity.Y += ry * side * 150f;
            pawn.AbsVelocity.Z += 255f;

            ResetLookAroundForBot(player);
        }

        if (!IsCompetitiveProfile())
        {
            _isBombBeingDefused = true;
            StartDefuseSmokeCycle();
        }

        return HookResult.Continue;
    }

    private static void ResetLookAroundForBot(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || !player.IsBot) return;
        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return;
        var bot = pawn.Bot;
        if (bot == null) return;

        ref float inhibitLookAroundTimestamp = ref bot.InhibitLookAroundTimestamp;
        inhibitLookAroundTimestamp = 0f;

        ref int checkedHidingSpotCount = ref bot.CheckedHidingSpotCount;
        checkedHidingSpotCount = 0;

        ref float lookAroundStateTimestamp = ref bot.LookAroundStateTimestamp;
        lookAroundStateTimestamp = 0f;
    }
    //---------------------------------------------------------------------------------------
    private static void ApplyBotState(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return;

        var bot = pawn.Bot;
        if (bot == null) return;

        ref float safeTime = ref bot.SafeTime;
        safeTime = 0f;

        ref bool hasVisitedEnemySpawn = ref bot.HasVisitedEnemySpawn;
        hasVisitedEnemySpawn = true;
    }

    private void ClearSaveMode()
    {
        if (_saveModeSlots.Count == 0)
            return;

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot || !_saveModeSlots.Contains(player.Slot))
                continue;

            var pawn = player.PlayerPawn?.Value;
            var bot = pawn?.IsValid == true ? pawn.Bot : null;
            if (bot == null) continue;

            ref bool allowActive = ref bot.AllowActive;
            allowActive = true;
        }

        _saveModeSlots.Clear();
    }

    private static bool HasValuableWeapon(CCSPlayerController player)
    {
        string? primary = player.PlayerPawn?.Value?.WeaponServices?.MyWeapons
            ?.Select(handle => handle.Value?.DesignerName)
            .FirstOrDefault(BuyPlanner.IsPrimaryWeapon);
        return WeaponValuePolicy.IsHighValue(primary);
    }

    private static bool HasDefuser(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn?.Value;
        if (pawn?.ItemServices == null || pawn.ItemServices.Handle == nint.Zero)
            return false;

        var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices!.Handle);
        return itemServices.HasDefuser;
    }
    //---------------------------------------------------------------------------------------
    private bool IsReloading(CCSPlayerController player)
    {
        if (player == null || !player.IsValid)
            return false;

        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid)
            return false;

        var activeWeapon = pawn.WeaponServices?.ActiveWeapon?.Value;
        if (activeWeapon == null || !activeWeapon.IsValid)
            return false;

        return Schema.GetRef<bool>(activeWeapon.Handle, "CCSWeaponBase", "m_bInReload");
    }
    //---------------------------------------------------------------------------------------
    // Pre-roll flash avoidance per (bot, flash). On the first tick the bot can both see
    // (FOV + raytrace LOS) the flash projectile, draw against the time-to-detonate tier
    // probability. The result is consumed in OnPlayerBlind.
    private void ProcessFlashbangAvoidance()
    {
        if (_rayTrace == null || _scratchEye == null) return;

        float now = Server.CurrentTime;

        var live = new List<(uint idx, Vector pos, float detonateAt)>();
        foreach (var ent in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("flashbang_projectile"))
        {
            if (!ent.IsValid) continue;
            var pos = ent.AbsOrigin;
            if (pos == null) continue;

            uint eidx = ent.Index;
            bool isNew = !_flashThrownAt.ContainsKey(eidx);
            if (isNew)
            {
                _flashThrownAt[eidx] = now;
                if (_debugFlash)
                    BroadcastDebug($"[Smarter-Bot/Flash] new flash#{eidx} at ({pos.X:F0},{pos.Y:F0},{pos.Z:F0}) fuse={FlashFuseSeconds:F2}s");
            }
            live.Add((eidx, pos, _flashThrownAt[eidx] + FlashFuseSeconds));
        }

        // Drop tracking for flashes that no longer exist (detonated / round end). Decisions
        // linger for 2 seconds past detonation so OnPlayerBlind can still match them.
        if (_flashThrownAt.Count > live.Count)
        {
            var alive = new HashSet<uint>(live.Select(f => f.idx));
            var stale = _flashThrownAt.Keys.Where(k => !alive.Contains(k)).ToList();
            foreach (var k in stale)
            {
                if (_debugFlash)
                {
                    foreach (var key in _flashDecisions.Keys.Where(p => p.flash == k).ToList())
                    {
                        var d = _flashDecisions[key];
                        BroadcastDebug(
                            $"[Smarter-Bot/Flash] flash#{k} ended; bot#{key.bot} visible {(d.LastSeen - d.FirstSeen) * 1000f:F0}ms");
                    }
                }
                _flashThrownAt.Remove(k);
                foreach (var s in _flashRolledByBot.Values) s.Remove(k);
                _flashRejectLogged.RemoveWhere(p => p.flash == k);
            }
        }

        // Expire stale decisions (2s past their detonation) so we don't leak across rounds.
        if (_flashDecisions.Count > 0)
        {
            var expired = _flashDecisions.Where(kvp => now - kvp.Value.DetonateAt > 2f)
                                          .Select(kvp => kvp.Key)
                                          .ToList();
            foreach (var k in expired) _flashDecisions.Remove(k);
        }

        if (live.Count == 0) return;

        foreach (var bot in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!bot.IsValid || !bot.IsBot) continue;
            if (bot.HasBeenControlledByPlayerThisRound) continue;

            var pawn = bot.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;

            int bidx = (int)bot.Index;
            if (!_flashRolledByBot.TryGetValue(bidx, out var rolled))
            {
                rolled = new HashSet<uint>();
                _flashRolledByBot[bidx] = rolled;
            }

            foreach (var (fidx, fpos, detonateAt) in live)
            {
                if (now > detonateAt) continue;

                bool inFov = IsInFov(pawn, fpos, FlashFovHorizDeg, FlashFovVertDeg,
                                     out float dYaw, out float dPit);
                if (!inFov)
                {
                    if (_debugFlash && _flashRejectLogged.Add((bidx, fidx)))
                        BroadcastDebug($"[Smarter-Bot/Flash] bot={bot.PlayerName} flash#{fidx} REJECT-FOV dYaw={dYaw:F1} dPit={dPit:F1}");
                    continue;
                }
                if (!BotCanSee(pawn, fpos))
                {
                    if (_debugFlash && _flashRejectLogged.Add((bidx, fidx)))
                        BroadcastDebug($"[Smarter-Bot/Flash] bot={bot.PlayerName} flash#{fidx} REJECT-LOS dYaw={dYaw:F1} dPit={dPit:F1}");
                    continue;
                }
                _flashRejectLogged.Remove((bidx, fidx));

                var key = (bidx, fidx);

                if (rolled.Contains(fidx))
                {
                    // Already rolled — refresh lastSeen so visible duration reflects full sight window
                    if (_flashDecisions.TryGetValue(key, out var d))
                    {
                        d.LastSeen = now;
                        _flashDecisions[key] = d;
                    }
                    continue;
                }
                rolled.Add(fidx);

                float msLeft = (detonateAt - now) * 1000f;
                double prob = msLeft <= 150f ? 0.05
                            : msLeft <= 250f ? 0.20
                            : msLeft <= 400f ? 0.50
                            : msLeft <= 600f ? 0.90
                            : 0.95;

                bool avoided = _random.NextDouble() <= prob;

                _flashDecisions[key] = new FlashDecision
                {
                    FirstSeen = now,
                    LastSeen = now,
                    DetonateAt = detonateAt,
                    Avoided = avoided,
                };

                if (_debugFlash)
                {
                    BroadcastDebug(
                        $"[Smarter-Bot/Flash] bot={bot.PlayerName} sees flash#{fidx} t-{msLeft:F0}ms prob={prob * 100:F0}% roll={(avoided ? "AVOID" : "flash")}");
                }
            }
        }
    }

    // Decoupled horizontal/vertical FOV check. Source 2 QAngle convention:
    //   EyeAngles.Y = yaw   (0 deg => +X axis, 90 deg => +Y axis)
    //   EyeAngles.X = pitch (positive => looking DOWN; this is the Quake/Source convention)
    // Returns true when target is inside both cones; outDeltaYaw/outDeltaPitch are
    // signed angle deltas (target relative to bot view) for debug logging.
    private static bool IsInFov(CCSPlayerPawn pawn, Vector target,
                                float horizDeg, float vertDeg,
                                out float outDeltaYaw, out float outDeltaPitch)
    {
        outDeltaYaw = 0f;
        outDeltaPitch = 0f;

        var origin = pawn.AbsOrigin;
        if (origin == null) return false;

        float eyeZ = origin.Z + pawn.ViewOffset.Z;

        double dx = target.X - origin.X;
        double dy = target.Y - origin.Y;
        double dz = target.Z - eyeZ;

        double horizDist = Math.Sqrt(dx * dx + dy * dy);
        if (horizDist < 1e-3 && Math.Abs(dz) < 1e-3) return true;

        double yawToTarget = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        double pitchToTarget = -Math.Atan2(dz, horizDist) * 180.0 / Math.PI;

        double yawDelta = NormalizeAngleDeg(yawToTarget - pawn.EyeAngles.Y);
        double pitchDelta = NormalizeAngleDeg(pitchToTarget - pawn.EyeAngles.X);

        outDeltaYaw = (float)yawDelta;
        outDeltaPitch = (float)pitchDelta;

        return Math.Abs(yawDelta) <= horizDeg * 0.5
            && Math.Abs(pitchDelta) <= vertDeg * 0.5;
    }

    private static double NormalizeAngleDeg(double a)
    {
        a %= 360.0;
        if (a > 180.0) a -= 360.0;
        if (a < -180.0) a += 360.0;
        return a;
    }

    private bool BotCanSee(CCSPlayerPawn pawn, Vector target)
    {
        if (_rayTrace == null || _scratchEye == null) return false;

        var origin = pawn.AbsOrigin;
        if (origin == null) return false;

        _scratchEye.X = origin.X;
        _scratchEye.Y = origin.Y;
        _scratchEye.Z = origin.Z + pawn.ViewOffset.Z;

        var opts = new TraceOptions(InteractionLayers.MASK_WORLD_ONLY);
        _rayTrace.TraceEndShape(_scratchEye, target, pawn, opts, out var result);
        return result.Fraction >= 0.999f;
    }
}
//---------------------------------------------------------------------------------------
