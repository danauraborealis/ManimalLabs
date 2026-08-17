using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Audio.AmbientSubsystem;
using Audio.SpatialSystem;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Manimal.LabsBoiler;

[HarmonyPatch]
internal static class LabsBoilerAcoustics
{
    private const string MxScene = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";

    private static GameObject _marker;

    private static bool Done
    {
        get => _marker;
        set
        {
            if (value) _marker = new GameObject("LabsBoiler_AcousticsMarker");
        }
    }

    internal static readonly List<SpatialAudioRoom> CreatedRooms = [];
    internal static readonly List<SpatialAudioPortal> CreatedPortals = [];

    [HarmonyPatch(typeof(SpatialAudioSystem), nameof(SpatialAudioSystem.Initialize))]
    [HarmonyPrefix]
    private static void BeforeInit()
    {
        try
        {
            Inject();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[LabsBoiler] acoustics inject failed: {e}");
        }
    }

    private static readonly FieldInfo FRoomId = AccessTools.Field(typeof(SpatialAudioRoom), "_iD");
    private static readonly FieldInfo FRoomType = AccessTools.Field(typeof(SpatialAudioRoom), "_type");
    private static readonly FieldInfo FRoomIso = AccessTools.Field(typeof(SpatialAudioRoom), "Isolated");
    private static readonly FieldInfo FRoomOutdoor = AccessTools.Field(typeof(SpatialAudioRoom), "Outdoor");
    private static readonly FieldInfo FRoomBounds = AccessTools.Field(typeof(SpatialAudioRoom), "_bounds");
    private static readonly FieldInfo FRoomSize = AccessTools.Field(typeof(SpatialAudioRoom), "_roomSize");
    private static readonly FieldInfo FAreaCollider = AccessTools.Field(typeof(ServerRoomColliderArea), "areaCollider");

    private static readonly FieldInfo FAreaValidate =
        AccessTools.Field(typeof(ServerRoomColliderArea), "_validatePlayerCenterInside");

    private static void Inject()
    {
        if (Done) return;
        if (!UnityEngine.SceneManagement.SceneManager.GetSceneByName(MxScene).isLoaded) return;

        var dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".";
        var jsonPath = Path.Combine(dir, "labs_office_audio.json");
        if (!File.Exists(jsonPath))
        {
            Plugin.Log.LogWarning($"[LabsBoiler] {jsonPath} missing — no office acoustics");
            return;
        }

        if (JObject.Parse(File.ReadAllText(jsonPath))["scenes"]?["Laboratory_Sound"] is not JObject sound) return;

        _fallbackSound = sound;
        _fallbackStarted = false;
        CreatedRooms.Clear();
        CreatedPortals.Clear();

        var natives = UnityEngine.Object.FindObjectsOfType<SpatialAudioRoom>(true);
        var nativePos = new List<Vector3>();
        var maxNativeId = 0;
        AudioClip donorTone = null;
        foreach (var nr in natives)
        {
            nativePos.Add(nr.transform.position);
            maxNativeId = Math.Max(maxNativeId, Convert.ToInt32(FRoomId.GetValue(nr)));
            if (!donorTone && nr.AmbientData?.RoomTone)
            {
                donorTone = nr.AmbientData.RoomTone;
            }
        }

        if (nativePos.Count == 0)
        {
            Plugin.Log.LogWarning("[LabsBoiler] no native SpatialAudioRooms yet — acoustics deferred");
            return;
        }

        var nextId = (short)(maxNativeId + 1);

        var areaRows = new Dictionary<long, JToken>();
        foreach (var a in sound["AudioTriggerArea"] as JArray ?? [])
        {
            areaRows[a.Value<long>("path_id")] = a;
        }

        var nativeBoxes = new List<Bounds>();
        foreach (var nta in UnityEngine.Object.FindObjectsOfType<AudioTriggerArea>(true))
        {
            var col = nta.GetComponent<BoxCollider>();
            if (col) nativeBoxes.Add(col.bounds);
        }

        
        var rootGo = new GameObject("LabsBoiler_Acoustics");
        rootGo.SetActive(false);
        int rooms = 0, areas = 0, skipped = 0;
        
        var roomByRowId = new Dictionary<long, SpatialAudioRoom>();

        foreach (var rr in sound["SpatialAudioRoom"] as JArray ?? [])
        {
            if (rr["fields"] is not JObject f) continue;
            
            var wt = rr["world_trs"];
            var pos = wt?["pos"] != null
                ? new Vector3((float)wt["pos"][0], (float)wt["pos"][1], (float)wt["pos"][2])
                : Vector3.zero;
            var exists = false;
            SpatialAudioRoom nativeMatch = null;
            foreach (var nr in natives)
                if (Vector3.Distance(nr.transform.position, pos) < 0.5f)
                {
                    exists = true;
                    nativeMatch = nr;
                    break;
                }

            if (exists)
            {
                roomByRowId[rr.Value<long>("path_id")] = nativeMatch;
                skipped++;
                continue;
            }

            var iD = nextId++;
            var roomGo = new GameObject($"LabsBoiler_Room_{iD}");
            roomGo.SetActive(false);
            roomGo.transform.SetParent(rootGo.transform, false);
            SetWorld(roomGo.transform, wt);

            var room = roomGo.AddComponent<SpatialAudioRoom>();
            roomByRowId[rr.Value<long>("path_id")] = room;
            FRoomId.SetValue(room, iD);
            FRoomType.SetValue(room, (EAudioRoomTypeMask)(f.Value<int?>("_type") ?? 0));

            FRoomIso.SetValue(room, (f.Value<int?>("Isolated") ?? 0) != 0);
            FRoomOutdoor.SetValue(room, (f.Value<int?>("Outdoor") ?? 0) != 0);
            FRoomSize.SetValue(room, f.Value<float?>("_roomSize") ?? 0f);

            if (f["_bounds"] is JObject b)
            {
                FRoomBounds.SetValue(room, new Bounds(V3(b["m_Center"]), V3(b["m_Extent"]) * 2f));
            }

            room.priority = f.Value<int?>("priority") ?? 0;
            room.WallOcclusion = f.Value<float?>("WallOcclusion") ?? 0.5f;
            room.FitToGeometry = false;
            room.OnlyWired = (f.Value<int?>("OnlyWired") ?? 1) != 0;

            var amb = new RoomAmbientData();
            if (f["AmbientData"] is JObject ad)
            {
                amb.RoomToneVolume = ad.Value<float?>("RoomToneVolume") ?? 1f;
                amb.FadeInSeconds = ad.Value<float?>("FadeInSeconds") ?? 0.25f;
                amb.FadeOutSeconds = ad.Value<float?>("FadeOutSeconds") ?? 0.5f;
            }

            if (donorTone)
            {
                amb.RoomTone = donorTone;
            }

            room.AmbientData = amb;

            room.Areas = [];
            foreach (var aref in f["Areas"] as JArray ?? [])
            {
                var apid = (aref as JObject)?.Value<long?>("ref");
                if (apid == null || !areaRows.TryGetValue(apid.Value, out var arow)) continue;

                var awt = arow["world_trs"];
                var ap = new Vector3((float)awt["pos"][0], (float)awt["pos"][1], (float)awt["pos"][2]);
                var asc = new Vector3((float)awt["scale"][0], (float)awt["scale"][1], (float)awt["scale"][2]);
                var candidate = new Bounds(ap, asc);
                if (CoveredByNative(candidate, nativeBoxes))
                {
                    Plugin.Log.LogDebug($"[LabsBoiler] area {apid} skipped — native coverage at {ap}");
                    continue;
                }

                var areaGo = new GameObject($"LabsBoiler_Area_{apid}");
                areaGo.SetActive(false);
                areaGo.transform.SetParent(roomGo.transform, false);
                SetWorld(areaGo.transform, awt);
                var box = areaGo.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.size = Vector3.one;
                box.center = Vector3.zero;
                var area = areaGo.AddComponent<AudioTriggerArea>();
                FAreaCollider?.SetValue(area, box);
                FAreaValidate?.SetValue(area, (arow["fields"]?.Value<int?>("_validatePlayerCenterInside") ?? 1) != 0);
                room.Areas.Add(area);
                areas++;
                Plugin.Log.LogInfo(
                    $"[LabsBoiler] area {apid} kept: center=({ap.x:F1},{ap.y:F1},{ap.z:F1}) size=({asc.x:F1},{asc.y:F1},{asc.z:F1})");
            }

            if (room.Areas.Count == 0)
            {
                Plugin.Log.LogInfo($"[LabsBoiler] room {iD} dropped — all its area boxes already native-covered");
                UnityEngine.Object.Destroy(roomGo);
                nextId--;
                continue;
            }

            room.roomConnections = [];
            CreatedRooms.Add(room);
            rooms++;
        }

        var nativePortals = UnityEngine.Object.FindObjectsOfType<SpatialAudioPortal>(true);
        var maxPortalId = 0;
        var knownDoorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fPortalId = AccessTools.Field(typeof(BaseSpatialAudioPortal), "_iD");
        var fPortalRooms = AccessTools.Field(typeof(BaseSpatialAudioPortal), "_connectedRooms");
        var fPortalName = AccessTools.Field(typeof(BaseSpatialAudioPortal), "portalName");
        foreach (var np in nativePortals)
        {
            maxPortalId = Math.Max(maxPortalId, Convert.ToInt32(fPortalId.GetValue(np)));
            if (!string.IsNullOrEmpty(np.DoorID)) knownDoorIds.Add(np.DoorID);
        }

        var nextPortalId = (short)(maxPortalId + 1);
        int portals = 0, portalsSkipped = 0;

        var portalsRoot = new GameObject("LabsBoiler_Portals");
        portalsRoot.SetActive(false);

        foreach (var pr in sound["SpatialAudioPortal"] as JArray ?? [])
        {
            var f = pr["fields"] as JObject;
            var wt2 = pr["world_trs"];
            if (f == null || wt2?["pos"] == null) continue;
            var ppos = new Vector3((float)wt2["pos"][0], (float)wt2["pos"][1], (float)wt2["pos"][2]);
            var nativeHas = false;
            foreach (var np in nativePortals)
            {
                if (!(Vector3.Distance(np.transform.position, ppos) < 0.5f))
                {
                    continue;
                }

                nativeHas = true;
                break;
            }

            if (nativeHas)
            {
                portalsSkipped++;
                continue;
            }

            var conn = new List<SpatialAudioRoom>();
            foreach (var cr in f["_connectedRooms"] as JArray ?? [])
            {
                var rid = (cr as JObject)?.Value<long?>("ref");
                if (rid != null && roomByRowId.TryGetValue(rid.Value, out var r0) && r0 != null && !conn.Contains(r0))
                {
                    conn.Add(r0);
                }
            }

            if (conn.Count == 0)
            {
                portalsSkipped++;
                continue;
            } // connects nothing we know

            var doorId = f.Value<string>("DoorID") ?? "";
            var window = doorId.StartsWith("window", StringComparison.OrdinalIgnoreCase)
                         || (f.Value<string>("portalName") ?? "").StartsWith("AudioWindowPortal");
            MakePortal($"LabsBoiler_Portal_{doorId}", ppos, wt2, doorId, window, conn, portalsRoot, fPortalId,
                ref nextPortalId, fPortalName, fPortalRooms, knownDoorIds);
            portals++;
        }

        foreach (var door in UnityEngine.Object.FindObjectsOfType<EFT.Interactive.Door>(true))
        {
            var id = door.Id;
            if (string.IsNullOrEmpty(id) || knownDoorIds.Contains(id)) continue;
            if (!id.Contains("Office_Above_Boiler_Room") && !id.Contains("345345")) continue; // ours only

            SpatialAudioRoom home = null;
            var best = 25f;
            foreach (var kv in roomByRowId)
            {
                var cand = kv.Value;
                if (!cand || !cand.name.StartsWith("LabsBoiler_")) continue;
                var contains = false;
                foreach (var a in cand.Areas ?? [])
                {
                    var col = a ? a.GetComponent<BoxCollider>() : null;
                    if (!col || !new Bounds(a.transform.position, Vector3.Scale(col.size, a.transform.lossyScale))
                            .Contains(door.transform.position)) { continue; }

                    contains = true;
                    break;
                }

                var dd = contains ? 0f : Vector3.Distance(cand.transform.position, door.transform.position);

                if (!(dd < best)) { continue; }

                best = dd;
                home = cand;
            }

            if (!home) { continue; }
            
            MakePortal($"LabsBoiler_Portal_synth_{id}", door.transform.position, null, id, false, [home], portalsRoot,
                fPortalId, ref nextPortalId, fPortalName, fPortalRooms, knownDoorIds);

            portals++;
            Plugin.Log.LogInfo($"[LabsBoiler] synthetic portal for doorless '{id}' -> room '{home.name}' ({best:F1}m)");
        }

        rootGo.AddComponent<SpatialAudioCrossSceneGroup>();
        SetActiveDeep(rootGo);
        _pendingPortalsRoot = portalsRoot;
        Plugin.Log.LogInfo(
            $"[LabsBoiler] portals: {portals} staged (inactive), {portalsSkipped} skipped (native/unresolvable)");

        Done = true;
        Plugin.Log.LogInfo($"[LabsBoiler] acoustics injected: {rooms} office room(s) + {areas} trigger area(s) " +
                           $"({skipped} already native; tone '{(donorTone ? donorTone.name : "none")}')");

        if (Plugin.Instance) Plugin.Instance.StartCoroutine(InitWatchdog(rootGo));
    }

    private static void MakePortal(string name, Vector3 pos, JToken wt2, string doorId, bool window,
        List<SpatialAudioRoom> conn, GameObject portalsRoot, FieldInfo fPortalId, ref short nextPortalId,
        FieldInfo fPortalName, FieldInfo fPortalRooms, HashSet<string> knownDoorIds)
    {
        var pgo = new GameObject(name);
        pgo.SetActive(false);
        pgo.transform.SetParent(portalsRoot.transform, false);
        
        if (wt2 != null) SetWorld(pgo.transform, wt2);
        else pgo.transform.position = pos;
        
        var pbox = pgo.AddComponent<BoxCollider>();
        pbox.isTrigger = true;
        pbox.size = Vector3.one;
        pbox.center = Vector3.zero;
        
        if (wt2 == null) pbox.size = new Vector3(1.5f, 2.2f, 1.5f);
        var portal = pgo.AddComponent<SpatialAudioPortal>();
        
        fPortalId.SetValue(portal, nextPortalId++);
        fPortalName?.SetValue(portal, name);
        
        portal.DoorID = doorId ?? "";
        portal.portalType = window ? BaseSpatialAudioPortal.PortalType.Window : BaseSpatialAudioPortal.PortalType.Opening;
        portal.state = BaseSpatialAudioPortal.PortalState.Open;
        portal.portalCollider = pbox;
        
        fPortalRooms.SetValue(portal, conn);
        if (!string.IsNullOrEmpty(doorId)) knownDoorIds.Add(doorId);
        CreatedPortals.Add(portal);
    }

    private static bool CoveredByNative(Bounds b, List<Bounds> nativeBoxes)
    {
        foreach (var nb in nativeBoxes)
        {
            if (!nb.Intersects(b)) continue;

            var min = Vector3.Max(nb.min, b.min);
            var max = Vector3.Min(nb.max, b.max);
            var ov = (max.x - min.x) * (max.y - min.y) * (max.z - min.z);
            var vol = b.size.x * b.size.y * b.size.z;
            if (vol > 0f && ov / vol > 0.25f) return true;
        }

        return false;
    }

    private static JObject _fallbackSound;
    private static bool _fallbackStarted;

    internal static void StartAmbientFallback()
    {
        if (_fallbackStarted || _fallbackSound == null || Plugin.Instance == null) return;
        _fallbackStarted = true;
        Plugin.Log.LogWarning("[LabsBoiler] bake merge unavailable — falling back to plain ambient fill");
        Plugin.Instance.StartCoroutine(AmbientFill(_fallbackSound));
    }

    private static System.Collections.IEnumerator AmbientFill(JObject sound)
    {
        AudioClip tone = null;
        for (var i = 0; i < 300 && !tone; i++)
        {
            foreach (var nr in UnityEngine.Object.FindObjectsOfType<SpatialAudioRoom>())
            {
                if (!nr.AmbientData?.RoomTone)
                {
                    continue;
                }

                tone = nr.AmbientData.RoomTone;
                break;
            }

            if (!tone) yield return null;
        }

        if (!tone)
        {
            Plugin.Log.LogWarning("[LabsBoiler] no native RoomTone found — ambient fill skipped");
            yield break;
        }

        UnityEngine.Audio.AudioMixerGroup group = null;
        foreach (var s in UnityEngine.Object.FindObjectsOfType<AudioSource>(true))
        {
            if (!s.outputAudioMixerGroup) { continue; }

            group = s.outputAudioMixerGroup;
            break;
        }

        var made = 0;
        foreach (var a in sound["AudioTriggerArea"] as JArray ?? [])
        {
            var wt = a["world_trs"];
            if (wt?["pos"] == null) continue;
            var pos = new Vector3((float)wt["pos"][0], (float)wt["pos"][1], (float)wt["pos"][2]);
            var scl = wt["scale"] != null
                ? new Vector3((float)wt["scale"][0], (float)wt["scale"][1], (float)wt["scale"][2])
                : Vector3.one * 6f;
            var go = new GameObject($"LabsBoiler_Ambience_{made}")
            {
                transform =
                {
                    position = pos
                }
            };
            
            var src = go.AddComponent<AudioSource>();
            src.clip = tone;
            src.loop = true;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = 2.5f;
            src.maxDistance = Mathf.Max(scl.x, scl.z) * 0.9f + 4f;
            src.volume = 0.5f;

            if (group)
            {
                src.outputAudioMixerGroup = group;
            }
            
            src.Play();
            made++;
        }

        Plugin.Log.LogInfo($"[LabsBoiler] ambient FALLBACK: {made} tone source(s) placed (tone '{tone.name}')");
    }

    private static GameObject _pendingPortalsRoot;

    private static System.Collections.IEnumerator InitWatchdog(GameObject rootGo)
    {
        for (var i = 0; i < 1800 && !SpatialAudioSystem.Initialized; i++) yield return null;
        if (!rootGo) yield break;

        for (var i = 0; i < 1800 && !Comfort.Common.Singleton<EFT.GameWorld>.Instantiated; i++) yield return null;
        yield return new WaitForSeconds(1f);
        if (_pendingPortalsRoot)
        {
            foreach (var t in _pendingPortalsRoot.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.SetActive(true);
            }

            _pendingPortalsRoot.SetActive(true);
            Plugin.Log.LogInfo(
                $"[LabsBoiler] portals activated post-game-start ({_pendingPortalsRoot.transform.childCount})");
        }

        int lateInit = 0, valid = 0, total = 0;
        foreach (var room in rootGo.GetComponentsInChildren<SpatialAudioRoom>(true))
        {
            total++;
            if (!room.IsInitialized)
            {
                try
                {
                    room.Initialize();
                    lateInit++;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[LabsBoiler] room {room.name} Initialize failed: {e.Message}");
                }
            }

            if (room.IsValid) valid++;
        }

        Plugin.Log.LogInfo($"[LabsBoiler] acoustics watchdog: systemInit={SpatialAudioSystem.Initialized}, " +
                           $"{valid}/{total} injected room(s) valid, {lateInit} late-initialized");

        var sys = MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated
            ? MonoBehaviourSingleton<SpatialAudioSystem>.Instance
            : null;
        var lastId = short.MinValue;

        // ReSharper disable once LoopVariableIsNeverChangedInsideLoop
        while (sys)
        {
            var cur = sys.ListenerCurrentRoom;
            var id = (short)(cur?.IsValid == true ? cur.ID : -1);
            if (id != lastId)
            {
                lastId = id;
                var label = "NONE (unroomed — hear-through zone)";
                if (cur?.IsValid == true)
                {
                    var c = cur as Component;
                    var ours = c && c.gameObject.name.StartsWith("LabsBoiler_");
                    label = $"{(ours ? "OURS" : "native")} id={cur.ID}{(c ? $" '{c.gameObject.name}'" : "")}";
                }

                Plugin.Log.LogInfo($"[LabsBoiler] listener room -> {label}");
            }

            yield return new WaitForSeconds(0.5f);
        }
    }

    private static void SetWorld(Transform t, JToken wt)
    {
        if (wt == null) return;
        var p = wt["pos"];
        var r = wt["rot"];
        var s = wt["scale"];
        if (p != null) t.position = new Vector3((float)p[0], (float)p[1], (float)p[2]);
        if (r != null) t.rotation = new Quaternion((float)r[0], (float)r[1], (float)r[2], (float)r[3]);
        if (s != null) t.localScale = new Vector3((float)s[0], (float)s[1], (float)s[2]);
    }

    private static Vector3 V3(JToken o) =>
        o == null ? Vector3.zero : new Vector3((float)o["x"], (float)o["y"], (float)o["z"]);

    private static void SetActiveDeep(GameObject root)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            t.gameObject.SetActive(true);
        }

        root.SetActive(true);
    }
}