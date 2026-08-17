using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace Manimal.LabsBoiler;

[BepInPlugin(BuildInfo.ModGuid, "Manimal-LabsBoiler", BuildInfo.Version)]
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

    private void Awake()
    {
        Log = Logger;
        Instance = this;

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

        SceneManager.sceneLoaded += (scene, _) =>
        {
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
        };

        // patch each class separately: a blanket PatchAll aborts on the FIRST bad patch
        // and silently drops every class after it — one broken diagnostic patch took the
        // scene swap down with it (2026-08-17)
        var harmony = new Harmony(BuildInfo.ModGuid);
        foreach (var type in typeof(Plugin).Assembly.GetTypes())
        {
            if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            try { harmony.PatchAll(type); }
            catch (Exception e) { Log.LogError($"[LabsBoiler] patching {type.Name} failed: {e}"); }
        }

        Log.LogInfo($"Manimal-LabsBoiler {BuildInfo.Version} loaded");
    }
}