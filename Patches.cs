﻿using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BattleTech;
using BattleTech.Save;
using BattleTech.Save.SaveGameStructure;
using BattleTech.Serialization.Models;
using BattleTech.UI;
using Harmony;
using UnityEngine;
using static PanicSystem.Controller;
using static PanicSystem.PanicSystem;
using static PanicSystem.Logger;
using Stopwatch = HBS.Stopwatch;

// ReSharper disable UnusedMember.Local

// HUGE thanks to RealityMachina and mpstark for their work, outstanding.
namespace PanicSystem
{
    public static class Patches
    {
        [HarmonyPatch(typeof(AttackStackSequence), "OnAttackComplete")]
        public static class AttackStackSequenceOnAttackCompletePatch
        {
            public static void Prefix(AttackStackSequence __instance, MessageCenterMessage message)
            {
                var stopwatch = new Stopwatch();
                if (SkipProcessingAttack(__instance, message))
                {
                    return;
                }

                var attackCompleteMessage = message as AttackCompleteMessage;
                Debug(new string(c: '-', count: 60));
                Debug($"{__instance.directorSequences[0].attacker.LogDisplayName}\n-> attacks ->\n" +
                      $"{__instance.directorSequences[0].target.LogDisplayName}");

                var targetMech = (Mech) __instance.directorSequences[0].target;
                if (!ShouldPanic(targetMech, attackCompleteMessage?.attackSequence))
                {
                    return;
                }

                // TODO test
                if (targetMech.IsFlaggedForKnockdown && targetMech.pilot.pilotDef.PilotTags.Contains("pilot_klutz"))
                {
                    if (Random.Range(1, 100) == 13)
                    {
                        targetMech.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage
                            (new ShowActorInfoSequence(targetMech, "WOOPS!", FloatieMessage.MessageNature.Debuff, false)));
                        Debug("Very klutzy!");
                        return;
                    }
                }

                stopwatch.Start();

                // panic saving throw
                if (SavedVsPanic(targetMech, attackCompleteMessage?.attackSequence))
                {
                    return;
                }

                // stop if pilot isn't panicked
                var index = GetTrackedPilotIndex(targetMech);
                if (TrackedPilots[index].PilotStatus != PanicStatus.Panicked)
                {
                    return;
                }

                // eject saving throw

                if (SavedVsEject(targetMech, attackCompleteMessage?.attackSequence))
                {
                    return;
                }

                Debug("Ejecting");
                if (ModSettings.EnableEjectPhrases)
                {
                    var ejectMessage = EjectPhraseList[Random.Range(0, EjectPhraseList.Count - 1)];
                    targetMech.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage
                        (new ShowActorInfoSequence(targetMech, ejectMessage, FloatieMessage.MessageNature.Debuff, true)));

                    // this is necessary to avoid vanilla hangs.  the list has nulls so the try/catch deals with silently.  thanks jo
                    //    var combat = Traverse.Create(__instance).Property("Combat").GetValue<CombatGameState>();
                    //    var effectsTargeting = combat.EffectManager.GetAllEffectsTargeting(mech);

                    foreach (var effect in __instance.Combat.EffectManager.GetAllEffectsTargeting(targetMech))
                    {
                        try
                        {
                            targetMech.CancelEffect(effect);
                        }
                        // ReSharper disable once EmptyGeneralCatchClause
                        catch // deliberately silent
                        {
                        }
                    }

                    targetMech.EjectPilot(targetMech.GUID, attackCompleteMessage.stackItemUID, DeathMethod.PilotEjection, false);
                    Debug("Ejected");
                    Debug($"Runtime to exit {stopwatch.ElapsedMilliSeconds}ms");
                }
            }

            [HarmonyPatch(typeof(AbstractActor), "OnNewRound")]
            public static class AbstractActorBeginNewRoundPatch
            {
                public static void Prefix(AbstractActor __instance)
                {
                    if (!(__instance is Mech mech) || mech.IsDead || mech.IsFlaggedForDeath && mech.HasHandledDeath)
                    {
                        return;
                    }

                    var pilot = mech.GetPilot();
                    if (pilot == null)
                    {
                        return;
                    }

                    var index = GetTrackedPilotIndex(mech);
                    if (index == -1)
                    {
                        TrackedPilots.Add(new PanicTracker(mech)); // add a new tracker to tracked pilot
                        SaveTrackedPilots(); // TODO ensure this isn't causing indexing errors by running overlaps or something
                        return;
                    }

                    // reduce panic level
                    var originalStatus = TrackedPilots[index].PilotStatus;
                    var stats = __instance.StatCollection;
                    if (!TrackedPilots[index].ChangedRecently && (int) TrackedPilots[index].PilotStatus > 0)
                    {
                        TrackedPilots[index].PilotStatus--;
                        TrackedPilots[index].ChangedRecently = false;
                    }
                    else if (TrackedPilots[index].PilotStatus != originalStatus) // status has changed, reset modifiers
                    {
                        stats.ModifyStat("Panic Turn Reset: Accuracy", -1, "AccuracyModifier", StatCollection.StatOperation.Set, 0f);
                        stats.ModifyStat("Panic Turn Reset: Mech To Hit", -1, "ToHitThisActor", StatCollection.StatOperation.Set, 0f);

                        if (TrackedPilots[index].PilotStatus == PanicStatus.Unsettled)
                        {
                            Debug("IMPROVED TO UNSETTLED!");
                            __instance.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage(new ShowActorInfoSequence(mech, "IMPROVED TO UNSETTLED", FloatieMessage.MessageNature.Buff, false)));
                            stats.ModifyStat("Panic Turn: Unsettled Aim", -1, "AccuracyModifier", StatCollection.StatOperation.Float_Add, ModSettings.UnsettledAttackModifier);
                        }
                        else if (TrackedPilots[index].PilotStatus == PanicStatus.Stressed)
                        {
                            Debug("IMPROVED TO STRESSED!");
                            stats.ModifyStat("Panic Turn: Stressed Aim", -1, "AccuracyModifier", StatCollection.StatOperation.Float_Add, ModSettings.StressedAimModifier);
                            stats.ModifyStat("Panic Turn: Stressed Defence", -1, "ToHitThisActor", StatCollection.StatOperation.Float_Add, ModSettings.StressedToHitModifier);
                        }
                        else
                        {
                            Debug("IMPROVED TO CONFIDENT!");
                            __instance.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage(new ShowActorInfoSequence(mech, "IMPROVED TO CONFIDENT", FloatieMessage.MessageNature.Buff, false)));
                        }

                        // TODO do we need to add ChangedRecently = true;?
                        SayStatusFloatie(mech, true);
                    }

                    SaveTrackedPilots();
                }
            }

            [HarmonyPatch(typeof(GameInstance), "LaunchContract", new[] {typeof(Contract), typeof(string)})]
            public static class BattleTech_GameInstance_LaunchContract_Patch
            {
                private static void Postfix(GameInstance __instance)
                {
                    // reset on new contracts
                    Reset();
                    Debug("Done reset");
                }
            }

            //[HarmonyPatch(typeof(LanceSpawnerGameLogic), nameof(LanceSpawnerGameLogic.OnTriggerSpawn))]
            //public static class LanceSpawnerGameLogicPatch
            //{
            //    public static void Postfix(LanceSpawnerGameLogic __instance)
            //    {
            //        foreach (var mech in __instance.Combat.AllMechs)
            //        {
            //            Debug($"Mech {mech.DisplayName} - {CheckTrackedPilots(mech)}");
            //            CheckTrackedPilots(mech);
            //        }
            //    }
            //}

            //  TODO this may not be necessary but I saw a game where 2 mechs were not tracked for unknown reasons
            // [HarmonyPatch(typeof(CombatGameState), "_Init")]
            // public static class CombatGameStatePatch
            // {
            //     public static void Postfix(CombatGameState __instance)
            //     {
            //         var combat = __instance;
            //         foreach (var mech in combat.AllMechs)
            //         {
            //             CheckTrackedPilots(mech);
            //         }
            //     }
            // }

            [HarmonyPatch(typeof(AAR_SalvageScreen), "OnCompleted")]
            public static class BattletechSalvageScreenPatch
            {
                private static void Postfix()
                {
                    Reset(); //don't keep data we don't need after a mission
                }
            }

            [HarmonyPatch(typeof(Mech), "OnLocationDestroyed")]
            public static class BattletechMechLocationDestroyedPatch
            {
                private static void Postfix(Mech __instance)
                {
                    var mech = __instance;
                    if (mech == null || mech.IsDead || mech.IsFlaggedForDeath && mech.HasHandledDeath)
                    {
                        return;
                    }

                    var index = GetTrackedPilotIndex(mech);
                    if (!ModSettings.LosingLimbAlwaysPanics)
                    {
                        return;
                    }

                    if (TrackedPilots[index].TrackedMech != mech.GUID)
                    {
                        return;
                    }

                    if (TrackedPilots[index].ChangedRecently && ModSettings.OneChangePerTurn)
                    {
                        return;
                    }

                    if (index < 0)
                    {
                        TrackedPilots.Add(new PanicTracker(mech)); //add a new tracker to tracked pilot, then we run it all over again;
                        index = GetTrackedPilotIndex(mech);
                        if (index < 0) // G  Why does this matter?
                        {
                            return;
                        }
                    }

                    ApplyPanicDebuff(mech);
                }
            }

            [HarmonyPatch(typeof(GameInstanceSave))]
            [HarmonyPatch(new[] {typeof(GameInstance), typeof(SaveReason)})]
            public static class GameInstanceSaveConstructorPatch
            {
                private static void Postfix(GameInstanceSave __instance)
                {
                    SerializeStorageJson(__instance.InstanceGUID, __instance.SaveTime);
                }
            }

            [HarmonyPatch(typeof(GameInstance), "Load")]
            public static class GameInstanceLoadPatch
            {
                private static void Prefix(GameInstanceSave save)
                {
                    Resync(save.SaveTime);
                }
            }

            [HarmonyPatch(typeof(SimGameState), "_OnFirstPlayInit")]
            public static class SimGameStateFirstPlayInitPatch
            {
                private static void Postfix(SimGameState __instance) //we're doing a new campaign, so we need to sync the json with the new addition
                {
                    SyncNewCampaign();
                }
            }
        }
    }
}