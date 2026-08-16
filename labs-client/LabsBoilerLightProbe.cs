using System.Collections;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.LabsBoiler
{
    // diagnostic: dumps the LIVE state of every gate in the area-light chain so a
    // "still dark" report maps to an exact culprit. v2: PROXIMITY-triggered — the
    // fixed 10/40/100s schedule sampled while the player was 90m away, where
    // vis=False is correct and proves nothing. now dumps only within 32m of the
    // office, where the lights MUST be visible and driven. also logs the shadow
    // fields (round-10 culprit: m_Shadows=true + m_ShadowmapRes=0 threw in
    // onPreCull every frame and killed all later light draws).
    internal static class LabsBoilerLightProbe
    {
        private const string MxScene = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";
        private static readonly Vector3 OfficeCenter = new Vector3(-263f, 5.5f, -374f);

        internal static void OnSceneLoaded(Scene scene)
        {
            if (scene.name != MxScene || Plugin.Instance == null) return;
            Plugin.Instance.StartCoroutine(Probe());
        }

        private static IEnumerator Probe()
        {
            for (int i = 0; i < 3600 && !Comfort.Common.Singleton<EFT.GameWorld>.Instantiated; i++) yield return null;
            var fBool0 = AccessTools.Field(typeof(AreaLight), "bool_0");
            var fMat1 = AccessTools.Field(typeof(AreaLight), "material_1");
            var fLight = AccessTools.Field(typeof(CullingAdvancedLightObject), "_light");
            var fF1 = AccessTools.Field(typeof(CullingAdvancedLightObject), "float_1");
            var fMax = AccessTools.Field(typeof(CullingAdvancedLightObject), "_maxLightIntensity");
            var fCull = AccessTools.Field(typeof(CullingObject), "CullDistance");

            int dumps = 0;
            while (dumps < 8)
            {
                yield return new WaitForSeconds(2f);
                var cam = Camera.main;
                if (cam == null) continue;
                if (Vector3.Distance(cam.transform.position, OfficeCenter) > 32f) continue;

                var sb = new StringBuilder("[LabsBoiler] LIGHT PROBE (in office)\n");
                int lit = 0, total = 0;
                try
                {
                    foreach (var al in Object.FindObjectsOfType<AreaLight>(true))
                    {
                        if (al == null || al.gameObject.scene.name != MxScene) continue;
                        total++;
                        CullingAdvancedLightObject cadlo = null;
                        for (var t = al.transform; t != null && cadlo == null; t = t.parent)
                            cadlo = t.GetComponent<CullingAdvancedLightObject>();

                        float dist = Vector3.Distance(cam.transform.position, al.transform.position);
                        if (al.m_Intensity > 0f) lit++;
                        sb.Append($"  {al.gameObject.name}: mInt={al.m_Intensity:F2} res={(bool)fBool0.GetValue(al)} " +
                                  $"mat1={fMat1.GetValue(al) != null} shadows={al.m_Shadows}/{(int)al.m_ShadowmapRes} " +
                                  $"dist={dist:F0}m");
                        if (cadlo != null)
                        {
                            var ptr = fLight.GetValue(cadlo) as Object;
                            sb.Append($" | cadlo vis={cadlo.IsVisible} f1={(float)fF1.GetValue(cadlo):F2} " +
                                      $"max={(float)fMax.GetValue(cadlo):F2} cullD={(float)fCull.GetValue(cadlo):F0} " +
                                      $"light={(ReferenceEquals(ptr, al) ? "OK" : (ptr == null ? "NULL" : "STALE"))}");
                        }
                        else sb.Append(" | cadlo MISSING");
                        sb.Append('\n');
                    }
                    sb.Append($"  verdict: {lit}/{total} with m_Intensity>0");
                    Plugin.Log.LogInfo(sb.ToString());
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogError($"[LabsBoiler] light probe dump failed: {e.Message}");
                    yield break;
                }
                dumps++;
                yield return new WaitForSeconds(12f);
            }
        }
    }
}
