using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.LabsBoiler
{
    // strip Perfect Culling from the swapped scene — the long-planned fallback for
    // the scene-swap's one open culling risk. our scene's PerfectCullingCrossSceneGroup
    // dies with an NRE in Start (PrepareRuntimeContent — the ripped renderer set no
    // longer matches the map-wide bake), and a group that crashed mid-init doesn't
    // log again but can keep mis-toggling renderers/lights per camera cell for the
    // whole raid = the map-wide light flicker (2026-08-16). destroying the components
    // at sceneLoaded (which fires BEFORE Start) prevents the broken init entirely;
    // cost: the office renderers are simply always-on, which is cheap.
    internal static class LabsBoilerCullingStrip
    {
        private const string MxScene = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";

        internal static void OnSceneLoaded(Scene scene)
        {
            if (scene.name != MxScene) return;
            int stripped = 0;
            var counts = new Dictionary<string, int>();
            foreach (var root in scene.GetRootGameObjects())
                foreach (var comp in root.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null) continue;
                    var ns = comp.GetType().Namespace ?? "";
                    if (!ns.StartsWith("Koenigz.PerfectCulling")) continue;
                    counts.TryGetValue(comp.GetType().Name, out var c);
                    counts[comp.GetType().Name] = c + 1;
                    Object.DestroyImmediate(comp);
                    stripped++;
                }
            Plugin.Log.LogInfo($"[LabsBoiler] stripped {stripped} PerfectCulling component(s) from {MxScene} " +
                               $"({string.Join(", ", FormatCounts(counts))}) — office renders unmanaged, broken cross-scene group never initializes");
        }

        private static IEnumerable<string> FormatCounts(Dictionary<string, int> counts)
        {
            foreach (var kv in counts) yield return $"{kv.Key}x{kv.Value}";
        }
    }
}
