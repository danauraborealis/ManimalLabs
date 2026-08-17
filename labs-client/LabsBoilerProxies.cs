using System;
using System.Collections.Generic;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.LabsBoiler;

internal static class LabsBoilerProxies
{
    private const string MxScene = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";

    private static readonly List<Component> Proxies = [];
    private static Type _proxyType;
    private static System.Reflection.FieldInfo _ipField;

    internal static void OnSceneLoaded(Scene scene)
    {
        if (scene.name != MxScene) return;
        _proxyType ??= AccessTools.TypeByName("InteractiveProxy");
        if (_proxyType == null)
        {
            Plugin.Log.LogWarning("[LabsBoiler] InteractiveProxy type not found");
            return;
        }
        _ipField ??= AccessTools.Field(_proxyType, "_interactionPosition");

        Proxies.Clear();
        foreach (var root in scene.GetRootGameObjects())
        {
            Proxies.AddRange(root.GetComponentsInChildren(_proxyType, true));
        }
        Reapply();
        Plugin.Log.LogInfo($"[LabsBoiler] set interaction anchor on {Proxies.Count} keycard swiper(s) " +
                           $"(offset {Plugin.ProxyInteractX.Value},{Plugin.ProxyInteractY.Value},{Plugin.ProxyInteractZ.Value})");

        FixNullTriggersMaps(scene);
        if (Plugin.Instance) Plugin.Instance.StartCoroutine(FixWindowBreakers(scene));
    }

    private static System.Collections.IEnumerator FixWindowBreakers(Scene scene)
    {
        var ours = new List<WindowBreaker>();
        foreach (var root in scene.GetRootGameObjects())
        {
            ours.AddRange(root.GetComponentsInChildren<WindowBreaker>(true));
        }
        
        if (ours.Count == 0) yield break;

        WindowBreaker donor = null;
        for (var i = 0; i < 600 && !donor; i++)
        {
            foreach (var wb in UnityEngine.Object.FindObjectsOfType<WindowBreaker>(true))
            {
                if (ours.Contains(wb) || !wb.BrokenWindow) { continue; }

                donor = wb;
                break;
            }
            if (!donor) yield return null;
        }
        
        if (!donor)
        {
            Plugin.Log.LogWarning("[LabsBoiler] no native WindowBreaker donor — office windows will NRE when shot");
            yield break;
        }
        
        int fixedUp = 0, rebaked = 0;
        
        foreach (var wb in ours)
        {
            var touched = false;
            if (!wb.BrokenWindow) { wb.BrokenWindow = donor.BrokenWindow; touched = true; }
            if (!wb.Material) { wb.Material = donor.Material; touched = true; }

            if (!wb.ObstructiveCollider)
                foreach (var col in wb.GetComponentsInChildren<Collider>(true))
                {
                    if (col.gameObject.name.IndexOf("Obstructive", StringComparison.OrdinalIgnoreCase) < 0) { continue; }

                    wb.ObstructiveCollider = col;
                    touched = true;
                    break;
                }
            if (touched) fixedUp++;

            if (ApplyRetailWindowValues(wb)) rebaked++;
            else
            {
                try { wb.method_5(); rebaked++; }
                catch (Exception e) { Plugin.Log.LogWarning($"[LabsBoiler] window '{wb.name}' rebake failed: {e.Message}"); }
            }
        }
        Plugin.Log.LogInfo($"[LabsBoiler] WindowBreaker fix: configs from '{donor.gameObject.name}' onto {fixedUp}/{ours.Count}, glass parametrization set on {rebaked}/{ours.Count}");
    }

    private static JArray _windowRows;

    private static bool ApplyRetailWindowValues(WindowBreaker wb)
    {
        try
        {
            if (_windowRows == null)
            {
                var dir = System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".";
                var p = System.IO.Path.Combine(dir, "boiler_windows.json");
                if (!System.IO.File.Exists(p)) return false;
                _windowRows = JObject.Parse(System.IO.File.ReadAllText(p))["components"]?["WindowBreaker"] as JArray;
            }
            if (_windowRows == null) return false;

            JObject fields = null;
            var best = 0.5f;
            foreach (var row in _windowRows)
            {
                var w = row["world"];
                if (w == null) continue;
                var d = Vector3.Distance(wb.transform.position, new Vector3((float)w[0], (float)w[1], (float)w[2]));
                if (!(d < best)) { continue; }

                best = d; fields = row["fields"] as JObject;
            }
            
            if (fields == null) return false;

            var t = typeof(WindowBreaker);

            I("AngleMode", t, fields, wb);
            F("MinThickness", t, fields, wb); F("ThicknessMultyplier", t, fields, wb); F("EdgesWidth", t, fields, wb);
            F("CracksScale", t, fields, wb);
            F("MassMultyplier", t, fields, wb); F("AirDrag", t, fields, wb); F("TimeUntilPartDie", t, fields, wb);
            F("FirstShotRadius", t, fields, wb); F("ShotRadius", t, fields, wb); F("FirstShotSoundVolume", t, fields, wb);
            F("InstantExplosionCoef", t, fields, wb); F("ShotDirectionCoef", t, fields, wb);
            V2("TorqueRandomMinMaxCoefs", t, fields, wb);
            I("AxisX", t, fields, wb); I("AxisY", t, fields, wb); I("AxisZ", t, fields, wb);
            V2("UvMult", t, fields, wb); V2("UvAdd", t, fields, wb); V2("ZSurfs", t, fields, wb);
            V4("Box", t, fields, wb); Q("Rotation", t, fields, wb); B("NeedToSwap", t, fields, wb);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[LabsBoiler] retail window values failed for '{wb.name}': {e.Message}");
            return false;
        }
    }

    private static void Q(string n, Type t, JObject fields, WindowBreaker wb)
    {
        var fi = AccessTools.Field(t, n);
        var v = fields[n];
        if (fi != null && v != null)
        {
            fi.SetValue(wb, new Quaternion((float)v["x"], (float)v["y"], (float)v["z"], (float)v["w"]));
        }
    }

    private static void V4(string n, Type t, JObject fields, WindowBreaker wb)
    {
        var fi = AccessTools.Field(t, n);
        var v = fields[n];
        if (fi != null && v != null)
        {
            fi.SetValue(wb, new Vector4((float)v["x"], (float)v["y"], (float)v["z"], (float)v["w"]));
        }
    }

    private static void V2(string n, Type t, JObject fields, WindowBreaker wb)
    {
        var fi = AccessTools.Field(t, n);
        var v = fields[n];
        if (fi != null && v != null)
        {
            fi.SetValue(wb, new Vector2((float)v["x"], (float)v["y"]));
        }
    }

    private static void B(string n, Type t, JObject fields, WindowBreaker wb)
    {
        var fi = AccessTools.Field(t, n);
        var v = fields[n];
        if (fi != null && v != null)
        {
            fi.SetValue(wb, (int)v != 0);
        }
    }

    private static void I(string n, Type t, JObject fields, WindowBreaker wb)
    {
        var fi = AccessTools.Field(t, n);
        var v = fields[n];
        if (fi != null && v != null)
        {
            fi.SetValue(wb, fi.FieldType.IsEnum ? Enum.ToObject(fi.FieldType, (int)v) : (int)v);
        }
    }

    private static void F(string n, Type t, JObject fields, WindowBreaker wb)
    {
        var fi = AccessTools.Field(t, n);
        var v = fields[n];
        if (fi != null && v != null)
        {
            fi.SetValue(wb, (float)v);
        }
    }

    private static void FixNullTriggersMaps(Scene scene)
    {
        var wioType = AccessTools.TypeByName("EFT.Interactive.WorldInteractiveObject");
        var tmField = wioType != null ? AccessTools.Field(wioType, "TriggersMap") : null;
        if (tmField == null) { Plugin.Log.LogWarning("[LabsBoiler] TriggersMap field not found — door opens will NRE"); return; }
        var fixedUp = 0;
        foreach (var root in scene.GetRootGameObjects())
        foreach (var wio in root.GetComponentsInChildren(wioType, true))
        {
            if (tmField.GetValue(wio) != null) { continue; }

            tmField.SetValue(wio, Array.CreateInstance(tmField.FieldType.GetElementType()!, 0));
            fixedUp++;
        }
        Plugin.Log.LogInfo($"[LabsBoiler] initialized null TriggersMap on {fixedUp} door(s)/interactive(s) — open-hand animation unblocked");
    }

    internal static void Reapply()
    {
        if (_ipField == null) return;
        var v = new Vector3(Plugin.ProxyInteractX.Value, Plugin.ProxyInteractY.Value, Plugin.ProxyInteractZ.Value);
        foreach (var px in Proxies)
        {
            if (px)
            {
                _ipField.SetValue(px, v);
            }
        }
    }
}