using EFT;
using EFT.Interactive;
using HarmonyLib;

namespace Manimal.LabsBoiler
{
    // diagnostic: logs the exact interaction parameters when the player interacts
    // with one of OUR doors. the door-open hand animation in SPT 4.0 is
    // SetInteractInHands((EInteraction)AnimationId) gated on !interactWithoutAnimation
    // (MovementState.ExecuteDoorInteraction) — AnimationId is computed geometrically
    // (23-28 hinge push/pull; retail's PushID/CloseID fields DON'T EXIST in the 4.0
    // runtime). if the left hand still doesn't play after the GripPose/DoorHandle
    // rebake, this shows which input is wrong.
    [HarmonyPatch]
    internal static class LabsBoilerDoorProbe
    {
        private static bool Ours(WorldInteractiveObject interactive)
        {
            if (interactive == null) return false;
            var id = interactive.Id ?? "";
            return id.Contains("Office_Above_Boiler_Room") || id.Contains("345345")
                   || interactive.gameObject.name.Contains("Office_Above_Boiler_Room");
        }

        // the swipe/locked path (UI -> vmethod_0 -> StartInteraction)
        [HarmonyPatch(typeof(MovementContext), nameof(MovementContext.StartInteraction))]
        [HarmonyPostfix]
        private static void AfterStart(MovementContext __instance, WorldInteractiveObject interactive)
        {
            try
            {
                if (!Ours(interactive)) return;
                var p = __instance.InteractionParameters;
                Plugin.Log.LogInfo($"[LabsBoiler] DOOR PROBE start '{interactive.gameObject.name}': state={interactive.DoorState}, " +
                                   $"AnimationId={p.AnimationId}, Snap={p.Snap}, grip={(p.Grip != null ? p.Grip.name : "NULL")}, " +
                                   $"noAnim={interactive.interactWithoutAnimation}");
            }
            catch { }
        }

        // the OPEN/CLOSE dispatcher: UI action -> Player.vmethod_1 ->
        // CurrentManagedState.ExecuteDoorInteraction. logs WHICH state class handles
        // it — v2's patch on the virtual BASE never fired because Harmony base
        // patches don't cover overrides (DoorInteractionStateClass etc.)
        [HarmonyPatch(typeof(Player), nameof(Player.vmethod_1))]
        [HarmonyPrefix]
        private static void BeforeVmethod1(Player __instance, WorldInteractiveObject door)
        {
            try
            {
                if (!Ours(door)) return;
                Plugin.Log.LogInfo($"[LabsBoiler] DOOR PROBE open-dispatch '{door.gameObject.name}': state={door.DoorState}, " +
                                   $"handler={__instance.CurrentManagedState?.GetType().Name}, noAnim={door.interactWithoutAnimation}");
            }
            catch { }
        }

        // ground truth: the actual hand-animation trigger. fires for EVERY
        // interaction on every door — open a NATIVE door and one of OURS in the same
        // raid and the diff (or the absence of a line) names the culprit.
        [HarmonyPatch(typeof(MovementContext), nameof(MovementContext.SetInteractInHands))]
        [HarmonyPrefix]
        private static void BeforeSetInteract(MovementContext __instance, EInteraction interaction)
        {
            try
            {
                bool idling = false;
                try
                {
                    var player = AccessTools.Field(typeof(MovementContext), "_player")?.GetValue(__instance) as Player;
                    idling = player?.HandsController?.FirearmsAnimator?.IsIdling() ?? false;
                }
                catch { }
                Plugin.Log.LogInfo($"[LabsBoiler] DOOR PROBE hands: interaction={interaction} ({(int)interaction}), " +
                                   $"sprint={__instance.IsSprintEnabled}, mounted={__instance.IsInMountedState}, " +
                                   $"stationary={__instance.IsStationaryWeaponInHands}, firearmsIdling={idling}");
            }
            catch { }
        }
    }
}
