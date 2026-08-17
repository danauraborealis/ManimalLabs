using System;
using System.Collections;
using System.IO;
using System.Reflection;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.LabsBoiler;

[HarmonyPatch]
internal static class LabsBoilerSwap
{
    private const string OfficeScene = "Laboratory_Office_Above_Boiler_Room_floor_1";
    private const string OfficeSceneMx = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";
    private const string BundleFile = "manimal_labs_boiler.bundle";

    private static AssetBundle _bundle;
    private static bool _bundleTried;

    [HarmonyPatch(typeof(GClass2287), nameof(GClass2287.method_1))]
    [HarmonyPrefix]
    private static void BeforePresetLoad(ScenesPreset preset)
    {
        try
        {
            if (!preset || preset.ServerName != "laboratory") return;
            if (!EnsureBundle()) return;
            RewriteOfficeEntry(preset);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[LabsBoiler] swap prefix failed — native Labs loads unmodified: {e}");
        }
    }

    private static bool EnsureBundle()
    {
        if (_bundle) return true;
        if (_bundleTried) return false;
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
        if (!_bundle)
        {
            Plugin.Log.LogError($"[LabsBoiler] failed to load bundle: {path}");
            return false;
        }
        Plugin.Log.LogInfo($"[LabsBoiler] loaded office bundle in {sw.ElapsedMilliseconds}ms " +
                           $"(scenes: {string.Join(", ", _bundle.GetAllScenePaths())})");
        return true;
    }

    private static FieldInfo _keysField;
    private static FieldInfo _pathField;
    private static FieldInfo _rcidField;

    private static void RewriteOfficeEntry(ScenesPreset preset)
    {
        _keysField ??= AccessTools.Field(typeof(ScenesPreset), "_scenesResourceKeys");
        if (_keysField == null) { Plugin.Log.LogError("[LabsBoiler] ScenesPreset._scenesResourceKeys not found — layout changed"); return; }

        if (_keysField.GetValue(preset) is not IList keys) return;

        foreach (var key in keys)
        {
            if (key == null) continue;
            _pathField ??= AccessTools.Field(key.GetType(), "path");
            _rcidField ??= AccessTools.Field(key.GetType(), "rcid");
            if (_pathField == null) continue;

            var path = _pathField.GetValue(key) as string;
            if (string.IsNullOrEmpty(path)) { continue; }

            if (!path.EndsWith($"/{OfficeScene}.unity", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith($"\\{OfficeScene}.unity", StringComparison.OrdinalIgnoreCase)) { continue; }

            var newPath = path[..^(OfficeScene.Length + ".unity".Length)] + OfficeSceneMx + ".unity";
            
            _pathField.SetValue(key, newPath);
            if (_rcidField != null)
            {
                var rcid = _rcidField.GetValue(key) as string;
                if (!string.IsNullOrEmpty(rcid))
                {
                    _rcidField.SetValue(key, rcid.Replace(OfficeScene, OfficeSceneMx));
                }
            }
            
            Plugin.Log.LogInfo($"[LabsBoiler] swapped office scene entry: {path} -> {newPath}");
            return;
        }
        
        Plugin.Log.LogWarning($"[LabsBoiler] office scene entry '{OfficeScene}' not found in laboratory preset — nothing swapped");
    }
}