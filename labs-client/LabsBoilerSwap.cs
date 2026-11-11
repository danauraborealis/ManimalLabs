using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using ZLinq;

namespace Manimal.LabsBoiler;

internal sealed class LabsBoilerSwap : ModulePatch
{
    private const string OfficeScene = "Laboratory_Office_Above_Boiler_Room_floor_1";
    private const string OfficeSceneMx = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";
    private const string BundleFile = "manimal_labs_boiler.bundle";

    private static AssetBundle _bundle;
    private static bool _bundleTried;

    internal LabsBoilerSwap() : base(BuildInfo.ModGuid + "." + typeof(LabsBoilerSwap).FullName) { }

    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(LoadScenesFromPresetOperation), nameof(LoadScenesFromPresetOperation.LoadPresetAsync),
            [typeof(ScenesPreset)]);

    [PatchPrefix]
    private static bool BeforePresetLoad(LoadScenesFromPresetOperation __instance, ScenesPreset preset, ref Task __result, out bool __state)
    {
        __state = false;
        if (Plugin.IsShuttingDown) return true;
        if (preset == null || !string.Equals(preset.ServerName, "laboratory", StringComparison.OrdinalIgnoreCase)) return true;

        // Native child presets do not contain the office scene entry. Let those
        // recursive loads proceed while the accepted top-level operation remains
        // the sole owner of the scoped patches.
        if (!HasOfficeEntry(preset)) return true;

        if (!Plugin.CanAcceptLaboratoryPreset(preset, out var overlapReason))
        {
            return Plugin.RejectPresetLoad(__instance, ref __result, overlapReason);
        }

        var customOfficeAccepted = false;
        var prepared = false;
        try
        {
            if (EnsureBundle())
            {
                // Acquire the lifecycle host and dynamic hooks before mutating
                // the shared preset. A failed preparation must leave native Labs
                // keys untouched so the loader cannot enter an unprotected MX
                // scene.
                prepared = Plugin.TryPrepareLaboratoryPreset(preset, reserveForOperation: true);
                if (prepared)
                {
                    customOfficeAccepted = RewriteOfficeEntry(preset);
                    if (!customOfficeAccepted)
                    {
                        Plugin.RejectLaboratoryPreset(preset, "laboratory office entry not accepted");
                    }
                }
            }
        }
        catch (Exception e)
        {
            if (prepared) Plugin.RejectLaboratoryPreset(preset, "laboratory preset preparation failed");
            Plugin.Log.LogError($"[LabsBoiler] swap prefix failed — native Labs loads unmodified: {e}");
        }

        if (!customOfficeAccepted)
        {
            // Keep the bootstrap alive for the next raid, and release any prior
            // custom raid that this native preset supersedes.
            var nativeEntryRestored = true;
            try { nativeEntryRestored = RestoreOfficeEntry(preset); }
            catch (Exception e)
            {
                nativeEntryRestored = false;
                Plugin.Log.LogError($"[LabsBoiler] native office restore failed: {e}");
            }
            try
            {
                Plugin.ObserveLaboratoryPreset(preset, false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[LabsBoiler] preset lifecycle setup failed — native Labs loads unmodified: {e}");
            }

            // A failed restore must not let the original loader enter a possibly
            // still-mutated MX scene without the dynamic hooks.
            if (!nativeEntryRestored)
            {
                return Plugin.RejectPresetLoad(__instance, ref __result, "native office restore failed");
            }

            return true;
        }

        __state = true;
        return true;
    }

    [PatchPostfix]
    private static void AfterPresetLoad(LoadScenesFromPresetOperation __instance, ScenesPreset preset, Task __result, bool __state)
    {
        if (__state && Plugin.IsActiveLaboratoryPreset(preset))
        {
            Plugin.WatchPresetOperation(__instance, __result, preset);
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

    private static bool HasOfficeEntry(ScenesPreset preset)
    {
        try
        {
            _keysField ??= AccessTools.Field(typeof(ScenesPreset), "_scenesResourceKeys");
            if (_keysField == null || _keysField.GetValue(preset) is not IList keys) return false;

            return ((IEnumerable)keys).AsValueEnumerable().Any(IsOfficeEntry);
        }
        catch (Exception e)
        {
            Plugin.Log?.LogError($"[LabsBoiler] could not inspect laboratory preset keys: {e}");
        }

        return false;
    }

    private static bool IsOfficeEntry(object key)
    {
        if (key == null) return false;
        _pathField ??= AccessTools.Field(key.GetType(), "path");
        if (_pathField == null) return false;

        var path = _pathField.GetValue(key) as string;
        return path != null
            && (path.EndsWith($"/{OfficeScene}.unity", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith($"\\{OfficeScene}.unity", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith($"/{OfficeSceneMx}.unity", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith($"\\{OfficeSceneMx}.unity", StringComparison.OrdinalIgnoreCase));
    }

    private static bool RewriteOfficeEntry(ScenesPreset preset)
    {
        _keysField ??= AccessTools.Field(typeof(ScenesPreset), "_scenesResourceKeys");
        if (_keysField == null) { Plugin.Log.LogError("[LabsBoiler] ScenesPreset._scenesResourceKeys not found — layout changed"); return false; }

        if (_keysField.GetValue(preset) is not IList keys) return false;

        foreach (var key in keys)
        {
            if (key == null) continue;
            _pathField ??= AccessTools.Field(key.GetType(), "path");
            _rcidField ??= AccessTools.Field(key.GetType(), "rcid");
            if (_pathField == null) continue;

            var path = _pathField.GetValue(key) as string;
            if (string.IsNullOrEmpty(path)) { continue; }

            if (path.EndsWith($"/{OfficeSceneMx}.unity", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith($"\\{OfficeSceneMx}.unity", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!path.EndsWith($"/{OfficeScene}.unity", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith($"\\{OfficeScene}.unity", StringComparison.OrdinalIgnoreCase)) { continue; }

            var newPath = path[..^(OfficeScene.Length + ".unity".Length)] + OfficeSceneMx + ".unity";
            
            var originalRcid = _rcidField?.GetValue(key);
            try
            {
                _pathField.SetValue(key, newPath);
                if (_rcidField != null)
                {
                    var rcid = originalRcid as string;
                    if (!string.IsNullOrEmpty(rcid))
                    {
                        _rcidField.SetValue(key, rcid.Replace(OfficeScene, OfficeSceneMx));
                    }
                }
            }
            catch
            {
                _pathField.SetValue(key, path);
                if (_rcidField != null && originalRcid != null) _rcidField.SetValue(key, originalRcid);
                throw;
            }
            
            Plugin.Log.LogInfo($"[LabsBoiler] swapped office scene entry: {path} -> {newPath}");
            return true;
        }
        
        Plugin.Log.LogWarning($"[LabsBoiler] office scene entry '{OfficeScene}' not found in laboratory preset — nothing swapped");
        return false;
    }

    private static bool RestoreOfficeEntry(ScenesPreset preset)
    {
        if (_keysField == null || _keysField.GetValue(preset) is not IList keys) return true;

        foreach (var key in keys)
        {
            if (key == null) continue;
            _pathField ??= AccessTools.Field(key.GetType(), "path");
            _rcidField ??= AccessTools.Field(key.GetType(), "rcid");
            if (_pathField == null) return false;

            var path = _pathField.GetValue(key) as string;
            if (string.IsNullOrEmpty(path)
                || (!path.EndsWith($"/{OfficeSceneMx}.unity", StringComparison.OrdinalIgnoreCase)
                    && !path.EndsWith($"\\{OfficeSceneMx}.unity", StringComparison.OrdinalIgnoreCase))) continue;

            _pathField.SetValue(key, path.Replace(OfficeSceneMx, OfficeScene));
            if (_rcidField != null)
            {
                var rcid = _rcidField.GetValue(key) as string;
                if (!string.IsNullOrEmpty(rcid)) _rcidField.SetValue(key, rcid.Replace(OfficeSceneMx, OfficeScene));
            }

            Plugin.Log.LogInfo($"[LabsBoiler] restored native office scene entry after rejected custom preset: {path}");
        }

        return true;
    }
}
