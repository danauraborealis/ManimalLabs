using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace Manimal.LabsBoiler
{

    // runtime map-surgery: swaps native Labs' level74 office scene for our rebuilt _MX
    // (which carries retail's floors 2-3 expansion + the grafted keycard access), then
    // rebinds the ripped scene's dummy shaders to the game's and copies the AreaLight
    // shader refs the rip couldn't carry. no full-map backport — native Labs does 95%
    // of the work; we edit one scene into its multi-scene load at runtime.
    [BepInPlugin(BuildInfo.ModGuid, "Manimal-LabsBoiler", BuildInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static Plugin Instance;

        // office lamp brightness — scale multiplies, clamp caps (the clamp tames the
        // too-bright hallway MainSpots without dimming the fill lights). both live.
        internal static ConfigEntry<float> LampIntensity;
        internal static ConfigEntry<float> LampMaxIntensity;
        internal static ConfigEntry<bool> LampForceOn;
        internal static ConfigEntry<float> EmissiveBoost;
        // keycard swipe interaction anchor, proxy-local (default 0 = at the reader). live.
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
            // key renamed from LampMaxIntensity: stale saved 1.0s from the force-on era
            // were silently capping every light to a quarter of retail (log: clamp=1.0)
            LampMaxIntensity = Config.Bind("Lights", "LampBrightnessCap", 8.0f,
                new ConfigDescription("brightness ceiling. 8 = no clamp = exactly retail; lower it to cap the brightest lamps (live)",
                    new AcceptableValueRange<float>(0.5f, 8f)));
            // retail leaves ~36 office spot lamps OFF on purpose (the room is lit by area
            // lights); default false = retail-accurate. true lights every lamp = brighter.
            LampForceOn = Config.Bind("Lights", "ForceAllLampsOn", false,
                new ConfigDescription("light EVERY office lamp, including the ~36 retail leaves off — brighter than retail (set at raid start)"));
            LampIntensity.SettingChanged += (_, __) => { try { LabsBoilerLights.ReapplyIntensity(); } catch { } };
            LampMaxIntensity.SettingChanged += (_, __) => { try { LabsBoilerLights.ReapplyIntensity(); } catch { } };
            EmissiveBoost = Config.Bind("Lights", "EmissiveBoost", 1.0f,
                new ConfigDescription("multiplier on the office emissive materials' _EmissionVisibility (logos/monitors/LEDs). 1 = retail-authored. drag it LIVE in-raid: if the glow appears at 2-4, retail's look needs a boost here; if NOTHING changes, the emission path isn't rendering at all",
                    new AcceptableValueRange<float>(0.25f, 8f)));
            EmissiveBoost.SettingChanged += (_, __) => { try { LabsBoilerShaders.ReapplyEmissiveBoost(); } catch { } };
            // the AreaLightsEnabled kill switch is GONE: it bit twice (default-false era
            // 08-15, stale saved false 08-16) and each time silently blanked the office's
            // primary light source. the bug it guarded (shared-material greedy copy) is
            // fixed — the area lights are now unconditional.

            // no audio mode config: piggyback/ambient were disproven experiments — the
            // bake merge is the only path now, with a codeless ambient-fill fallback
            // if the merge itself ever fails (user call: "why keep options that dont work")

            ProxyInteractX = Config.Bind("Keycard", "SwipeAnchorX", 0f,
                new ConfigDescription("keycard swipe interaction anchor, proxy-local X (0 = at the reader; retail was 0.81 = off to the side) (live)",
                    new AcceptableValueRange<float>(-2f, 2f)));
            ProxyInteractY = Config.Bind("Keycard", "SwipeAnchorY", 0f,
                new ConfigDescription("keycard swipe anchor, proxy-local Y (live)", new AcceptableValueRange<float>(-2f, 2f)));
            ProxyInteractZ = Config.Bind("Keycard", "SwipeAnchorZ", 0f,
                new ConfigDescription("keycard swipe anchor, proxy-local Z (live)", new AcceptableValueRange<float>(-2f, 2f)));
            ProxyInteractX.SettingChanged += (_, __) => { try { LabsBoilerProxies.Reapply(); } catch { } };
            ProxyInteractY.SettingChanged += (_, __) => { try { LabsBoilerProxies.Reapply(); } catch { } };
            ProxyInteractZ.SettingChanged += (_, __) => { try { LabsBoilerProxies.Reapply(); } catch { } };

            // material ownership capture + the AreaLight shader copy both key off our
            // scene arriving in the additive Labs load
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                // FIRST: kill our scene's PerfectCulling before its Start ever runs �
                // the broken cross-scene group thrashes visibility map-wide (light
                // intensity blips via CADLO OnDisable resetting to full brightness)
                try { LabsBoilerCullingStrip.OnSceneLoaded(scene); }
                catch (Exception e) { Log.LogError($"[LabsBoiler] culling strip failed: {e}"); }
                try { LabsBoilerShaders.OnSceneLoaded(scene); }
                catch (Exception e) { Log.LogError($"[LabsBoiler] shader handler failed: {e}"); }
                try { LabsBoilerLights.OnSceneLoaded(scene); }
                catch (Exception e) { Log.LogError($"[LabsBoiler] light-revival handler failed: {e}"); }
                // native office light branch delete moved into the AreaLight rebuild
                // (LabsBoilerShaders): it only fires once the grafted lights VERIFY as
                // renderable — deleting blind removed the only working lights while the
                // graft was silently broken = the all-dark raids (user caught it 08-16)
                try { LabsBoilerProxies.OnSceneLoaded(scene); }
                catch (Exception e) { Log.LogError($"[LabsBoiler] proxy-anchor handler failed: {e}"); }
                // acoustics now injects via a Harmony prefix on SpatialAudioSystem.Initialize
                // (LabsBoilerAcoustics), not a scene-load hook
            };

            var harmony = new Harmony(BuildInfo.ModGuid);
            try { harmony.PatchAll(); }
            catch (Exception e) { Log.LogError($"[LabsBoiler] PatchAll failed — the swap may not apply: {e}"); }

            Log.LogInfo($"Manimal-LabsBoiler {BuildInfo.Version} loaded");
        }
    }
}
