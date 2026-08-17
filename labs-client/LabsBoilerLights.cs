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

    internal static void TryDeleteNativeOfficeBranch()
    {
        if (NativeBranchHandled) return;
        var mx = SceneManager.GetSceneByName(MxScene);
        var light = SceneManager.GetSceneByName("Laboratory_LIGHT");
        if (!mx.isLoaded || !light.isLoaded) return;
        
        int branches = 0, objects = 0;
        foreach (var root in light.GetRootGameObjects())
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (!t || t.name != "Laboratory_Office_Above_Boiler_Room_floor_1") continue;
            objects += t.GetComponentsInChildren<Transform>(true).Length;
            // deactivate, NEVER destroy: destroying at sceneLoaded runs before Start, so
            // never-registered CullingObjects unregister with default Index=0 and corrupt
            // CullingManager slot 0 (map-wide light cross-wiring). deactivation also kills
            // the floor-1 double-lighting — this line IS the point of the method.
            t.gameObject.SetActive(false);
            branches++;
        }
        
        NativeBranchHandled = true;
        if (branches > 0)
        {
            Plugin.Log.LogInfo(
                $"[LabsBoiler] DEACTIVATED {branches} NATIVE office light branch(es) ({objects} objects) — sole owner, no culling unregister storm");
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

            _cadloF1?.SetValue(a.Culling, target); _cadloMax?.SetValue(a.Culling, target);
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
}