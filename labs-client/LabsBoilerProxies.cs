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
            if (Plugin.Instance != null) Plugin.Instance.StartCoroutine(FixWindowBreakers(scene));
        }

        // shooting an office window NREs in WindowBreaker.method_20: `BrokenWindow`
        // (a WindowBreakingConfig ScriptableObject) and `Material` are external asset
        // refs the rip nulled. copy both from any native Labs WindowBreaker — the
        // config is map-shared, same glass. (the NRE aborts the ballistics
        // ShotDelegate mid-shot, so it's not just cosmetic.)
        private static System.Collections.IEnumerator FixWindowBreakers(Scene scene)
        {
            var ours = new List<EFT.Interactive.WindowBreaker>();
            foreach (var root in scene.GetRootGameObjects())
                ours.AddRange(root.GetComponentsInChildren<EFT.Interactive.WindowBreaker>(true));
            if (ours.Count == 0) yield break;

            // native scenes (and their donor windows) can load after _MX — poll a while
            EFT.Interactive.WindowBreaker donor = null;
            for (int i = 0; i < 600 && donor == null; i++)   // ~10s at 60fps
            {
                foreach (var wb in UnityEngine.Object.FindObjectsOfType<EFT.Interactive.WindowBreaker>(true))
                    if (!ours.Contains(wb) && wb.BrokenWindow != null) { donor = wb; break; }
                if (donor == null) yield return null;
            }
            if (donor == null)
            {
                Plugin.Log.LogWarning("[LabsBoiler] no native WindowBreaker donor — office windows will NRE when shot");
                yield break;
            }
            int fixedUp = 0, rebaked = 0;
            foreach (var wb in ours)
            {
                bool touched = false;
                if (wb.BrokenWindow == null) { wb.BrokenWindow = donor.BrokenWindow; touched = true; }
                if (wb.Material == null) { wb.Material = donor.Material; touched = true; }
                // ObstructiveCollider is a serialized child ref (the 'Obstructive
                // Collider' child) the import dropped — on break the code does
                // `if (ObstructiveCollider != null) SetActive(false)`, so a null here
                // silently leaves the pane's collision standing after it shatters
                if (wb.ObstructiveCollider == null)
                    foreach (var col in wb.GetComponentsInChildren<Collider>(true))
                        if (col.gameObject.name.IndexOf("Obstructive", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        { wb.ObstructiveCollider = col; touched = true; break; }
                if (touched) fixedUp++;
                // the glass-plane parametrization (AxisX/UvMult/Box/Rotation/ZSurfs/
                // NeedToSwap) is EDITOR-baked into hidden fields and husked to garbage.
                // method_5 (the editor bake routine) can't run here — our ripped glass
                // meshes are not CPU-readable (Unity returns EMPTY vertex/uv arrays and
                // just logs an error, hence the silent bad bake). instead apply RETAIL's
                // authored values from the extraction sidecar, matched by position.
                if (ApplyRetailWindowValues(wb)) rebaked++;
                else
                {
                    try { wb.method_5(); rebaked++; }   // fallback: works if the mesh happens to be readable
                    catch (System.Exception e) { Plugin.Log.LogWarning($"[LabsBoiler] window '{wb.name}' rebake failed: {e.Message}"); }
                }
            }
            Plugin.Log.LogInfo($"[LabsBoiler] WindowBreaker fix: configs from '{donor.gameObject.name}' onto {fixedUp}/{ours.Count}, glass parametrization set on {rebaked}/{ours.Count}");
        }


        // retail WindowBreaker values (boiler_windows.json beside the dll) — the whole
        // scalar surface: tunables + the hidden editor-baked plane parametrization.
        private static Newtonsoft.Json.Linq.JArray _windowRows;

        private static bool ApplyRetailWindowValues(EFT.Interactive.WindowBreaker wb)
        {
            try
            {
                if (_windowRows == null)
                {
                    var dir = System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".";
                    var p = System.IO.Path.Combine(dir, "boiler_windows.json");
                    if (!System.IO.File.Exists(p)) return false;
                    _windowRows = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(p))
                                  ["components"]?["WindowBreaker"] as Newtonsoft.Json.Linq.JArray;
                }
                if (_windowRows == null) return false;

                Newtonsoft.Json.Linq.JObject fields = null;
                float best = 0.5f;
                foreach (var row in _windowRows)
                {
                    var w = row["world"];
                    if (w == null) continue;
                    var d = UnityEngine.Vector3.Distance(wb.transform.position,
                        new UnityEngine.Vector3((float)w[0], (float)w[1], (float)w[2]));
                    if (d < best) { best = d; fields = row["fields"] as Newtonsoft.Json.Linq.JObject; }
                }
                if (fields == null) return false;

                var t = typeof(EFT.Interactive.WindowBreaker);
                void F(string n) { var fi = AccessTools.Field(t, n); var v = fields[n]; if (fi != null && v != null) fi.SetValue(wb, (float)v); }
                void I(string n) { var fi = AccessTools.Field(t, n); var v = fields[n]; if (fi != null && v != null) fi.SetValue(wb, fi.FieldType.IsEnum ? System.Enum.ToObject(fi.FieldType, (int)v) : (object)(int)v); }
                void B(string n) { var fi = AccessTools.Field(t, n); var v = fields[n]; if (fi != null && v != null) fi.SetValue(wb, (int)v != 0); }
                void V2(string n) { var fi = AccessTools.Field(t, n); var v = fields[n]; if (fi != null && v != null) fi.SetValue(wb, new UnityEngine.Vector2((float)v["x"], (float)v["y"])); }
                void V4(string n) { var fi = AccessTools.Field(t, n); var v = fields[n]; if (fi != null && v != null) fi.SetValue(wb, new UnityEngine.Vector4((float)v["x"], (float)v["y"], (float)v["z"], (float)v["w"])); }
                void Q(string n) { var fi = AccessTools.Field(t, n); var v = fields[n]; if (fi != null && v != null) fi.SetValue(wb, new UnityEngine.Quaternion((float)v["x"], (float)v["y"], (float)v["z"], (float)v["w"])); }

                I("AngleMode");
                F("MinThickness"); F("ThicknessMultyplier"); F("EdgesWidth"); F("CracksScale");
                F("MassMultyplier"); F("AirDrag"); F("TimeUntilPartDie");
                F("FirstShotRadius"); F("ShotRadius"); F("FirstShotSoundVolume");
                F("InstantExplosionCoef"); F("ShotDirectionCoef");
                V2("TorqueRandomMinMaxCoefs");
                I("AxisX"); I("AxisY"); I("AxisZ");
                V2("UvMult"); V2("UvAdd"); V2("ZSurfs");
                V4("Box"); Q("Rotation"); B("NeedToSwap");
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[LabsBoiler] retail window values failed for '{wb.name}': {e.Message}");
                return false;
            }
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
