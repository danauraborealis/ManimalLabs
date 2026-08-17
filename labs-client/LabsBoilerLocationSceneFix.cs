using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace Manimal.LabsBoiler;

// the grafted scene's LocationScene ships serialized object arrays with rip-nulled
// slots (refs whose targets didnt survive extraction). EFT's own consumers shrug
// those off, but LocationScene.GetAllObjectsAndWhenISayAllIActuallyMeanIt hands the
// raw arrays to any mod that asks, and e.g. SkillsExtended's FixDoors dereferences
// each element unguarded — one null slot = NRE inside GameWorld.OnGameStarted =
// raid aborted with the bare "Object reference not set" dialog (found 2026-08-17
// via the error-screen probe). compact both the serialized fields and the
// Awake-built dictionary copies — Awake stores the same references via method_0,
// and it has already run by sceneLoaded, so the two must be scrubbed in step.
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

            // serialized public arrays first (native code can re-read them later)
            foreach (var f in typeof(LocationScene).GetFields())
            {
                if (!f.FieldType.IsArray) continue;
                var arr = f.GetValue(ls) as Array;
                var compacted = Compact(arr, ref removed);
                if (compacted != null) f.SetValue(ls, compacted);
            }

            // then the registry dictionary Awake built from them
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

    // static type is object, so == is plain reference equality (true for rip-nulled
    // serialized slots); the typed !uo check catches destroyed-but-referenced objects
    private static bool IsDead(object e) => e == null || (e is UnityEngine.Object uo && !uo);
}
