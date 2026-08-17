using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.LabsBoiler;

internal static class LabsBoilerShaders
{
    private const string MxScene = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";

    private static readonly List<(Material mat, float vis, float pow)> Emissives = [];
    private static readonly int EmissionVisibility = Shader.PropertyToID("_EmissionVisibility");
    private static readonly int EmissionPower = Shader.PropertyToID("_EmissionPower");

    internal static void ReapplyEmissiveBoost()
    {
        var b = Plugin.EmissiveBoost.Value;
        foreach (var (mat, vis, pow) in Emissives)
        {
            if (!mat) continue;
            mat.SetFloat(EmissionVisibility, vis * b);
            mat.SetFloat(EmissionPower, pow * (b >= 1f ? 1f + (b - 1f) * 0.5f : b));
        }
    }

    internal static void OnSceneLoaded(Scene scene)
    {
        if (scene.name != MxScene) return;
        RebindShaders(scene);
        if (Plugin.Instance)
        {
            Plugin.Instance.StartCoroutine(CopyAreaLightShaders(scene));
        }
    }

    private static void RebindShaders(Scene scene)
    {
        var rebound = 0;
        var seen = new HashSet<Material>();
        var misses = new Dictionary<string, int>();
        foreach (var root in scene.GetRootGameObjects())
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        foreach (var m in r.sharedMaterials)
        {
            if (!m || !m.shader || !seen.Add(m)) continue;
            
            var game = Shader.Find(m.shader.name) ?? GClass872.Find(m.shader.name);
            if (game && game != m.shader)
            {
                m.shader = game; rebound++;
            }
            else if (!game)
            {
                misses.TryGetValue(m.shader.name, out var c);
                misses[m.shader.name] = c + 1;
            }
        }
        Plugin.Log.LogInfo($"[LabsBoiler] rebound {rebound} material(s) to game shaders in {scene.name}");

        foreach (var kv in misses)
            Plugin.Log.LogWarning($"[LabsBoiler] shader NOT rebound ({kv.Value} mat(s)): '{kv.Key}' — renders the ripped dummy");

        Emissives.Clear();
        foreach (var m in seen)
        {
            if (!m || !m.HasProperty(EmissionVisibility)) { continue; }
            
            Emissives.Add((m, m.GetFloat(EmissionVisibility), 
                m.HasProperty(EmissionPower) ? m.GetFloat(EmissionPower) : 1f));
        }
        ReapplyEmissiveBoost();
        Plugin.Log.LogInfo($"[LabsBoiler] {Emissives.Count} emissive material(s) on the boost slider (EmissiveBoost={Plugin.EmissiveBoost.Value:F2})");
    }

    private static IEnumerator CopyAreaLightShaders(Scene scene)
    {
        var alType = AccessTools.TypeByName("AreaLight");
        if (alType == null)
        {
            Plugin.Log.LogWarning("[LabsBoiler] AreaLight type not found — skipping shader copy");
            yield break;
        }
        var proxyF = AccessTools.Field(alType, "m_ProxyShader");
        
        // These two are just never used it seems
        // var smapF = AccessTools.Field(alType, "m_ShadowmapShader");
        // var blurF = AccessTools.Field(alType, "m_BlurShadowmapShader");
        
        if (proxyF == null)
        {
            Plugin.Log.LogWarning("[LabsBoiler] AreaLight.m_ProxyShader not found");
            yield break;
        }

        var ours = new List<Component>();
        foreach (var root in scene.GetRootGameObjects())
        {
            ours.AddRange(root.GetComponentsInChildren(alType, true));
        }
        if (ours.Count == 0) { yield break; }

        UnityEngine.Object donor = null;
        for (var frame = 0; frame < 120 && !donor; frame++)
        {
            foreach (var al in UnityEngine.Object.FindObjectsOfType(alType))
            {
                if (ours.Contains(al as Component)) continue;
                if (proxyF.GetValue(al) == null) { continue; }

                donor = al; break;
            }
            if (!donor) { yield return null; }
        }
        
        if (!donor)
        {
            Plugin.Log.LogWarning("[LabsBoiler] no native AreaLight donor found — grafted area lights stay unlit");
            yield break;
        }

        var copyFields = new List<FieldInfo>();
        foreach (var name in new[] { "m_Quad", "m_Cube", "m_ProxyShader", "m_ShadowmapShader", "m_BlurShadowmapShader" })
        {
            var fi = AccessTools.Field(alType, name);
            if (fi != null) copyFields.Add(fi);
        }

        var allFields = new List<FieldInfo>();
        for (var t = alType; t != null && t != typeof(MonoBehaviour) && t != typeof(Behaviour); t = t.BaseType)
        {
            foreach (var fi in AccessTools.GetDeclaredFields(t))
            {
                if (!fi.IsStatic) allFields.Add(fi);
            }
        }
        var cadloType = AccessTools.TypeByName("CullingAdvancedLightObject");
        var cadloLight = cadloType != null ? AccessTools.Field(cadloType, "_light") : null;

        int rebuilt = 0, repointed = 0, orphaned = 0, shadowFixed = 0;
        var freshOnes = new List<(Component light, bool active)>();
        foreach (var al in ours.ToArray())
        {
            if (!al) { continue; }
            
            var go = al.gameObject;
            var wasActive = go.activeSelf;
            go.SetActive(false);

            Component cadlo = null;
            if (cadloType != null)
            {
                for (var tt = go.transform; tt && !cadlo; tt = tt.parent)
                {
                    cadlo = tt.GetComponent(cadloType);
                }
            }

            var vals = new Dictionary<FieldInfo, object>();
            foreach (var fi in allFields) vals[fi] = fi.GetValue(al);
            UnityEngine.Object.DestroyImmediate(al);

            var fresh = go.AddComponent(alType);
            foreach (var kv in vals)
            {
                try
                {
                    kv.Key.SetValue(fresh, kv.Value);
                }
                catch(Exception e)
                {
                    Plugin.Log.LogError(e);
                }
            }
            
            foreach (var fi in copyFields)
            {
                if (fi.GetValue(fresh) == null && fi.GetValue(donor) != null)
                {
                    fi.SetValue(fresh, fi.GetValue(donor));
                }
            }
            
            var a = (AreaLight)fresh;
            a.m_SourceColor = Color.white;
            a.IsShadowCubeAnimated = false;
            a.ShadowCube = null;
            a.InvertedShadowCube = null;
            a.ShadowRendererToDraw = null;
            a.ShadowRendererMaterial = null;
            a.IsShadowRendererInverted = false;
            a.ShadowFeather = 0f;
            a.InvertedShadowFeather = 0f;
            a.m_Spot = false;
            a.m_Shadows = false;
            a.m_ShadowCullingMask = -1;
            a.m_ShadowmapRes = AreaLight.TextureSize.x2048;
            a.m_ReceiverSearchDistance = 24f;
            a.m_ReceiverDistanceScale = 5f;
            a.m_LightNearSize = 4f;
            a.m_LightFarSize = 22f;
            a.m_ShadowBias = 0.001f;
            shadowFixed++;
            if (cadlo && cadloLight != null) { cadloLight.SetValue(cadlo, fresh); repointed++; }
            else orphaned++;
            go.SetActive(wasActive);
            freshOnes.Add((fresh, wasActive && go.activeInHierarchy));
            rebuilt++;
        }
        Plugin.Log.LogInfo($"[LabsBoiler] REBUILT {rebuilt} grafted area light(s) with donor meshes/shaders from '{((Component)donor).gameObject.name}' " +
                           $"({repointed} CADLO(s) repointed, {orphaned} without a driver, {shadowFixed} drift-tail(s) sanitized)");

        try
        {
            LabsBoilerLights.OnSceneLoaded(scene);
        }
        catch(Exception e)
        {
            Plugin.Log.LogError(e);
        }
        
        var fBool0 = AccessTools.Field(alType, "bool_0");
        var fMat1 = AccessTools.Field(alType, "material_1");
        int verifiable = 0, verified = 0;
        foreach (var (light, active) in freshOnes)
        {
            if (!light || !active) continue;
            verifiable++;
            if (fBool0 != null && (bool)fBool0.GetValue(light) && fMat1?.GetValue(light) != null) verified++;
        }
        if (verifiable > 0 && verified == verifiable && orphaned == 0)
        {
            Plugin.Log.LogInfo($"[LabsBoiler] grafted area lights VERIFIED ({verified}/{verifiable} renderable) — deactivating native office light branch");
            
            for (var i = 0; i < 1800 && !LabsBoilerLights.NativeBranchHandled; i++)
            {
                try { LabsBoilerLights.TryDeleteNativeOfficeBranch(); }
                catch (Exception e) { Plugin.Log.LogError($"[LabsBoiler] native-branch deactivate failed: {e}"); break; }
                if (!LabsBoilerLights.NativeBranchHandled) yield return null;
            }
        }
        else
        {
            Plugin.Log.LogWarning($"[LabsBoiler] grafted area lights NOT verified ({verified}/{verifiable} renderable, {orphaned} driverless) — " +
                                  "KEEPING the native light branch so the office stays lit (floor-1 may double-light once the graft works)");
        }
    }
}