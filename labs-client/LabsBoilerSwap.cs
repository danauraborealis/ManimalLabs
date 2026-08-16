using System;
using System.IO;
using System.Reflection;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.LabsBoiler
{
    // THE swap. native Labs loads its ~49 scenes through ScenesPreset._scenesResourceKeys,
    // fed one-by-one into the loader by GClass2287.method_1. we prefix that method: when the
    // laboratory preset comes through, we (1) load our bundle so the _MX scene is registered
    // with SceneManager by name, and (2) rewrite the level74 office entry's path/rcid to our
    // _MX scene name. the loader then LoadSceneAsync's OUR scene instead of the built-in one
    // — built-in scenes and our bundled scene resolve the same way (by name), and the loader's
    // bundle fetch for the entry fails silently exactly as it does for every native entry.
    // native level74 never loads; our _MX (whole floor-1 geometry + floors 2-3 + grafts) takes
    // its place. everything else in Labs is untouched.
    [HarmonyPatch]
    internal static class LabsBoilerSwap
    {
        private const string OfficeScene = "Laboratory_Office_Above_Boiler_Room_floor_1";
        private const string OfficeSceneMx = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";
        private const string BundleFile = "manimal_labs_boiler.bundle";

        private static AssetBundle _bundle;
        private static bool _bundleTried;

        // the instance loader that walks the preset's resource keys
        [HarmonyPatch(typeof(GClass2287), nameof(GClass2287.method_1))]
        [HarmonyPrefix]
        private static void BeforePresetLoad(ScenesPreset preset)
        {
            try
            {
                if (preset == null || preset.ServerName != "laboratory") return;
                if (!EnsureBundle()) return;   // no bundle = leave native Labs untouched
                RewriteOfficeEntry(preset);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[LabsBoiler] swap prefix failed — native Labs loads unmodified: {e}");
            }
        }

        // LoadFromFile is memory-mapped and cheap; the office scene bundle is small. loading
        // it here (first laboratory preset) makes the _MX scene resolvable by name for the
        // LoadSceneAsync the loader is about to issue.
        private static bool EnsureBundle()
        {
            if (_bundle != null) return true;
            if (_bundleTried) return false;   // don't hammer a missing/broken file every raid
            _bundleTried = true;

            var dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            var path = Path.Combine(dir ?? ".", BundleFile);
            if (!File.Exists(path))
            {
                Plugin.Log.LogError($"[LabsBoiler] bundle not found beside the dll: {path} — office expansion disabled");
                return false;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _bundle = AssetBundle.LoadFromFile(path);
            if (_bundle == null)
            {
                Plugin.Log.LogError($"[LabsBoiler] failed to load bundle: {path}");
                return false;
            }
            Plugin.Log.LogInfo($"[LabsBoiler] loaded office bundle in {sw.ElapsedMilliseconds}ms " +
                               $"(scenes: {string.Join(", ", _bundle.GetAllScenePaths())})");
            return true;
        }

        // the preset property ScenesResourceKeys returns a filtered COPY — mutate the backing
        // _scenesResourceKeys list's entry in place so the loader sees the rewrite.
        private static FieldInfo _keysField;
        private static FieldInfo _pathField;
        private static FieldInfo _rcidField;

        private static void RewriteOfficeEntry(ScenesPreset preset)
        {
            _keysField ??= AccessTools.Field(typeof(ScenesPreset), "_scenesResourceKeys");
            if (_keysField == null) { Plugin.Log.LogError("[LabsBoiler] ScenesPreset._scenesResourceKeys not found — layout changed"); return; }

            var keys = _keysField.GetValue(preset) as System.Collections.IList;
            if (keys == null) return;

            foreach (var key in keys)
            {
                if (key == null) continue;
                _pathField ??= AccessTools.Field(key.GetType(), "path");
                _rcidField ??= AccessTools.Field(key.GetType(), "rcid");
                if (_pathField == null) continue;

                var path = _pathField.GetValue(key) as string;
                if (string.IsNullOrEmpty(path)) continue;
                // match the exact office scene, NOT a scene whose name contains it as a prefix
                if (!path.EndsWith($"/{OfficeScene}.unity", StringComparison.OrdinalIgnoreCase)
                    && !path.EndsWith($"\\{OfficeScene}.unity", StringComparison.OrdinalIgnoreCase)) continue;

                var newPath = path.Substring(0, path.Length - (OfficeScene.Length + ".unity".Length))
                              + OfficeSceneMx + ".unity";
                _pathField.SetValue(key, newPath);
                if (_rcidField != null)
                {
                    var rcid = _rcidField.GetValue(key) as string;
                    if (!string.IsNullOrEmpty(rcid))
                        _rcidField.SetValue(key, rcid.Replace(OfficeScene, OfficeSceneMx));
                }
                Plugin.Log.LogInfo($"[LabsBoiler] swapped office scene entry: {path} -> {newPath}");
                return;
            }
            Plugin.Log.LogWarning($"[LabsBoiler] office scene entry '{OfficeScene}' not found in laboratory preset — nothing swapped");
        }
    }
}
