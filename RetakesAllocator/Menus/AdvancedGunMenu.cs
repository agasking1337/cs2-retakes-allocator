using System;
using System.Threading.Tasks;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesAllocator;
using RetakesAllocatorCore;
using RetakesAllocatorCore.Config;
using RetakesAllocatorCore.Db;
using T3MenuSharedApi;

namespace RetakesAllocator.AdvancedMenus;

public class AdvancedGunMenu
{
    public IT3MenuManager? MenuManager { get; set; }

    public HookResult OnEventPlayerChat(EventPlayerChat @event, GameEventInfo info)
    {
        if (@event == null)
        {
            return HookResult.Continue;
        }

        var player = Utilities.GetPlayerFromUserid(@event.Userid);
        if (!Helpers.PlayerIsValid(player))
        {
            return HookResult.Continue;
        }

        var message = (@event.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(message))
        {
            return HookResult.Continue;
        }

        var commands = Configs.GetConfigData().InGameGunMenuCenterCommands.Split(',');
        if (commands.Any(cmd => cmd.Equals(message, StringComparison.OrdinalIgnoreCase)))
        {
            _ = OpenMenuForPlayerAsync(player!);
        }

        return HookResult.Continue;
    }

    public void OnTick()
    {
        // Menu updates are handled by the Kitsune menu framework.
    }

    public HookResult OnEventPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event?.Userid == null)
        {
            return HookResult.Continue;
        }

        var player = @event.Userid;
        if (!Helpers.PlayerIsValid(player))
        {
            return HookResult.Continue;
        }

        return HookResult.Continue;
    }

    private async Task OpenMenuForPlayerAsync(CCSPlayerController player)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        if (!Configs.GetConfigData().CanPlayersSelectWeapons())
        {
            Helpers.WriteNewlineDelimited(Translator.Instance["weapon_preference.cannot_choose"], player.PrintToChat);
            return;
        }

        var team = Helpers.GetTeam(player);
        if (team is not CsTeam.Terrorist and not CsTeam.CounterTerrorist)
        {
            Helpers.WriteNewlineDelimited(Translator.Instance["weapon_preference.join_team"], player.PrintToChat);
            return;
        }

        var steamId = Helpers.GetSteamId(player);
        if (steamId == 0)
        {
            Helpers.WriteNewlineDelimited(Translator.Instance["guns_menu.invalid_steam_id"], player.PrintToChat);
            return;
        }

        var data = await BuildMenuDataAsync(team, steamId);
        if (data == null)
        {
            Helpers.WriteNewlineDelimited(Translator.Instance["weapon_preference.not_saved"], player.PrintToChat);
            return;
        }

        Server.NextFrame(() =>
        {
            if (!Helpers.PlayerIsValid(player))
            {
                return;
            }

            ShowMenu(player, data);
        });
    }

    private async Task<GunMenuData?> BuildMenuDataAsync(CsTeam team, ulong steamId)
    {
        var userSettings = await Queries.GetUserSettings(steamId);
        var config = Configs.GetConfigData();
        var useUnion = config.EnableAllWeaponsForEveryone;

        List<CsItem> primaryOptions;
        List<CsItem> halfBuyPrimaryOptions;
        List<CsItem> secondaryOptions;
        List<CsItem> pistolOptions;

        if (useUnion)
        {
            var t = CsTeam.Terrorist;
            var ct = CsTeam.CounterTerrorist;
            primaryOptions = WeaponHelpers
                .GetPossibleWeaponsForAllocationType(WeaponAllocationType.FullBuyPrimary, t)
                .Union(WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.FullBuyPrimary, ct))
                .Distinct()
                .ToList();
            halfBuyPrimaryOptions = WeaponHelpers
                .GetPossibleWeaponsForAllocationType(WeaponAllocationType.HalfBuyPrimary, t)
                .Union(WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.HalfBuyPrimary, ct))
                .Distinct()
                .ToList();
            secondaryOptions = WeaponHelpers
                .GetPossibleWeaponsForAllocationType(WeaponAllocationType.Secondary, t)
                .Union(WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.Secondary, ct))
                .Distinct()
                .ToList();
            pistolOptions = WeaponHelpers
                .GetPossibleWeaponsForAllocationType(WeaponAllocationType.PistolRound, t)
                .Union(WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.PistolRound, ct))
                .Distinct()
                .ToList();
        }
        else
        {
            var primaryOptionsRaw = WeaponHelpers
                .GetPossibleWeaponsForAllocationType(WeaponAllocationType.FullBuyPrimary, team)
                .ToList();
            var halfBuyPrimaryOptionsRaw = WeaponHelpers
                .GetPossibleWeaponsForAllocationType(WeaponAllocationType.HalfBuyPrimary, team)
                .ToList();
            var secondaryOptionsRaw = WeaponHelpers
                .GetPossibleWeaponsForAllocationType(WeaponAllocationType.Secondary, team)
                .ToList();
            var pistolOptionsRaw = WeaponHelpers
                .GetPossibleWeaponsForAllocationType(WeaponAllocationType.PistolRound, team)
                .ToList();

            // Team-strict lists
            primaryOptions = FilterByRoundAndTeam(primaryOptionsRaw, RoundType.FullBuy, team, WeaponAllocationType.FullBuyPrimary);
            halfBuyPrimaryOptions = FilterByRoundAndTeam(halfBuyPrimaryOptionsRaw, RoundType.HalfBuy, team, WeaponAllocationType.HalfBuyPrimary);
            secondaryOptions = FilterByRoundAndTeam(secondaryOptionsRaw, RoundType.FullBuy, team, WeaponAllocationType.Secondary);
            pistolOptions = FilterByRoundAndTeam(pistolOptionsRaw, RoundType.Pistol, team, WeaponAllocationType.PistolRound);
        }

        var currentPrimary = userSettings?.GetWeaponPreference(team, WeaponAllocationType.FullBuyPrimary) ??
                             GetDefaultWeapon(team, WeaponAllocationType.FullBuyPrimary, primaryOptions);
        var currentHalfBuyPrimary = userSettings?.GetWeaponPreference(team, WeaponAllocationType.HalfBuyPrimary) ??
                             GetDefaultWeapon(team, WeaponAllocationType.HalfBuyPrimary, halfBuyPrimaryOptions);
        var currentSecondary = userSettings?.GetWeaponPreference(team, WeaponAllocationType.Secondary) ??
                               GetDefaultWeapon(team, WeaponAllocationType.Secondary, secondaryOptions);
        var currentPistol = userSettings?.GetWeaponPreference(team, WeaponAllocationType.PistolRound) ??
                            GetDefaultWeapon(team, WeaponAllocationType.PistolRound, pistolOptions);

        // Coerce saved selections to valid lists
        if (currentPrimary.HasValue && !primaryOptions.Contains(currentPrimary.Value))
        {
            currentPrimary = GetDefaultWeapon(team, WeaponAllocationType.FullBuyPrimary, primaryOptions);
        }
        if (currentHalfBuyPrimary.HasValue && !halfBuyPrimaryOptions.Contains(currentHalfBuyPrimary.Value))
        {
            currentHalfBuyPrimary = GetDefaultWeapon(team, WeaponAllocationType.HalfBuyPrimary, halfBuyPrimaryOptions);
        }
        if (currentSecondary.HasValue && !secondaryOptions.Contains(currentSecondary.Value))
        {
            currentSecondary = GetDefaultWeapon(team, WeaponAllocationType.Secondary, secondaryOptions);
        }
        if (currentPistol.HasValue && !pistolOptions.Contains(currentPistol.Value))
        {
            currentPistol = GetDefaultWeapon(team, WeaponAllocationType.PistolRound, pistolOptions);
        }

        var preferredSniper = userSettings?.GetWeaponPreference(team, WeaponAllocationType.Preferred);

        return new GunMenuData
        {
            SteamId = steamId,
            Team = team,
            PrimaryOptions = primaryOptions,
            HalfBuyPrimaryOptions = halfBuyPrimaryOptions,
            SecondaryOptions = secondaryOptions,
            PistolOptions = pistolOptions,
            CurrentPrimary = currentPrimary,
            CurrentHalfBuyPrimary = currentHalfBuyPrimary,
            CurrentSecondary = currentSecondary,
            CurrentPistol = currentPistol,
            PreferredSniper = preferredSniper,
            ZeusEnabled = userSettings?.ZeusEnabled ?? false,
            EnemyStuffPreference = NormalizeEnemyStuffPreference(userSettings?.EnemyStuffTeamPreference)
        };
    }

    private static List<CsItem> FilterByRoundAndTeam(IEnumerable<CsItem> items, RoundType round, CsTeam team, WeaponAllocationType expectedType)
    {
        var filtered = new List<CsItem>();
        foreach (var item in items)
        {
            var at = WeaponHelpers.GetWeaponAllocationTypeForWeaponAndRound(round, team, item);
            if (at.HasValue && at.Value == expectedType)
            {
                filtered.Add(item);
            }
        }
        return filtered;
    }

    private void ShowMenu(CCSPlayerController player, GunMenuData data)
    {
        if (MenuManager == null)
        {
            return;
        }

        var teamDisplayName = GetTeamDisplayName(data.Team);
        var menuTitle = Translator.Instance["guns_menu.title", teamDisplayName];
        IT3Menu menu = MenuManager.CreateMenu(menuTitle, isSubMenu: false);

        var config = Configs.GetConfigData();
        var isVip = Helpers.IsVip(player);
        var canUseEnemyStuff = config.EnableEnemyStuffPreference && Helpers.HasEnemyStuffPermission(player);

        var fullBuyTitle = RoundTypeHelpers.TranslateRoundTypeName(RoundType.FullBuy);
        menu.AddOption(fullBuyTitle, (ply, opt) =>
        {
            var sub = BuildFullBuySubMenu(ply, data, isVip, canUseEnemyStuff, config.EnableZeusPreference);
            MenuManager.OpenSubMenu(ply, sub);
        });

        var halfBuyTitle = RoundTypeHelpers.TranslateRoundTypeName(RoundType.HalfBuy);
        menu.AddOption(halfBuyTitle, (ply, opt) =>
        {
            var sub = BuildHalfBuySubMenu(data);
            MenuManager.OpenSubMenu(ply, sub);
        });

        var pistolTitle = RoundTypeHelpers.TranslateRoundTypeName(RoundType.Pistol);
        menu.AddOption(pistolTitle, (ply, opt) =>
        {
            var sub = BuildPistolSubMenu(data);
            MenuManager.OpenSubMenu(ply, sub);
        });

        menu.AddOption(Translator.Instance["menu.exit"], (ply, opt) => { });
        MenuManager.OpenMainMenu(player, menu);
    }

    private IT3Menu BuildHalfBuySubMenu(GunMenuData data)
    {
        var title = RoundTypeHelpers.TranslateRoundTypeName(RoundType.HalfBuy);
        var sub = MenuManager!.CreateMenu(title, isSubMenu: true);

        if (data.HalfBuyPrimaryOptions.Count > 0)
        {
            sub.AddOption(Translator.Instance["weapon_type.primary"], (ply, opt) =>
            {
                var wsub = BuildWeaponSubMenu(
                    data,
                    Translator.Instance["weapon_type.primary"],
                    data.HalfBuyPrimaryOptions,
                    data.CurrentHalfBuyPrimary,
                    (p, d, w) =>
                    {
                        d.CurrentHalfBuyPrimary = w;
                        ApplyWeaponSelection(p, d.SteamId, d.Team, RoundType.HalfBuy, w);
                    },
                    ply2 => BuildHalfBuySubMenu(data)
                );
                MenuManager!.OpenSubMenu(ply, wsub);
            });
        }

        if (data.SecondaryOptions.Count > 0)
        {
            sub.AddOption(Translator.Instance["weapon_type.secondary"], (ply, opt) =>
            {
                var wsub = BuildWeaponSubMenu(
                    data,
                    Translator.Instance["weapon_type.secondary"],
                    data.SecondaryOptions,
                    data.CurrentPistol,
                    (p, d, w) =>
                    {
                        d.CurrentPistol = w;
                        ApplyWeaponSelection(p, d.SteamId, d.Team, RoundType.Pistol, w);
                    },
                    ply2 => BuildHalfBuySubMenu(data)
                );
                MenuManager!.OpenSubMenu(ply, wsub);
            });
        }

        sub.AddOption(Translator.Instance["menu.exit"], (ply, opt) => { ShowMenu(ply, data); });
        return sub;
    }

    private IT3Menu BuildFullBuySubMenu(CCSPlayerController player, GunMenuData data, bool isVip, bool canUseEnemyStuff, bool zeusEnabledSetting)
    {
        var title = RoundTypeHelpers.TranslateRoundTypeName(RoundType.FullBuy);
        var sub = MenuManager!.CreateMenu(title, isSubMenu: true);

        if (data.PrimaryOptions.Count > 0)
        {
            sub.AddOption(Translator.Instance["weapon_type.primary"], (ply, opt) =>
            {
                var wsub = BuildWeaponSubMenu(
                    data,
                    Translator.Instance["weapon_type.primary"],
                    data.PrimaryOptions,
                    data.CurrentPrimary,
                    (p, d, w) =>
                    {
                        d.CurrentPrimary = w;
                        ApplyWeaponSelection(p, d.SteamId, d.Team, RoundType.FullBuy, w);
                    },
                    ply2 => BuildFullBuySubMenu(player, data, isVip, canUseEnemyStuff, zeusEnabledSetting)
                );
                MenuManager!.OpenSubMenu(ply, wsub);
            });
        }

        if (data.SecondaryOptions.Count > 0)
        {
            sub.AddOption(Translator.Instance["weapon_type.secondary"], (ply, opt) =>
            {
                var wsub = BuildWeaponSubMenu(
                    data,
                    Translator.Instance["weapon_type.secondary"],
                    data.SecondaryOptions,
                    data.CurrentSecondary,
                    (p, d, w) =>
                    {
                        d.CurrentSecondary = w;
                        ApplyWeaponSelection(p, d.SteamId, d.Team, RoundType.FullBuy, w);
                    },
                    ply2 => BuildFullBuySubMenu(player, data, isVip, canUseEnemyStuff, zeusEnabledSetting)
                );
                MenuManager!.OpenSubMenu(ply, wsub);
            });
        }

        if (isVip)
        {
            sub.AddOption(Translator.Instance["guns_menu.sniper_label"], (ply, opt) =>
            {
                var ssub = BuildSniperSubMenu(data);
                MenuManager!.OpenSubMenu(ply, ssub);
            });
        }

        if (canUseEnemyStuff)
        {
            sub.AddOption(Translator.Instance["guns_menu.enemy_stuff_label"], (ply, opt) =>
            {
                var esub = BuildEnemyStuffSubMenu(data);
                MenuManager!.OpenSubMenu(ply, esub);
            });
        }

        if (zeusEnabledSetting)
        {
            var zeusChoices = new[]
            {
                Translator.Instance["guns_menu.zeus_choice_disable"],
                Translator.Instance["guns_menu.zeus_choice_enable"]
            };

            sub.AddBoolOption(
                Translator.Instance["guns_menu.zeus_label"],
                defaultValue: data.ZeusEnabled,
                (ply, option) =>
                {
                    var enabled = false;
                    if (option is IT3Option opt && opt.DefaultValue is bool b)
                    {
                        enabled = b;
                    }
                    var choice = enabled ? zeusChoices[1] : zeusChoices[0];
                    HandleZeusChoice(ply, data, choice, zeusChoices);
                    MenuManager!.Refresh();
                });
        }

        sub.AddOption(Translator.Instance["menu.exit"], (ply, opt) => { ShowMenu(ply, data); });
        return sub;
    }

    private IT3Menu BuildPistolSubMenu(GunMenuData data)
    {
        var title = RoundTypeHelpers.TranslateRoundTypeName(RoundType.Pistol);
        var sub = MenuManager!.CreateMenu(title, isSubMenu: true);

        if (data.PistolOptions.Count > 0)
        {
            var current = data.CurrentPistol;
            var items = data.PistolOptions;
            var ordered = items;
            if (current.HasValue)
            {
                ordered = items.OrderBy(i => i.Equals(current.Value) ? 0 : 1).ToList();
            }

            foreach (var item in ordered)
            {
                var w = item;
                var isSelected = current.HasValue && current.Value == w;
                var label = isSelected ? $"✔ {w.GetName()}" : w.GetName();
                sub.AddOption(label, (ply, opt) =>
                {
                    data.CurrentPistol = w;
                    ApplyWeaponSelection(ply, data.SteamId, data.Team, RoundType.Pistol, w);
                    ShowMenu(ply, data);
                });
            }
        }

        sub.AddOption(Translator.Instance["menu.exit"], (ply, opt) => { ShowMenu(ply, data); });
        return sub;
    }

    private IT3Menu BuildWeaponSubMenu(
        GunMenuData data,
        string title,
        List<CsItem> items,
        CsItem? current,
        Action<CCSPlayerController, GunMenuData, CsItem> onSelect,
        Func<CCSPlayerController, IT3Menu> getParentMenu
    )
    {
        var sub = MenuManager!.CreateMenu(title, isSubMenu: true);
        var orderedItems = items;
        if (current.HasValue)
        {
            orderedItems = items
                .OrderBy(i => i.Equals(current.Value) ? 0 : 1)
                .ToList();
        }

        foreach (var item in orderedItems)
        {
            var w = item;
            var isSelected = current.HasValue && current.Value == w;
            var label = isSelected ? $"✔ {w.GetName()}" : w.GetName();
            sub.AddOption(label, (ply, opt) =>
            {
                onSelect(ply, data, w);
                // Reset root to main, then open the parent submenu to avoid stacking
                ShowMenu(ply, data);
                var parent = getParentMenu(ply);
                MenuManager!.OpenSubMenu(ply, parent);
            });
        }

        sub.AddOption(Translator.Instance["menu.exit"], (ply, opt) =>
        {
            ShowMenu(ply, data);
        });
        return sub;
    }

    private IT3Menu BuildSniperSubMenu(GunMenuData data)
    {
        var title = Translator.Instance["guns_menu.sniper_label"];
        var sub = MenuManager!.CreateMenu(title, isSubMenu: true);

        var awp = Translator.Instance["guns_menu.sniper_awp"];
        var ssg = Translator.Instance["guns_menu.sniper_ssg"];
        var random = Translator.Instance["guns_menu.sniper_random"];
        var disabled = Translator.Instance["guns_menu.sniper_disabled"];

        var pref = data.PreferredSniper;
        var isAwp = pref.HasValue && pref.Value == CsItem.AWP;
        var isSsg = pref.HasValue && pref.Value == CsItem.Scout;
        var isRandom = pref.HasValue && WeaponHelpers.IsRandomSniperPreference(pref.Value);
        var isDisabled = !pref.HasValue;

        var awpLabel = isAwp ? $"✔ {awp}" : awp;
        sub.AddOption(awpLabel, (ply, opt) =>
        {
            ApplySniperPreference(ply, data, CsItem.AWP);
            sub.Title = $"{title} - {awp}";
            MenuManager!.Refresh();
        });

        var ssgLabel = isSsg ? $"✔ {ssg}" : ssg;
        sub.AddOption(ssgLabel, (ply, opt) =>
        {
            ApplySniperPreference(ply, data, CsItem.Scout);
            sub.Title = $"{title} - {ssg}";
            MenuManager!.Refresh();
        });

        var randomLabel = isRandom ? $"✔ {random}" : random;
        sub.AddOption(randomLabel, (ply, opt) =>
        {
            ApplySniperPreference(ply, data, WeaponHelpers.RandomSniperPreference);
            sub.Title = $"{title} - {random}";
            MenuManager!.Refresh();
        });

        var disabledLabel = isDisabled ? $"✔ {disabled}" : disabled;
        sub.AddOption(disabledLabel, (ply, opt) =>
        {
            ApplySniperPreference(ply, data, null);
            sub.Title = $"{title} - {disabled}";
            MenuManager!.Refresh();
        });

        sub.AddOption(Translator.Instance["menu.back"], (ply, opt) =>
        {
            ShowMenu(ply, data);
        });

        return sub;
    }

    private IT3Menu BuildEnemyStuffSubMenu(GunMenuData data)
    {
        var title = Translator.Instance["guns_menu.enemy_stuff_label"];
        var sub = MenuManager!.CreateMenu(title, isSubMenu: true);

        var labels = new[]
        {
            Translator.Instance["guns_menu.enemy_stuff_choice_disable"],
            Translator.Instance["guns_menu.enemy_stuff_choice_t_only"],
            Translator.Instance["guns_menu.enemy_stuff_choice_ct_only"],
            Translator.Instance["guns_menu.enemy_stuff_choice_both"]
        };
        var values = new[]
        {
            EnemyStuffTeamPreference.None,
            EnemyStuffTeamPreference.Terrorist,
            EnemyStuffTeamPreference.CounterTerrorist,
            EnemyStuffTeamPreference.Both
        };

        var current = NormalizeEnemyStuffPreference(data.EnemyStuffPreference);

        for (var i = 0; i < labels.Length; i++)
        {
            var idx = i;
            var label = labels[idx];
            var val = values[idx];
            var isSelected = current == val;
            var display = isSelected ? $"✔ {label}" : label;
            sub.AddOption(display, (ply, opt) =>
            {
                HandleEnemyStuffChoice(ply, data, label, labels, values);
                sub.Title = $"{title} - {label}";
                MenuManager!.Refresh();
            });
        }

        sub.AddOption(Translator.Instance["menu.back"], (ply, opt) =>
        {
            ShowMenu(ply, data);
        });

        return sub;
    }

    private void HandlePrimaryChoice(CCSPlayerController player, GunMenuData data, string choice)
    {
        var weapon = FindWeaponByName(data.PrimaryOptions, choice);
        if (weapon == null)
        {
            return;
        }

        data.CurrentPrimary = weapon;
        ApplyWeaponSelection(player, data.SteamId, data.Team, RoundType.FullBuy, weapon.Value);
    }

    private void HandleSecondaryChoice(CCSPlayerController player, GunMenuData data, string choice)
    {
        var weapon = FindWeaponByName(data.SecondaryOptions, choice);
        if (weapon == null)
        {
            return;
        }

        data.CurrentSecondary = weapon;
        ApplyWeaponSelection(player, data.SteamId, data.Team, RoundType.FullBuy, weapon.Value);
    }

    private void HandlePistolChoice(CCSPlayerController player, GunMenuData data, string choice)
    {
        var weapon = FindWeaponByName(data.PistolOptions, choice);
        if (weapon == null)
        {
            return;
        }

        data.CurrentPistol = weapon;
        ApplyWeaponSelection(player, data.SteamId, data.Team, RoundType.Pistol, weapon.Value);
    }

    private void ApplyWeaponSelection(CCSPlayerController player, ulong steamId, CsTeam team,
        RoundType roundType, CsItem weapon)
    {
        var weaponName = weapon.GetName();
        _ = Task.Run(async () =>
        {
            var result = await OnWeaponCommandHelper.HandleAsync(new[] { weaponName }, steamId, roundType, team, false);
            // Mirror selection to the other team if union mode is enabled
            try
            {
                var config = Configs.GetConfigData();
                if (config.EnableAllWeaponsForEveryone)
                {
                    var otherTeam = team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
                    var thisAlloc = WeaponHelpers.GetWeaponAllocationTypeForWeaponAndRound(roundType, team, weapon);
                    if (thisAlloc.HasValue)
                    {
                        await Queries.SetWeaponPreferenceForUserAsync(steamId, otherTeam, thisAlloc.Value, weapon);
                    }
                }
            }
            catch
            {
                // best-effort mirror; ignore failures
            }
            if (string.IsNullOrWhiteSpace(result.Item1))
            {
                return;
            }

            Server.NextFrame(() =>
            {
                if (!Helpers.PlayerIsValid(player))
                {
                    return;
                }

                Helpers.WriteNewlineDelimited(result.Item1, player.PrintToChat);
            });
        });
    }

    private void HandleSniperChoice(CCSPlayerController player, GunMenuData data, string choice, IReadOnlyList<string> options)
    {
        if (choice == options[0])
        {
            ApplySniperPreference(player, data, CsItem.AWP);
        }
        else if (choice == options[1])
        {
            ApplySniperPreference(player, data, CsItem.Scout);
        }
        else if (choice == options[2])
        {
            ApplySniperPreference(player, data, WeaponHelpers.RandomSniperPreference);
        }
        else
        {
            ApplySniperPreference(player, data, null);
        }
    }
    private void HandleZeusChoice(CCSPlayerController player, GunMenuData data, string choice, IReadOnlyList<string> options)
    {
        var enabled = choice == options[1];
        if (data.ZeusEnabled == enabled)
        {
            return;
        }

        data.ZeusEnabled = enabled;
        Queries.SetZeusPreference(data.SteamId, enabled);

        var messageKey = enabled ? "guns_menu.zeus_enabled_message" : "guns_menu.zeus_disabled_message";
        Helpers.WriteNewlineDelimited(Translator.Instance[messageKey], player.PrintToChat);
    }
    private void HandleEnemyStuffChoice(
        CCSPlayerController player,
        GunMenuData data,
        string choice,
        IReadOnlyList<string> options,
        IReadOnlyList<EnemyStuffTeamPreference> values)
    {
        if (!Helpers.HasEnemyStuffPermission(player))
        {
            Helpers.WriteNewlineDelimited(Translator.Instance["weapon_preference.only_vip_can_use"], player.PrintToChat);
            return;
        }

        var selectedIndex = -1;
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i].Equals(choice, StringComparison.Ordinal))
            {
                selectedIndex = i;
                break;
            }
        }

        if (selectedIndex < 0 || selectedIndex >= values.Count)
        {
            return;
        }

        var selectedPreference = NormalizeEnemyStuffPreference(values[selectedIndex]);
        if (NormalizeEnemyStuffPreference(data.EnemyStuffPreference) == selectedPreference)
        {
            return;
        }

        data.EnemyStuffPreference = selectedPreference;
        Queries.SetEnemyStuffPreference(data.SteamId, selectedPreference);

        var messageKey = selectedPreference switch
        {
            EnemyStuffTeamPreference.None => "guns_menu.enemy_stuff_disabled_message",
            EnemyStuffTeamPreference.Terrorist => "guns_menu.enemy_stuff_enabled_t_message",
            EnemyStuffTeamPreference.CounterTerrorist => "guns_menu.enemy_stuff_enabled_ct_message",
            _ => "guns_menu.enemy_stuff_enabled_both_message"
        };
        Helpers.WriteNewlineDelimited(Translator.Instance[messageKey], player.PrintToChat);
    }

    private static EnemyStuffTeamPreference NormalizeEnemyStuffPreference(EnemyStuffTeamPreference? preference)
    {
        if (preference is null)
        {
            return EnemyStuffTeamPreference.None;
        }

        var value = preference.Value;
        var includesT = value.HasFlag(EnemyStuffTeamPreference.Terrorist);
        var includesCt = value.HasFlag(EnemyStuffTeamPreference.CounterTerrorist);

        return (includesT, includesCt) switch
        {
            (true, true) => EnemyStuffTeamPreference.Both,
            (true, false) => EnemyStuffTeamPreference.Terrorist,
            (false, true) => EnemyStuffTeamPreference.CounterTerrorist,
            _ => EnemyStuffTeamPreference.None,
        };
    }
    private void ApplySniperPreference(CCSPlayerController player, GunMenuData data, CsItem? preference)
    {
        var steamId = data.SteamId;
        var previousPreference = data.PreferredSniper;
        data.PreferredSniper = preference;

        _ = Task.Run(async () =>
        {
            await Queries.SetAwpWeaponPreferenceAsync(steamId, preference);

            string message;
            if (preference.HasValue)
            {
                message = WeaponHelpers.IsRandomSniperPreference(preference.Value)
                    ? Translator.Instance["weapon_preference.set_preference_preferred_random"]
                    : Translator.Instance["weapon_preference.set_preference_preferred", preference.Value];
            }
            else
            {
                message = previousPreference.HasValue && WeaponHelpers.IsRandomSniperPreference(previousPreference.Value)
                    ? Translator.Instance["weapon_preference.unset_preference_preferred_random"]
                    : Translator.Instance["weapon_preference.unset_preference_preferred", previousPreference ?? CsItem.AWP];
            }

            Server.NextFrame(() =>
            {
                if (!Helpers.PlayerIsValid(player))
                {
                    return;
                }

                Helpers.WriteNewlineDelimited(message, player.PrintToChat);
            });
        });
    }

    private static CsItem? GetDefaultWeapon(CsTeam team, WeaponAllocationType type, IReadOnlyList<CsItem> fallback)
    {
        if (Configs.GetConfigData().DefaultWeapons.TryGetValue(team, out var defaults) &&
            defaults.TryGetValue(type, out var configured))
        {
            return configured;
        }

        return fallback.Count > 0 ? fallback[0] : null;
    }

    private static CsItem? FindWeaponByName(IEnumerable<CsItem> items, string choice)
    {
        return items.FirstOrDefault(item => item.GetName().Equals(choice, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetTeamDisplayName(CsTeam team)
    {
        return team == CsTeam.Terrorist
            ? Translator.Instance["teams.terrorist"]
            : Translator.Instance["teams.counter_terrorist"];
    }

    private sealed class GunMenuData
    {
        public required ulong SteamId { get; init; }
        public required CsTeam Team { get; init; }
        public required List<CsItem> PrimaryOptions { get; init; }
        public required List<CsItem> HalfBuyPrimaryOptions { get; init; }
        public required List<CsItem> SecondaryOptions { get; init; }
        public required List<CsItem> PistolOptions { get; init; }
        public CsItem? CurrentPrimary { get; set; }
        public CsItem? CurrentHalfBuyPrimary { get; set; }
        public CsItem? CurrentSecondary { get; set; }
        public CsItem? CurrentPistol { get; set; }
        public CsItem? PreferredSniper { get; set; }
        public bool ZeusEnabled { get; set; }
        public EnemyStuffTeamPreference EnemyStuffPreference { get; set; }
    }
}











