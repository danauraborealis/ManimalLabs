using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZLinq;

namespace Manimal.LabsBoiler;

[BepInPlugin(BuildInfo.ModGuid, BuildInfo.ClientName, BuildInfo.Version)]
[BepInDependency("com.arys.unitytoolkit")]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static Plugin Instance;

    internal static ConfigEntry<float> LampIntensity;
    internal static ConfigEntry<float> LampMaxIntensity;
    internal static ConfigEntry<bool> LampForceOn;
    internal static ConfigEntry<float> EmissiveBoost;

    internal static ConfigEntry<float> ProxyInteractX;
    internal static ConfigEntry<float> ProxyInteractY;
    internal static ConfigEntry<float> ProxyInteractZ;

    internal static LabsBoilerSwap SwapPatch;
    internal static LabsBoilerAcoustics AcousticsPatch;
    internal static LabsBoilerBakeMerge BakeMergePatch;

    private static readonly HashSet<int> LoadedLabsScenes = [];
    private static readonly HashSet<Coroutine> OwnedCoroutines = [];
    private static LifecycleHost _lifecycleHost;
    private static ScenesPreset _activePreset;
    private static LoadScenesFromPresetOperation _watchedOperation;
    private static Task _watchedTask;
    private static int _watchedGeneration;
    private static int _presetGeneration;
    private static bool _presetLoading;
    private static bool _presetAwaitingOperation;
    private static bool _presetOperationPending;
    private static bool _labsPresetActive;
    // Tracks accepted Labs work before dynamic wrappers report IsActive. This
    // keeps idle shutdown from touching never-acquired raid state while still
    // allowing partial-enable rollback.
    private static bool _raidCleanupOwned;
    private static bool _raidResourcesReset;
    private static bool _acousticsTeardownComplete;
    private static bool _bakeMergeTeardownComplete;
    private static bool _sceneHandlerWorkOwned;
    private static bool _destroying;
    private static bool _sceneHandlersAttached;
    private static bool _swapPatchTeardownDone;

    private const string LabsHandlerSceneName = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";

    private void Awake()
    {
        Log = Logger;
        Instance = this;
        _destroying = false;
        _raidCleanupOwned = false;
        _raidResourcesReset = false;
        _acousticsTeardownComplete = false;
        _bakeMergeTeardownComplete = false;
        _sceneHandlerWorkOwned = false;
        _swapPatchTeardownDone = false;

        LampIntensity = Config.Bind("Lights", "LampIntensityScale", 1.0f,
            new ConfigDescription("multiplier on the revived office lamp brightness (live)",
                new AcceptableValueRange<float>(0f, 4f)));
        
        LampMaxIntensity = Config.Bind("Lights", "LampBrightnessCap", 8.0f,
            new ConfigDescription("brightness ceiling. 8 = no clamp = exactly retail; lower it to cap the brightest lamps (live)",
                new AcceptableValueRange<float>(0.5f, 8f)));
        
        LampForceOn = Config.Bind("Lights", "ForceAllLampsOn", false,
            new ConfigDescription("light EVERY office lamp, including the ~36 retail leaves off — brighter than retail (set at raid start)"));
        LampIntensity.SettingChanged += (_, _) => { try { LabsBoilerLights.ReapplyIntensity(); } catch(Exception e) { Log.LogError(e); } };
        LampMaxIntensity.SettingChanged += (_, _) => { try { LabsBoilerLights.ReapplyIntensity(); } catch(Exception e) { Log.LogError(e); } };
        EmissiveBoost = Config.Bind("Lights", "EmissiveBoost", 1.0f,
            new ConfigDescription("multiplier on the office emissive materials' _EmissionVisibility (logos/monitors/LEDs). 1 = retail-authored. drag it LIVE in-raid: if the glow appears at 2-4, retail's look needs a boost here; if NOTHING changes, the emission path isn't rendering at all",
                new AcceptableValueRange<float>(0.25f, 8f)));
        EmissiveBoost.SettingChanged += (_, _) => { try { LabsBoilerShaders.ReapplyEmissiveBoost(); } catch(Exception e) { Log.LogError(e); } };

        ProxyInteractX = Config.Bind("Keycard", "SwipeAnchorX", 0f,
            new ConfigDescription("keycard swipe interaction anchor, proxy-local X (0 = at the reader; retail was 0.81 = off to the side) (live)",
                new AcceptableValueRange<float>(-2f, 2f)));
        ProxyInteractY = Config.Bind("Keycard", "SwipeAnchorY", 0f,
            new ConfigDescription("keycard swipe anchor, proxy-local Y (live)", new AcceptableValueRange<float>(-2f, 2f)));
        ProxyInteractZ = Config.Bind("Keycard", "SwipeAnchorZ", 0f,
            new ConfigDescription("keycard swipe anchor, proxy-local Z (live)", new AcceptableValueRange<float>(-2f, 2f)));
        
        ProxyInteractX.SettingChanged += (_, _) => { try { LabsBoilerProxies.Reapply(); } catch(Exception e) { Log.LogError(e); } };
        ProxyInteractY.SettingChanged += (_, _) => { try { LabsBoilerProxies.Reapply(); } catch(Exception e) { Log.LogError(e); } };
        ProxyInteractZ.SettingChanged += (_, _) => { try { LabsBoilerProxies.Reapply(); } catch(Exception e) { Log.LogError(e); } };

        EnsureLifecycleHost();
        AttachSceneHandlers();

        try
        {
            SwapPatch = new LabsBoilerSwap();
            SwapPatch.Enable();
        }
        catch (Exception e)
        {
            Log.LogError($"[LabsBoiler] preset bootstrap patch failed: {e}");
            DisableSwapPatch();
        }

        try
        {
            AcousticsPatch = new LabsBoilerAcoustics();
            BakeMergePatch = new LabsBoilerBakeMerge();
        }
        catch (Exception e)
        {
            Log.LogError($"[LabsBoiler] audio patch construction failed: {e}");
        }

        Log.LogInfo($"Manimal-LabsBoiler {BuildInfo.Version} loaded");
    }

    internal static Coroutine StartOwnedCoroutine(IEnumerator routine)
    {
        if (routine == null) return null;
        MarkRaidWorkOwned();
        var host = _lifecycleHost;
        if (host == null && !_destroying) host = EnsureLifecycleHost();
        if (host == null) return null;
        try
        {
            var coroutine = host.StartCoroutine(routine);
            if (coroutine != null) OwnedCoroutines.Add(coroutine);
            return coroutine;
        }
        catch (Exception e)
        {
            Log?.LogError($"[LabsBoiler] owned coroutine start failed: {e}");
            return null;
        }
    }

    private static void StopOwnedCoroutines()
    {
        if (_lifecycleHost != null)
        {
            foreach (var coroutine in OwnedCoroutines)
            {
                if (coroutine != null) _lifecycleHost.StopCoroutine(coroutine);
            }
        }

        OwnedCoroutines.Clear();
    }

    private static LifecycleHost EnsureLifecycleHost()
    {
        if (_lifecycleHost != null) return _lifecycleHost;

        try
        {
            var go = new GameObject("ManimalLabsBoiler.Lifecycle");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _lifecycleHost = go.AddComponent<LifecycleHost>();
            if (_lifecycleHost == null) Log?.LogError("[LabsBoiler] lifecycle host component could not be created");
        }
        catch (Exception e)
        {
            Log?.LogError($"[LabsBoiler] lifecycle host creation failed: {e}");
        }

        return _lifecycleHost;
    }

    private static void AttachSceneHandlers()
    {
        if (_sceneHandlersAttached) return;
        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
        _sceneHandlersAttached = true;
    }

    private static void DetachSceneHandlers()
    {
        if (!_sceneHandlersAttached) return;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        _sceneHandlersAttached = false;
    }

    internal static void ObserveLaboratoryPreset(ScenesPreset preset, bool customOfficeAccepted)
    {
        if (preset == null || _destroying) return;

        if (!customOfficeAccepted)
        {
            // Keep this bootstrap path alive for the next raid, but never leave an
            // earlier raid's dynamic audio patches active while native Labs loads.
            if (_labsPresetActive && !ReferenceEquals(_activePreset, preset))
            {
                ReleaseLabs("native Labs preset observed");
            }

            return;
        }

        TryPrepareLaboratoryPreset(preset);
    }

    internal static bool TryPrepareLaboratoryPreset(ScenesPreset preset, bool reserveForOperation = false)
    {
        if (preset == null || _destroying) return false;
        if (!_labsPresetActive && HasRaidCleanupOwnership())
        {
            Log?.LogWarning("[LabsBoiler] laboratory preset rejected while prior Labs-owned state is still being released");
            return false;
        }

        if (EnsureLifecycleHost() == null)
        {
            Log?.LogError("[LabsBoiler] laboratory preset rejected: lifecycle host is unavailable");
            return false;
        }

        if (!_labsPresetActive || !ReferenceEquals(_activePreset, preset))
        {
            if (_labsPresetActive)
            {
                ReleaseLabs("new laboratory preset observed");
                if (HasRaidCleanupOwnership()) return false;
            }

            _activePreset = preset;
            _labsPresetActive = true;
            BeginRaidCleanupOwnership();
            _presetGeneration++;
        }
        else
        {
            _raidCleanupOwned = true;
        }

        _presetLoading = true;
        _presetAwaitingOperation = reserveForOperation;
        if (!EnableAudioPatches())
        {
            ReleaseLabs("dynamic audio patch activation failure");
            return false;
        }

        return true;
    }

    internal static void RejectLaboratoryPreset(ScenesPreset preset, string reason)
    {
        if (IsActiveLaboratoryPreset(preset)) ReleaseLabs(reason);
    }

    internal static bool IsActiveLaboratoryPreset(ScenesPreset preset) =>
        _labsPresetActive && ReferenceEquals(_activePreset, preset);

    internal static bool IsShuttingDown => _destroying;

    internal static bool CanAcceptLaboratoryPreset(ScenesPreset preset, out string reason)
    {
        if (_destroying)
        {
            reason = "plugin is shutting down";
            return false;
        }

        if (HasRaidCleanupOwnership())
        {
            reason = "another laboratory preset load, scene, or Labs-owned helper state is still owned";
            return false;
        }

        reason = null;
        return true;
    }

    internal static bool RejectPresetLoad(LoadScenesFromPresetOperation operation, ref Task result, string reason)
    {
        try
        {
            operation?.SetFailed(reason);
        }
        catch (Exception e)
        {
            Log?.LogError($"[LabsBoiler] failed to mark rejected preset load terminal: {e}");
        }

        result = Task.CompletedTask;
        Log?.LogWarning($"[LabsBoiler] rejected overlapping laboratory preset load: {reason}");
        return false;
    }

    private static bool EnableAudioPatches()
    {
        var success = true;
        try
        {
            if (AcousticsPatch == null) AcousticsPatch = new LabsBoilerAcoustics();
            if (!AcousticsPatch.IsActive) AcousticsPatch.Enable();
        }
        catch (Exception e)
        {
            Log.LogError($"[LabsBoiler] acoustics patch enable failed: {e}");
            success = false;
        }

        if (success)
        {
            try
            {
                if (BakeMergePatch == null) BakeMergePatch = new LabsBoilerBakeMerge();
                if (!BakeMergePatch.IsActive) BakeMergePatch.Enable();
            }
            catch (Exception e)
            {
                Log.LogError($"[LabsBoiler] bake-merge patch enable failed: {e}");
                success = false;
            }
        }

        if (!success)
        {
            // A ModulePatch can install its hook before reporting failure. Always
            // attempt both wrappers so a partial activation cannot leak into the
            // next raid, even when IsActive is already false.
            DisableAudioPatches();
        }

        return success;
    }

    private static bool DisableAudioPatches()
    {
        if (!_bakeMergeTeardownComplete)
        {
            try
            {
                BakeMergePatch?.Disable();
                _bakeMergeTeardownComplete = true;
            }
            catch (Exception e)
            {
                Log?.LogError($"[LabsBoiler] bake-merge patch disable failed: {e}");
            }
        }

        if (!_acousticsTeardownComplete)
        {
            try
            {
                AcousticsPatch?.Disable();
                _acousticsTeardownComplete = true;
            }
            catch (Exception e)
            {
                Log?.LogError($"[LabsBoiler] acoustics patch disable failed: {e}");
            }
        }

        return _bakeMergeTeardownComplete && _acousticsTeardownComplete;
    }

    internal static void WatchPresetOperation(LoadScenesFromPresetOperation operation, Task task, ScenesPreset preset)
    {
        if (!IsActiveLaboratoryPreset(preset) || task == null) return;
        if (EnsureLifecycleHost() == null)
        {
            Log?.LogError("[LabsBoiler] laboratory completion monitor unavailable; retaining raid-owned state");
            return;
        }

        var generation = _presetGeneration;
        _watchedOperation = operation;
        _watchedTask = task;
        _watchedGeneration = generation;
        _presetAwaitingOperation = false;
        _presetOperationPending = true;
    }

    private static void PollPresetOperation()
    {
        if (!_presetOperationPending || _watchedTask == null || !_watchedTask.IsCompleted) return;

        var operation = _watchedOperation;
        var task = _watchedTask;
        var generation = _watchedGeneration;
        _watchedOperation = null;
        _watchedTask = null;
        CompletePresetOperation(operation, task, generation);
    }

    private static void CompletePresetOperation(LoadScenesFromPresetOperation operation, Task task, int generation)
    {
        if (!_labsPresetActive || generation != _presetGeneration) return;
        _presetOperationPending = false;
        _presetLoading = false;
        RegisterLoadedLabsScenes();

        var failed = task.IsCanceled || task.IsFaulted || operation == null || operation.IsCanceled || !operation.Succeed;
        if (failed)
        {
            var detail = operation?.Error;
            if (string.IsNullOrEmpty(detail) && task.Exception != null) detail = task.Exception.GetBaseException().Message;
            Log.LogWarning($"[LabsBoiler] laboratory preset load did not complete ({detail ?? "unknown error"})");
        }

        // Failed/cancelled loads can leave zero scenes behind; successful loads with
        // no activated Labs scene are equally unable to use the dynamic patches.
        if (LoadedLabsScenes.Count == 0)
        {
            ReleaseLabs(failed ? "preset load failure/cancel" : "preset completed without Labs scenes");
        }
    }

    private static bool IsLabsScene(Scene scene) =>
        scene.IsValid() && (string.Equals(scene.name, "Laboratory", StringComparison.OrdinalIgnoreCase)
                            || scene.name.StartsWith("Laboratory_", StringComparison.OrdinalIgnoreCase));

    private static void RegisterLoadedLabsScenes()
    {
        if (!_labsPresetActive) return;

        var handles = ValueEnumerable.Range(0, SceneManager.sceneCount)
            .Select(SceneManager.GetSceneAt)
            .Where(IsLabsScene)
            .Select(scene => scene.handle)
            .ToArray();
        LoadedLabsScenes.UnionWith(handles);
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (_destroying && !_labsPresetActive) return;

        // During a preset load, ownership is assigned once the operation reaches a
        // terminal state. This prevents a reentrant prior raid's late sceneLoaded
        // event from being attributed to the new preset.
        if (_labsPresetActive && !_presetLoading && IsLabsScene(scene)) LoadedLabsScenes.Add(scene.handle);

        // Scene helpers can acquire and mutate resources synchronously before
        // their follow-up coroutines are registered. Track that ownership before
        // dispatch so a failed coroutine start cannot make idle teardown skip it.
        MarkSceneHandlerOwnership(scene);

        try { LabsBoilerCullingStrip.OnSceneLoaded(scene); }
        catch (Exception e) { Log.LogError($"[LabsBoiler] culling strip failed: {e}"); }

        try { LabsBoilerLocationSceneFix.OnSceneLoaded(scene); }
        catch (Exception e) { Log.LogError($"[LabsBoiler] LocationScene scrub failed: {e}"); }

        try { LabsBoilerShaders.OnSceneLoaded(scene); }
        catch (Exception e) { Log.LogError($"[LabsBoiler] shader handler failed: {e}"); }

        try { LabsBoilerLights.OnSceneLoaded(scene); }
        catch (Exception e) { Log.LogError($"[LabsBoiler] light-revival handler failed: {e}"); }

        try { LabsBoilerProxies.OnSceneLoaded(scene); }
        catch (Exception e) { Log.LogError($"[LabsBoiler] proxy-anchor handler failed: {e}"); }
    }

    private static void OnSceneUnloaded(Scene scene)
    {
        if (!_labsPresetActive)
        {
            if (_sceneHandlerWorkOwned && IsLabsHandlerScene(scene))
            {
                ReleaseLabs("owned Labs scene handlers unloaded");
            }

            return;
        }

        if (!IsLabsScene(scene)) return;
        // A reentrant preset can overlap teardown of the previous raid. Do not let
        // an old scene's unload event release the new preset's dynamic patches.
        if (!LoadedLabsScenes.Remove(scene.handle)) return;
        if (LoadedLabsScenes.Count == 0 && !_presetLoading)
        {
            ReleaseLabs("last owned/native Labs scene unloaded");
        }
    }

    internal static void ReleaseLabs(string reason)
    {
        if (!HasRaidCleanupOwnership())
        {
            if (_destroying) FinalizeShutdown();
            return;
        }

        StopOwnedCoroutines();
        var audioPatchesDisabled = DisableAudioPatches();

        if (!_raidResourcesReset)
        {
            LabsBoilerAcoustics.Reset();
            LabsBoilerLights.Reset();
            LabsBoilerShaders.Reset();
            LabsBoilerProxies.Reset();
            _raidResourcesReset = true;
        }

        LoadedLabsScenes.Clear();
        _activePreset = null;
        _watchedOperation = null;
        _watchedTask = null;
        _watchedGeneration = 0;
        _labsPresetActive = false;
        _presetLoading = false;
        _presetAwaitingOperation = false;
        _presetOperationPending = false;
        _presetGeneration++;
        _sceneHandlerWorkOwned = false;
        _raidCleanupOwned = !audioPatchesDisabled;
        Log?.LogInfo($"[LabsBoiler] released raid-owned state: {reason}");

        if (_destroying) FinalizeShutdown();
    }

    private static bool HasPendingLifecycle() =>
        _labsPresetActive && (_presetAwaitingOperation || _presetOperationPending || LoadedLabsScenes.Count > 0);

    private static bool HasRaidCleanupOwnership() =>
        _raidCleanupOwned || _labsPresetActive || _presetLoading || _presetAwaitingOperation ||
        _presetOperationPending || LoadedLabsScenes.Count > 0 || OwnedCoroutines.Count > 0 ||
        _sceneHandlerWorkOwned;

    private static bool IsLabsHandlerScene(Scene scene) =>
        scene.IsValid() && string.Equals(scene.name, LabsHandlerSceneName, StringComparison.OrdinalIgnoreCase);

    private static void MarkSceneHandlerOwnership(Scene scene)
    {
        if (!IsLabsHandlerScene(scene)) return;

        _sceneHandlerWorkOwned = true;
        MarkRaidWorkOwned();
    }

    private static void MarkRaidWorkOwned()
    {
        // A failed unpatch leaves ownership pending. Preserve wrappers that did
        // disable successfully, but make newly acquired helper resources eligible
        // for one more resource reset on the eventual retry.
        if (!_raidCleanupOwned)
        {
            _acousticsTeardownComplete = false;
            _bakeMergeTeardownComplete = false;
        }

        _raidCleanupOwned = true;
        _raidResourcesReset = false;
    }

    private static void BeginRaidCleanupOwnership()
    {
        _raidCleanupOwned = true;
        _raidResourcesReset = false;
        _acousticsTeardownComplete = false;
        _bakeMergeTeardownComplete = false;
    }

    private static void DisableSwapPatch()
    {
        if (_swapPatchTeardownDone) return;
        try
        {
            // Disable is intentionally unconditional: an SPT patch can have
            // installed its hook before reporting a failed Enable call.
            SwapPatch?.Disable();
            _swapPatchTeardownDone = true;
        }
        catch (Exception e)
        {
            Log?.LogError($"[LabsBoiler] preset bootstrap patch disable failed: {e}");
        }
    }

    private static void FinalizeShutdown()
    {
        DetachSceneHandlers();
        StopOwnedCoroutines();
        DisableSwapPatch();

        var host = _lifecycleHost;
        _lifecycleHost = null;
        if (host != null)
        {
            try { UnityEngine.Object.Destroy(host.gameObject); }
            catch (Exception e) { Log?.LogError($"[LabsBoiler] lifecycle host destroy failed: {e}"); }
        }

        Instance = null;
        Log = null;
    }

    private static void OnLifecycleHostDestroyed(LifecycleHost host)
    {
        if (!ReferenceEquals(_lifecycleHost, host)) return;
        _lifecycleHost = null;

        // The host is the independent lifetime anchor. If Unity destroys it
        // unexpectedly, fail closed instead of leaving hooks in the game.
        if (HasRaidCleanupOwnership())
        {
            Log?.LogWarning("[LabsBoiler] lifecycle host was destroyed; releasing raid-owned state");
            ReleaseLabs("lifecycle host destroyed");
            return;
        }

        if (_destroying) FinalizeShutdown();
    }

    private void OnDestroy()
    {
        _destroying = true;

        // No new preset may be accepted after plugin destruction. An already
        // accepted preset remains owned by the persistent lifecycle host until
        // its load settles and its final Labs scene unloads.
        DisableSwapPatch();
        if (HasPendingLifecycle())
        {
            if (ReferenceEquals(Instance, this)) Instance = null;
            return;
        }

        ReleaseLabs("plugin destroy");
        if (ReferenceEquals(Instance, this)) Instance = null;
    }

    private sealed class LifecycleHost : MonoBehaviour
    {
        private void Awake() => UnityEngine.Object.DontDestroyOnLoad(gameObject);

        private void Update() => PollPresetOperation();

        private void OnDestroy() => OnLifecycleHostDestroyed(this);
    }
}
