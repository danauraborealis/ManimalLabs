using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Audio.AmbientSubsystem;
using Audio.SpatialSystem;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Manimal.LabsBoiler
{
    // injects the office's spatial-audio ROOMS + TRIGGER AREAS into Labs' already-running
    // SpatialAudioSystem, so the new floors get room detection (→ room tone ambience) and
    // wall occlusion. mechanism (verified in the client assembly):
    //   * the room tracker enumerates rooms via SpatialAudioCrossSceneGroup.AllCrossGroups
    //     (a CrossSceneGroup self-collects its child rooms at Awake) — so we build our
    //     office rooms as children of a NEW CrossSceneGroup and the live system sees them.
    //   * GetIsolationFactor: listener in NO room → 0 (hear through walls, the bug); no
    //     baked room-pair → 1f (full block). we can't regenerate the offline route bake,
    //     but forcing our rooms Isolated makes the 1f fallback block cross-room sound.
    //   * room tone (ambience) plays from the room's AmbientData.RoomTone — an external
    //     clip we don't ship, so we copy it off a live native Labs room.
    // typed against Assembly-CSharp like IcebreakerAcoustics — reflection only for the
    // private serialized fields (_iD/_type/_bounds/_roomSize/Outdoor/Isolated/areaCollider).
    // data: labs_office_audio.json beside the dll. PREFIX on SpatialAudioSystem.Initialize,
    // gated to Labs via the _MX scene. deduped by room _iD so natives never double.
    [HarmonyPatch]
    internal static class LabsBoilerAcoustics
    {
        private const string MxScene = "Laboratory_Office_Above_Boiler_Room_floor_1_MX";
        // scene-lifetime marker instead of a process-lifetime bool: a destroyed Unity
        // object compares == null, so this self-resets at raid teardown and repeat
        // raids in one session re-inject (the old static bool silently skipped them)
        private static GameObject _marker;
        private static bool _done { get { return _marker != null; } set { if (value) _marker = new GameObject("LabsBoiler_AcousticsMarker"); } }

        // what the injector built this raid — the bake merge consumes these
        internal static readonly List<SpatialAudioRoom> CreatedRooms = new List<SpatialAudioRoom>();
        internal static readonly List<SpatialAudioPortal> CreatedPortals = new List<SpatialAudioPortal>();

        [HarmonyPatch(typeof(SpatialAudioSystem), nameof(SpatialAudioSystem.Initialize))]
        [HarmonyPrefix]
        private static void BeforeInit()
        {
            try { Inject(); }
            catch (Exception e) { Plugin.Log.LogError($"[LabsBoiler] acoustics inject failed: {e}"); }
        }

        private static readonly FieldInfo FRoomId = AccessTools.Field(typeof(SpatialAudioRoom), "_iD");
        private static readonly FieldInfo FRoomType = AccessTools.Field(typeof(SpatialAudioRoom), "_type");
        private static readonly FieldInfo FRoomIso = AccessTools.Field(typeof(SpatialAudioRoom), "Isolated");
        private static readonly FieldInfo FRoomOutdoor = AccessTools.Field(typeof(SpatialAudioRoom), "Outdoor");
        private static readonly FieldInfo FRoomBounds = AccessTools.Field(typeof(SpatialAudioRoom), "_bounds");
        private static readonly FieldInfo FRoomSize = AccessTools.Field(typeof(SpatialAudioRoom), "_roomSize");
        private static readonly FieldInfo FAreaCollider = AccessTools.Field(typeof(ServerRoomColliderArea), "areaCollider");
        private static readonly FieldInfo FAreaValidate = AccessTools.Field(typeof(ServerRoomColliderArea), "_validatePlayerCenterInside");

        private static void Inject()
        {
            if (_done) return;
            // gate to Labs: our swapped scene only ever loads there
            if (!UnityEngine.SceneManagement.SceneManager.GetSceneByName(MxScene).isLoaded) return;

            var dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".";
            var jsonPath = Path.Combine(dir, "labs_office_audio.json");
            if (!File.Exists(jsonPath)) { Plugin.Log.LogWarning($"[LabsBoiler] {jsonPath} missing — no office acoustics"); return; }
            var sound = JObject.Parse(File.ReadAllText(jsonPath))["scenes"]?["Laboratory_Sound"] as JObject;
            if (sound == null) return;

            // single path now: inject retail's rooms/areas/portals below, then
            // LabsBoilerBakeMerge registers them into the loaded route bake (rooms and
            // portals join the registries, new RoomPairs + routes are BFS-computed over
            // the merged portal graph) before the room tracker builds its emitter
            // octree — full system membership, which is what every previous audio
            // iteration was missing. piggyback/ambient modes are gone: disproven.
            _fallbackSound = sound;   // ambient fill only ever runs if the merge fails
            _fallbackStarted = false;
            CreatedRooms.Clear();
            CreatedPortals.Clear();

            // native rooms: POSITION-based dedup (BSG REUSES room ids — all 8 office room
            // ids collided with unrelated native rooms and the id-dedup skipped everything,
            // 2026-08-15 raid log) + a RoomTone donor so our rooms sound like Labs. new
            // rooms get fresh ids ABOVE the native max so pair lookups can't cross-match.
            var natives = UnityEngine.Object.FindObjectsOfType<SpatialAudioRoom>(true);
            var nativePos = new List<Vector3>();
            int maxNativeId = 0;
            AudioClip donorTone = null;
            foreach (var nr in natives)
            {
                nativePos.Add(nr.transform.position);
                maxNativeId = Math.Max(maxNativeId, Convert.ToInt32(FRoomId.GetValue(nr)));
                if (donorTone == null && nr.AmbientData?.RoomTone != null)
                    donorTone = nr.AmbientData.RoomTone;
            }
            if (nativePos.Count == 0) { Plugin.Log.LogWarning("[LabsBoiler] no native SpatialAudioRooms yet — acoustics deferred"); return; }
            short nextId = (short)(maxNativeId + 1);

            var areaRows = new Dictionary<long, JToken>();
            foreach (var a in (sound["AudioTriggerArea"] as JArray) ?? new JArray())
                areaRows[a.Value<long>("path_id")] = a;

            // native trigger coverage: retail RE-AUTHORED some office rooms to span space
            // the natives already cover — injecting those areas double-claims it and the
            // listener lands in our force-isolated room while sources resolve native =
            // point-blank muffling (the safe, 2026-08-15 raid, box-verified). only keep
            // area boxes that are genuinely NEW coverage.
            var nativeBoxes = new List<Bounds>();
            foreach (var nta in UnityEngine.Object.FindObjectsOfType<AudioTriggerArea>(true))
            {
                var col = nta.GetComponent<BoxCollider>();
                if (col != null) nativeBoxes.Add(col.bounds);
            }

            bool CoveredByNative(Bounds b)
            {
                foreach (var nb in nativeBoxes)
                {
                    if (!nb.Intersects(b)) continue;
                    // overlap volume vs our box volume — >25% counts as already-covered
                    var min = Vector3.Max(nb.min, b.min);
                    var max = Vector3.Min(nb.max, b.max);
                    var ov = (max.x - min.x) * (max.y - min.y) * (max.z - min.z);
                    var vol = b.size.x * b.size.y * b.size.z;
                    if (vol > 0f && ov / vol > 0.25f) return true;
                }
                return false;
            }

            // built inactive so Awakes only run once everything is wired + parented
            var rootGo = new GameObject("LabsBoiler_Acoustics");
            rootGo.SetActive(false);
            int rooms = 0, areas = 0, skipped = 0;
            // extraction row path_id -> live room (created OR the native it duplicates) —
            // portals resolve their _connectedRooms through this
            var roomByRowId = new Dictionary<long, SpatialAudioRoom>();

            foreach (var rr in (sound["SpatialAudioRoom"] as JArray) ?? new JArray())
            {
                var f = rr["fields"] as JObject;
                if (f == null) continue;
                // native already covers this spot? (0.5m — same-room re-extraction jitter)
                var wt = rr["world_trs"];
                var pos = wt?["pos"] != null ? new Vector3((float)wt["pos"][0], (float)wt["pos"][1], (float)wt["pos"][2]) : Vector3.zero;
                bool exists = false;
                SpatialAudioRoom nativeMatch = null;
                foreach (var nr in natives)
                    if (Vector3.Distance(nr.transform.position, pos) < 0.5f) { exists = true; nativeMatch = nr; break; }
                if (exists)
                {
                    roomByRowId[rr.Value<long>("path_id")] = nativeMatch;   // portals can still connect to it
                    skipped++; continue;
                }

                short iD = nextId++;
                var roomGo = new GameObject($"LabsBoiler_Room_{iD}");
                roomGo.SetActive(false);
                roomGo.transform.SetParent(rootGo.transform, false);
                SetWorld(roomGo.transform, wt);

                var room = roomGo.AddComponent<SpatialAudioRoom>();
                roomByRowId[rr.Value<long>("path_id")] = room;
                FRoomId.SetValue(room, iD);
                FRoomType.SetValue(room, (EAudioRoomTypeMask)(f.Value<int?>("_type") ?? 0));
                // retail-authored isolation: with the bake merge our rooms have REAL
                // pairs, so retail's Isolated flags behave as designed (the old forced
                // values were workarounds for pairless rooms)
                FRoomIso.SetValue(room, (f.Value<int?>("Isolated") ?? 0) != 0);
                FRoomOutdoor.SetValue(room, (f.Value<int?>("Outdoor") ?? 0) != 0);
                FRoomSize.SetValue(room, f.Value<float?>("_roomSize") ?? 0f);
                if (f["_bounds"] is JObject b)
                    FRoomBounds.SetValue(room, new Bounds(V3(b["m_Center"]), V3(b["m_Extent"]) * 2f));
                room.priority = f.Value<int?>("priority") ?? 0;
                room.WallOcclusion = f.Value<float?>("WallOcclusion") ?? 0.5f;
                room.FitToGeometry = false;   // geometry fit needs colliders we don't rebuild — bounds are authored
                room.OnlyWired = (f.Value<int?>("OnlyWired") ?? 1) != 0;

                var amb = new RoomAmbientData();
                if (f["AmbientData"] is JObject ad)
                {
                    amb.RoomToneVolume = ad.Value<float?>("RoomToneVolume") ?? 1f;
                    amb.FadeInSeconds = ad.Value<float?>("FadeInSeconds") ?? 0.25f;
                    amb.FadeOutSeconds = ad.Value<float?>("FadeOutSeconds") ?? 0.5f;
                }
                if (donorTone != null) amb.RoomTone = donorTone;
                room.AmbientData = amb;

                room.Areas = new List<AudioTriggerArea>();
                foreach (var aref in (f["Areas"] as JArray) ?? new JArray())
                {
                    var apid = (aref as JObject)?.Value<long?>("ref");
                    if (apid == null || !areaRows.TryGetValue(apid.Value, out var arow)) continue;
                    // world AABB of this candidate box (unit collider scaled by transform)
                    var awt = arow["world_trs"];
                    var ap = new Vector3((float)awt["pos"][0], (float)awt["pos"][1], (float)awt["pos"][2]);
                    var asc = new Vector3((float)awt["scale"][0], (float)awt["scale"][1], (float)awt["scale"][2]);
                    var candidate = new Bounds(ap, asc);
                    if (CoveredByNative(candidate))
                    {
                        Plugin.Log.LogDebug($"[LabsBoiler] area {apid} skipped — native coverage at {ap}");
                        continue;
                    }
                    var areaGo = new GameObject($"LabsBoiler_Area_{apid}");
                    areaGo.SetActive(false);
                    areaGo.transform.SetParent(roomGo.transform, false);
                    SetWorld(areaGo.transform, awt);
                    var box = areaGo.AddComponent<BoxCollider>();
                    box.isTrigger = true; box.size = Vector3.one; box.center = Vector3.zero;
                    var area = areaGo.AddComponent<AudioTriggerArea>();
                    FAreaCollider?.SetValue(area, box);
                    FAreaValidate?.SetValue(area, (arow["fields"]?.Value<int?>("_validatePlayerCenterInside") ?? 1) != 0);
                    room.Areas.Add(area);
                    areas++;
                    Plugin.Log.LogInfo($"[LabsBoiler] area {apid} kept: center=({ap.x:F1},{ap.y:F1},{ap.z:F1}) size=({asc.x:F1},{asc.y:F1},{asc.z:F1})");
                }
                // a room whose every box is native-covered adds nothing but misdetection
                if (room.Areas.Count == 0)
                {
                    Plugin.Log.LogInfo($"[LabsBoiler] room {iD} dropped — all its area boxes already native-covered");
                    UnityEngine.Object.Destroy(roomGo);
                    nextId--;
                    continue;
                }
                room.roomConnections = new List<SpatialAudioRoom.RoomConnection>();
                CreatedRooms.Add(room);
                rooms++;
            }

            // PORTALS — the fix for "door sounds like it's in another room": door-attached
            // audio (open/close/squeak/keycard beeps) routes through the door↔portal
            // binding (SpatialAudioPortal.DoorID matched to WorldInteractiveObject.Id;
            // CheckOcclusion plays a bound door's OWN sound unoccluded when the listener
            // is in either connected room). without portals every grafted door's sounds
            // fall into the occluded path regardless of distance.
            var nativePortals = UnityEngine.Object.FindObjectsOfType<SpatialAudioPortal>(true);
            int maxPortalId = 0;
            var knownDoorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fPortalId = AccessTools.Field(typeof(BaseSpatialAudioPortal), "_iD");
            var fPortalRooms = AccessTools.Field(typeof(BaseSpatialAudioPortal), "_connectedRooms");
            var fPortalName = AccessTools.Field(typeof(BaseSpatialAudioPortal), "portalName");
            foreach (var np in nativePortals)
            {
                maxPortalId = Math.Max(maxPortalId, Convert.ToInt32(fPortalId.GetValue(np)));
                if (!string.IsNullOrEmpty(np.DoorID)) knownDoorIds.Add(np.DoorID);
            }
            short nextPortalId = (short)(maxPortalId + 1);
            int portals = 0, portalsSkipped = 0;

            // portals live under a SEPARATE root that stays INACTIVE through raid load:
            // portal Awake kicks Subscribe (GlobalEventHandlerClass.Instance) + an async
            // Init awaiting game start — at our prefix timing those singletons don't
            // exist and the load task dies with the NRE that blocked loading (08-15).
            // the watchdog activates this root once the system + world are up.
            var portalsRoot = new GameObject("LabsBoiler_Portals");
            portalsRoot.SetActive(false);

            SpatialAudioPortal MakePortal(string name, Vector3 pos, JToken wt2, string doorId, bool window, List<SpatialAudioRoom> conn)
            {
                var pgo = new GameObject(name);
                pgo.SetActive(false);
                pgo.transform.SetParent(portalsRoot.transform, false);
                if (wt2 != null) SetWorld(pgo.transform, wt2); else pgo.transform.position = pos;
                var pbox = pgo.AddComponent<BoxCollider>();
                pbox.isTrigger = true;   // portal colliders mark openings — a solid one is an invisible wall
                pbox.size = Vector3.one; pbox.center = Vector3.zero;
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
                return portal;
            }

            foreach (var pr in (sound["SpatialAudioPortal"] as JArray) ?? new JArray())
            {
                var f = pr["fields"] as JObject;
                var wt2 = pr["world_trs"];
                if (f == null || wt2?["pos"] == null) continue;
                var ppos = new Vector3((float)wt2["pos"][0], (float)wt2["pos"][1], (float)wt2["pos"][2]);
                bool nativeHas = false;
                foreach (var np in nativePortals)
                    if (Vector3.Distance(np.transform.position, ppos) < 0.5f) { nativeHas = true; break; }
                if (nativeHas) { portalsSkipped++; continue; }

                var conn = new List<SpatialAudioRoom>();
                foreach (var cr in (f["_connectedRooms"] as JArray) ?? new JArray())
                {
                    var rid = (cr as JObject)?.Value<long?>("ref");
                    if (rid != null && roomByRowId.TryGetValue(rid.Value, out var r0) && r0 != null && !conn.Contains(r0))
                        conn.Add(r0);
                }
                if (conn.Count == 0) { portalsSkipped++; continue; }   // connects nothing we know

                var doorId = f.Value<string>("DoorID") ?? "";
                bool window = doorId.StartsWith("window", StringComparison.OrdinalIgnoreCase)
                              || (f.Value<string>("portalName") ?? "").StartsWith("AudioWindowPortal");
                MakePortal($"LabsBoiler_Portal_{doorId}", ppos, wt2, doorId, window, conn);
                portals++;
            }

            // synthetic portals for grafted doors retail never gave one (the SAFE) — an
            // Open portal bound to the door's Id, connected to the room that contains it,
            // so the door's own sounds resolve unoccluded beside it
            foreach (var door in UnityEngine.Object.FindObjectsOfType<EFT.Interactive.Door>(true))
            {
                var id = door.Id;
                if (string.IsNullOrEmpty(id) || knownDoorIds.Contains(id)) continue;
                if (!id.Contains("Office_Above_Boiler_Room") && !id.Contains("345345")) continue; // ours only
                // bind to OUR injected rooms ONLY — the |Δy| transform filter still picked
                // the basement boiler room (room transforms sit far from their volumes,
                // 08-15 log). prefer the injected room whose trigger box CONTAINS the door;
                // fall back to the nearest injected room.
                SpatialAudioRoom home = null; float best = 25f;
                foreach (var kv in roomByRowId)
                {
                    var cand = kv.Value;
                    if (cand == null || !cand.name.StartsWith("LabsBoiler_")) continue;
                    bool contains = false;
                    foreach (var a in cand.Areas ?? new List<AudioTriggerArea>())
                    {
                        var col = a != null ? a.GetComponent<BoxCollider>() : null;
                        if (col != null && new Bounds(a.transform.position, Vector3.Scale(col.size, a.transform.lossyScale)).Contains(door.transform.position))
                        { contains = true; break; }
                    }
                    float dd = contains ? 0f : Vector3.Distance(cand.transform.position, door.transform.position);
                    if (dd < best) { best = dd; home = cand; }
                }
                if (home == null) continue;
                MakePortal($"LabsBoiler_Portal_synth_{id}", door.transform.position, null, id, false,
                           new List<SpatialAudioRoom> { home });
                portals++;
                Plugin.Log.LogInfo($"[LabsBoiler] synthetic portal for doorless '{id}' -> room '{home.name}' ({best:F1}m)");
            }

            // the CrossSceneGroup on the root self-collects child rooms/portals at Awake
            // and registers into AllCrossGroups — the ONLY path the room tracker enumerates
            rootGo.AddComponent<SpatialAudioCrossSceneGroup>();
            SetActiveDeep(rootGo);
            _pendingPortalsRoot = portalsRoot;   // activated by the watchdog post-game-start
            Plugin.Log.LogInfo($"[LabsBoiler] portals: {portals} staged (inactive), {portalsSkipped} skipped (native/unresolvable)");

            _done = true;
            Plugin.Log.LogInfo($"[LabsBoiler] acoustics injected: {rooms} office room(s) + {areas} trigger area(s) " +
                               $"({skipped} already native; tone '{(donorTone != null ? donorTone.name : "none")}')");

            // WATCHDOG: a room is IsValid only once Initialize() ran (it also creates the
            // RoomAmbientSoundPlayer = the tone). if the system enumerated rooms before our
            // registration, ours never initialize → invalid → no detection, no tone. wait
            // for the system, then self-initialize any injected room left behind + report.
            if (Plugin.Instance != null) Plugin.Instance.StartCoroutine(InitWatchdog(rootGo));
        }

        // EMERGENCY FALLBACK ONLY — fires when the bake merge fails, so a broken raid
        // degrades to "office tone plays" instead of dead silence. no config.
        private static JObject _fallbackSound;
        private static bool _fallbackStarted;

        internal static void StartAmbientFallback()
        {
            if (_fallbackStarted || _fallbackSound == null || Plugin.Instance == null) return;
            _fallbackStarted = true;
            Plugin.Log.LogWarning("[LabsBoiler] bake merge unavailable — falling back to plain ambient fill");
            Plugin.Instance.StartCoroutine(AmbientFill(_fallbackSound));
        }

        // ambient fill: one looping room-tone source per trigger-area footprint, tone +
        // mixer group borrowed from a live native room so it matches Labs. fallback-only.
        private static System.Collections.IEnumerator AmbientFill(JObject sound)
        {
            // wait for the native rooms to exist and carry a tone
            AudioClip tone = null;
            for (int i = 0; i < 300 && tone == null; i++)
            {
                foreach (var nr in UnityEngine.Object.FindObjectsOfType<SpatialAudioRoom>())
                    if (nr.AmbientData?.RoomTone != null) { tone = nr.AmbientData.RoomTone; break; }
                if (tone == null) yield return null;
            }
            if (tone == null) { Plugin.Log.LogWarning("[LabsBoiler] no native RoomTone found — ambient fill skipped"); yield break; }
            UnityEngine.Audio.AudioMixerGroup group = null;
            foreach (var s in UnityEngine.Object.FindObjectsOfType<AudioSource>(true))
                if (s.outputAudioMixerGroup != null) { group = s.outputAudioMixerGroup; break; }

            int made = 0;
            foreach (var a in (sound["AudioTriggerArea"] as JArray) ?? new JArray())
            {
                var wt = a["world_trs"];
                if (wt?["pos"] == null) continue;
                var pos = new Vector3((float)wt["pos"][0], (float)wt["pos"][1], (float)wt["pos"][2]);
                var scl = wt["scale"] != null
                    ? new Vector3((float)wt["scale"][0], (float)wt["scale"][1], (float)wt["scale"][2])
                    : Vector3.one * 6f;
                var go = new GameObject($"LabsBoiler_Ambience_{made}");
                go.transform.position = pos;
                var src = go.AddComponent<AudioSource>();
                src.clip = tone;
                src.loop = true;
                src.spatialBlend = 1f;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = 2.5f;
                src.maxDistance = Mathf.Max(scl.x, scl.z) * 0.9f + 4f;
                src.volume = 0.5f;
                if (group != null) src.outputAudioMixerGroup = group;
                src.Play();
                made++;
            }
            Plugin.Log.LogInfo($"[LabsBoiler] ambient FALLBACK: {made} tone source(s) placed (tone '{tone.name}')");
        }

        private static GameObject _pendingPortalsRoot;

        private static System.Collections.IEnumerator InitWatchdog(GameObject rootGo)
        {
            // give the system up to ~30s (bake load is async on some rigs)
            for (int i = 0; i < 1800 && !SpatialAudioSystem.Initialized; i++) yield return null;
            if (rootGo == null) yield break;

            // activate the staged portals only once the WORLD exists — portal Awake
            // subscribes to global events + awaits game start; at inject time (preset
            // load prefix) those singletons are absent and the load task NREs
            for (int i = 0; i < 1800 && !Comfort.Common.Singleton<EFT.GameWorld>.Instantiated; i++) yield return null;
            yield return new WaitForSeconds(1f);
            if (_pendingPortalsRoot != null)
            {
                foreach (var t in _pendingPortalsRoot.GetComponentsInChildren<Transform>(true))
                    t.gameObject.SetActive(true);
                _pendingPortalsRoot.SetActive(true);
                Plugin.Log.LogInfo($"[LabsBoiler] portals activated post-game-start ({_pendingPortalsRoot.transform.childCount})");
            }
            int lateInit = 0, valid = 0, total = 0;
            foreach (var room in rootGo.GetComponentsInChildren<SpatialAudioRoom>(true))
            {
                total++;
                if (!room.IsInitialized) { try { room.Initialize(); lateInit++; } catch (Exception e) { Plugin.Log.LogWarning($"[LabsBoiler] room {room.name} Initialize failed: {e.Message}"); } }
                if (room.IsValid) valid++;
            }
            Plugin.Log.LogInfo($"[LabsBoiler] acoustics watchdog: systemInit={SpatialAudioSystem.Initialized}, " +
                               $"{valid}/{total} injected room(s) valid, {lateInit} late-initialized");

            // listener-room transition logger — ground truth for every "muffled/silent
            // here" report: which room owns the player, ours or native. cheap (2 Hz).
            var sys = MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated
                ? MonoBehaviourSingleton<SpatialAudioSystem>.Instance : null;
            short lastId = short.MinValue;
            while (sys != null)
            {
                var cur = sys.ListenerCurrentRoom;
                short id = (short)(cur?.IsValid == true ? cur.ID : -1);
                if (id != lastId)
                {
                    lastId = id;
                    string label = "NONE (unroomed — hear-through zone)";
                    if (cur?.IsValid == true)
                    {
                        var c = cur as Component;
                        bool ours = c != null && c.gameObject.name.StartsWith("LabsBoiler_");
                        label = $"{(ours ? "OURS" : "native")} id={cur.ID}{(c != null ? $" '{c.gameObject.name}'" : "")}";
                    }
                    Plugin.Log.LogInfo($"[LabsBoiler] listener room -> {label}");
                }
                yield return new WaitForSeconds(0.5f);
            }
        }

        private static void SetWorld(Transform t, JToken wt)
        {
            if (wt == null) return;
            var p = wt["pos"]; var r = wt["rot"]; var s = wt["scale"];
            if (p != null) t.position = new Vector3((float)p[0], (float)p[1], (float)p[2]);
            if (r != null) t.rotation = new Quaternion((float)r[0], (float)r[1], (float)r[2], (float)r[3]);
            if (s != null) t.localScale = new Vector3((float)s[0], (float)s[1], (float)s[2]);
        }

        private static Vector3 V3(JToken o) =>
            o == null ? Vector3.zero : new Vector3((float)o["x"], (float)o["y"], (float)o["z"]);

        private static void SetActiveDeep(GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.SetActive(true);
            root.SetActive(true);
        }
    }
}
