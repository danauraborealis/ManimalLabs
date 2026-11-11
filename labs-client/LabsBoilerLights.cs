using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.LabsBoiler;

internal static class LabsBoilerLights
{
    private const string MxScene = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";

    internal static bool NativeBranchHandled { get; private set; }

    internal static void TryMuteNativeOfficeLights()
    {
        if (NativeBranchHandled) return;
        var mx = SceneManager.GetSceneByName(MxScene);
        var light = SceneManager.GetSceneByName("Laboratory_LIGHT");
        if (!mx.isLoaded || !light.isLoaded) return;
        
        int branches = 0, unityLights = 0, areaLights = 0, cullingDrivers = 0;
        foreach (var root in light.GetRootGameObjects())
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (!t || t.name != "Laboratory_Office_Above_Boiler_Room_floor_1") continue;
            // retail nests the branch under OO/LightGroup, the 4.1 client has it straight under OO
            var group = t.parent && t.parent.name == "LightGroup" ? t.parent.parent : t.parent;
            if (!group || group.name != "OO" || t.root.name != "SBG_Laboratory_Light")
            {
                Plugin.Log.LogWarning($"[LabsBoiler] refused to mute unexpected native light branch path '{Path(t)}'");
                continue;
            }

            // Keep the native hierarchy and culling registrations intact. Deactivating
            // or destroying this imported subtree can disturb Labs' global light-culling
            // state; only mute the duplicate office emitters that our graft replaces.
            foreach (var clo in t.GetComponentsInChildren<CullingLightObject>(true))
            {
                clo.IntensityMultiplier = 0f;
                cullingDrivers++;
            }
            foreach (var cadlo in t.GetComponentsInChildren<CullingAdvancedLightObject>(true))
            {
                cadlo.IntensityMultiplier = 0f;
                cullingDrivers++;
            }
            foreach (var nativeLight in t.GetComponentsInChildren<Light>(true))
            {
                nativeLight.intensity = 0f;
                nativeLight.enabled = false;
                unityLights++;
            }
            foreach (var nativeArea in t.GetComponentsInChildren<BaseLight>(true))
            {
                nativeArea.m_Intensity = 0f;
                nativeArea.enabled = false;
                areaLights++;
            }
            branches++;
        }
        
        NativeBranchHandled = true;
        if (branches > 0)
        {
            Plugin.Log.LogInfo(
                $"[LabsBoiler] MUTED duplicate emitters in {branches} exact native office light branch(es): " +
                $"{unityLights} Unity light(s), {areaLights} area light(s), {cullingDrivers} culling driver(s); hierarchy preserved");
        }
        else
        {
            Plugin.Log.LogWarning(
                "[LabsBoiler] native office light branch not found by name in Laboratory_LIGHT — doubles possible");
        }
    }
    private const float DefaultIntensity = 1.5f;

    private struct Lamp { public Light Light; public float Base; public Component Culling; public bool WasActive; }
    private struct Area { public Component BaseLight; public float Base; public Component Culling; public bool WasActive; }
    private static readonly List<Lamp> Lamps = [];
    private static readonly List<Area> Areas = [];

    private static Type _cloType, _cadloType, _baseLightType;
    private static FieldInfo _cloMax, _cloF1, _cadloMax, _cadloMaxFinal, _baseIntensity;

    internal static void OnSceneLoaded(Scene scene)
    {
        if (scene.name != MxScene) return;
        _cloType ??= AccessTools.TypeByName("CullingLightObject");
        _cadloType ??= AccessTools.TypeByName("CullingAdvancedLightObject");
        _baseLightType ??= AccessTools.TypeByName("BaseLight");
        if (_cloType != null) { _cloMax ??= AccessTools.Field(_cloType, "_maxLightIntensity"); _cloF1 ??= AccessTools.Field(_cloType, "float_1"); }
        if (_cadloType != null) { _cadloMax ??= AccessTools.Field(_cadloType, "_maxLightIntensity"); _cadloMaxFinal ??= AccessTools.Field(_cadloType, "_maxIntensityFinal"); }
        if (_baseLightType != null) _baseIntensity ??= AccessTools.Field(_baseLightType, "m_Intensity");

        // The grafted scene is deliberately unmanaged by PerfectCulling. Keep its EFT
        // light wrappers unmanaged too: ripped CullingObject target arrays can otherwise
        // toggle components belonging to the native multi-scene map. sceneLoaded runs
        // before Start, so disabling here prevents registration without unregistering a
        // default Index=0 and poisoning the global CullingManager.
        var disabledCulling = 0;
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var clo in root.GetComponentsInChildren<CullingLightObject>(true))
            {
                if (!clo.enabled) continue;
                clo.enabled = false;
                disabledCulling++;
            }
            foreach (var cadlo in root.GetComponentsInChildren<CullingAdvancedLightObject>(true))
            {
                if (!cadlo.enabled) continue;
                cadlo.enabled = false;
                disabledCulling++;
            }
        }
        if (disabledCulling > 0)
        {
            Plugin.Log.LogInfo($"[LabsBoiler] disabled {disabledCulling} grafted EFT light-culling wrapper(s) before registration — office lights run locally/unmanaged");
        }

        Lamps.Clear();
        Areas.Clear();
        
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var light in root.GetComponentsInChildren<Light>(true))
            {
                var clo = _cloType != null ? light.GetComponent(_cloType) : null;
                var baseValue = ReadFloat(clo, _cloMax) ?? DefaultIntensity;
                if (baseValue <= 0f) baseValue = DefaultIntensity;
                Lamps.Add(new Lamp { Light = light, Base = baseValue, Culling = clo, WasActive = light.gameObject.activeInHierarchy });
            }

            if (_baseLightType == null) { continue; }
            
            foreach (var bl in root.GetComponentsInChildren(_baseLightType, true))
            {
                var cadlo = _cadloType != null ? bl.GetComponent(_cadloType) : null;
                var baseValue = ReadFloat(cadlo, _cadloMax) ?? 1.0f;
                if (baseValue <= 0f) baseValue = 1.0f;
                Areas.Add(new Area { BaseLight = bl, Base = baseValue, Culling = cadlo, WasActive = bl.gameObject.activeInHierarchy });
            }
        }
        
        ReapplyIntensity();
        var onLamps = 0; foreach (var l in Lamps) if (l.WasActive || Plugin.LampForceOn.Value) onLamps++;
        var onAreas = 0; foreach (var a in Areas) if (a.WasActive || Plugin.LampForceOn.Value) onAreas++;
        Plugin.Log.LogInfo($"[LabsBoiler] office lights: {onLamps}/{Lamps.Count} lamps + {onAreas}/{Areas.Count} area lights driven " +
                           $"(retail-active only; forceOn={Plugin.LampForceOn.Value}, scale={Plugin.LampIntensity.Value:F2}, clamp={Plugin.LampMaxIntensity.Value:F1})");
    }

    internal static void ReapplyIntensity()
    {
        var scale = Plugin.LampIntensity.Value;
        var clamp = Plugin.LampMaxIntensity.Value;
        var force = Plugin.LampForceOn.Value;

        foreach (var l in Lamps)
        {
            if (!l.Light) continue;
            if (!l.WasActive && !force) continue;
            if (force) ActivateChain(l.Light.transform);
            var target = Mathf.Min(l.Base * scale, clamp);
            l.Light.intensity = target;
            if (!l.Culling) { continue; }

            _cloF1?.SetValue(l.Culling, target); _cloMax?.SetValue(l.Culling, target);
        }
        foreach (var a in Areas)
        {
            if (!a.BaseLight) continue;
            if (!a.WasActive && !force) continue;
            if (force) ActivateChain(a.BaseLight.transform);
            var target = Mathf.Min(a.Base * scale, clamp);
            _baseIntensity?.SetValue(a.BaseLight, target);
            if (!a.Culling) { continue; }

            _cadloMaxFinal?.SetValue(a.Culling, target); _cadloMax?.SetValue(a.Culling, target);
        }
    }

    private static void ActivateChain(Transform t)
    {
        for (var cur = t; cur; cur = cur.parent)
        {
            if (!cur.gameObject.activeSelf)
            {
                cur.gameObject.SetActive(true);
            }
        }
    }

    private static float? ReadFloat(Component c, FieldInfo f)
    {
        if (!c || f == null) return null;
        try { return (float)f.GetValue(c); }
        catch { return null; }
    }

    private static string Path(Transform t)
    {
        var path = t.name;
        for (var p = t.parent; p; p = p.parent) path = p.name + "/" + path;
        return path;
    }

    internal static void Reset()
    {
        NativeBranchHandled = false;
        Lamps.Clear();
        Areas.Clear();
        _cloType = null;
        _cadloType = null;
        _baseLightType = null;
        _cloMax = null;
        _cloF1 = null;
        _cadloMax = null;
        _cadloMaxFinal = null;
        _baseIntensity = null;
    }
}
