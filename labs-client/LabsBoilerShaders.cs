using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.LabsBoiler
{
    // two fix-ups our _MX scene needs at load, both because the retail rip was lossy:
    //  1. dummy-shader rebind — AssetRipper exported materials on dummy shaders (magenta).
    //     the name-rebind swaps each material's shader for the game's real one of the same
    //     name; native Labs is loaded around us so every Labs shader is available.
    //  2. AreaLight shader-ref copy — the 7 grafted BSG area lights lost their serialized
    //     m_ProxyShader/m_ShadowmapShader/m_BlurShadowmapShader (shader PPtrs the rip nulled).
    //     these aren't material.shader so the rebind can't see them — copy them off a live
    //     native Labs AreaLight instead (deferred until one exists).
    internal static class LabsBoilerShaders
    {
        private const string MxScene = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";

        // emissive-boost experiment: p0 emissive materials carry authored
        // _EmissionVisibility/_EmissionPower; the boost slider multiplies visibility
        // LIVE so "is it a values problem?" is a slider drag, not a UnityExplorer dig.
        private static readonly List<(Material mat, float vis, float pow)> _emissives
            = new List<(Material, float, float)>();

        internal static void ReapplyEmissiveBoost()
        {
            float b = Plugin.EmissiveBoost.Value;
            foreach (var (mat, vis, pow) in _emissives)
            {
                if (mat == null) continue;
                mat.SetFloat("_EmissionVisibility", vis * b);
                mat.SetFloat("_EmissionPower", pow * (b >= 1f ? 1f + (b - 1f) * 0.5f : b));
            }
        }

        internal static void OnSceneLoaded(Scene scene)
        {
            if (scene.name != MxScene) return;
            RebindShaders(scene);
            if (Plugin.Instance != null)
                Plugin.Instance.StartCoroutine(CopyAreaLightShaders(scene));
        }

        private static void RebindShaders(Scene scene)
        {
            int rebound = 0;
            var seen = new HashSet<Material>();
            var misses = new Dictionary<string, int>();
            foreach (var root in scene.GetRootGameObjects())
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null || m.shader == null || !seen.Add(m)) continue;
                        // GClass872.Find is the game's bundle-shader lookup — decal and
                        // other non-built-in shaders (the emissive logos' 'Decal_Ultra
                        // Deferred Decal Of God 3000') only resolve through it
                        var game = Shader.Find(m.shader.name) ?? GClass872.Find(m.shader.name);
                        if (game != null && game != m.shader) { m.shader = game; rebound++; }
                        else if (game == null)
                        {
                            misses.TryGetValue(m.shader.name, out var c);
                            misses[m.shader.name] = c + 1;
                        }
                    }
            Plugin.Log.LogInfo($"[LabsBoiler] rebound {rebound} material(s) to game shaders in {scene.name}");
            // ground truth per the playbook: a shader that never rebinds renders the
            // ripped dummy — name every miss so 'looks wrong' maps to a shader
            foreach (var kv in misses)
                Plugin.Log.LogWarning($"[LabsBoiler] shader NOT rebound ({kv.Value} mat(s)): '{kv.Key}' — renders the ripped dummy");

            // catalogue the emissive materials for the live boost slider
            _emissives.Clear();
            foreach (var m in seen)
            {
                if (m == null || !m.HasProperty("_EmissionVisibility")) continue;
                _emissives.Add((m, m.GetFloat("_EmissionVisibility"),
                                m.HasProperty("_EmissionPower") ? m.GetFloat("_EmissionPower") : 1f));
            }
            ReapplyEmissiveBoost();
            Plugin.Log.LogInfo($"[LabsBoiler] {_emissives.Count} emissive material(s) on the boost slider (EmissiveBoost={Plugin.EmissiveBoost.Value:F2})");
        }

        // the grafted AreaLights render nothing until their proxy shader is set; that shader
        // lives on every native Labs AreaLight. those load in other Labs scenes that may
        // arrive after us, so poll a few frames for a donor before giving up.
        private static IEnumerator CopyAreaLightShaders(Scene scene)
        {
            var alType = AccessTools.TypeByName("AreaLight");
            if (alType == null)
            {
                Plugin.Log.LogWarning("[LabsBoiler] AreaLight type not found — skipping shader copy");
                yield break;
            }
            var proxyF = AccessTools.Field(alType, "m_ProxyShader");
            var smapF = AccessTools.Field(alType, "m_ShadowmapShader");
            var blurF = AccessTools.Field(alType, "m_BlurShadowmapShader");
            if (proxyF == null) { Plugin.Log.LogWarning("[LabsBoiler] AreaLight.m_ProxyShader not found"); yield break; }

            // collect OUR area lights (in _MX, missing the proxy shader)
            var ours = new List<Component>();
            foreach (var root in scene.GetRootGameObjects())
                ours.AddRange(root.GetComponentsInChildren(alType, true));
            if (ours.Count == 0) yield break;

            // no kill switch anymore — it disabled the office's primary light source twice
            // via stale saved configs (08-15, 08-16). the rebuild below is unconditional.
            UnityEngine.Object donor = null;
            for (int frame = 0; frame < 120 && donor == null; frame++)   // ~2s at 60fps
            {
                foreach (var al in UnityEngine.Object.FindObjectsOfType(alType))
                {
                    if (ours.Contains(al as Component)) continue;
                    if (proxyF.GetValue(al) != null) { donor = al; break; }
                }
                if (donor == null) yield return null;
            }
            if (donor == null)
            {
                Plugin.Log.LogWarning("[LabsBoiler] no native AreaLight donor found — grafted area lights stay unlit");
                yield break;
            }

            // copy ONLY the shared authoring refs the rip nulled: the proxy meshes
            // (AreaLight renders NOTHING while m_Quad/m_Cube are null — AreaLight.cs:29/367)
            // and the three shaders. NEVER the greedy everything-null copy from the last
            // build: that dragged in the donor's PRIVATE RUNTIME instances (mesh_0,
            // material_1 — a shared material instance made 16 lights inherit each other's
            // brightness = the random-too-bright regression) and donor-specific authored
            // refs (SourceMaterial/ShadowRendererMaterial) that are legitimately null on ours.
            var copyFields = new List<FieldInfo>();
            foreach (var name in new[] { "m_Quad", "m_Cube", "m_ProxyShader", "m_ShadowmapShader", "m_BlurShadowmapShader" })
            {
                var fi = AccessTools.Field(alType, name);
                if (fi != null) copyFields.Add(fi);
            }

            // REBUILD each area light, don't just patch it: AreaLight creates its draw
            // resources (instantiated quad/cube meshes, proxy material) in Awake — which
            // already ran at scene load with null meshes, and toggling enabled never
            // re-runs Awake, so patched refs still rendered NOTHING (the every-raid-dark
            // mystery). inactive-add rebuild: capture fields, destroy the dead component,
            // add a fresh one on the inactive GO, restore fields + donor refs, reactivate
            // — Awake then runs once with everything present (icebreaker inactive-add
            // pattern). CADLO._light is re-pointed at the fresh component.
            var allFields = new List<FieldInfo>();
            for (var t = alType; t != null && t != typeof(MonoBehaviour) && t != typeof(Behaviour); t = t.BaseType)
                foreach (var fi in AccessTools.GetDeclaredFields(t))
                    if (!fi.IsStatic) allFields.Add(fi);
            var cadloType = AccessTools.TypeByName("CullingAdvancedLightObject");
            var cadloLight = cadloType != null ? AccessTools.Field(cadloType, "_light") : null;

            int rebuilt = 0, repointed = 0, orphaned = 0, shadowFixed = 0;
            var freshOnes = new List<(Component light, bool active)>();
            foreach (var al in ours.ToArray())
            {
                if (al == null) continue;
                var go = al.gameObject;
                bool wasActive = go.activeSelf;
                go.SetActive(false);

                // find the driving CADLO BEFORE destroying: it can live on a PARENT
                // (its Awake does GetComponentInChildren) — the old same-GO-only lookup
                // silently missed those, leaving the CADLO writing intensity into the
                // DESTROYED component forever = fresh light stays m_Intensity 0 = dark
                Component cadlo = null;
                if (cadloType != null)
                    for (var tt = go.transform; tt != null && cadlo == null; tt = tt.parent)
                        cadlo = tt.GetComponent(cadloType);

                var vals = new Dictionary<FieldInfo, object>();
                foreach (var fi in allFields) vals[fi] = fi.GetValue(al);
                UnityEngine.Object.DestroyImmediate(al);

                var fresh = go.AddComponent(alType);
                foreach (var kv in vals)
                {
                    try { kv.Key.SetValue(fresh, kv.Value); } catch { }
                }
                foreach (var fi in copyFields)                       // donor meshes/shaders win over nulls
                    if (fi.GetValue(fresh) == null && fi.GetValue(donor) != null)
                        fi.SetValue(fresh, fi.GetValue(donor));
                // DRIFT-TAIL SANITIZE: the retail extraction's AreaLight fields drift
                // from m_SourceColor onward (denormal floats, random PPtrs, m_Spot/
                // m_Shadows flipped on, m_ShadowmapRes 0 = the every-frame onPreCull
                // crash). a garbage m_Spot clips the light to an ANGLE FRUSTUM instead
                // of its clip box = the horizontal bright/dark seam across the room.
                // reset the whole tail to class defaults — the trustworthy prefix
                // (size/length/depth/clipbox/angle/color/ambient) keeps the retail look.
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
                if (cadlo != null && cadloLight != null) { cadloLight.SetValue(cadlo, fresh); repointed++; }
                else orphaned++;
                go.SetActive(wasActive);
                freshOnes.Add((fresh, wasActive && go.activeInHierarchy));
                rebuilt++;
            }
            Plugin.Log.LogInfo($"[LabsBoiler] REBUILT {rebuilt} grafted area light(s) with donor meshes/shaders from '{((Component)donor).gameObject.name}' " +
                               $"({repointed} CADLO(s) repointed, {orphaned} without a driver, {shadowFixed} drift-tail(s) sanitized)");
            // recatalog the lights driver — it captured the destroyed components
            try { LabsBoilerLights.OnSceneLoaded(scene); } catch { }

            // VERIFY before killing the native branch: the blind delete removed SPT's
            // ~56 working floor-1 area lights while the graft was silently broken —
            // that WAS the all-dark office (user caught the correlation 08-16). a
            // rebuilt light is renderable iff Awake latched its resources (bool_0) and
            // built the proxy material — both readable right now, distance-independent.
            var fBool0 = AccessTools.Field(alType, "bool_0");
            var fMat1 = AccessTools.Field(alType, "material_1");
            int verifiable = 0, verified = 0;
            foreach (var (light, active) in freshOnes)
            {
                if (light == null || !active) continue;   // retail-off lights never Awake — can't verify, don't need to
                verifiable++;
                if (fBool0 != null && (bool)fBool0.GetValue(light) && fMat1?.GetValue(light) != null) verified++;
            }
            if (verifiable > 0 && verified == verifiable && orphaned == 0)
            {
                Plugin.Log.LogInfo($"[LabsBoiler] grafted area lights VERIFIED ({verified}/{verifiable} renderable) — deleting native office light branch");
                try { LabsBoilerLights.TryDeleteNativeOfficeBranch(); }
                catch (Exception e) { Plugin.Log.LogError($"[LabsBoiler] native-branch delete failed: {e}"); }
            }
            else
            {
                Plugin.Log.LogWarning($"[LabsBoiler] grafted area lights NOT verified ({verified}/{verifiable} renderable, {orphaned} driverless) — " +
                                      "KEEPING the native light branch so the office stays lit (floor-1 may double-light once the graft works)");
            }
        }
    }
}
