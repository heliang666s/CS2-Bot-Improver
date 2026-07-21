using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CompetitiveBotCore;
using System.Collections.Generic;
using System.Linq;

namespace BotBuyPatch;

public sealed class BotBuyPatch : BasePlugin
{
    public override string ModuleName        => "BotBuyPatch";
    public override string ModuleVersion     => "1.0.12";
    public override string ModuleAuthor      => "ed0ard";
    public override string ModuleDescription => "Enable bots to take more buy options";

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _terroristScore = 0;
            _counterTerroristScore = 0;
            _botUserIdToIndex.Clear();
            _botIndexCounter = 0;
        });

        if (hotReload)
            AddTimer(0.2f, SynchronizeCompetitiveScore);
    }

    private Dictionary<int, int> _botUserIdToIndex = new();
    private int _botIndexCounter = 0;

    Dictionary<CsTeam, List<CCSPlayerController>> _poorPlayersByTeam = new();

    private Dictionary<int, List<string>> _prevWeapons = new();
    private Dictionary<int, int> _prevMoney = new();
    private Dictionary<int, int> _prevArmor = new();
    private readonly RoundEconomyLedger _economyLedger = new();
    private EconomyRewardRules? _lastRewardSnapshot;
    private int _terroristScore;
    private int _counterTerroristScore;
//----------------------------------------------------------------------------------------------
    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsBot) GetBotIndex(player);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null) RemoveBot(player);
        return HookResult.Continue;
    }

    [GameEventHandler]// Unlock Refund Restriction
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (CurrentProfile() == BotMatchProfile.Competitive && player != null && player.IsValid)
        {
            _economyLedger.RecordDeath((int)player.Index);
            var attacker = @event.Attacker;
            if (attacker != null && attacker.IsValid && attacker.Index != player.Index)
                _economyLedger.RecordKill((int)attacker.Index, (int)player.Index);
        }

        if (player != null && player.IsValid && player.IsBot)
        {
            ClearPreviousInventory(player);
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (CurrentProfile() == BotMatchProfile.Competitive)
        {
            TeamSide? winner = ResolveRoundWinner(@event);
            if (winner.HasValue)
            {
                if (winner.Value == TeamSide.Terrorist)
                    _terroristScore++;
                else
                    _counterTerroristScore++;

                var players = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
                    .Where(player => player.IsValid
                        && (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist))
                    .ToList();
                _economyLedger.CompleteRound(
                    winner.Value,
                    players.Where(player => player.PawnIsAlive).Select(player => (int)player.Index),
                    players.Where(player => player.PawnIsAlive && CurrentPrimary(player) != null)
                        .Select(player => (int)player.Index));
                _lastRewardSnapshot = ReadEconomyRewards();
                Server.PrintToConsole(
                    $"[BotBuy] EconomyFacts winner={winner.Value} "
                    + $"planted={(_economyLedger.BombPlanted ? 1 : 0)} "
                    + $"defused={(_economyLedger.BombDefused ? 1 : 0)} "
                    + $"exploded={(_economyLedger.BombExploded ? 1 : 0)} "
                    + $"rules.kill={_lastRewardSnapshot.KillReward} "
                    + $"rules.loser={_lastRewardSnapshot.LoserBonus}");
            }
        }

        SavePreviousInventory();

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        if (CurrentProfile() == BotMatchProfile.Competitive)
            _economyLedger.RecordBombPlanted(ReadEventPlayerSlot(@event));
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        if (CurrentProfile() == BotMatchProfile.Competitive)
            _economyLedger.RecordBombDefused(ReadEventPlayerSlot(@event));
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBombExploded(EventBombExploded @event, GameEventInfo info)
    {
        if (CurrentProfile() == BotMatchProfile.Competitive)
            _economyLedger.RecordBombExploded();
        return HookResult.Continue;
    }
//----------------------------------------------------------------------------------------------
    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // Don't Buy on Aim_Rush
        if (Server.MapName == "aim_rush") return HookResult.Continue;

        bool competitive = CurrentProfile() == BotMatchProfile.Competitive;
        if (competitive)
            ResetRoundEconomyFacts();

        if (competitive
            && string.IsNullOrEmpty(ConVar.Find("bot_loadout")?.StringValue))
        {
            AddTimer(0.4f, ApplyCompetitiveBuy);
            AddTimer(1.0f, RecordGroundWeaponOpportunities);
            return HookResult.Continue;
        }

        List<CCSPlayerController> allPlayers = new();
        List<CCSPlayerController> allCT = new();
        List<CCSPlayerController> allT = new();
        List<CCSPlayerController> ctBots = new();
        List<CCSPlayerController> tBots = new();

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid) continue;  
            allPlayers.Add(player);

            if (player.Team == CsTeam.CounterTerrorist)
            {
                allCT.Add(player);
                if (player.IsBot) ctBots.Add(player);
            }
            else if (player.Team == CsTeam.Terrorist)
            {
                allT.Add(player);
                if (player.IsBot) tBots.Add(player);
            }
        }
        // Drop Weapons
        _poorPlayersByTeam.Clear();
        var poorCT = allPlayers.Where(p => p.IsValid && p.Team == CsTeam.CounterTerrorist && p.InGameMoneyServices?.Account < 2800).ToList();
        var poorT = allPlayers.Where(p => p.IsValid && p.Team == CsTeam.Terrorist && p.InGameMoneyServices?.Account < 2800).ToList();
        _poorPlayersByTeam[CsTeam.CounterTerrorist] = poorCT;
        _poorPlayersByTeam[CsTeam.Terrorist] = poorT;

        ConVar? botLoadout = ConVar.Find("bot_loadout");
        if (botLoadout != null && !string.IsNullOrEmpty(botLoadout.StringValue))
        {
            return HookResult.Continue;
        }
        // Swap HKP2000
        foreach (var player in allPlayers.Where(p => p.IsValid && p.IsBot))
        {
            // Swap HKP2000
            if (Random.Shared.NextSingle() < 0.8f)
            {
                Swap(player, "weapon_hkp2000", "weapon_usp_silencer");
            }
        }
        // Force Buy
        bool allCtInRange = ctBots.Count > 0 && ctBots.All(p =>
            p.InGameMoneyServices != null && p.InGameMoneyServices.Account > 1000 && p.InGameMoneyServices.Account < 2800);

        bool allTInRange = tBots.Count > 0 && tBots.All(p =>
            p.InGameMoneyServices != null && p.InGameMoneyServices.Account > 1000 && p.InGameMoneyServices.Account < 2800);
        AddTimer(0.4f, () =>
        {
            if (allCtInRange)
            {
                float roll = Random.Shared.NextSingle();
                foreach (var bot in ctBots)
                {
                    if (roll < 0.10f)
                    {
                        Swap(bot, "weapon_usp_silencer", "weapon_fiveseven");
                        Swap(bot, "weapon_hkp2000", "weapon_fiveseven");
                    }
                    else if (roll < 0.20f)
                    {
                        Buy(bot, "weapon_mp9");
                    }
                }
            }

            if (allTInRange)
            {
                float roll = Random.Shared.NextSingle();
                foreach (var bot in tBots)
                {
                    if (roll < 0.10f)
                    {
                        Swap(bot, "weapon_glock", "weapon_tec9");
                    }
                    else if (roll < 0.20f)
                    {
                        Buy(bot, "weapon_mac10");
                    }
                }
            }
        });
        // Don't buy if we have scar20/g3sg1
        foreach (var player in allPlayers.Where(p => p.IsValid && p.IsBot))
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) continue;

            var activeWeapon = pawn.WeaponServices.ActiveWeapon.Value;
            if (activeWeapon == null) continue;

            string initialGun = activeWeapon.DesignerName;
            if (initialGun != "weapon_scar20" && initialGun != "weapon_g3sg1") continue;

            var copyPlayer = player;
            AddTimer(0.5f, () =>
            {
                if (!copyPlayer.IsValid) return;
                var p2 = copyPlayer.PlayerPawn.Value;
                if (p2 == null || !p2.IsValid || p2.WeaponServices == null) return;

                var currentWeapon = p2.WeaponServices.ActiveWeapon.Value;
                if (currentWeapon == null) return;

                string currentGun = currentWeapon.DesignerName;
                if (currentGun != "weapon_scar20" && currentGun != "weapon_g3sg1")
                {
                    Refund(copyPlayer, currentGun);
                }
            });
        }
        // Swap AUG
        foreach (var player in allPlayers.Where(p => p.IsValid && p.IsBot))
        {
            var copyPlayer = player;
            float rand = Random.Shared.NextSingle();

            if (rand < 0.06f)
            {
            }
            else if (rand < 0.53f)
            {
                AddTimer(0.4f, () =>
                {
                    if (!copyPlayer.IsValid) return;
                    Swap(copyPlayer, "weapon_aug", "weapon_m4a1");
                });
            }
            else
            {
                AddTimer(0.4f, () =>
                {
                    if (!copyPlayer.IsValid) return;
                    Swap(copyPlayer, "weapon_aug", "weapon_m4a1_silencer");
                });
            }
        }
        // Swap P90
        AddTimer(0.4f, () =>
        {
            foreach (var p in allPlayers)
            {
                var pawn = p.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) continue;

                var weapon = pawn.WeaponServices.ActiveWeapon.Value;
                if (weapon == null || weapon.DesignerName != "weapon_p90") continue;

                float roll = Random.Shared.NextSingle();
                if (roll < 0.3f) Swap(p, "weapon_p90", "weapon_bizon");
                else if (roll < 0.4f) Swap(p, "weapon_p90", "weapon_mp7");
                else if (roll < 0.5f) Swap(p, "weapon_p90", "weapon_mp5sd");
                else if (roll < 0.6f) Swap(p, "weapon_p90", "weapon_ump45");
            }
        });
        // Swap XM1014
        AddTimer(0.4f, () =>
        {
            foreach (var p in allPlayers)
            {
                var pawn = p.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) continue;

                var weapon = pawn.WeaponServices.ActiveWeapon.Value;
                if (weapon == null || weapon.DesignerName != "weapon_xm1014") continue;

                float roll = Random.Shared.NextSingle();
                if (roll < 0.5f)
                {
                    Swap(p, "weapon_xm1014", "weapon_negev");
                }
                else if (p.Team == CsTeam.CounterTerrorist && roll < 0.6f)
                {
                    Swap(p, "weapon_xm1014", "weapon_mag7");
                }
                else if (p.Team == CsTeam.Terrorist && roll < 0.65f)
                {
                    Swap(p, "weapon_xm1014", "weapon_sawedoff");
                }
            }
        });
        // Swap SSG08
        AddTimer(0.4f, () =>
        {
            if (ConVar.Find("sv_gravity")?.GetPrimitiveValue<float>() == 230f) return;
            foreach (var p in allPlayers)
            {
                var pawn = p.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) continue;

                var weapon = pawn.WeaponServices.ActiveWeapon.Value;
                if (weapon == null || weapon.DesignerName != "weapon_ssg08") continue;

                float roll = Random.Shared.NextSingle();

                if (roll < 0.05f)
                {
                    if (p.Team == CsTeam.CounterTerrorist)
                    {
                        Refund(p, "weapon_usp_silencer");
                        Refund(p, "weapon_hkp2000");
                    }
                    else
                    {
                        Refund(p, "weapon_glock");
                    }
                    Swap(p, "weapon_ssg08", "weapon_deagle");
                }
                else if (roll < 0.45f)
                {
                    if (p.Team == CsTeam.Terrorist)
                        Swap(p, "weapon_ssg08", "weapon_mac10");
                    else
                        Swap(p, "weapon_ssg08", "weapon_mp9");
                }
            }
        });
        // Big Advantage
        AddTimer(0.6f, () =>
        {
            if (!IsFirstRoundOfHalf())  
            {
                foreach (var p in allPlayers)
                {
                    if (p.InGameMoneyServices == null || p.InGameMoneyServices.Account < 5200)
                        continue;

                    var pawn = p.PlayerPawn.Value;
                    if (pawn == null || !pawn.IsValid)
                        continue;

                    var weaponServices = pawn.WeaponServices;
                    if (weaponServices == null)
                        continue;

                    var activeWeapon = weaponServices.ActiveWeapon.Value;
                    if (activeWeapon == null)
                        continue;

                    var currentWeapon = activeWeapon.DesignerName;
                    if (string.IsNullOrEmpty(currentWeapon))
                        continue;

                    float roll = Random.Shared.NextSingle();

                    if (roll < 0.10f)
                    {
                        string newGun = p.Team == CsTeam.CounterTerrorist ? "weapon_scar20" : "weapon_g3sg1";
                        Swap(p, currentWeapon, newGun);
                    }
                    else if (roll < 0.14f)
                    {
                        Swap(p, currentWeapon, "weapon_m249");
                    }
                }
            }
        });
        // Buy Defuser
        AddTimer(3.0f, () =>
        {
            foreach (var p in allCT)
            {
                if (!p.IsValid) continue;
                if (p.InGameMoneyServices == null) continue;

                bool isPoor = _poorPlayersByTeam[CsTeam.CounterTerrorist].Contains(p);
                // Don't buy defuser if poor // Exception: pistol round with 500 left
                if (isPoor && !(IsFirstRoundOfHalf() && p.InGameMoneyServices.Account == 500))
                    continue;

                if (p.InGameMoneyServices.Account < 400)
                    continue;

                var pawn = p.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.ItemServices == null || pawn.ItemServices.Handle == nint.Zero)
                    continue;

                var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
                if (itemServices.HasDefuser)
                    continue;

                Buy(p, "item_defuser");
            }
        });
        // Don't buy Armor if it's above 40
        AddTimer(1.0f, () =>
        {
            if (!IsFirstRoundOfHalf())  
            {
                foreach (var p in allPlayers)
                {
                    if (!p.IsValid || p.PlayerPawn.Value == null) continue;

                    var pawn = p.PlayerPawn.Value;
                    var (_, _, prevArmor) = PreviousInventory(p);

                    if (pawn.ItemServices == null || pawn.ItemServices.Handle == nint.Zero)
                    continue;
                    var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);

                    int currentArmor = pawn.ArmorValue;

                    if (prevArmor > 40 && prevArmor <= 99 && currentArmor > 99 && itemServices.HasHelmet)
                    {
                        Refund(
                            p,
                            "item_assaultsuit",
                            BuyPlanner.GetAssaultSuitPurchaseCost(ArmorLevel.Half));
                        p.GiveNamedItem("item_assaultsuit");
                        ref int armorValue = ref pawn.ArmorValue;
                        armorValue = prevArmor;
                        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
                    }
                }
            }
        });
        // Special Rounds Buy Assistant
        AddTimer(0.4f, () =>
        {
            if (IsFirstRoundOfHalf())
            {
                foreach (var p in allPlayers)
                {
                    if (!p.IsValid || p.InGameMoneyServices == null) continue;
                    int money = p.InGameMoneyServices.Account;
                    float r = Random.Shared.NextSingle();

                    // Comp Pistol Rounds
                    if (money == 800)
                    {
                        if (p.Team == CsTeam.CounterTerrorist)
                        {
                            if (r < 0.50f)  Buy(p, "item_kevlar");    // 50%
                            else if (r < 0.65f) { Swap(p, "weapon_usp_silencer", "weapon_elite"); Swap(p, "weapon_hkp2000", "weapon_elite"); } // 15%
                            else if (r < 0.75f) { Swap(p, "weapon_usp_silencer", "weapon_p250"); Swap(p, "weapon_hkp2000", "weapon_p250"); }   // 10%
                            else if (r < 0.83f) { Swap(p, "weapon_usp_silencer", "weapon_deagle"); Swap(p, "weapon_hkp2000", "weapon_deagle"); } // 8%
                            else if (r < 0.91f) { Swap(p, "weapon_usp_silencer", "weapon_cz75a"); Swap(p, "weapon_hkp2000", "weapon_cz75a"); }   // 8%
                            else if (r < 0.98f) { Swap(p, "weapon_usp_silencer", "weapon_fiveseven"); Swap(p, "weapon_hkp2000", "weapon_fiveseven"); } //7%
                            else if (r < 1.00f) { Swap(p, "weapon_usp_silencer", "weapon_revolver"); Swap(p, "weapon_hkp2000", "weapon_revolver"); } //2%
                        }
                        else
                        {
                            if (r < 0.50f)  Buy(p, "item_kevlar");    // 50%
                            else if (r < 0.65f) Swap(p, "weapon_glock", "weapon_elite"); //15%
                            else if (r < 0.77f) Swap(p, "weapon_glock", "weapon_p250");  //12%
                            else if (r < 0.85f) Swap(p, "weapon_glock", "weapon_deagle");//8%
                            else if (r < 0.87f) Swap(p, "weapon_glock", "weapon_revolver");//2%
                            else if (r < 1.00f) Swap(p, "weapon_glock", "weapon_tec9");//13%
                        }
                    }

                    // Casual Pistol Rounds
                    else if (money == 1000)
                    {
                        if (p.Team == CsTeam.CounterTerrorist)
                        {
                            if (r < 0.20f)  { Swap(p, "weapon_usp_silencer", "weapon_elite"); Swap(p, "weapon_hkp2000", "weapon_elite"); } //20%
                            else if (r < 0.50f) { Swap(p, "weapon_usp_silencer", "weapon_deagle"); Swap(p, "weapon_hkp2000", "weapon_deagle"); } //30%
                            else if (r < 0.65f) { Swap(p, "weapon_usp_silencer", "weapon_cz75a"); Swap(p, "weapon_hkp2000", "weapon_cz75a"); } //15%
                            else if (r < 0.95f) { Swap(p, "weapon_usp_silencer", "weapon_fiveseven"); Swap(p, "weapon_hkp2000", "weapon_fiveseven"); } //30%
                            else if (r < 1.00f) { Swap(p, "weapon_usp_silencer", "weapon_revolver"); Swap(p, "weapon_hkp2000", "weapon_revolver"); } //5%
                        }
                        else
                        {
                            if (r < 0.20f)  Swap(p, "weapon_glock", "weapon_elite"); //20%
                            else if (r < 0.30f) Swap(p, "weapon_glock", "weapon_p250"); //10%
                            else if (r < 0.55f) Swap(p, "weapon_glock", "weapon_deagle");//25%
                            else if (r < 0.60f) Swap(p, "weapon_glock", "weapon_revolver");//5%
                            else if (r < 1.00f) Swap(p, "weapon_glock", "weapon_tec9");//40%
                        }
                    }

                    // First Round in OT
                    else if (money == 10000)
                    {
                        Buy(p, "item_assaultsuit");

                        if (p.Team == CsTeam.CounterTerrorist)
                        {
                            if (r < 0.35f)  Buy(p, "weapon_m4a1");
                            else if (r < 0.70f) Buy(p, "weapon_m4a1_silencer");
                            else if (r < 0.90f) Buy(p, "weapon_awp");
                            else if (r < 1.00f) Buy(p, "weapon_scar20");
                        }
                        else
                        {
                            if (r < 0.70f)  Buy(p, "weapon_ak47");
                            else if (r < 0.90f) Buy(p, "weapon_awp");
                            else if (r < 1.00f) Buy(p, "weapon_g3sg1");
                        }
                    }
                }
            }
        });
        // Drop Weapons
        AddTimer(2.0f, () =>
        {
            if (!IsFirstRoundOfHalf())  
            {
                foreach (var team in new[] { CsTeam.CounterTerrorist, CsTeam.Terrorist })
                {
                    if (!_poorPlayersByTeam.TryGetValue(team, out var poor))
                        poor = new List<CCSPlayerController>();
                    poor = poor.Where(p => p.IsValid && p.InGameMoneyServices != null).ToList();

                    var richBots = allPlayers.Where(p => p.IsValid && p.Team == team && p.IsBot && p.InGameMoneyServices?.Account >= 2900).ToList();

                    if (poor.Count == 0 || richBots.Count == 0) continue;

                    var giftedPoor = new HashSet<CCSPlayerController>();

                    var shuffledPoor = poor.Where(p => !HasPrimaryWeapon(p)).OrderBy(_ => Random.Shared.Next()).ToList();
                    int poorIndex = 0;

                    foreach (var rich in richBots)
                    {
                        if (poorIndex >= shuffledPoor.Count) break;
                        if (rich.InGameMoneyServices == null) continue;

                        int richMoney = rich.InGameMoneyServices.Account;
                        int price = team == CsTeam.CounterTerrorist ? 2900 : 2700;

                        int maxGive = richMoney / price;
                        if (maxGive > 3) maxGive = 3;
                        if (maxGive <= 0) continue;

                        int given = 0;
                        while (given < maxGive && poorIndex < shuffledPoor.Count)
                        {
                            var poorPlayer = shuffledPoor[poorIndex];
                            poorIndex++;

                            if (!poorPlayer.IsValid || giftedPoor.Contains(poorPlayer)) continue;

                            string gun = team == CsTeam.CounterTerrorist
                                ? (Random.Shared.Next(2) == 0 ? "weapon_m4a1_silencer" : "weapon_m4a1")
                                : "weapon_ak47";
                            poorPlayer.GiveNamedItem(gun);
                            giftedPoor.Add(poorPlayer);

                            rich.InGameMoneyServices.Account -= price;
                            if (rich.InGameMoneyServices.Account < 0) rich.InGameMoneyServices.Account = 0;
                            Utilities.SetStateChanged(rich, "CCSPlayerController", "m_pInGameMoneyServices");

                            foreach (var teammate in allPlayers.Where(p => p.IsValid && p.Team == team))
                                teammate.PrintToChat($"{ChatColors.Green}{rich.PlayerName}{ChatColors.Yellow}: {poorPlayer.PlayerName}, I dropped a weapon for ya");
                            given++;
                        }
                    }
                }
            }
        });
        // Armor Gift Cycle: richest non-poor bot buys armor for a random unarmored teammate
        AddTimer(2.5f, () =>
        {
            foreach (var team in new[] { CsTeam.CounterTerrorist, CsTeam.Terrorist })
            {
                _poorPlayersByTeam.TryGetValue(team, out var poor);
                var poorSet = new HashSet<CCSPlayerController>(poor ?? new List<CCSPlayerController>());

                while (true)
                {
                    var needArmor = allPlayers
                        .Where(p => p.IsValid && p.IsBot && p.Team == team
                            && HasPrimaryWeapon(p)
                            && (p.PlayerPawn.Value?.ArmorValue ?? 1) == 0)
                        .ToList();

                    if (needArmor.Count == 0) break;

                    var buyer = allPlayers
                        .Where(p => p.IsValid && p.IsBot && p.Team == team
                            && !poorSet.Contains(p)
                            && p.InGameMoneyServices?.Account >= 650)
                        .OrderByDescending(p => p.InGameMoneyServices!.Account)
                        .FirstOrDefault();
                    // No one has enough money anymore
                    if (buyer == null) break;

                    var target = needArmor[Random.Shared.Next(needArmor.Count)];
                    if (!target.IsValid) continue;

                    int buyerMoney = buyer.InGameMoneyServices!.Account;
                    // Terrorist bots only buy full armor
                    if (team == CsTeam.Terrorist && buyerMoney < 1000) break;
                    string item = buyerMoney >= 1000 ? "item_assaultsuit" : "item_kevlar";
                    int price   = buyerMoney >= 1000 ? 1000 : 650;

                    target.GiveNamedItem(item);
                    buyer.InGameMoneyServices.Account -= price;
                    Utilities.SetStateChanged(buyer, "CCSPlayerController", "m_pInGameMoneyServices");
                }
            }
        });
        return HookResult.Continue;
    }

    private BotMatchProfile CurrentProfile()
        => ProfilePolicy.Resolve(
            ProfileConfig.Load(ProfileConfig.DefaultPath(Server.GameDirectory)),
            IsEntertainmentMode());

    private void ResetRoundEconomyFacts()
    {
        var seeds = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(player => player.IsValid
                && (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist))
            .Select(player => new EconomyPlayerSeed(
                (int)player.Index,
                player.Team == CsTeam.Terrorist ? TeamSide.Terrorist : TeamSide.CounterTerrorist,
                player.IsBot));
        _economyLedger.ResetRound(seeds);
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

    private static int ReadEventPlayerSlot(object @event)
    {
        try
        {
            var player = (CCSPlayerController?)((dynamic)@event).Userid;
            return player?.IsValid == true ? (int)player.Index : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static EconomyRewardRules ReadEconomyRewards()
        => new(
            ReadIntConVar("cash_player_killed_enemy_default"),
            ReadFloatConVar("cash_player_killed_enemy_factor"),
            ReadIntConVar("cash_team_loser_bonus"),
            ReadIntConVar("cash_team_loser_bonus_consecutive_rounds"),
            ReadIntConVar("cash_team_elimination_bomb_map"),
            ReadIntConVar("cash_team_terrorist_win_bomb"),
            ReadIntConVar("cash_team_win_by_defusing_bomb"),
            ReadIntConVar("cash_player_bomb_planted"),
            ReadIntConVar("cash_player_bomb_defused"),
            ReadIntConVar("cash_team_bonus_shorthanded"),
            ReadIntConVar("cash_team_planted_bomb_but_defused"));

    private static int ReadIntConVar(string name)
    {
        try
        {
            return ConVar.Find(name)?.GetPrimitiveValue<int>() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static float ReadFloatConVar(string name)
    {
        try
        {
            return ConVar.Find(name)?.GetPrimitiveValue<float>() ?? 0f;
        }
        catch
        {
            return 0f;
        }
    }

    private static bool IsEntertainmentMode()
    {
        bool teammatesAreEnemies = ConVar.Find("mp_teammates_are_enemies")?.StringValue is "1" or "true";
        bool noSpread = ConVar.Find("weapon_accuracy_nospread")?.StringValue is "1" or "true";
        bool unlimitedMoney = ConVar.Find("mp_maxmoney")?.StringValue == "0";
        return teammatesAreEnemies || noSpread || unlimitedMoney;
    }

    private void ApplyCompetitiveBuy()
    {
        SynchronizeCompetitiveScore();

        var players = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(p => p.IsValid && (p.Team == CsTeam.Terrorist || p.Team == CsTeam.CounterTerrorist))
            .ToList();
        if (players.Count == 0) return;

        var tPlayers = players.Where(p => p.Team == CsTeam.Terrorist).ToList();
        var ctPlayers = players.Where(p => p.Team == CsTeam.CounterTerrorist).ToList();
        ApplyCompetitiveTeamBuy(
            tPlayers.Where(p => p.IsBot).ToList(),
            tPlayers,
            ctPlayers,
            TeamSide.Terrorist);
        ApplyCompetitiveTeamBuy(
            ctPlayers.Where(p => p.IsBot).ToList(),
            ctPlayers,
            tPlayers,
            TeamSide.CounterTerrorist);
    }

    private void RecordGroundWeaponOpportunities()
    {
        // The current repository has no BotController pickup ABI. Observe
        // nearby opportunities for diagnostics only; never mutate inventory
        // or cash from this path.
        string[] weaponNames =
        [
            "weapon_ak47", "weapon_m4a1", "weapon_m4a1_silencer",
            "weapon_famas", "weapon_galilar", "weapon_awp",
            "weapon_mp9", "weapon_mac10",
        ];
        var groundWeapons = weaponNames
            .SelectMany(name => Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(name))
            .Where(entity => entity.IsValid && entity.AbsOrigin != null)
            .ToArray();
        if (groundWeapons.Length == 0)
            return;

        foreach (var bot in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
                     .Where(player => player.IsValid && player.IsBot && player.PawnIsAlive
                         && CurrentPrimary(player) == null
                         && player.PlayerPawn?.Value?.AbsOrigin is not null))
        {
            var origin = bot.PlayerPawn!.Value!.AbsOrigin!;
            var nearest = groundWeapons
                .Select(weapon => new
                {
                    Weapon = weapon,
                    Distance = DistanceSquared(origin, weapon.AbsOrigin!),
                })
                .OrderBy(entry => entry.Distance)
                .FirstOrDefault();
            if (nearest is not null && nearest.Distance <= 350f * 350f)
            {
                Server.PrintToConsole(
                    $"[BotBuy] GroundWeaponOpportunity slot={bot.Index} weapon={nearest.Weapon.DesignerName} distance={MathF.Sqrt(nearest.Distance):F0} nativePickup=unavailable");
            }
        }
    }

    private static float DistanceSquared(Vector a, Vector b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private void ApplyCompetitiveTeamBuy(
        List<CCSPlayerController> teamBots,
        List<CCSPlayerController> allTeamPlayers,
        List<CCSPlayerController> allOpponentPlayers,
        TeamSide side)
    {
        if (teamBots.Count == 0) return;

        int roundsPlayed = CurrentRoundsPlayed();
        int teamScore = side == TeamSide.Terrorist ? _terroristScore : _counterTerroristScore;
        int opponentScore = side == TeamSide.Terrorist ? _counterTerroristScore : _terroristScore;
        int maxRounds = ReadMaxRounds();
        int overtimeMaxRounds = ReadOvertimeMaxRounds();
        bool overtimeRound = RoundSchedule.IsOvertimeRound(roundsPlayed, maxRounds);
        bool overtimeFirstRound = RoundSchedule.IsOvertimeFirstRound(roundsPlayed, maxRounds);
        bool pistolRound = RoundSchedule.IsFirstRoundOfHalf(
            roundsPlayed,
            maxRounds,
            overtimeMaxRounds)
            && !overtimeRound;
        bool lastRoundOfHalf = RoundSchedule.IsLastRoundOfHalf(
            roundsPlayed,
            maxRounds,
            overtimeMaxRounds);
        bool nextRoundIsLastRoundOfHalf = RoundSchedule.IsLastRoundOfHalf(
            roundsPlayed + 1,
            maxRounds,
            overtimeMaxRounds);
        bool lastRegulationRound = RoundSchedule.IsLastRegulationRound(
            teamScore,
            opponentScore,
            roundsPlayed,
            maxRounds);
        bool matchPoint = RoundSchedule.IsMatchPoint(
            teamScore,
            opponentScore,
            roundsPlayed,
            maxRounds,
            overtimeMaxRounds);
        bool opponentEcoLikely = allOpponentPlayers.Count > 0
            && allOpponentPlayers.Count(player => (player.InGameMoneyServices?.Account ?? 0) < 1800)
                >= Math.Max(1, allOpponentPlayers.Count - 1);
        int forceEligible = allTeamPlayers.Count(player => (player.InGameMoneyServices?.Account ?? 0)
            >= BuyPlanner.KevlarPrice + 1250);
        int forceThreshold = Math.Max(1, (int)Math.Ceiling(allTeamPlayers.Count * 0.60d));
        bool forceBuySignal = !pistolRound && !lastRoundOfHalf && !lastRegulationRound
            && (IsSecondToLastRoundOfHalf() || forceEligible >= forceThreshold)
            && allTeamPlayers.Any(player => (player.InGameMoneyServices?.Account ?? 0) >= 1900);

        var snapshot = new TeamEconomySnapshot(
            side,
            allTeamPlayers.Select(player => player.InGameMoneyServices?.Account ?? 0).ToArray(),
            pistolRound,
            lastRoundOfHalf || lastRegulationRound,
            forceBuySignal,
            opponentEcoLikely)
        {
            IsOvertimeFirstRound = overtimeFirstRound,
        };
        var phase = BuyPlanner.Classify(snapshot);
        int transferWeaponCost = side == TeamSide.Terrorist
            ? BuyPlanner.GetWeaponCost("weapon_ak47")
            : BuyPlanner.GetWeaponCost("weapon_m4a1");
        bool hasFeasibleTeamTransfer = teamBots.Any(donor =>
            (donor.InGameMoneyServices?.Account ?? 0) >= transferWeaponCost
            && teamBots.Any(recipient =>
                recipient != donor && CurrentPrimary(recipient) is null));
        var observedPlans = allTeamPlayers.ToDictionary(
            player => (int)player.Index,
            player => ObserveCurrentPlan(player, side, phase));
        var observedUtilities = allTeamPlayers.ToDictionary(
            player => (int)player.Index,
            player => CurrentUtilityCounts(player, side));
        int currentPower = allTeamPlayers
            .Where(player => player.PawnIsAlive)
            .Sum(player => observedPlans[(int)player.Index].Tier);
        var rewardRules = ReadEconomyRewards();
        _lastRewardSnapshot = rewardRules;
        var intentParticipants = allTeamPlayers
            .Select(player =>
            {
                int slot = (int)player.Index;
                var observed = observedPlans[slot];
                _economyLedger.Players.TryGetValue(slot, out var facts);
                return new ScenarioParticipant(
                    slot,
                    side,
                    player.InGameMoneyServices?.Account ?? 0,
                    observed,
                    facts?.Kills ?? 0,
                    facts?.IsPlanter ?? false,
                    facts?.IsDefuser ?? false,
                    observed.Tier);
            })
            .ToArray();
        int nextRoundPower = NextRoundEconomyPolicy.EstimateWorstCaseCombatPower(
            intentParticipants,
            rewardRules,
            _economyLedger.ConsecutiveLosses.GetValueOrDefault(side),
            allTeamPlayers.Count,
            allOpponentPlayers.Count);
        var purchaseContext = new PurchaseIntentContext(
            phase,
            teamScore,
            opponentScore,
            roundsPlayed,
            maxRounds,
            currentPower,
            nextRoundPower)
        {
            HasFeasibleTeamTransfer = hasFeasibleTeamTransfer,
            IsMatchPoint = matchPoint,
            IsLastRegulationRound = lastRegulationRound,
            IsLastRoundOfHalf = lastRoundOfHalf,
            IsNextRoundLastRoundOfHalf = nextRoundIsLastRoundOfHalf,
            IsOvertimeFirstRound = overtimeFirstRound,
        };
        var purchaseIntent = PurchaseIntentPolicy.Evaluate(purchaseContext);
        Server.PrintToConsole($"[BotBuy] Competitive BuyPlan side={side} phase={phase} intent={purchaseIntent} score={teamScore}-{opponentScore} rounds={roundsPlayed} bots={teamBots.Count} players={allTeamPlayers.Count} matchPoint={matchPoint} halfEnd={lastRoundOfHalf} otFirst={overtimeFirstRound} currentPower={currentPower} nextPower={nextRoundPower}");

        // Only one high-money bot per side may become the designated AWP role.
        var awper = teamBots
            .OrderByDescending(bot => bot.InGameMoneyServices?.Account ?? 0)
            .FirstOrDefault(bot => (bot.InGameMoneyServices?.Account ?? 0) >= 5400);

        var members = allTeamPlayers
            .Select(player =>
            {
                int slot = (int)player.Index;
                var observed = observedPlans[slot];
                var candidates = player.IsBot
                    ? BuyPlanner.BuildCandidatePlans(
                        side,
                        phase,
                        player.InGameMoneyServices?.Account ?? 0,
                        designatedAwper: ReferenceEquals(awper, player),
                        opponentEcoLikely,
                        observed.ArmorLevel,
                        observed.PrimaryWeapon,
                        observed.SecondaryWeapon,
                        observed.BuysHelmet,
                        observed.BuysDefuser,
                        observedUtilities[slot],
                        purchaseIntent)
                    : [observed];
                return new TeamBuyMember(
                    slot,
                    IsBot: player.IsBot,
                    IsAwper: ReferenceEquals(awper, player),
                    candidates);
            })
            .ToArray();
        var balancedPlan = TeamTierPlanner.Balance(members);
        var dpMembers = allTeamPlayers
            .Select(player =>
            {
                var observed = ObserveCurrentPlan(player, side, phase);
                _economyLedger.Players.TryGetValue((int)player.Index, out var facts);
                var candidates = player.IsBot
                    ? members.First(member => member.Slot == (int)player.Index).Candidates
                    : [observed];
                return new TeamDpMember(
                    (int)player.Index,
                    player.IsBot,
                    ReferenceEquals(awper, player),
                    player.InGameMoneyServices?.Account ?? 0,
                    candidates,
                    observed.PrimaryWeapon,
                    observed.Tier,
                    facts?.Kills ?? 0,
                    facts?.IsPlanter ?? false,
                    facts?.IsDefuser ?? false);
            })
            .ToArray();
        var teamPlan = TeamDpPlanner.Optimize(
            side,
            phase,
            dpMembers,
            balancedPlan.MinTier,
            rewardRules,
            _economyLedger.ConsecutiveLosses.GetValueOrDefault(side),
            allOpponentPlayers.Count,
            purchaseIntent,
            purchaseContext);
        Server.PrintToConsole($"[BotBuy] TeamBuyPlan side={side} phase={phase} intent={purchaseIntent} minTier={teamPlan.MinTier} maxTier={teamPlan.MaxTier} totalCost={teamPlan.TotalCost} humans={teamPlan.HumanObservations.Count} humanPenalty={teamPlan.HumanTierPenalty} reason={teamPlan.Reason}");

        if (teamPlan.BotPlans.Count == 0)
        {
            Server.PrintToConsole($"[BotBuy] TeamBuyPlanSkipped side={side} reason={teamPlan.Reason}");
            return;
        }

        var worstForecast = teamPlan.Forecasts.First(prediction =>
            prediction.Scenario == NextRoundScenario.LossNoPlantNoKillsAllDead);
        var bestForecast = teamPlan.Forecasts.First(prediction => prediction.Scenario == NextRoundScenario.Best);
        Server.PrintToConsole(
            $"[BotBuy] NextRoundForecast side={side} worstTier={worstForecast.MinTier} "
            + $"bestTier={bestForecast.MinTier} "
            + $"floorOk={(NextRoundEconomyPolicy.MeetsWorstCaseFloor(worstForecast, teamPlan.MinTier) ? 1 : 0)}");

        var transfers = teamPlan.Transfers;
        Server.PrintToConsole($"[BotBuy] TransferPlan side={side} count={transfers.Count}");
        var transferRecipients = transfers.Select(transfer => transfer.Recipient).ToHashSet();
        var originalPlans = teamPlan.BotPlans.ToDictionary(entry => entry.Key, entry => entry.Value);

        foreach (var bot in teamBots)
        {
            // A transfer recipient must not spend its primary-weapon slot
            // before the donor grant. Defer the whole recipient plan until the
            // transfer has been preflighted; this also narrows the race with
            // the native bot buy path.
            if (transferRecipients.Contains((int)bot.Index))
                continue;

            if (teamPlan.BotPlans.TryGetValue((int)bot.Index, out var plan))
                ExecuteCompetitiveBuyPlan(bot, side, plan);
        }

        foreach (var transfer in transfers)
        {
            var donor = teamBots.FirstOrDefault(bot => (int)bot.Index == transfer.Donor);
            var recipient = teamBots.FirstOrDefault(bot => (int)bot.Index == transfer.Recipient);
            var fallbackPlan = recipient != null && originalPlans.TryGetValue(transfer.Recipient, out var fallback)
                ? fallback
                : null;
            if (donor == null
                || recipient == null
                || !ExecuteCompetitiveTransfer(donor, recipient, side, transfer, fallbackPlan))
            {
                bool recipientAlreadyArmed = recipient != null && CurrentPrimary(recipient) != null;
                var recoveryPlan = recipientAlreadyArmed && fallbackPlan != null
                    ? fallbackPlan with { PrimaryWeapon = null }
                    : fallbackPlan;
                Server.PrintToConsole(
                    $"[BotBuy] TransferFailed side={side} donor={transfer.Donor} "
                    + $"recipient={transfer.Recipient} item={transfer.Item} "
                    + $"recipientAlreadyArmed={(recipientAlreadyArmed ? 1 : 0)}");
                if (recipient != null && recoveryPlan != null)
                    ExecuteCompetitiveBuyPlan(recipient, side, recoveryPlan);
            }
            else if (recipient != null && fallbackPlan != null)
            {
                // The transferred primary is now owned by the recipient. Buy
                // only the remaining armor/utility/secondary parts of the
                // original plan; never ask the recipient to buy another gun.
                ExecuteCompetitiveBuyPlan(
                    recipient,
                    side,
                    fallbackPlan with { PrimaryWeapon = null });
            }
        }
    }

    private bool ExecuteCompetitiveTransfer(
        CCSPlayerController donor,
        CCSPlayerController recipient,
        TeamSide side,
        TransferPlan transfer,
        PlayerBuyPlan? fallbackPlan)
    {
        if (!donor.IsValid || !recipient.IsValid || !donor.IsBot || !recipient.IsBot)
            return false;
        if (side == TeamSide.Terrorist && (donor.Team != CsTeam.Terrorist || recipient.Team != CsTeam.Terrorist))
            return false;
        if (side == TeamSide.CounterTerrorist && (donor.Team != CsTeam.CounterTerrorist || recipient.Team != CsTeam.CounterTerrorist))
            return false;
        var preflight = TransferPreflightPolicy.Evaluate(
            recipientHasPrimary: CurrentPrimary(recipient) != null,
            recipientHasTransferItem: HasWeapon(recipient, transfer.Item));
        if (!preflight.ShouldTransfer)
        {
            Server.PrintToConsole(
                $"[BotBuy] TransferCancelled donor={donor.Index} recipient={recipient.Index} "
                + $"item={transfer.Item} reason={preflight.Reason}");
            return false;
        }
        if (donor.InGameMoneyServices == null || donor.InGameMoneyServices.Account < transfer.Cost)
            return false;
        if (BuyPlanner.GetWeaponCost(transfer.Item) != transfer.Cost)
            return false;

        // Competitive sharing is centralized: the donor pays, while the
        // recipient receives the item directly. GiveNamedItem may update the
        // inventory on a later frame, so settlement and switching are deferred
        // until the weapon is actually observable.
        try
        {
            recipient.GiveNamedItem(transfer.Item);
        }
        catch
        {
            return false;
        }

        donor.InGameMoneyServices.Account -= transfer.Cost;
        Utilities.SetStateChanged(donor, "CCSPlayerController", "m_pInGameMoneyServices");
        ConfirmWeaponGrant(
            recipient,
            transfer.Item,
            transfer.Cost,
            donor,
            onConfirmed: () => BotWeaponSwitchQueue.Enqueue((int)recipient.Index, transfer.Item),
            onFailed: () =>
            {
                Server.PrintToConsole(
                    $"[BotBuy] TransferFailedAfterGrant donor={donor.Index} recipient={recipient.Index} item={transfer.Item}");
                if (fallbackPlan != null)
                {
                    bool recipientAlreadyArmed = CurrentPrimary(recipient) != null;
                    ExecuteCompetitiveBuyPlan(
                        recipient,
                        side,
                        recipientAlreadyArmed
                            ? fallbackPlan with { PrimaryWeapon = null }
                            : fallbackPlan);
                }
            },
            rejectConflictingPrimary: true);
        return true;
    }

    private PlayerBuyPlan ObserveCurrentPlan(
        CCSPlayerController player,
        TeamSide side,
        BuyPhase phase)
    {
        var pawn = player.PlayerPawn?.Value;
        ArmorLevel armor = pawn?.ArmorValue > 0
            ? HasHelmet(player) ? ArmorLevel.Full : ArmorLevel.Half
            : ArmorLevel.None;
        string? primary = CurrentPrimary(player);
        string? secondary = CurrentSecondary(player);

        return new PlayerBuyPlan(
            phase,
            armor,
            primary,
            secondary,
            HasHelmet(player),
            side == TeamSide.CounterTerrorist && HasDefuser(player),
            Array.Empty<string>(),
            0)
        {
            Tier = BuyPlanner.GetTier(armor, primary, secondary),
        };
    }

    private static IReadOnlyDictionary<string, int> CurrentUtilityCounts(
        CCSPlayerController player,
        TeamSide side)
    {
        var counts = new Dictionary<string, int>
        {
            ["smoke"] = CountWeapon(player, "weapon_smokegrenade"),
            ["flash"] = CountWeapon(player, "weapon_flashbang"),
            ["he"] = CountWeapon(player, "weapon_hegrenade"),
            ["molotov"] = side == TeamSide.Terrorist
                ? CountWeapon(player, "weapon_molotov")
                : CountWeapon(player, "weapon_incgrenade"),
        };
        return counts;
    }

    private void ExecuteCompetitiveBuyPlan(CCSPlayerController player, TeamSide side, PlayerBuyPlan plan)
    {
        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid || player.InGameMoneyServices == null) return;

        // Do not spend on secondary/utility before the core armor decision.
        if (plan.BuysArmor && pawn.ArmorValue <= 0)
            Buy(player, "item_kevlar");

        string? currentPrimary = CurrentPrimary(player);
        bool shouldUpgradePrimary = plan.PrimaryWeapon != null
            && currentPrimary != null
            && !string.Equals(currentPrimary, plan.PrimaryWeapon, StringComparison.Ordinal)
            && plan.Intent is PurchaseIntent.Standard or PurchaseIntent.AllIn or PurchaseIntent.LastRound
            && BuyPlanner.GetTier(ArmorLevel.Full, plan.PrimaryWeapon, null)
                > BuyPlanner.GetTier(ArmorLevel.Full, currentPrimary, null);
        // Keep the old primary until the engine accepts the new one. A failed
        // purchase therefore never leaves the bot without a weapon.
        if (plan.PrimaryWeapon != null && (currentPrimary == null || shouldUpgradePrimary))
        {
            bool bought = Buy(
                player,
                plan.PrimaryWeapon,
                onWeaponConfirmed: () => BotWeaponSwitchQueue.Enqueue(
                    (int)player.Index,
                    plan.PrimaryWeapon!,
                    shouldUpgradePrimary ? currentPrimary : null));
            if (!bought)
            {
                Server.PrintToConsole(
                    $"[BotBuy] PrimaryUpgradeFailed slot={player.Index} old={currentPrimary ?? "none"} new={plan.PrimaryWeapon} reason=GiveNamedItem-rejected");
            }
        }

        if (plan.SecondaryWeapon != null && !HasWeapon(player, plan.SecondaryWeapon))
            Buy(player, plan.SecondaryWeapon);

        if (plan.BuysHelmet && pawn.ArmorValue > 0 && !HasHelmet(player))
            Buy(player, "item_assaultsuit");

        if (plan.BuysDefuser && side == TeamSide.CounterTerrorist && !HasDefuser(player))
            Buy(player, "item_defuser");

        foreach (var utilityGroup in plan.Utility.GroupBy(utility => utility))
        {
            string utility = utilityGroup.Key;
            string item = utility switch
            {
                "smoke" => "weapon_smokegrenade",
                "flash" => "weapon_flashbang",
                "he" => "weapon_hegrenade",
                "molotov" when side == TeamSide.Terrorist => "weapon_molotov",
                "molotov" => "weapon_incgrenade",
                _ => string.Empty,
            };
            if (item.Length == 0) continue;

            int currentCount = CountWeapon(player, item);
            for (int i = currentCount; i < utilityGroup.Count(); i++)
                Buy(player, item);
        }

        if (plan.Phase == BuyPhase.LastRound)
            SpendLastRoundRemainder(player, side);
    }

    private void SpendLastRoundRemainder(CCSPlayerController player, TeamSide side)
    {
        if (player.InGameMoneyServices == null) return;

        if (side == TeamSide.CounterTerrorist
            && player.PlayerPawn?.Value?.ArmorValue > 0
            && !HasHelmet(player))
            Buy(player, "item_assaultsuit");
        if (!HasWeapon(player, "weapon_deagle")) Buy(player, "weapon_deagle");
        if (!HasWeapon(player, "weapon_taser")) Buy(player, "weapon_taser");
        if (!HasWeapon(player, "weapon_hegrenade")) Buy(player, "weapon_hegrenade");
        if (!HasWeapon(player, "weapon_flashbang")) Buy(player, "weapon_flashbang");
    }

    private string? CurrentPrimary(CCSPlayerController player)
        => player.PlayerPawn?.Value?.WeaponServices?.MyWeapons
            .Select(handle => handle.Value?.DesignerName)
            .FirstOrDefault(name => BuyPlanner.IsPrimaryWeapon(name));

    private static string? CurrentSecondary(CCSPlayerController player)
        => player.PlayerPawn?.Value?.WeaponServices?.MyWeapons
            .Select(handle => handle.Value?.DesignerName)
            .FirstOrDefault(name => name != null && IsSecondaryName(name));

    private static bool IsSecondaryName(string name)
        => name is "weapon_glock" or "weapon_usp_silencer" or "weapon_hkp2000"
            or "weapon_p250" or "weapon_fiveseven" or "weapon_tec9"
            or "weapon_deagle" or "weapon_elite" or "weapon_cz75a"
            or "weapon_revolver";

    private static bool HasWeapon(CCSPlayerController player, string designerName)
        => player.PlayerPawn?.Value?.WeaponServices?.MyWeapons
            .Any(handle => handle.Value?.DesignerName == designerName) == true;

    private static int CountWeapon(CCSPlayerController player, string designerName)
        => player.PlayerPawn?.Value?.WeaponServices?.MyWeapons
            .Count(handle => handle.Value?.DesignerName == designerName) ?? 0;

    private static bool HasHelmet(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn?.Value;
        if (pawn?.ItemServices == null || pawn.ItemServices.Handle == nint.Zero) return false;
        return new CCSPlayer_ItemServices(pawn.ItemServices.Handle).HasHelmet;
    }

    private static bool HasDefuser(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn?.Value;
        if (pawn?.ItemServices == null || pawn.ItemServices.Handle == nint.Zero) return false;
        return new CCSPlayer_ItemServices(pawn.ItemServices.Handle).HasDefuser;
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        ConVar? botLoadout = ConVar.Find("bot_loadout");
        if (botLoadout != null && !string.IsNullOrEmpty(botLoadout.StringValue))
        {
            return HookResult.Continue;
        }

        if (CurrentProfile() == BotMatchProfile.Competitive)
        {
            // Prevent the native bot economy cvar from reintroducing the old
            // per-bot save/force-buy heuristics after our BuyPlan runs.
            Server.ExecuteCommand(IsLastMatchPointRound()
                ? "bot_eco_limit 0"
                : "bot_eco_limit 2800");
            return HookResult.Continue;
        }

        // Don't save money in the last round of each half
        var ecoLimitCvar = ConVar.Find("bot_eco_limit");
        if (ecoLimitCvar != null)
        {
            if (IsSecondToLastRoundOfHalf())
                Server.ExecuteCommand("bot_eco_limit 0");
            else
                Server.ExecuteCommand("bot_eco_limit 2800");
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

// Nothing here
        }
        return HookResult.Continue;
    }
//----------------------------------------------------------------------------------------------
    private bool HasPrimaryWeapon(CCSPlayerController player)
    {
        if (!player.IsValid || player.PlayerPawn.Value == null)
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn.WeaponServices == null)
            return false;

        var activeWeapon = pawn.WeaponServices.ActiveWeapon;
        if (!activeWeapon.IsValid || activeWeapon.Value == null)
            return false;

        var weaponName = activeWeapon.Value.DesignerName;
        if (string.IsNullOrEmpty(weaponName)) return false;

        return weaponName.StartsWith("weapon_ak") ||
            weaponName.StartsWith("weapon_m4") ||
            weaponName.StartsWith("weapon_aug") ||
            weaponName.StartsWith("weapon_galilar") ||
            weaponName.StartsWith("weapon_famas") ||
            weaponName.StartsWith("weapon_awp") ||
            weaponName.StartsWith("weapon_ssg08") ||
            weaponName.StartsWith("weapon_mp") ||
            weaponName.StartsWith("weapon_ump") ||
            weaponName.StartsWith("weapon_p90") ||
            weaponName.StartsWith("weapon_bizon") ||
            weaponName.StartsWith("weapon_nova") ||
            weaponName.StartsWith("weapon_mag7") ||
            weaponName.StartsWith("weapon_sawedoff") ||
            weaponName.StartsWith("weapon_xm1014") ||
            weaponName.StartsWith("weapon_negev") ||
            weaponName.StartsWith("weapon_m249");
    }

//----------------------------------------------------------------------------------------------
    private bool Buy(
        CCSPlayerController player,
        string itemName,
        Action? onWeaponConfirmed = null)
    {
        if (!player.IsValid || !player.IsBot || player.InGameMoneyServices == null)
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return false;

        int money = player.InGameMoneyServices.Account;
        bool isCT = player.Team == CsTeam.CounterTerrorist;
        bool isT = player.Team == CsTeam.Terrorist;
        int price = 0;
        bool canBuy = true;
        int armor = pawn.ArmorValue;

        switch (itemName)
        {
            case "item_kevlar":              price = 650; break;
            case "item_assaultsuit":
                price = BuyPlanner.GetAssaultSuitPurchaseCost(
                    armor > 0 ? ArmorLevel.Half : ArmorLevel.None);
                break;

            case "item_defuser":             price = 400; canBuy = isCT; break;
            case "weapon_taser":             price = 200; break;

            case "weapon_glock":             canBuy = isT; break;
            case "weapon_hkp2000":           canBuy = isCT; break;
            case "weapon_usp_silencer":      canBuy = isCT; break;
            case "weapon_elite":             price = 300;  break;
            case "weapon_p250":              price = 300;  break;
            case "weapon_tec9":              price = 500;  canBuy = isT; break;
            case "weapon_fiveseven":         price = 500;  canBuy = isCT; break;
            case "weapon_deagle":            price = 700;  break;
            case "weapon_smokegrenade":      price = 300;  break;
            case "weapon_flashbang":         price = 200;  break;
            case "weapon_hegrenade":         price = 300;  break;
            case "weapon_molotov":           price = 400;  canBuy = isT; break;
            case "weapon_incgrenade":        price = 500;  canBuy = isCT; break;
            case "weapon_cz75a":             price = 500;  break;
            case "weapon_revolver":          price = 600;  break;

            case "weapon_mac10":             price = 1050; canBuy = isT; break;
            case "weapon_mp9":               price = 1250; canBuy = isCT; break;
            case "weapon_mp7":               price = 1500; break;
            case "weapon_mp5sd":             price = 1500; break;
            case "weapon_ump45":             price = 1200; break;
            case "weapon_bizon":             price = 1400; break;   
            case "weapon_p90":               price = 2350; break;

            case "weapon_nova":              price = 1050; break;
            case "weapon_xm1014":           price = 2000; break;
            case "weapon_sawedoff":          price = 1100; canBuy = isT; break;
            case "weapon_mag7":              price = 1300; canBuy = isCT; break;

            case "weapon_galilar":           price = 1800; canBuy = isT; break;
            case "weapon_ak47":              price = 2700; canBuy = isT; break;
            case "weapon_sg556":             price = 3000; canBuy = isT; break;
            case "weapon_famas":             price = 1950; canBuy = isCT; break;
            case "weapon_m4a1":              price = 2900; canBuy = isCT; break;
            case "weapon_m4a1_silencer":     price = 2900; canBuy = isCT; break;
            case "weapon_aug":               price = 3300; canBuy = isCT; break;

            case "weapon_ssg08":             price = 1700; break;
            case "weapon_awp":               price = 4750; break;
            case "weapon_scar20":            price = 5000; canBuy = isCT; break;
            case "weapon_g3sg1":             price = 5000; canBuy = isT; break;

            case "weapon_negev":             price = 1700; break;
            case "weapon_m249":              price = 5200; break;

            default: canBuy = false; break;
        }

        if (!canBuy)
            return false;

        if (money < price)
            return false;

        try
        {
            player.GiveNamedItem(itemName);
        }
        catch
        {
            return false;
        }

        player.InGameMoneyServices.Account -= price;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");

        if (itemName.StartsWith("weapon_", StringComparison.Ordinal))
        {
            ConfirmWeaponGrant(
                player,
                itemName,
                price,
                player,
                onWeaponConfirmed,
                onFailed: () => Server.PrintToConsole(
                    $"[BotBuy] WeaponGrantFailed slot={player.Index} item={itemName}; purchase refunded"));
        }

        return true;
    }

    private void ConfirmWeaponGrant(
        CCSPlayerController recipient,
        string weapon,
        int price,
        CCSPlayerController refundPlayer,
        Action? onConfirmed,
        Action? onFailed,
        int attempt = 0,
        bool rejectConflictingPrimary = false)
    {
        if (!recipient.IsValid)
        {
            if (refundPlayer.IsValid && refundPlayer.InGameMoneyServices != null)
            {
                refundPlayer.InGameMoneyServices.Account += price;
                Utilities.SetStateChanged(refundPlayer, "CCSPlayerController", "m_pInGameMoneyServices");
            }
            onFailed?.Invoke();
            return;
        }

        bool hasWeapon = HasWeapon(recipient, weapon);
        bool hasConflictingPrimary = rejectConflictingPrimary
            && CurrentPrimary(recipient) is { } currentPrimary
            && !string.Equals(currentPrimary, weapon, StringComparison.Ordinal);
        var settlement = WeaponGrantPolicy.Evaluate(
            callAccepted: true,
            hasWeapon,
            hasConflictingPrimary);
        if (settlement.Confirmed)
        {
            onConfirmed?.Invoke();
            return;
        }

        if (attempt < 3)
        {
            AddTimer(0.05f, () => ConfirmWeaponGrant(
                recipient,
                weapon,
                price,
                refundPlayer,
                onConfirmed,
                onFailed,
                attempt + 1,
                rejectConflictingPrimary));
            return;
        }

        if (hasConflictingPrimary)
        {
            try
            {
                recipient.RemoveItemByDesignerName(weapon);
            }
            catch
            {
                // Inventory cleanup is best effort; the refund and fallback
                // decision below remain authoritative.
            }
        }

        if (settlement.ShouldRefund
            && refundPlayer.IsValid
            && refundPlayer.InGameMoneyServices != null)
        {
            refundPlayer.InGameMoneyServices.Account += price;
            Utilities.SetStateChanged(refundPlayer, "CCSPlayerController", "m_pInGameMoneyServices");
        }
        onFailed?.Invoke();
    }

    private bool Refund(CCSPlayerController player, string itemName, int? priceOverride = null)
    {
        if (!player.IsValid || !player.IsBot || player.InGameMoneyServices == null)
        return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return false;

        if (!CanRefund(player, itemName))
            return false;

        bool hasItem = false;
        int price = 0;
        bool isCT = player.Team == CsTeam.CounterTerrorist;
        bool isT = player.Team == CsTeam.Terrorist;

        if (itemName.StartsWith("weapon_"))
        {
            hasItem = pawn.WeaponServices != null && pawn.WeaponServices.MyWeapons
                .Any(w => w.Value != null && w.Value.DesignerName == itemName);
        }
        else if (itemName == "item_assaultsuit" || itemName == "item_kevlar")
        {
            hasItem = pawn.ArmorValue > 0;
        }

        if (!hasItem)
            return false;

        bool canRefund = true;

        switch (itemName)
        {
            case "item_kevlar":              price = 650; break;
            case "item_assaultsuit":         price = 1000; break;

            case "weapon_taser":             price = 200; break;

            case "weapon_glock":             canRefund = isT; break;
            case "weapon_hkp2000":           canRefund = isCT; break;
            case "weapon_usp_silencer":      canRefund = isCT; break;
            case "weapon_elite":             price = 300;  break;
            case "weapon_p250":              price = 300;  break;
            case "weapon_tec9":              price = 500;  canRefund = isT; break;
            case "weapon_fiveseven":         price = 500;  canRefund = isCT; break;
            case "weapon_deagle":            price = 700;  break;
            case "weapon_cz75a":             price = 500;  break;
            case "weapon_revolver":          price = 600;  break;

            case "weapon_mac10":             price = 1050; canRefund = isT; break;
            case "weapon_mp9":               price = 1250; canRefund = isCT; break;
            case "weapon_mp7":               price = 1500; break;
            case "weapon_mp5sd":             price = 1500; break;
            case "weapon_ump45":             price = 1200; break;
            case "weapon_bizon":             price = 1400; break;
            case "weapon_p90":               price = 2350; break;

            case "weapon_nova":              price = 1050; break;
            case "weapon_xm1014":            price = 2000; break;
            case "weapon_sawedoff":          price = 1100; canRefund = isT; break;
            case "weapon_mag7":              price = 1300; canRefund = isCT; break;

            case "weapon_galilar":           price = 1800; canRefund = isT; break;
            case "weapon_ak47":              price = 2700; canRefund = isT; break;
            case "weapon_sg556":             price = 3000; canRefund = isT; break;
            case "weapon_famas":             price = 1950; canRefund = isCT; break;
            case "weapon_m4a1":              price = 2900; canRefund = isCT; break;
            case "weapon_m4a1_silencer":     price = 2900; canRefund = isCT; break;
            case "weapon_aug":               price = 3300; canRefund = isCT; break;

            case "weapon_ssg08":             price = 1700; break;
            case "weapon_awp":               price = 4750; break;
            case "weapon_scar20":            price = 5000; canRefund = isCT; break;
            case "weapon_g3sg1":             price = 5000; canRefund = isT; break;

            case "weapon_negev":             price = 1700; break;
            case "weapon_m249":              price = 5200; break;

            default: return false;
        }

        if (!canRefund)
            return false;

        if (itemName == "item_assaultsuit" && priceOverride.HasValue)
            price = priceOverride.Value;

        if (itemName.StartsWith("weapon_"))
        {
            player.RemoveItemByDesignerName(itemName);
        }
        else if (itemName == "item_assaultsuit" || itemName == "item_kevlar")
        {
            pawn.ArmorValue = 0;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        }

        player.InGameMoneyServices.Account += price;
        // Cap at mp_maxmoney
        int maxMoney = ConVar.Find("mp_maxmoney")?.GetPrimitiveValue<int>() ?? 16000;
        if (player.InGameMoneyServices.Account > maxMoney)
            player.InGameMoneyServices.Account = maxMoney;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");

        return true;
    }

    private bool CanRefund(CCSPlayerController player, string itemName)
    {
        if (IsFirstRoundOfHalf()) 
            return true;

        if (!player.IsValid || !player.IsBot)
            return false;

        // Check refund restrictions
        var (prevWeapons, _, _) = PreviousInventory(player);
        return !prevWeapons.Contains(itemName);
    }

    private bool Swap(CCSPlayerController player, string oldItem, string newItem)
    {
        if (!player.IsValid || !player.IsBot || player.InGameMoneyServices == null)
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return false;

        if (!Refund(player, oldItem))
            return false;

        if (!Buy(player, newItem))
        {
            Buy(player, oldItem);
            return false;
        }
        return true;
    }

    private bool IsFirstRoundOfHalf()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;

            if (gameRules == null)
                return false;

            int played = gameRules.TotalRoundsPlayed;
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            int otMaxRounds = ConVar.Find("mp_overtime_maxrounds")?.GetPrimitiveValue<int>() ?? 6;

            if (maxRounds <= 0) maxRounds = 24;
            if (otMaxRounds <= 0) otMaxRounds = 6;

            int half = maxRounds / 2;
            int otHalf = otMaxRounds / 2;

            return played == 0
                || played == half
                || played == maxRounds
                || (played > maxRounds && (played - maxRounds) % otHalf == 0);
        }
        catch
        {
            return false;
        }
    }

    private bool IsSecondToLastRoundOfHalf()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;

            if (gameRules == null)
                return false;

            int played = gameRules.TotalRoundsPlayed;
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            if (maxRounds <= 0) maxRounds = 24;
            int half = maxRounds / 2;

            return played == half - 2 || played == maxRounds - 2;
        }
        catch { return false; }
    }

    private int CurrentRoundsPlayed()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
            return Math.Max(0, gameRules?.TotalRoundsPlayed ?? 0);
        }
        catch
        {
            return 0;
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

    private bool IsLastMatchPointRound()
    {
        SynchronizeCompetitiveScore();
        int roundsPlayed = CurrentRoundsPlayed();
        int maxRounds = ReadMaxRounds();
        int overtimeMaxRounds = ReadOvertimeMaxRounds();
        return RoundSchedule.IsMatchPoint(
                _terroristScore,
                _counterTerroristScore,
                roundsPlayed,
                maxRounds,
                overtimeMaxRounds)
            || RoundSchedule.IsMatchPoint(
                _counterTerroristScore,
                _terroristScore,
                roundsPlayed,
                maxRounds,
                overtimeMaxRounds);
    }

    private bool IsLastRegulationRound()
    {
        SynchronizeCompetitiveScore();
        int roundsPlayed = CurrentRoundsPlayed();
        int maxRounds = ReadMaxRounds();
        return RoundSchedule.IsLastRegulationRound(
                _terroristScore,
                _counterTerroristScore,
                roundsPlayed,
                maxRounds)
            || RoundSchedule.IsLastRegulationRound(
                _counterTerroristScore,
                _terroristScore,
                roundsPlayed,
                maxRounds);
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

    private bool IsLastRoundOfHalf()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;

            if (gameRules == null)
                return false;

            int played = gameRules.TotalRoundsPlayed;
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            int overtimeMaxRounds = ConVar.Find("mp_overtime_maxrounds")?.GetPrimitiveValue<int>() ?? 6;
            return RoundSchedule.IsLastRoundOfHalf(played, maxRounds, overtimeMaxRounds);
        }
        catch { return false; }
    }

    private int GetBotIndex(CCSPlayerController player)
    {
        if (!player.IsValid || !player.IsBot) return -1;
        int userId = player.UserId ?? -1;
        if (userId == -1) return -1;

        if (_botUserIdToIndex.TryGetValue(userId, out int idx)) return idx;
        int newIdx = ++_botIndexCounter;
        _botUserIdToIndex[userId] = newIdx;
        return newIdx;
    }

    private void RemoveBot(CCSPlayerController player)
    {
        if (player == null) return;
        int userId = player.UserId ?? -1;
        if (userId == -1) return;

        if (_botUserIdToIndex.Remove(userId, out int idx))
        {
            _prevWeapons.Remove(idx);
            _prevMoney.Remove(idx);
            _prevArmor.Remove(idx);
        }
    }

    private void SavePreviousInventory()
    {
        if (IsFirstRoundOfHalf())
        {
            _prevWeapons.Clear();
            _prevMoney.Clear();
            _prevArmor.Clear();
            return;
        }

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot) continue;
            int idx = GetBotIndex(player);
            if (idx == -1) continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid) continue;

            List<string> weapons = new();
            if (pawn.WeaponServices != null)
            {
                foreach (var wHandle in pawn.WeaponServices.MyWeapons)
                {
                    var w = wHandle.Value;
                    if (w == null) continue;
                    string name = w.DesignerName;
                    if (name == "item_kevlar" || name == "item_assaultsuit" || name == "item_defuser") continue;
                    weapons.Add(name);
                }
            }

            int money = player.InGameMoneyServices?.Account ?? 0;
            int armor = pawn.ArmorValue;

            _prevWeapons[idx] = weapons;
            _prevMoney[idx] = money;
            _prevArmor[idx] = armor;
        }
    }

    private void ClearPreviousInventory(CCSPlayerController player)
    {
        if (!player.IsValid || !player.IsBot)
            return;

        int idx = GetBotIndex(player);
        if (idx == -1) return;

        _prevWeapons.Remove(idx);
        _prevArmor.Remove(idx);
    }

    private (List<string> Weapons, int Money, int Armor) Inventory(CCSPlayerController player)
    {
        List<string> weapons = new();
        int money = 0;
        int armor = 0;

        if (!player.IsValid || !player.IsBot) return (weapons, money, armor);
        int idx = GetBotIndex(player);
        if (idx == -1) return (weapons, money, armor);

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return (weapons, money, armor);

        money = player.InGameMoneyServices?.Account ?? 0;
        armor = pawn.ArmorValue;

        if (pawn.WeaponServices != null)
        {
            foreach (var wHandle in pawn.WeaponServices.MyWeapons)
            {
                var w = wHandle.Value;
                if (w == null) continue;
                string name = w.DesignerName;
                if (name == "item_kevlar" || name == "item_assaultsuit" || name == "item_defuser") continue;
                weapons.Add(name);
            }
        }

        return (weapons, money, armor);
    }

    private (List<string> Weapons, int Money, int Armor) PreviousInventory(CCSPlayerController player)
    {
        List<string> weapons = new();
        int money = 0;
        int armor = 0;

        if (!player.IsValid || !player.IsBot) return (weapons, money, armor);
        int idx = GetBotIndex(player);
        if (idx == -1) return (weapons, money, armor);

        if (IsFirstRoundOfHalf()) return (weapons, money, armor);

        if (_prevWeapons.TryGetValue(idx, out var w)) weapons = w;
        if (_prevMoney.TryGetValue(idx, out int m)) money = m;
        if (_prevArmor.TryGetValue(idx, out int a)) armor = a;

        return (weapons, money, armor);
    }
}
//----------------------------------------------------------------------------------------------
