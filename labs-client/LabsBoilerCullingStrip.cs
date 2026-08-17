using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.LabsBoiler;

internal static class LabsBoilerCullingStrip
{
    private const string MxScene = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";

    internal static void OnSceneLoaded(Scene scene)
    {
        if (scene.name != MxScene) return;
        var stripped = 0;
        var counts = new Dictionary<string, int>();
        foreach (var root in scene.GetRootGameObjects())
        foreach (var comp in root.GetComponentsInChildren<Component>(true))
        {
            if (!comp) continue;
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