using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.LabsBoiler
{
    // office lighting, RETAIL-ACCURATE. the mental model:
    //  * EFT lights serialize intensity 0; a CullingLightObject drives
    //    light.intensity = _maxLightIntensity * distanceFade each frame (float_1 is the
    //    cached max). AreaLights work the same via CullingAdvancedLightObject.
    //  * on/off is the GameObject active state, NOT intensity. retail turns the office's
    //    spot lamps OFF by deactivating their Light_Round_Normal_275 parent — 36 of them
    //    are off on purpose. the room is lit by 64 AreaLights + a few active lamps + the
    //    glowstick + emissives.
    // so we DON'T force anything on (that was the "too bright" bug — reviving retail's
    // deliberately-off lamps). we only take control of the lights retail has ACTIVE, so
    // the sliders can tune them, and leave the culling system to fade our value. the
    // AreaLight shader refs the rip nulled are restored separately (LabsBoilerShaders).
    // LampForceOn (config) is the opt-in "light every lamp" override for a brighter room.
    internal static class LabsBoilerLights
    {
        private const string MxScene = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";

        // SPT's native LIGHT scene loads its OWN (pre-expansion) office lighting branch —
        // leaving it alive under our grafted retail branch double-lights the space and
        // fights the retail-authentic look. delete the native branch once both scenes
        // exist; our rebaked branch is the sole owner. called from BOTH scene handlers
        // so load order doesn't matter.
        private static bool _nativeBranchDeleted;

        internal static void TryDeleteNativeOfficeBranch()
        {
            if (_nativeBranchDeleted) return;
            var mx = SceneManager.GetSceneByName(MxScene);
            var light = SceneManager.GetSceneByName("Laboratory_LIGHT");
            if (!mx.isLoaded || !light.isLoaded) return;
            // search by NAME, not path — SPT's native (pre-expansion) LIGHT scene has a
            // different grouping than retail's, so the retail-derived path missed (08-15)
            int branches = 0, objects = 0;
            foreach (var root in light.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null || t.name != "Laboratory_Office_Above_Boiler_Room_floor_1") continue;
                    objects += t.GetComponentsInChildren<Transform>(true).Length;
                    UnityEngine.Object.Destroy(t.gameObject);
                    branches++;
                }
            _nativeBranchDeleted = true;
            if (branches > 0)
                Plugin.Log.LogInfo($"[LabsBoiler] deleted {branches} NATIVE office light branch(es) ({objects} objects) — grafted retail branch is sole owner");
            else
                Plugin.Log.LogWarning("[LabsBoiler] native office light branch not found by name in Laboratory_LIGHT — doubles possible");
        }
        private const float DefaultIntensity = 1.5f;

        private struct Lamp { public Light Light; public float Base; public Component Culling; public bool WasActive; }
        private struct Area { public Component BaseLight; public float Base; public Component Culling; public bool WasActive; }
        private static readonly List<Lamp> _lamps = new List<Lamp>();
        private static readonly List<Area> _areas = new List<Area>();

        private static Type _cloType, _cadloType, _baseLightType;
        private static FieldInfo _cloMax, _cloF1, _cadloMax, _cadloF1, _baseIntensity;

        internal static void OnSceneLoaded(Scene scene)
        {
            if (scene.name != MxScene) return;
            _cloType ??= AccessTools.TypeByName("CullingLightObject");
            _cadloType ??= AccessTools.TypeByName("CullingAdvancedLightObject");
            _baseLightType ??= AccessTools.TypeByName("BaseLight");
            if (_cloType != null) { _cloMax ??= AccessTools.Field(_cloType, "_maxLightIntensity"); _cloF1 ??= AccessTools.Field(_cloType, "float_1"); }
            if (_cadloType != null) { _cadloMax ??= AccessTools.Field(_cadloType, "_maxLightIntensity"); _cadloF1 ??= AccessTools.Field(_cadloType, "float_1"); }
            if (_baseLightType != null) _baseIntensity ??= AccessTools.Field(_baseLightType, "m_Intensity");

            _lamps.Clear();
            _areas.Clear();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var light in root.GetComponentsInChildren<Light>(true))
                {
                    var clo = _cloType != null ? light.GetComponent(_cloType) : null;
                    float base_ = ReadFloat(clo, _cloMax) ?? DefaultIntensity;
                    if (base_ <= 0f) base_ = DefaultIntensity;
                    _lamps.Add(new Lamp { Light = light, Base = base_, Culling = clo, WasActive = light.gameObject.activeInHierarchy });
                }
                if (_baseLightType != null)
                    foreach (var bl in root.GetComponentsInChildren(_baseLightType, true))
                    {
                        var cadlo = _cadloType != null ? bl.GetComponent(_cadloType) : null;
                        float base_ = ReadFloat(cadlo, _cadloMax) ?? 1.0f;
                        if (base_ <= 0f) base_ = 1.0f;
                        _areas.Add(new Area { BaseLight = bl, Base = base_, Culling = cadlo, WasActive = bl.gameObject.activeInHierarchy });
                    }
            }
            ReapplyIntensity();
            int onLamps = 0; foreach (var l in _lamps) if (l.WasActive || Plugin.LampForceOn.Value) onLamps++;
            int onAreas = 0; foreach (var a in _areas) if (a.WasActive || Plugin.LampForceOn.Value) onAreas++;
            Plugin.Log.LogInfo($"[LabsBoiler] office lights: {onLamps}/{_lamps.Count} lamps + {onAreas}/{_areas.Count} area lights driven " +
                               $"(retail-active only; forceOn={Plugin.LampForceOn.Value}, scale={Plugin.LampIntensity.Value:F2}, clamp={Plugin.LampMaxIntensity.Value:F1})");
        }

        // drive only retail-active lights (or all, if LampForceOn). set the culling
        // system's cached max (float_1) + _maxLightIntensity so the distance-fade
        // recompute uses OUR value — scale 1 + no clamp == exactly retail.
        internal static void ReapplyIntensity()
        {
            float scale = Plugin.LampIntensity.Value;
            float clamp = Plugin.LampMaxIntensity.Value;
            bool force = Plugin.LampForceOn.Value;

            foreach (var l in _lamps)
            {
                if (l.Light == null) continue;
                if (!l.WasActive && !force) continue;                 // retail keeps it off — leave it
                if (force) ActivateChain(l.Light.transform);
                float target = Mathf.Min(l.Base * scale, clamp);
                l.Light.intensity = target;
                if (l.Culling != null) { _cloF1?.SetValue(l.Culling, target); _cloMax?.SetValue(l.Culling, target); }
            }
            foreach (var a in _areas)
            {
                if (a.BaseLight == null) continue;
                if (!a.WasActive && !force) continue;
                if (force) ActivateChain((a.BaseLight as Component).transform);
                float target = Mathf.Min(a.Base * scale, clamp);
                _baseIntensity?.SetValue(a.BaseLight, target);
                if (a.Culling != null) { _cadloF1?.SetValue(a.Culling, target); _cadloMax?.SetValue(a.Culling, target); }
            }
        }

        private static void ActivateChain(Transform t)
        {
            for (var cur = t; cur != null; cur = cur.parent)
                if (!cur.gameObject.activeSelf) cur.gameObject.SetActive(true);
        }

        private static float? ReadFloat(Component c, FieldInfo f)
        {
            if (c == null || f == null) return null;
            try { return (float)f.GetValue(c); }
            catch { return null; }
        }
    }
}
