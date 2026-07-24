# Competitive Equipment Economy and Tactics Implementation Plan

> **For agentic workers:** This plan is executed inline in the current task with TDD checkpoints.

**Goal:** Make mixed human/bot teams plan purchases from real equipment state, then complete recoverable buying, gifting, fake-defuse safety, and CT/T tactical runtime integration.

**Architecture:** Add one shared equipment/economy snapshot and utility-demand policy in `CompetitiveBotCore`, then make `BuyPlanner` and `BoundedTeamBuyPlanner` consume human snapshots as fixed DP state while bots remain the only purchasable variables. Keep CounterStrikeSharp reads/writes in `BotBuy`/`BotState`; Core owns deterministic policies and regression coverage.

**Tech Stack:** C#/.NET, xUnit, CounterStrikeSharp plugins, existing Core records and planner APIs.

## Global Constraints

- Human players are fixed equal-weight team state; human cash is never bot budget and humans are never bot gift recipients.
- Equipment value is sampled from current inventory; never add round-start cash to current equipment value.
- Core behavior is TDD-first and each delivery batch must pass its scoped Core tests before plugin builds.
- Keep `.idea/` untouched and preserve unrelated user changes.
- Core tests do not prove CounterStrikeSharp runtime, nav connectivity, or real-server behavior; report those boundaries explicitly.

### Task 1: Shared equipment snapshots and economy classification

**Files:**
- Create: `addons/counterstrikesharp/shared/CompetitiveBotCore/EquipmentEconomy.cs`
- Modify: `addons/counterstrikesharp/shared/CompetitiveBotCore/CompetitiveBotCore.cs`
- Modify: `addons/counterstrikesharp/plugins/BotBuy/BotBuy.cs`
- Test: `tests/CompetitiveBotCore.Tests/BuyPlannerTests.cs`

Add stable price tables, `PlayerEquipmentSnapshot`, current-value/补齐-cost helpers, and per-player/team phase classification. Add red tests for human M4+armor with `$500`, damaged armor, and current-equipment value not double-counting round-start cash; then wire BotBuy refreshes at round start, final calibration, and inventory events.

### Task 2: Human-fixed DP, demand policy, role packages, and FAMAS correction

**Files:**
- Create: `addons/counterstrikesharp/shared/CompetitiveBotCore/TeamUtilityDemandPolicy.cs`
- Modify: `addons/counterstrikesharp/shared/CompetitiveBotCore/CompetitiveArchitectureV2.cs`
- Modify: `addons/counterstrikesharp/shared/CompetitiveBotCore/CompetitiveBotCore.cs`
- Modify: `tests/CompetitiveBotCore.Tests/CompetitiveArchitectureV2Tests.cs`
- Modify: `tests/CompetitiveBotCore.Tests/EconomyTacticalPolicyTests.cs`

Inject human weapons/utilities/defusers into the initial planner state, enforce one-AWP team structure, use role utility packages with capacity limits, make FullBuy prefer a real rifle when affordable, and reject unexplained empty plans. Add red/green coverage for human utility deduction, rich FAMAS upgrade, Half/Force utility, shotgun+pistol restraint, and human-AWP suppression.

### Task 3: Recoverable purchase execution and gift/native-buy boundary

**Files:**
- Modify: `addons/counterstrikesharp/shared/CompetitiveBotCore/CompetitiveArchitectureV2.cs`
- Modify: `addons/counterstrikesharp/plugins/BotBuy/BotBuy.cs`
- Modify: `tests/CompetitiveBotCore.Tests/InventoryTransactionTests.cs`
- Modify: `tests/CompetitiveBotCore.Tests/MatchStatePolicyTests.cs`

Split core/team-critical/optional item phases, poll inventory confirmation across ticks, retry idempotently, extend final calibration to the `0.35–0.45s` execution window, cancel bot primary purchase after a valid gift, and fail closed when custom planning is suppressed or native loadout is active.

### Task 4: Stable AWP role and tactical runtime completion

**Files:**
- Modify: `addons/counterstrikesharp/shared/CompetitiveBotCore/CompetitiveArchitectureV2.cs`
- Modify: `addons/counterstrikesharp/shared/CompetitiveBotCore/CompetitiveTacticalV2.cs`
- Modify: `addons/counterstrikesharp/plugins/BotState/BotState.cs`
- Modify: `tests/CompetitiveBotCore.Tests/CompetitiveArchitectureV2Tests.cs`
- Modify: `tests/CompetitiveBotCore.Tests/TacticalCoreTests.cs`
- Modify: `tests/CompetitiveBotCore.Tests/CompetitiveTacticalV2Tests.cs`

Choose AWPer by current weapon, controller preference, stable prior role, then affordability; disable competitive fake-defuse velocity injection unless a reliable stationary implementation exists; expose CT full-buy defaults/reinforcement/rotation and T pre-plant Stage/Probe/Execute/Split/Fake/Rotate with human position/carrier/contact as fixed context.

## Verification

Run after each applicable batch:

```bash
rtk dotnet test tests/CompetitiveBotCore.Tests/CompetitiveBotCore.Tests.csproj -c Release --no-restore
rtk dotnet build addons/counterstrikesharp/plugins/BotBuy/BotBuy.csproj -c Release --no-restore
rtk dotnet build addons/counterstrikesharp/plugins/BotState/BotState.csproj -c Release --no-restore
```

At handoff, report exact pass/fail output and separate Core proof, plugin build proof, and real-server/manual proof.
