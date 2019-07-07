﻿using System;
using System.Collections.Generic;
using BattleTech;
using BattleTech.Save;
using BattleTech.Save.SaveGameStructure;
using BattleTech.UI;
using Harmony;
using HBS;
using Localize;
using static PanicSystem.Controller;
using static PanicSystem.PanicSystem;
using static PanicSystem.Logger;
using Random = UnityEngine.Random;

// ReSharper disable UnusedMember.Global
// ReSharper disable InconsistentNaming
// ReSharper disable UnusedMember.Local

// HUGE thanks to RealityMachina and mpstark for their work, outstanding.
namespace PanicSystem
{
    public static class Patches
    {
        public static float mechArmorBeforeAttack;
        public static float mechStructureBeforeAttack;
        public static float mechHeatBeforeAttack = 0;
        public static float heatDamage = 0;

        // have to patch both because they're used in different situations, with the same messages
        [HarmonyPatch(typeof(CombatHUDFloatieStack), "AddFloatie", typeof(FloatieMessage))]
        public static class CombatHUDFloatieStack_AddFloatie_Patch1
        {
            public static void Postfix(CombatHUDFloatieStack __instance, FloatieMessage message)
            {
                if (modSettings.ColorizeFloaties)
                    ColorFloaties.Colorize(__instance);
            }
        }

        [HarmonyPatch(typeof(CombatHUDFloatieStack), "AddFloatie", typeof(Text), typeof(FloatieMessage.MessageNature))]
        public static class CombatHUDFloatieStack_AddFloatie_Patch2
        {
            public static void Postfix(CombatHUDFloatieStack __instance, Text text)
            {
                if (modSettings.ColorizeFloaties)
                    ColorFloaties.Colorize(__instance);
            }
        }

        [HarmonyPatch(typeof(AAR_SalvageScreen), "OnCompleted")]
        public static class AAR_SalvageScreenPatch
        {
            private static void Postfix() => Reset();
        }

        [HarmonyPatch(typeof(AbstractActor), "OnNewRound")]
        public static class AbstractActorBeginNewRoundPatch
        {
            public static void Prefix(AbstractActor __instance)
            {
                if (!(__instance is Mech mech) || mech.IsDead || mech.IsFlaggedForDeath && mech.HasHandledDeath) return;

                var pilot = mech.GetPilot();
                if (pilot == null) return;

                var index = GetPilotIndex(mech);
                // reduce panic level
                var originalStatus = trackedPilots[index].panicStatus;

                if (!trackedPilots[index].panicWorsenedRecently && (int) trackedPilots[index].panicStatus > 0)
                {
                    trackedPilots[index].panicStatus--;
                }

                if (trackedPilots[index].panicStatus != originalStatus) // status has changed, reset modifiers
                {
                    int Uid() => Random.Range(1, int.MaxValue);
                    var effectManager = UnityGameInstance.BattleTechGame.Combat.EffectManager;

                    // remove all PanicSystem effects first
                    var effects = Traverse.Create(effectManager).Field("effects").GetValue<List<Effect>>();
                    for (var i = 0; i < effects.Count; i++)
                    {
                        if (effects[i].id.StartsWith("PanicSystem") && Traverse.Create(effects[i]).Field("target").GetValue<object>() == mech)
                        {
                            effectManager.CancelEffect(effects[i]);
                        }
                    }

                    // re-apply effects
                    var message = __instance.Combat.MessageCenter;
                    switch (trackedPilots[index].panicStatus)
                    {
                        case PanicStatus.Unsettled:
                            LogDebug($"{mech.DisplayName} condition improved: Unsettled");
                            message.PublishMessage(new AddSequenceToStackMessage(new ShowActorInfoSequence(mech, "IMPROVED TO UNSETTLED!", FloatieMessage.MessageNature.Buff, false)));
                            effectManager.CreateEffect(StatusEffect.UnsettledToHit, "PanicSystemToHit", Uid(), mech, mech, new WeaponHitInfo(), 0);
                            break;
                        case PanicStatus.Stressed:
                            LogDebug($"{mech.DisplayName} condition improved: Stressed");
                            message.PublishMessage(new AddSequenceToStackMessage(new ShowActorInfoSequence(mech, "IMPROVED TO STRESSED!", FloatieMessage.MessageNature.Buff, false)));
                            effectManager.CreateEffect(StatusEffect.StressedToHit, "PanicSystemToHit", Uid(), mech, mech, new WeaponHitInfo(), 0);
                            effectManager.CreateEffect(StatusEffect.StressedToBeHit, "PanicSystemToBeHit", Uid(), mech, mech, new WeaponHitInfo(), 0);
                            break;
                        default:
                            LogDebug($"{mech.DisplayName} condition improved: Confident");
                            message.PublishMessage(new AddSequenceToStackMessage(new ShowActorInfoSequence(mech, "IMPROVED TO CONFIDENT!", FloatieMessage.MessageNature.Buff, false)));
                            break;
                    }
                }

                // reset flag after reduction effect
                trackedPilots[index].panicWorsenedRecently = false;
                SaveTrackedPilots();
            }
        }

        // save the pre-attack condition
        [HarmonyPatch(typeof(AttackStackSequence), "OnAttackBegin")]
        public static class OnAttackBeginPatch
        {
            public static void Prefix(AttackStackSequence __instance)
            {
                var target = __instance.directorSequences[0].chosenTarget;
                mechArmorBeforeAttack = target.SummaryArmorCurrent;
                mechStructureBeforeAttack = target.SummaryStructureCurrent;

                // get defender's current heat
                if (__instance.directorSequences[0].chosenTarget is Mech defender)
                {
                    LogDebug($"Defender pre-attack heat {defender.CurrentHeat}");
                    mechHeatBeforeAttack = defender.CurrentHeat;
                }
            }
        }

        public static void ManualPatching()
        {
            //var targetMethod = AccessTools.Method(typeof(Mech), "CheckForHeatDamage");
            //var transpiler = SymbolExtensions.GetMethodInfo(() => Mech_CheckForHeatDamage_Patch.Transpiler(null));
            //var postfix = SymbolExtensions.GetMethodInfo(() => Mech_CheckForHeatDamage_Patch.Postfix());
            //harmony.Patch(targetMethod, null, new HarmonyMethod(postfix), new HarmonyMethod(transpiler));
        }

        // patch works to determine how much heat damage was done by overheating... which isn't really required
        // multiply a local variable by 7 and aggregate it on global `static float heatDamage`
        //public class Mech_CheckForHeatDamage_Patch
        //{
        //    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        //    {
        //        var codes = instructions.ToList();
        //        var heatDamageField = AccessTools.Field(typeof(Patches), nameof(heatDamage));
        //        var log = SymbolExtensions.GetMethodInfo(() => LogDebug(null));
        //
        //        var newStack = new List<CodeInstruction>
        //        {
        //            new CodeInstruction(OpCodes.Ldloc_0), // push float (2)
        //            new CodeInstruction(OpCodes.Ldc_R4, 7f), // push float (7)
        //            new CodeInstruction(OpCodes.Mul), // multiply   (14)
        //            new CodeInstruction(OpCodes.Ldsfld, heatDamageField), // push float (0)
        //            new CodeInstruction(OpCodes.Add), // add        (14)
        //            new CodeInstruction(OpCodes.Stsfld, heatDamageField), // store result
        //        };
        //
        //        codes.InsertRange(codes.Count - 1, newStack);
        //        return codes.AsEnumerable();
        //    }
        //
        //    public static void Postfix() => LogDebug($"heatDamage: {heatDamage}");
        //}
        //
        
        // properly aggregates heat damage?
        [HarmonyPatch(typeof(Mech), "AddExternalHeat")]
        public class fsdwert
        {
            static void Prefix(Mech __instance, int amt)
            {
                heatDamage += amt;
                LogDebug($"Running heat total: {heatDamage}");
            }
        }
        
        [HarmonyPatch(typeof(AttackStackSequence), "OnAttackComplete")]
        public static class AttackStackSequenceOnAttackCompletePatch
        {
            public static void Prefix(AttackStackSequence __instance, MessageCenterMessage message)
            {
                var stopwatch = new Stopwatch();
                stopwatch.Start();

                if (SkipProcessingAttack(__instance, message)) return;

                if (!(message is AttackCompleteMessage attackCompleteMessage)) return;

                var director = __instance.directorSequences;
                if (director == null) return;

                LogDebug(new string('#', 46));
                LogDebug($"{director[0].attacker.DisplayName} attacks {director[0].chosenTarget.DisplayName}");

                // get the attacker in case they have mech quirks
                var defender = (Mech) director[0]?.chosenTarget;
                Mech attacker = null;
                if (director[0].attacker is Mech)
                {
                    attacker = (Mech) director[0].attacker;
                }

                var index = GetPilotIndex(defender);
                if (!ShouldPanic(defender, attackCompleteMessage.attackSequence)) return;

                // automatically eject a klutzy pilot on an additional roll yielding 13
                if (defender.IsFlaggedForKnockdown && defender.pilot.pilotDef.PilotTags.Contains("pilot_klutz"))
                {
                    if (Random.Range(1, 100) == 13)
                    {
                        defender.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage
                            (new ShowActorInfoSequence(defender, "WOOPS!", FloatieMessage.MessageNature.Debuff, false)));
                        LogDebug("Very klutzy!");
                        return;
                    }
                }

                // store saving throw
                // check it against panic
                // check it again ejection
                var savingThrow = GetSavingThrow(defender, attacker);
                heatDamage = 0;
                // panic saving throw
                if (SavedVsPanic(defender, savingThrow)) return;

                // stop if pilot isn't Panicked
                if (trackedPilots[index].panicStatus != PanicStatus.Panicked) return;

                // eject saving throw
                if (SavedVsEject(defender, savingThrow)) return;

                // ejecting
                // random phrase
                try
                {
                    if (modSettings.EnableEjectPhrases &&
                        Random.Range(1, 100) <= modSettings.EjectPhraseChance)
                    {
                        var ejectMessage = ejectPhraseList[Random.Range(1, ejectPhraseList.Count)];
                        defender.Combat.MessageCenter.PublishMessage(
                            new AddSequenceToStackMessage(
                                new ShowActorInfoSequence(defender, $"{ejectMessage}", FloatieMessage.MessageNature.Debuff, true)));
                    }
                }
                catch (Exception e)
                {
                    LogError(e);
                }

                // remove effects, to prevent exceptions that occur for unknown reasons
                try
                {
                    var combat = UnityGameInstance.BattleTechGame.Combat;
                    List<Effect> effectsTargeting = combat.EffectManager.GetAllEffectsTargeting(defender);
                    foreach (var effect in effectsTargeting)
                    {
                        // some effects removal throw, so silently drop them
                        try
                        {
                            defender.CancelEffect(effect);
                        }
                        // ReSharper disable once EmptyGeneralCatchClause
                        catch
                        {
                        }
                    }
                }
                catch (Exception e)
                {
                    LogError(e);
                }

                defender.EjectPilot(defender.GUID, attackCompleteMessage.stackItemUID, DeathMethod.PilotEjection, false);
                LogDebug($"Ejected.  Runtime {stopwatch.ElapsedMilliSeconds}ms");
            }
        }

        [HarmonyPatch(typeof(GameInstanceSave), MethodType.Constructor)]
        [HarmonyPatch(new[] {typeof(GameInstance), typeof(SaveReason)})]
        public static class GameInstanceSaveConstructorPatch
        {
            private static void Postfix(GameInstanceSave __instance) => SerializeStorageJson(__instance.InstanceGUID, __instance.SaveTime);
        }

        [HarmonyPatch(typeof(LanceSpawnerGameLogic), "OnTriggerSpawn")]
        public static class LanceSpawnerGameLogicPatch
        {
            // throw away the return of GetPilotIndex because the method is just adding the missing mechs
            public static void Postfix(LanceSpawnerGameLogic __instance) => __instance.Combat.AllMechs.ForEach(x => GetPilotIndex(x));
        }

        [HarmonyPatch(typeof(GameInstance), "LaunchContract", typeof(Contract), typeof(string))]
        public static class LaunchContractPatch
        {
            // reset on new contracts
            private static void Postfix() => Reset();
        }

        [HarmonyPatch(typeof(GameInstance), "Load")]
        public static class LoadPatch
        {
            private static void Prefix(GameInstanceSave save) => Resync(save.SaveTime);
        }

        [HarmonyPatch(typeof(Mech), "OnLocationDestroyed")]
        public static class OnLocationDestroyedPatch
        {
            private static void Postfix(Mech __instance)
            {
                var mech = __instance;
                if (!modSettings.LosingLimbAlwaysPanics ||
                    mech == null || mech.IsDead || mech.IsFlaggedForDeath && mech.HasHandledDeath)
                {
                    return;
                }

                var index = GetPilotIndex(mech);
                if (trackedPilots[index].mech != mech.GUID) return;

                if (trackedPilots[index].panicWorsenedRecently && modSettings.OneChangePerTurn) return;

                ApplyPanicDebuff(mech);
            }
        }

        [HarmonyPatch(typeof(SimGameState), "_OnFirstPlayInit")]
        public static class SimGameStateOnFirstPlayInitPatch
        {
            // we're doing a new campaign, so we need to sync the json with the new addition
            private static void Postfix()
            {
                // if campaigns are added this way, why does deleting the storage jsons not break it?
                SyncNewCampaign();
            }
        }
    }
}
