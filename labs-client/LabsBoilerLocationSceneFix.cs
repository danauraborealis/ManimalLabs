using System;
using System.Collections;
using System.Collections.Generic;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace Manimal.LabsBoiler;

// rip-damaged registry arrays: null slots NRE in unguarded mod iterators, and doors
// missing from them never reach World's registry so fika cant sync them. rebuild from
// the live scene and scrub the rest, updating the dictionary_0 copies Awake built.
internal static class LabsBoilerLocationSceneFix
{
    private const string MxScene = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";

    internal static void OnSceneLoaded(Scene scene)
    {
        if (scene.name != MxScene) return;

        var wios = new List<WorldInteractiveObject>();
        var containers = new List<LootableContainer>();
        foreach (var root in scene.GetRootGameObjects())
        {
            wios.AddRange(root.GetComponentsInChildren<WorldInteractiveObject>(true));
            containers.AddRange(root.GetComponentsInChildren<LootableContainer>(true));
        }

        // rip-lost Ids/NetIds get deterministic values so host and client tables agree
        wios.Sort((a, b) => string.CompareOrdinal(SortKey(a), SortKey(b)));
        int nextNet = 910000, named = 0, renumbered = 0;
        for (var i = 0; i < wios.Count; i++)
        {
            if (string.IsNullOrEmpty(wios[i].Id)) { wios[i].Id = $"labsboiler_wio_{i}"; named++; }
            if (wios[i].NetId == 0) { wios[i].NetId = nextNet++; renumbered++; }
        }

        var dictField = AccessTools.Field(typeof(LocationScene), "dictionary_0");
        int scenes = 0, removed = 0;
        foreach (var root in scene.GetRootGameObjects())
        foreach (var ls in root.GetComponentsInChildren<LocationScene>(true))
        {
            scenes++;
            var dict = dictField?.GetValue(ls) as IDictionary;

            // only the first LocationScene carries the rebuilt arrays — duplicates
            // across components would double-register every Id
            if (scenes == 1)
            {
                ls.WorldInteractiveObjects = wios.ToArray();
                ls.LootableContainers = containers.ToArray();
                if (dict != null)
                {
                    dict[typeof(WorldInteractiveObject)] = ls.WorldInteractiveObjects;
                    dict[typeof(LootableContainer)] = ls.LootableContainers;
                }
            }

            foreach (var f in typeof(LocationScene).GetFields())
            {
                if (!f.FieldType.IsArray) continue;
                var compacted = Compact(f.GetValue(ls) as Array, ref removed);
                if (compacted != null) f.SetValue(ls, compacted);
            }
            if (dict == null) continue;
            var keys = new List<object>();
            foreach (var k in dict.Keys) keys.Add(k);
            foreach (var k in keys)
            {
                var compacted = Compact(dict[k] as Array, ref removed);
                if (compacted != null) dict[k] = compacted;
            }
        }

        Plugin.Log.LogInfo($"[LabsBoiler] LocationScene registry rebuilt: {wios.Count} interactives ({named} ids assigned, " +
                           $"{renumbered} netids assigned), {containers.Count} containers, {removed} dead slots scrubbed across {scenes} component(s)");
    }

    private static string SortKey(WorldInteractiveObject wio)
    {
        var path = wio.Id ?? "";
        for (var t = wio.transform; t; t = t.parent) path = t.name + "/" + path;
        return path;
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
