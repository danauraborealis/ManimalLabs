using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace Manimal.LabsBoiler;

// the grafted LocationScene's serialized arrays carry rip-nulled slots; EFT tolerates
// them but mods iterating GetAllObjectsAndWhenISayAllIActuallyMeanIt unguarded NRE
// and abort the raid. scrub the fields AND the dictionary copies Awake already built.
internal static class LabsBoilerLocationSceneFix
{
    private const string MxScene = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";

    internal static void OnSceneLoaded(Scene scene)
    {
        if (scene.name != MxScene) return;

        var dictField = AccessTools.Field(typeof(LocationScene), "dictionary_0");
        int scenes = 0, removed = 0;

        foreach (var root in scene.GetRootGameObjects())
        foreach (var ls in root.GetComponentsInChildren<LocationScene>(true))
        {
            scenes++;

            foreach (var f in typeof(LocationScene).GetFields())
            {
                if (!f.FieldType.IsArray) continue;
                var arr = f.GetValue(ls) as Array;
                var compacted = Compact(arr, ref removed);
                if (compacted != null) f.SetValue(ls, compacted);
            }

            if (dictField?.GetValue(ls) is IDictionary dict)
            {
                var keys = new List<object>();
                foreach (var k in dict.Keys) keys.Add(k);
                foreach (var k in keys)
                {
                    var compacted = Compact(dict[k] as Array, ref removed);
                    if (compacted != null) dict[k] = compacted;
                }
            }
        }

        if (scenes == 0)
        {
            Plugin.Log.LogInfo("[LabsBoiler] no LocationScene component in the grafted scene — nothing to scrub");
        }
        else
        {
            Plugin.Log.LogInfo($"[LabsBoiler] LocationScene scrub: {scenes} component(s), {removed} dead slot(s) removed " +
                               "— registry arrays are null-free for unguarded mod iterators");
        }
    }

    // returns a null-free copy, or null when nothing needed removing
    private static Array Compact(Array arr, ref int removed)
    {
        if (arr == null || arr.Rank != 1) return null;
        var dead = 0;
        foreach (var e in arr)
        {
            if (IsDead(e)) dead++;
        }
        if (dead == 0) return null;

        var result = Array.CreateInstance(arr.GetType().GetElementType()!, arr.Length - dead);
        var i = 0;
        foreach (var e in arr)
        {
            if (!IsDead(e)) result.SetValue(e, i++);
        }
        removed += dead;
        return result;
    }

    // == catches rip-nulled slots (true nulls), !uo catches destroyed objects
    private static bool IsDead(object e) => e == null || (e is UnityEngine.Object uo && !uo);
}
