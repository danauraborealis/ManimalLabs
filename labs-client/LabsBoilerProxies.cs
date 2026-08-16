using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.LabsBoiler
{
    // the keycard swiper interaction anchor. InteractiveProxy.GetInteractionPosition
    // returns transform.TransformPoint(_interactionPosition); retail's authored offset
    // (0.81,-1.4,0.1) anchors it ~0.8m to the side of the reader (where the player
    // stands), which reads as "off to the right of the door". we set it at RUNTIME from
    // config (default 0 = right at the reader) so it can be tuned live without a bundle
    // rebuild. all three office proxies share one local orientation, so one offset fits.
    internal static class LabsBoilerProxies
    {
        private const string MxScene = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";

        private static readonly List<Component> _proxies = new List<Component>();
        private static Type _proxyType;
        private static System.Reflection.FieldInfo _ipField;

        internal static void OnSceneLoaded(Scene scene)
        {
            if (scene.name != MxScene) return;
            _proxyType ??= AccessTools.TypeByName("InteractiveProxy");
            if (_proxyType == null) { Plugin.Log.LogWarning("[LabsBoiler] InteractiveProxy type not found"); return; }
            _ipField ??= AccessTools.Field(_proxyType, "_interactionPosition");

            _proxies.Clear();
            foreach (var root in scene.GetRootGameObjects())
                _proxies.AddRange(root.GetComponentsInChildren(_proxyType, true));
            Reapply();
            Plugin.Log.LogInfo($"[LabsBoiler] set interaction anchor on {_proxies.Count} keycard swiper(s) " +
                               $"(offset {Plugin.ProxyInteractX.Value},{Plugin.ProxyInteractY.Value},{Plugin.ProxyInteractZ.Value})");

            FixNullTriggersMaps(scene);
        }

        // THE LEFT-HAND-ANIMATION BUG (probe-cornered 2026-08-16): every open of a
        // grafted door threw an NRE inside WorldInteractiveObject.method_3 — the
        // husked TriggersMap array is NULL (lists were dropped from the rebake) and
        // method_3 foreaches it unguarded. the throw happens INSIDE door.Interact,
        // which ExecuteDoorInteraction calls BEFORE SetInteractInHands — so the door
        // still opened but the hand animation call was never reached. native doors
        // carry an empty-but-initialized array. patch every grafted WIO to match.
        private static void FixNullTriggersMaps(Scene scene)
        {
            var wioType = AccessTools.TypeByName("EFT.Interactive.WorldInteractiveObject");
            var tmField = wioType != null ? AccessTools.Field(wioType, "TriggersMap") : null;
            if (tmField == null) { Plugin.Log.LogWarning("[LabsBoiler] TriggersMap field not found — door opens will NRE"); return; }
            int fixedUp = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var wio in root.GetComponentsInChildren(wioType, true))
                    if (tmField.GetValue(wio) == null)
                    {
                        tmField.SetValue(wio, Array.CreateInstance(tmField.FieldType.GetElementType(), 0));
                        fixedUp++;
                    }
            Plugin.Log.LogInfo($"[LabsBoiler] initialized null TriggersMap on {fixedUp} door(s)/interactive(s) — open-hand animation unblocked");
        }

        // (re)apply the configured local offset — called on load and on any slider change
        internal static void Reapply()
        {
            if (_ipField == null) return;
            var v = new Vector3(Plugin.ProxyInteractX.Value, Plugin.ProxyInteractY.Value, Plugin.ProxyInteractZ.Value);
            foreach (var px in _proxies)
                if (px != null) _ipField.SetValue(px, v);
        }
    }
}
