using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace ExpandedTelemetry;

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterRoomEntered))]
public static class AfterRoomEnteredPatch
{
    public static void Postfix(IRunState? runState, AbstractRoom room)
    {
        if (runState == null) return;
        TelemetryStreamWriter.Open();
        ulong player = runState.Players.Count > 0 ? runState.Players[0].NetId : 0;
        Log.Info($"[expanded-telemetry] Room entered: {room.RoomType} floor {runState.TotalFloor}");
        TelemetryStreamWriter.WriteRoomEntered(room.RoomType.ToString(), runState.TotalFloor, runState.CurrentActIndex + 1, player);

        // Emit shop_offered immediately after room_entered to guarantee ordering.
        // MerchantInventory is fully populated by the time AfterRoomEntered fires.
        // The Aug 2026 update made shops per-player (MerchantRoom.Inventories); we report
        // the local player's shop via GetLocalInventory(). CardEntries still exists as a
        // convenience over the new CharacterCardEntries + ColorlessCardEntries split.
        if (room is MerchantRoom merchantRoom)
        {
            var inventory = merchantRoom.GetLocalInventory();
            var items = new List<ShopItemSummary>();
            foreach (var card in inventory.CardEntries)
                if (card.CreationResult?.Card.Id.Entry is string cardId)
                    items.Add(new ShopItemSummary("card", cardId, card.Cost));
            foreach (var relic in inventory.RelicEntries)
                if (relic.Model?.Id.Entry is string relicId)
                    items.Add(new ShopItemSummary("relic", relicId, relic.Cost));
            foreach (var potion in inventory.PotionEntries)
                if (potion.Model?.Id.Entry is string potionId)
                    items.Add(new ShopItemSummary("potion", potionId, potion.Cost));
            if (inventory.CardRemovalEntry != null)
                items.Add(new ShopItemSummary("card_removal", null, inventory.CardRemovalEntry.Cost));
            if (items.Count > 0)
            {
                Log.Info($"[expanded-telemetry] Shop offered: {items.Count} items at floor {runState.TotalFloor}");
                TelemetryStreamWriter.WriteShopOffered(items, runState.TotalFloor, player);
            }
        }
    }
}

// RestSiteOption.OnSelect() is abstract — cannot patch the base directly.
// TargetMethods() discovers all concrete subclasses at patch time instead.
// Owner is protected — accessed via cached reflection.
[HarmonyPatch]
public static class RestSiteChoicePatch
{
    private static readonly PropertyInfo _ownerProp =
        typeof(RestSiteOption).GetProperty("Owner", BindingFlags.NonPublic | BindingFlags.Instance)!;

    public static IEnumerable<MethodBase> TargetMethods()
        => typeof(RestSiteOption).Assembly
            .GetTypes()
            .Where(t => t.IsSubclassOf(typeof(RestSiteOption)) && !t.IsAbstract)
            .Select(t => (MethodBase?)t.GetMethod(nameof(RestSiteOption.OnSelect)))
            .Where(m => m != null)
            .Cast<MethodBase>();

    public static void Prefix(RestSiteOption __instance)
    {
        if (_ownerProp.GetValue(__instance) is not Player owner) return;
        Log.Info($"[expanded-telemetry] Rest site choice: {__instance.OptionId}");
        TelemetryStreamWriter.WriteRestSiteChoice(__instance.OptionId, owner.RunState.TotalFloor, owner.NetId);
    }
}

// ChooseOptionForEvent is private — patch by name string with explicit parameter types.
[HarmonyPatch(typeof(EventSynchronizer), "ChooseOptionForEvent", new[] { typeof(Player), typeof(int) })]
public static class EventChoicePatch
{
    public static void Prefix(EventSynchronizer __instance, Player player, int optionIndex)
    {
        try
        {
            EventModel eventModel = __instance.GetEventForPlayer(player);
            if (eventModel == null) return;
            if (optionIndex < 0 || optionIndex >= eventModel.CurrentOptions.Count) return;
            EventOption option = eventModel.CurrentOptions[optionIndex];
            Log.Info($"[expanded-telemetry] Event choice: {eventModel.Id.Entry} -> {option.TextKey}");
            TelemetryStreamWriter.WriteEventChoice(eventModel.Id.Entry, option.TextKey, player.RunState.TotalFloor, player.NetId);
        }
        catch (Exception ex)
        {
            Log.Error("[expanded-telemetry] EventChoicePatch failed: " + ex.Message);
        }
    }
}

// Shop purchase uses two patches because MerchantEntry.ClearAfterPurchase() nulls the
// item model before Hook.AfterItemPurchased fires. Capture the item ID in a Prefix first.
[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.OnTryPurchaseWrapper))]
public static class ShopPurchaseCapturePatch
{
    internal static readonly ConditionalWeakTable<MerchantEntry, string> Pending = new();

    public static void Prefix(MerchantEntry __instance)
    {
        string? captured = __instance switch
        {
            MerchantCardEntry card   => card.CreationResult?.Card.Id.Entry is string cId   ? $"card:{cId}"   : null,
            MerchantRelicEntry relic => relic.Model?.Id.Entry                is string rId   ? $"relic:{rId}"  : null,
            MerchantPotionEntry pot  => pot.Model?.Id.Entry                  is string pId   ? $"potion:{pId}" : null,
            _                        => null
        };
        if (captured != null)
            Pending.AddOrUpdate(__instance, captured);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterItemPurchased))]
public static class ShopPurchasePatch
{
    public static void Postfix(IRunState runState, Player player, MerchantEntry itemPurchased, int goldSpent)
    {
        TelemetryStreamWriter.Open();
        string itemType;
        string? itemId = null;

        if (itemPurchased is MerchantCardRemovalEntry)
        {
            itemType = "card_removal";
        }
        else if (ShopPurchaseCapturePatch.Pending.TryGetValue(itemPurchased, out string? captured) && captured != null)
        {
            ShopPurchaseCapturePatch.Pending.Remove(itemPurchased);
            int sep = captured.IndexOf(':');
            itemType = sep > 0 ? captured[..sep] : captured;
            itemId   = sep > 0 ? captured[(sep + 1)..] : null;
        }
        else
        {
            return;
        }

        Log.Info($"[expanded-telemetry] Shop purchase: {itemType} {itemId} for {goldSpent}g");
        TelemetryStreamWriter.WriteShopPurchase(itemType, itemId, goldSpent, runState.TotalFloor, player.NetId);
    }
}

// RelicReward._relic is private — access via cached reflection for rewards_offered.
// CardReward card list is captured here for diffing in RewardTakenPatch.
//
// Hook.BeforeRewardsOffered was removed in the Aug 2026 game update. We now patch
// Hook.ModifyRewards — a synchronous hook fired from RewardsSet.GenerateWithoutOffering()
// AFTER the reward list is built and modifiers have mutated it in place, so the Postfix
// sees the final list. Caveat: GenerateWithoutOffering() also runs on the non-offering
// paths (RewardsCmd.GenerateForRoomEnd / GenerateCustom), so in principle this can fire
// for rewards that are never shown. Normal combat/treasure rewards go through
// OfferForRoomEnd → Offer → GenerateWithoutOffering, firing exactly once per screen.
[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyRewards))]
public static class RewardsOfferedPatch
{
    private static readonly FieldInfo _relicField =
        typeof(RelicReward).GetField("_relic", BindingFlags.NonPublic | BindingFlags.Instance)!;

    internal static readonly ConditionalWeakTable<CardReward, List<string>> CardSnapshot = new();

    public static void Postfix(IRunState runState, Player player, List<Reward> rewards)
    {
        TelemetryStreamWriter.Open();
        var summaries = new List<RewardSummary>();
        foreach (Reward reward in rewards)
        {
            switch (reward)
            {
                case CardReward cardReward:
                    var cardIds = cardReward.Cards.Select(c => c.Id.Entry).ToList();
                    CardSnapshot.AddOrUpdate(cardReward, cardIds);
                    foreach (var id in cardIds)
                        summaries.Add(new RewardSummary("card", id));
                    break;
                case RelicReward relicReward:
                    var relic = _relicField.GetValue(relicReward) as RelicModel;
                    summaries.Add(new RewardSummary("relic", relic?.Id.Entry));
                    break;
                case PotionReward potionReward:
                    summaries.Add(new RewardSummary("potion", potionReward.Potion?.Id.Entry));
                    break;
                case GoldReward goldReward:
                    summaries.Add(new RewardSummary("gold", goldReward.Amount.ToString()));
                    break;
            }
        }
        if (summaries.Count > 0)
        {
            Log.Info($"[expanded-telemetry] Rewards offered: {summaries.Count} items at floor {runState.TotalFloor}");
            TelemetryStreamWriter.WriteRewardsOffered(summaries, runState.TotalFloor, player.NetId);
        }
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterRewardTaken))]
public static class RewardTakenPatch
{
    public static void Postfix(IRunState runState, Player player, Reward reward)
    {
        string rewardType;
        string? itemId = null;
        int? amount = null;

        switch (reward)
        {
            case CardReward cardReward:
                rewardType = "card";
                if (RewardsOfferedPatch.CardSnapshot.TryGetValue(cardReward, out var offeredIds))
                {
                    RewardsOfferedPatch.CardSnapshot.Remove(cardReward);
                    var remaining = cardReward.Cards.Select(c => c.Id.Entry).ToHashSet();
                    itemId = offeredIds.FirstOrDefault(id => !remaining.Contains(id));
                }
                break;
            case RelicReward relicReward:
                rewardType = "relic";
                itemId = relicReward.ClaimedRelic?.Id.Entry;
                break;
            case PotionReward potionReward:
                rewardType = "potion";
                itemId = potionReward.ClaimedPotion?.Id.Entry;
                break;
            case GoldReward goldReward:
                rewardType = "gold";
                amount = goldReward.Amount;
                break;
            default:
                return;
        }

        Log.Info($"[expanded-telemetry] Reward taken: {rewardType} {itemId ?? amount?.ToString()}");
        TelemetryStreamWriter.WriteRewardTaken(rewardType, itemId, amount, runState.TotalFloor, player.NetId);
    }
}
