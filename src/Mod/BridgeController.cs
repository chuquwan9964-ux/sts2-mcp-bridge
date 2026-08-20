using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using Sts2McpBridge.Core;

namespace Sts2McpBridge;

public sealed class BridgeController : Node
{
    private readonly BridgeConfig _config;
    private readonly System.Net.Http.HttpClient _http;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _started;
    private long _version;
    private string? _fingerprint;
    private bool _reportedMultiplayer;
    private BridgeState? _published;

    private sealed record Draft(string Mode, string Screen, object Observation, IReadOnlyList<LegalAction> Actions);
    private sealed record CombatBinding(LegalAction Action, CardModel? Card, PotionModel? Potion, Creature? Target);
    private sealed record MerchantBinding(LegalAction Action, NMerchantSlot Slot);
    private sealed record RewardBinding(LegalAction Action, NRewardButton Button);

    public BridgeController(BridgeConfig config)
    {
        _config = config;
        _http = new() { BaseAddress = EnsureTrailingSlash(config.ServerUrl), Timeout = TimeSpan.FromSeconds(3) };
        _http.DefaultRequestHeaders.Authorization = new("Bearer", config.Token);
        Name = "Sts2McpBridgeController";
        ProcessMode = ProcessModeEnum.Always;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = RunLoopAsync();
    }

    public override void _ExitTree()
    {
        _lifetime.Cancel();
        _http.Dispose();
        _lifetime.Dispose();
    }

    private async Task RunLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested && GodotObject.IsInstanceValid(this) && IsInsideTree())
            {
                await FrameAsync();
                Draft draft = BuildDraft();
                BridgeState state = Materialize(draft);
                _published = state;
                try
                {
                    await RegisterAsync(state);
                    PendingAction? pending = await PollPendingAsync(state.StateVersion);
                    await FrameAsync();
                    if (pending is not null) await ExecutePendingAsync(pending);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
                {
                    // A disconnected bridge is a wait state. It must never trigger a game action.
                    await WaitFramesAsync(30);
                }
                await WaitFramesAsync(8);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { GD.PrintErr($"[Sts2McpBridge] controller stopped: {exception.GetType().Name}: {exception.Message}"); }
    }

    private Draft BuildDraft()
    {
        RunState? run = RunManager.Instance?.DebugOnlyGetState();
        if (run is null) return BuildMainMenu();
        if (run.Players.Count != 1)
        {
            if (!_reportedMultiplayer) { GD.PrintErr("[Sts2McpBridge] multiplayer runs are not supported"); _reportedMultiplayer = true; }
            return new("unsupported", "multiplayer", new { players = run.Players.Count }, []);
        }
        _reportedMultiplayer = false;
        NRun? runNode = NGame.Instance?.CurrentRunNode;
        if (runNode is not null && NMapScreen.Instance?.IsOpen == true) return BuildMap(runNode, run) ?? BaseRun(run, "map_wait");
        if (NOverlayStack.Instance?.Peek() is Node overlay) return BuildOverlay(overlay, run);
        if (CombatManager.Instance.IsInProgress) return BuildCombat(run);
        if (runNode is null) return BaseRun(run, "loading");
        return BuildRoom(runNode, run) ?? BuildMap(runNode, run) ?? BaseRun(run, "unsupported_screen");
    }

    private Draft BuildMainMenu()
    {
        NMainMenu? mainMenu = NGame.Instance?.MainMenu;
        if (mainMenu is null) return new("manual", "main_menu_loading", new { message = "Waiting for the main menu." }, []);
        Control? characterSelect = mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen");
        if (characterSelect?.IsVisibleInTree() == true)
        {
            List<NCharacterSelectButton> characters = UiHelper.FindAll<NCharacterSelectButton>(characterSelect)
                .Where(button => !button.IsLocked && !button.IsRandom)
                .OrderBy(button => button.Character.Id.Entry)
                .ToList();
            return new("main_menu", "character_select", new
            {
                message = "Choose an unlocked character. The action selects and confirms the character using the currently configured ascension preference.",
                characters = characters.Select(button => new { id = button.Character.Id.Entry, name = SafeText(() => button.Character.Title.GetFormattedText()) }).ToList()
            }, characters.Select(button => new LegalAction($"main_menu:start:{button.Character.Id.Entry}", "start_run", $"Start a standard run as {button.Character.Title.GetFormattedText()}")).ToList());
        }
        NButton? standard = mainMenu.GetNodeOrNull<NButton>("Submenus/SingleplayerSubmenu/StandardButton");
        if (standard?.IsVisibleInTree() == true && standard.IsEnabled)
        {
            return new("main_menu", "singleplayer_mode", new { message = "Choose the standard Single Player mode." }, [new("main_menu:standard", "open_standard", "Open Standard run character selection")]);
        }
        List<LegalAction> actions = [];
        NButton? continueButton = mainMenu.GetNodeOrNull<NButton>("MainMenuTextButtons/ContinueButton");
        if (continueButton?.Visible == true && continueButton.IsEnabled) actions.Add(new("main_menu:continue", "continue_run", "Continue the existing Single Player run"));
        NButton? singleplayer = mainMenu.GetNodeOrNull<NButton>("MainMenuTextButtons/SingleplayerButton");
        if (singleplayer?.Visible == true && singleplayer.IsEnabled) actions.Add(new("main_menu:singleplayer", "open_singleplayer", "Open Single Player"));
        return new("main_menu", "main_menu", new { message = "Continue an existing run or open Single Player to start a new one." }, actions);
    }

    private static Draft BaseRun(RunState run, string screen) => new("run", screen, new
    {
        run_objective = "Survive the act, reach its boss, and defeat it",
        current_act = run.CurrentActIndex + 1,
        act_floor = run.ActFloor,
        floor = run.TotalFloor,
        limitation = "No safe semantic action is implemented for this screen; use the game UI manually."
    }, []);

    private Draft BuildCombat(RunState run)
    {
        Player? player = LocalContext.GetMe(run);
        PlayerCombatState? pcs = player?.PlayerCombatState;
        ICombatState? combat = player?.Creature.CombatState;
        if (player is null || pcs?.Phase != PlayerTurnPhase.Play || combat is null) return BaseRun(run, "combat_wait");
        string token = CombatToken(player, combat);
        List<CombatBinding> bindings = BuildCombatBindings(player, token);
        bindings.Add(new(new($"combat:{token}:end", "end_turn", "End turn"), null, null, null));
        return new("run", "combat", new
        {
            run_objective = "Survive the act, reach its boss, and defeat it",
            current_act = run.CurrentActIndex + 1,
            act_floor = run.ActFloor,
            floor = run.TotalFloor,
            turn = pcs.TurnNumber,
            round = combat.RoundNumber,
            player = new
            {
                hp = player.Creature.CurrentHp,
                max_hp = player.Creature.MaxHp,
                block = player.Creature.Block,
                energy = pcs.Energy,
                max_energy = pcs.MaxEnergy,
                powers = Powers(player.Creature)
            },
            hand = pcs.Hand.Cards.Select((card, index) => Card(card, $"h{index}", PileType.Hand)).ToList(),
            draw = Pile(pcs.DrawPile, false),
            discard = Pile(pcs.DiscardPile, false),
            exhaust = Pile(pcs.ExhaustPile, false),
            relics = player.Relics.Select(relic => new { id = relic.Id.Entry, title = SafeText(() => relic.Title.GetFormattedText()), description = SafeText(() => relic.DynamicDescription.GetFormattedText()), counter = relic.ShowCounter ? relic.DisplayAmount : null as int? }).ToList(),
            potions = player.PotionSlots.Select((potion, index) => potion is null ? null : new { slot = index, id = potion.Id.Entry, title = SafeText(() => potion.Title.GetFormattedText()), description = SafeText(() => potion.DynamicDescription.GetFormattedText()), usable = CanUsePotion(player, potion) }).Where(value => value is not null).ToList(),
            enemies = combat.Enemies.Select((enemy, index) => new
            {
                key = $"e{index}", id = enemy.ModelId.Entry, name = enemy.Name, hp = enemy.CurrentHp, max_hp = enemy.MaxHp,
                block = enemy.Block, alive = enemy.IsAlive, hittable = enemy.IsHittable, powers = Powers(enemy), next_move = Forecast(enemy, combat)
            }).ToList()
        }, bindings.Select(binding => binding.Action).ToList());
    }

    private static List<CombatBinding> BuildCombatBindings(Player player, string token)
    {
        List<CombatBinding> result = [];
        PlayerCombatState? state = player.PlayerCombatState;
        ICombatState? combat = player.Creature.CombatState;
        if (state?.Phase != PlayerTurnPhase.Play || combat is null) return result;
        for (int hand = 0; hand < state.Hand.Cards.Count; hand++)
        {
            CardModel card = state.Hand.Cards[hand];
            if (!card.CanPlay(out _, out _)) continue;
            if (card.IsValidTarget(null)) result.Add(new(new($"combat:{token}:card:h{hand}:none", "play_card", $"Play h{hand} {card.Title}"), card, null, null));
            for (int target = 0; target < combat.Creatures.Count; target++)
            {
                Creature creature = combat.Creatures[target];
                if (creature.IsHittable && card.IsValidTarget(creature)) result.Add(new(new($"combat:{token}:card:h{hand}:t{target}", "play_card", $"Play h{hand} {card.Title} on {creature.Name}"), card, null, creature));
            }
        }
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            PotionModel? potion = player.PotionSlots[slot];
            if (potion is null || !CanUsePotion(player, potion)) continue;
            string title = SafeText(() => potion.Title.GetFormattedText()) ?? potion.Id.Entry;
            if (potion.IsValidTarget(null)) result.Add(new(new($"combat:{token}:potion:s{slot}:none", "use_potion", $"Use s{slot} {title}"), null, potion, null));
            for (int target = 0; target < combat.Creatures.Count; target++)
            {
                Creature creature = combat.Creatures[target];
                if (potion.IsValidTarget(creature)) result.Add(new(new($"combat:{token}:potion:s{slot}:t{target}", "use_potion", $"Use s{slot} {title} on {creature.Name}"), null, potion, creature));
            }
        }
        return result;
    }

    private Draft? BuildMap(NRun runNode, RunState run)
    {
        if (NMapScreen.Instance?.IsOpen != true || !runNode.GlobalUi.MapScreen.IsVisibleInTree()) return null;
        List<NMapPoint> points = EnabledMapPoints(runNode);
        return new("run", "map", new { current_act = run.CurrentActIndex + 1, act_floor = run.ActFloor, floor = run.TotalFloor, current = run.CurrentMapCoord?.ToString() },
            points.Select(point => new LegalAction($"map:r{point.Point.coord.row}:c{point.Point.coord.col}", "map_path", $"Choose {point.Point.PointType} at row {point.Point.coord.row}, column {point.Point.coord.col}")).ToList());
    }

    private Draft? BuildRoom(NRun runNode, RunState run)
    {
        Node? room = runNode.GetNodeOrNull("RoomContainer");
        Player? player = LocalContext.GetMe(run);
        if (room is null || player is null) return null;
        if (run.CurrentRoom?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.Event)
        {
            List<NEventOptionButton> options = UiHelper.FindAll<NEventOptionButton>(room).Where(option => option.IsEnabled && !option.Option.IsLocked).ToList();
            return new("run", "event", new { current_act = run.CurrentActIndex + 1, floor = run.TotalFloor }, options.Select((option, index) => new LegalAction($"event:{index}", "event_option", option.Option.Title.GetFormattedText())).ToList());
        }
        if (run.CurrentRoom?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.RestSite)
        {
            NRestSiteRoom? rest = room.GetNodeOrNull<NRestSiteRoom>("RestSiteRoom");
            List<NRestSiteButton> buttons = UiHelper.FindAll<NRestSiteButton>(room).Where(button => button.Option.IsEnabled).ToList();
            List<LegalAction> actions = buttons.Select((button, index) => new LegalAction($"rest:{index}", "rest_option", button.Option.GetType().Name)).ToList();
            if (rest?.ProceedButton.IsEnabled == true) actions.Add(new("rest:proceed", "proceed", "Proceed"));
            return new("run", "rest_site", new { hp = player.Creature.CurrentHp, max_hp = player.Creature.MaxHp, floor = run.TotalFloor }, actions);
        }
        if (run.CurrentRoom?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.Treasure)
        {
            NTreasureRoom? treasure = room.GetNodeOrNull<NTreasureRoom>("TreasureRoom");
            if (treasure is null) return null;
            List<LegalAction> actions = [];
            NClickableControl? chest = treasure.GetNodeOrNull<NClickableControl>("Chest");
            if (chest?.IsEnabled == true && chest.Visible) actions.Add(new("treasure:open", "open_treasure", "Open chest"));
            actions.AddRange(UiHelper.FindAll<NTreasureRoomRelicHolder>(treasure).Where(holder => holder.IsEnabled && holder.Visible).Select((_, index) => new LegalAction($"treasure:relic:{index}", "claim_treasure", "Take relic")));
            if (treasure.ProceedButton.IsEnabled) actions.Add(new("treasure:proceed", "proceed", "Proceed"));
            return new("run", "treasure", new { floor = run.TotalFloor }, actions);
        }
        if (run.CurrentRoom?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.Shop)
        {
            NMerchantRoom? shop = room.GetNodeOrNull<NMerchantRoom>("MerchantRoom");
            if (shop is null) return null;
            bool open = shop.Inventory.IsOpen;
            List<NMerchantSlot> stocked = open ? shop.Inventory.GetAllSlots().Where(slot => slot is not NMerchantCardRemoval && slot.Entry.IsStocked).ToList() : [];
            List<MerchantBinding> bindings = MerchantBindings(stocked.Where(slot => slot.Entry.EnoughGold));
            List<LegalAction> actions = bindings.Select(binding => binding.Action).ToList();
            actions.Add(open ? new("shop:close", "close_shop", "Close merchant inventory") : new("shop:open", "open_shop", "Open merchant inventory"));
            if (!open && shop.ProceedButton.IsEnabled) actions.Add(new("shop:proceed", "proceed", "Leave shop"));
            return new("run", "shop", new { gold = player.Gold, hp = player.Creature.CurrentHp, max_hp = player.Creature.MaxHp, items = stocked.Select(slot => new { id = MerchantId(slot.Entry), kind = slot.Entry.GetType().Name, title = MerchantTitle(slot.Entry), cost = slot.Entry.Cost, affordable = slot.Entry.EnoughGold }).ToList() }, actions);
        }
        return null;
    }

    private Draft BuildOverlay(Node overlay, RunState run)
    {
        List<NCardHolder> holders = UiHelper.FindAll<NCardHolder>(overlay).Where(holder => holder.IsVisibleInTree() && holder.CardModel is not null).ToList();
        if (holders.Count > 0)
        {
            List<(CardModel Card, LegalAction Action)> cards = StableCards(holders.Select(holder => holder.CardModel!), "choice");
            List<LegalAction> actions = cards.Select(card => card.Action).ToList();
            if (UiHelper.FindAll<NConfirmButton>(overlay).Any(button => button.IsEnabled)) actions.Add(new("choice:confirm", "confirm", "Confirm selection"));
            if (UiHelper.FindAll<NChoiceSelectionSkipButton>(overlay).Any(button => button.IsEnabled)) actions.Add(new("choice:skip", "skip", "Skip"));
            return new("run", overlay.GetType().Name, new { floor = run.TotalFloor, cards = cards.Select(card => Card(card.Card, card.Action.ActionId, card.Card.Pile?.Type ?? PileType.None)).ToList() }, actions);
        }
        List<RewardBinding> rewards = RewardBindings(overlay);
        if (rewards.Count > 0) return new("run", "rewards", new { floor = run.TotalFloor, screen = overlay.GetType().Name }, rewards.Select(reward => reward.Action).ToList());
        if (UiHelper.FindAll<NProceedButton>(overlay).Any(button => button.IsEnabled)) return new("run", "proceed", new { floor = run.TotalFloor, screen = overlay.GetType().Name }, [new("proceed", "proceed", "Proceed")]);
        return BaseRun(run, overlay.GetType().Name);
    }

    private BridgeState Materialize(Draft draft)
    {
        string json = JsonSerializer.Serialize(draft, BridgeJson.Options);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        if (fingerprint != _fingerprint) { _fingerprint = fingerprint; _version++; }
        JsonElement observation = JsonSerializer.SerializeToElement(draft.Observation, BridgeJson.Options);
        return new(_version, draft.Mode, draft.Screen, false, observation, draft.Actions, DateTimeOffset.UtcNow);
    }

    private async Task ExecutePendingAsync(PendingAction pending)
    {
        if (!NGame.IsMainThread()) throw new InvalidOperationException("Action execution left the Godot main thread.");
        Draft liveDraft = BuildDraft();
        BridgeState live = Materialize(liveDraft);
        if (_published is null || pending.StateVersion != live.StateVersion || live.LegalActions.Count(action => action.ActionId == pending.ActionId) != 1)
        {
            await ReportResultAsync(pending, "rejected_stale_or_illegal");
            return;
        }
        LegalAction action = live.LegalActions.Single(candidate => candidate.ActionId == pending.ActionId);
        try
        {
            await ExecuteAsync(live.Screen, action);
            await ReportResultAsync(pending, "executed");
        }
        catch (Exception exception)
        {
            GD.PrintErr($"[Sts2McpBridge] action failed: {exception.GetType().Name}: {exception.Message}");
            await ReportResultAsync(pending, "failed");
        }
    }

    private async Task ExecuteAsync(string screen, LegalAction action)
    {
        if (screen is "main_menu" or "singleplayer_mode" or "character_select") { await ExecuteMainMenuAsync(screen, action); return; }
        if (screen == "combat") { await ExecuteCombatAsync(action); return; }
        NRun? run = NGame.Instance?.CurrentRunNode;
        RunState? state = RunManager.Instance?.DebugOnlyGetState();
        if (run is null || state is null) return;
        if (screen == "map")
        {
            NMapPoint? point = EnabledMapPoints(run).SingleOrDefault(item => action.ActionId == $"map:r{item.Point.coord.row}:c{item.Point.coord.col}");
            if (point is not null) await UiHelper.Click(point);
            return;
        }
        if (NOverlayStack.Instance?.Peek() is Node overlay) { await ExecuteOverlayAsync(overlay, action); return; }
        Node room = run.GetNode("RoomContainer");
        int index = LastIndex(action.ActionId);
        if (screen == "event")
        {
            List<NEventOptionButton> options = UiHelper.FindAll<NEventOptionButton>(room).Where(option => option.IsEnabled && !option.Option.IsLocked).ToList();
            if (index >= 0 && index < options.Count) await UiHelper.Click(options[index]);
        }
        else if (screen == "rest_site")
        {
            NRestSiteRoom? rest = room.GetNodeOrNull<NRestSiteRoom>("RestSiteRoom");
            if (action.Kind == "proceed" && rest?.ProceedButton.IsEnabled == true) await UiHelper.Click(rest.ProceedButton);
            else { List<NRestSiteButton> options = UiHelper.FindAll<NRestSiteButton>(room).Where(button => button.Option.IsEnabled).ToList(); if (index >= 0 && index < options.Count) await UiHelper.Click(options[index]); }
        }
        else if (screen == "treasure")
        {
            NTreasureRoom? treasure = room.GetNodeOrNull<NTreasureRoom>("TreasureRoom");
            if (treasure is null) return;
            if (action.Kind == "open_treasure") { NClickableControl? chest = treasure.GetNodeOrNull<NClickableControl>("Chest"); if (chest?.IsEnabled == true) await UiHelper.Click(chest); }
            else if (action.Kind == "claim_treasure") { List<NTreasureRoomRelicHolder> holders = UiHelper.FindAll<NTreasureRoomRelicHolder>(treasure).Where(holder => holder.IsEnabled && holder.Visible).ToList(); if (index >= 0 && index < holders.Count) await UiHelper.Click(holders[index]); }
            else if (treasure.ProceedButton.IsEnabled) await UiHelper.Click(treasure.ProceedButton);
        }
        else if (screen == "shop") await ExecuteShopAsync(room, action);
    }

    private async Task ExecuteMainMenuAsync(string screen, LegalAction action)
    {
        NMainMenu? mainMenu = NGame.Instance?.MainMenu;
        if (mainMenu is null) return;
        if (screen == "main_menu")
        {
            string path = action.Kind == "continue_run" ? "MainMenuTextButtons/ContinueButton" : "MainMenuTextButtons/SingleplayerButton";
            NButton? button = mainMenu.GetNodeOrNull<NButton>(path);
            if (button?.IsEnabled == true && button.Visible) await UiHelper.Click(button);
            return;
        }
        if (screen == "singleplayer_mode")
        {
            NButton? standard = mainMenu.GetNodeOrNull<NButton>("Submenus/SingleplayerSubmenu/StandardButton");
            if (standard?.IsEnabled == true && standard.IsVisibleInTree()) await UiHelper.Click(standard);
            return;
        }
        if (screen == "character_select" && action.ActionId.StartsWith("main_menu:start:", StringComparison.Ordinal))
        {
            string characterId = action.ActionId["main_menu:start:".Length..];
            Control? characterSelect = mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen");
            NCharacterSelectButton? character = characterSelect is null ? null : UiHelper.FindAll<NCharacterSelectButton>(characterSelect)
                .SingleOrDefault(button => !button.IsLocked && !button.IsRandom && button.Character.Id.Entry == characterId);
            if (character is null) return;
            character.Select();
            for (int frame = 0; frame < 5; frame++) await FrameAsync();
            NButton? confirm = mainMenu.GetNodeOrNull<NButton>("Submenus/CharacterSelectScreen/ConfirmButton");
            if (confirm?.IsEnabled == true) await UiHelper.Click(confirm);
        }
    }

    private async Task ExecuteCombatAsync(LegalAction action)
    {
        RunState? run = RunManager.Instance?.DebugOnlyGetState();
        if (run is null || run.Players.Count != 1) return;
        Player? player = LocalContext.GetMe(run);
        ICombatState? combat = player?.Creature.CombatState;
        if (player?.PlayerCombatState?.Phase != PlayerTurnPhase.Play || combat is null) return;
        string token = CombatToken(player, combat);
        if (!action.ActionId.StartsWith($"combat:{token}:", StringComparison.Ordinal)) return;
        if (action.Kind == "end_turn") { PlayerCmd.EndTurn(player, false); return; }
        CombatBinding? binding = BuildCombatBindings(player, token).SingleOrDefault(candidate => candidate.Action.ActionId == action.ActionId);
        if (binding?.Card is CardModel card)
        {
            using IDisposable selector = CardSelectCmd.PushSelector(new McpCardSelector(this));
            if (!card.TryManualPlay(binding.Target)) return;
            ulong deadline = Time.GetTicksMsec() + 30_000;
            while (Time.GetTicksMsec() < deadline && card.Pile?.Type is PileType.Hand or PileType.Play) await FrameAsync();
        }
        else if (binding?.Potion is PotionModel potion && CanUsePotion(player, potion) && potion.IsValidTarget(binding.Target)) potion.EnqueueManualUse(binding.Target);
    }

    private async Task ExecuteShopAsync(Node room, LegalAction action)
    {
        NMerchantRoom? shop = room.GetNodeOrNull<NMerchantRoom>("MerchantRoom");
        if (shop is null) return;
        if (action.Kind == "buy")
        {
            MerchantBinding? binding = MerchantBindings(shop.Inventory.GetAllSlots().Where(slot => slot is not NMerchantCardRemoval && slot.Entry.IsStocked && slot.Entry.EnoughGold)).SingleOrDefault(item => item.Action.ActionId == action.ActionId);
            if (binding is not null) await binding.Slot.Entry.OnTryPurchaseWrapper(shop.Inventory.Inventory);
        }
        else if (action.Kind == "open_shop" && !shop.Inventory.IsOpen) shop.OpenInventory();
        else if (action.Kind == "close_shop")
        {
            NBackButton? back = shop.Inventory.GetNodeOrNull<NBackButton>("%BackButton");
            if (back?.IsEnabled == true) back.ForceClick(); else shop.Inventory.CallDeferred(NMerchantInventory.MethodName.Close);
        }
        else if (action.Kind == "proceed" && shop.ProceedButton.IsEnabled) await UiHelper.Click(shop.ProceedButton);
    }

    private async Task ExecuteOverlayAsync(Node overlay, LegalAction action)
    {
        if (action.Kind == "skip") UiHelper.FindAll<NChoiceSelectionSkipButton>(overlay).FirstOrDefault(button => button.IsEnabled)?.ForceClick();
        else if (action.Kind == "confirm") UiHelper.FindAll<NConfirmButton>(overlay).FirstOrDefault(button => button.IsEnabled)?.ForceClick();
        else if (action.Kind == "choose_card")
        {
            List<NCardHolder> holders = UiHelper.FindAll<NCardHolder>(overlay).Where(holder => holder.IsVisibleInTree() && holder.CardModel is not null).ToList();
            List<(CardModel Card, LegalAction Action)> cards = StableCards(holders.Select(holder => holder.CardModel!), "choice");
            int index = cards.FindIndex(card => card.Action.ActionId == action.ActionId);
            if (index >= 0) holders[index].EmitSignal(NCardHolder.SignalName.Pressed, holders[index]);
        }
        else if (action.Kind == "claim_reward") RewardBindings(overlay).SingleOrDefault(binding => binding.Action.ActionId == action.ActionId)?.Button.ForceClick();
        else if (action.Kind == "proceed") UiHelper.FindAll<NProceedButton>(overlay).FirstOrDefault(button => button.IsEnabled)?.ForceClick();
        await FrameAsync();
    }

    private sealed class McpCardSelector(BridgeController owner) : MegaCrit.Sts2.Core.TestSupport.ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect) => owner.SelectNestedCardsAsync(options, minSelect, maxSelect);
        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives) => default;
    }

    private async Task<IEnumerable<CardModel>> SelectNestedCardsAsync(IEnumerable<CardModel> source, int minSelect, int maxSelect)
    {
        List<CardModel> remaining = source.ToList();
        List<CardModel> selected = [];
        while (remaining.Count > 0 && selected.Count < Math.Min(maxSelect, remaining.Count + selected.Count))
        {
            List<(CardModel Card, LegalAction Action)> choices = StableCards(remaining, "nested");
            List<LegalAction> actions = choices.Select(choice => choice.Action).ToList();
            if (selected.Count >= minSelect) actions.Add(new("nested:done", "confirm", "Finish selection"));
            Draft draft = new("run", "nested_card_selection", new { min_select = minSelect, max_select = maxSelect, selected = selected.Select(card => Card(card, "selected", card.Pile?.Type ?? PileType.None)).ToList(), options = choices.Select(choice => Card(choice.Card, choice.Action.ActionId, choice.Card.Pile?.Type ?? PileType.None)).ToList() }, actions);
            BridgeState state = Materialize(draft);
            _published = state;
            PendingAction? pending = null;
            while (pending is null && !_lifetime.IsCancellationRequested)
            {
                try { await RegisterAsync(state); pending = await PollPendingAsync(state.StateVersion); }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException) { }
                await FrameAsync();
                if (BuildDraft().Screen != "combat") break;
            }
            if (pending is null) continue;
            if (pending.StateVersion != state.StateVersion || actions.All(action => action.ActionId != pending.ActionId)) { await ReportResultAsync(pending, "rejected_stale_or_illegal"); continue; }
            if (pending.ActionId == "nested:done") { await ReportResultAsync(pending, "executed"); break; }
            (CardModel Card, LegalAction Action) selectedChoice = choices.Single(choice => choice.Action.ActionId == pending.ActionId);
            selected.Add(selectedChoice.Card);
            remaining.Remove(selectedChoice.Card);
            await ReportResultAsync(pending, "executed");
        }
        return selected;
    }

    private async Task RegisterAsync(BridgeState state)
    {
        using HttpResponseMessage response = await _http.PutAsJsonAsync("v1/register", new RegisterRequest(_config.Token, state), BridgeJson.Options, _lifetime.Token);
        response.EnsureSuccessStatusCode();
    }

    private async Task<PendingAction?> PollPendingAsync(long version)
    {
        using HttpResponseMessage response = await _http.GetAsync($"v1/action/pending?state_version={version}", _lifetime.Token);
        response.EnsureSuccessStatusCode();
        PendingEnvelope? envelope = await response.Content.ReadFromJsonAsync<PendingEnvelope>(BridgeJson.Options, _lifetime.Token);
        return envelope?.Action;
    }

    private async Task ReportResultAsync(PendingAction action, string status)
    {
        try
        {
            using HttpResponseMessage response = await _http.PostAsJsonAsync("v1/action/result", new { state_version = action.StateVersion, action_id = action.ActionId, status }, BridgeJson.Options, _lifetime.Token);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { }
    }

    private sealed record PendingEnvelope(PendingAction? Action);

    private static List<NMapPoint> EnabledMapPoints(NRun run) => UiHelper.FindAll<NMapPoint>(run.GlobalUi.MapScreen).Where(point => point.IsEnabled && point.IsVisibleInTree()).OrderBy(point => point.Point.coord.row).ThenBy(point => point.Point.coord.col).ToList();
    private static bool CanUsePotion(Player player, PotionModel potion) => !potion.IsQueued && !potion.HasBeenRemovedFromState && player.Creature.IsAlive && player.CanRemovePotions && potion.PassesCustomUsabilityCheck && potion.Usage is PotionUsage.CombatOnly or PotionUsage.AnyTime;
    private static string CombatToken(Player player, ICombatState combat)
    {
        PlayerCombatState state = player.PlayerCombatState!;
        string source = $"turn={state.TurnNumber};round={combat.RoundNumber};phase={state.Phase};energy={state.Energy};hp={player.Creature.CurrentHp};block={player.Creature.Block};hand={string.Join(',', state.Hand.Cards.Select(card => $"{card.Id.Entry}+{card.CurrentUpgradeLevel}"))};enemies={string.Join(',', combat.Creatures.Select(creature => $"{creature.CombatId}:{creature.CurrentHp}:{creature.Block}:{creature.IsAlive}"))}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
    }

    private static object Card(CardModel card, string key, PileType pile) => new { key, id = card.Id.Entry, title = card.Title, type = card.Type.ToString(), rarity = card.Rarity.ToString(), cost = card.EnergyCost.CostsX ? "X" : card.EnergyCost.GetWithModifiers(CostModifiers.All).ToString(), upgraded = card.IsUpgraded, description = SafeText(() => card.GetDescriptionForPile(pile)), playable = pile == PileType.Hand ? card.CanPlay(out _, out _) : null as bool? };
    private static object Pile(CardPile pile, bool orderKnown) => new { count = pile.Cards.Count, order_known = orderKnown, cards = pile.Cards.OrderBy(card => card.Id.Entry).ThenBy(card => card.CurrentUpgradeLevel).Select((card, index) => Card(card, $"{pile.Type.ToString().ToLowerInvariant()}{index}", pile.Type)).ToList() };
    private static object Powers(Creature creature) => creature.Powers.Where(power => power.IsVisible).Select(power => new { id = power.Id.Entry, title = SafeText(() => power.Title.GetFormattedText()), description = SafeText(() => PowerDescription(power)), amount = power.DisplayAmount, type = power.TypeForCurrentAmount.ToString() }).ToList();
    private static string PowerDescription(PowerModel power) { LocString text = power.HasSmartDescription ? power.SmartDescription : power.Description; text.Add("Amount", power.Amount); text.Add("OnPlayer", power.Owner.IsPlayer); text.Add("IsMultiplayer", false); text.Add("PlayerCount", 1); power.DynamicVars.AddTo(text); return text.GetFormattedText(); }
    private static object? Forecast(Creature enemy, ICombatState combat)
    {
        if (enemy.Monster?.NextMove is not { } move) return null;
        List<AbstractIntent> intents = move.Intents.ToList();
        List<AttackIntent> attacks = intents.OfType<AttackIntent>().ToList();
        return new { move_id = move.StateId, attack = new { present = attacks.Count > 0, damage_per_hit = attacks.Count == 1 ? SafeInt(() => attacks[0].GetSingleDamage(combat.Allies, enemy)) : null, hits = attacks.Count == 1 ? Math.Max(1, attacks[0].Repeats) : null as int?, total = attacks.Count == 0 ? null : (int?)attacks.Sum(attack => SafeInt(() => attack.GetTotalDamage(combat.Allies, enemy)) ?? 0) }, status_card_count = intents.OfType<StatusIntent>().Sum(intent => intent.CardCount), defend = intents.Any(intent => intent is DefendIntent), heal = intents.Any(intent => intent is HealIntent), buff = intents.Any(intent => intent is BuffIntent), debuff = intents.Any(intent => intent is DebuffIntent or CardDebuffIntent), summon = intents.Any(intent => intent is SummonIntent), hidden = intents.Any(intent => intent is HiddenIntent or UnknownIntent) };
    }

    private static List<(CardModel Card, LegalAction Action)> StableCards(IEnumerable<CardModel> cards, string prefix)
    {
        Dictionary<string, int> occurrences = [];
        return cards.Select(card => { int occurrence = occurrences.GetValueOrDefault(card.Id.Entry); occurrences[card.Id.Entry] = occurrence + 1; return (card, new LegalAction($"{prefix}:{card.Id.Entry}:{occurrence}", "choose_card", $"Choose {card.Title}")); }).ToList();
    }

    private static List<RewardBinding> RewardBindings(Node overlay)
    {
        Dictionary<string, int> occurrences = [];
        return UiHelper.FindAll<NRewardButton>(overlay).Where(button => button.IsEnabled && button.IsVisibleInTree()).Select(button => { string type = button.Reward?.GetType().Name ?? "UnknownReward"; int occurrence = occurrences.GetValueOrDefault(type); occurrences[type] = occurrence + 1; return new RewardBinding(new($"reward:{type}:{occurrence}", "claim_reward", $"Claim {type}"), button); }).ToList();
    }

    private static List<MerchantBinding> MerchantBindings(IEnumerable<NMerchantSlot> slots)
    {
        Dictionary<string, int> occurrences = [];
        return slots.Select(slot => { string key = $"{slot.Entry.GetType().Name}:{MerchantId(slot.Entry)}:{slot.Entry.Cost}"; int occurrence = occurrences.GetValueOrDefault(key); occurrences[key] = occurrence + 1; return new MerchantBinding(new($"buy:{key}:{occurrence}", "buy", $"Buy {MerchantTitle(slot.Entry)} for {slot.Entry.Cost} gold"), slot); }).ToList();
    }

    private static string MerchantId(MerchantEntry entry) => entry switch { MerchantCardEntry card => card.CreationResult?.Card.Id.Entry ?? "sold", MerchantRelicEntry relic => relic.Model?.Id.Entry ?? "sold", MerchantPotionEntry potion => potion.Model?.Id.Entry ?? "sold", _ => entry.GetType().Name };
    private static string MerchantTitle(MerchantEntry entry) => entry switch { MerchantCardEntry card => card.CreationResult?.Card.Title ?? "Sold card", MerchantRelicEntry relic => SafeText(() => relic.Model?.Title.GetFormattedText() ?? "Sold relic") ?? "Relic", MerchantPotionEntry potion => SafeText(() => potion.Model?.Title.GetFormattedText() ?? "Sold potion") ?? "Potion", _ => entry.GetType().Name };
    private static string? SafeText(Func<string> value) { try { return value(); } catch (Exception) { return null; } }
    private static int? SafeInt(Func<int> value) { try { return value(); } catch (Exception) { return null; } }
    private static int LastIndex(string id) => int.TryParse(id[(id.LastIndexOf(':') + 1)..], out int value) ? value : -1;
    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri : new(uri.AbsoluteUri + "/");
    private async Task FrameAsync() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    private async Task WaitFramesAsync(int count) { for (int index = 0; index < count; index++) await FrameAsync(); }
}
