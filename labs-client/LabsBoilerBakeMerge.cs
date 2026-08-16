using System;
using System.Collections.Generic;
using Audio.SpatialSystem;
using HarmonyLib;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Manimal.LabsBoiler
{
    // the user's "rebuild the spatial audio ourselves, native bake PLUS our rooms" —
    // implemented as a MERGE into the loaded bake rather than a from-scratch rebuild.
    // mechanism (all verified in the assembly):
    //   * GClass1139.method_0 fires on the main thread after the StreamingAssets bake
    //     is parsed into GClass1138 (rooms/portals registered via FindObjectsOfType,
    //     RoomPairsByID/RelevantRoomPairsByRoomID/route arrays filled, RoomPairIndex
    //     built) and BEFORE GClass1122 is constructed — which is where the room
    //     tracker builds its emitter octree over room bounds. registering our rooms
    //     here means emitters in the new floors finally resolve to OUR rooms: the
    //     actual root cause of every "office sounds like another room" report.
    //   * a pair missing routes data makes GClass1137.PrepareData early-return with
    //     empty job arrays -> best cost stays float.MaxValue -> maximum occlusion.
    //     so each new pair gets real route data, appended to the container's native
    //     arrays (append-grow: old offsets in Dictionary_0/_2 stay valid untouched).
    //   * our portals are staged INACTIVE until game start (their Awake needs the
    //     game-start machinery), so FindObjectsOfType missed them — registered by
    //     hand here, with hand-built static portal data (collider.bounds is invalid
    //     on inactive objects; everything else is serialized and safe).
    [HarmonyPatch]
    internal static class LabsBoilerBakeMerge
    {
        [HarmonyPatch(typeof(GClass1139), nameof(GClass1139.method_0))]
        [HarmonyPostfix]
        private static void AfterBakeLoaded(GClass1138 dataContainer)
        {
            try { Merge(dataContainer); }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[LabsBoiler] bake merge failed: {e}");
                LabsBoilerAcoustics.StartAmbientFallback();
            }
        }

        private static void Merge(GClass1138 c)
        {
            if (c == null || c.IsDisposed) return;
            var ourRooms = new List<SpatialAudioRoom>();
            foreach (var r in LabsBoilerAcoustics.CreatedRooms) if (r != null) ourRooms.Add(r);
            if (ourRooms.Count == 0) return;

            // 1. rooms into the container. Initialize now so IsValid holds for pair
            // lookups (the tracker re-initializes later; Initialize is idempotent-ish
            // via IsInitialized).
            foreach (var r in ourRooms)
            {
                if (!r.IsInitialized)
                {
                    try { r.Initialize(); }
                    catch (Exception e) { Plugin.Log.LogWarning($"[LabsBoiler] merge: room {r.name} Initialize failed: {e.Message}"); }
                }
                c.RoomsByID[r.ID] = r;
            }

            // 2. portals into the container + the static portal data list
            int portalsAdded = 0;
            foreach (var p in LabsBoilerAcoustics.CreatedPortals)
            {
                if (p == null || c.PortalsByID.ContainsKey(p.ID)) continue;
                // pre-Awake the auto-property defaults to 0 = wall-like transmission;
                // ours are authored Open — full transmission until the live portal
                // syncs (UpdateInitialPortalsData / door-state events refresh later)
                p.PortalClosureLevel = 1f;
                c.PortalsByID[p.ID] = p;
                var data = BuildPortalData(p);
                c.Dictionary_1[p.ID] = data;
                c.Gclass1124_0.AddPortal(p.ID, data);
                if (!string.IsNullOrEmpty(p.DoorID)) c.InteractivePortalsByID[p.DoorID] = p;
                portalsAdded++;
            }

            // 3. adjacency over the MERGED portal graph (native + ours)
            var adj = new Dictionary<short, List<(short portalId, short other, Vector3 pos)>>();
            void AddEdge(short a, short b, short portalId, Vector3 pos)
            {
                if (!adj.TryGetValue(a, out var l)) adj[a] = l = new List<(short, short, Vector3)>();
                l.Add((portalId, b, pos));
            }
            foreach (var kv in c.PortalsByID)
            {
                var p = kv.Value;
                if (p == null) continue;
                var f = p.FrontRoom; var b = p.BackRoom;
                if (f == null || b == null || f.ID == b.ID) continue;
                AddEdge(f.ID, b.ID, kv.Key, p.transform.position);
                AddEdge(b.ID, f.ID, kv.Key, p.transform.position);
            }

            int maxDepth = 4;
            try
            {
                var li = MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated
                    ? MonoBehaviourSingleton<SpatialAudioSystem>.Instance.LocationInfo : null;
                if (li != null && li.bakeSettings != null)
                    maxDepth = Math.Max(2, Convert.ToInt32(li.bakeSettings.maxDepthIndoorToIndoor));
            }
            catch { }

            // 4. BFS from each of our rooms up to the bake's propagation depth —
            // fewest portals wins (matches what ShortestRouteLength feeds into
            // GetIsolationFactor), one route per pair
            uint nextPairId = 1;
            foreach (var k in c.RoomPairsByID.Keys) if (k >= nextPairId) nextPairId = k + 1;
            var seen = new HashSet<ValueTuple<short, short>>();
            var newPairs = new List<(RoomPair pair, short[] route, float dist)>();

            foreach (var R in ourRooms)
            {
                var best = new Dictionary<short, (List<short> path, float dist)>();
                var frontier = new List<(short room, List<short> path, float dist, Vector3 pos)>
                    { (R.ID, new List<short>(), 0f, R.transform.position) };
                best[R.ID] = (new List<short>(), 0f);
                for (int depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
                {
                    var next = new List<(short, List<short>, float, Vector3)>();
                    foreach (var (room, path, dist, pos) in frontier)
                    {
                        if (!adj.TryGetValue(room, out var edges)) continue;
                        foreach (var (portalId, other, ppos) in edges)
                        {
                            if (best.ContainsKey(other)) continue;   // first visit = fewest hops
                            var npath = new List<short>(path) { portalId };
                            var ndist = dist + Vector3.Distance(pos, ppos);
                            best[other] = (npath, ndist);
                            next.Add((other, npath, ndist, ppos));
                        }
                    }
                    frontier = next;
                }

                foreach (var kv in best)
                {
                    short x = kv.Key;
                    if (x == R.ID) continue;
                    var key = R.ID < x ? new ValueTuple<short, short>(R.ID, x) : new ValueTuple<short, short>(x, R.ID);
                    if (!seen.Add(key)) continue;                                    // our-room pairs found from both ends
                    if (c.RoomPairIndex != null && c.RoomPairIndex.ContainsKey(key)) continue;  // already baked
                    if (!c.RoomsByID.TryGetValue(x, out var other) || other == null) continue;
                    var route = new RoomPair.Route
                    {
                        PortalIDs = kv.Value.path.ToArray(),
                        HeuristicCost = kv.Value.dist,
                        TraverseDistance = kv.Value.dist
                    };
                    var pair = new RoomPair(nextPairId++, R, other, new[] { route }, route.PortalIDs.Length);
                    newPairs.Add((pair, route.PortalIDs, kv.Value.dist));
                }
            }

            if (newPairs.Count == 0)
            {
                Plugin.Log.LogWarning("[LabsBoiler] bake merge: rooms/portals registered but ZERO new pairs computed — check portal connections");
                LabsBoilerAcoustics.StartAmbientFallback();
                return;
            }

            // 5. pair dictionaries (natives' relevant-arrays grow to include our pairs)
            foreach (var (pair, _, _) in newPairs)
            {
                c.RoomPairsByID[pair.ID] = pair;
                AppendRelevant(c, pair.FirstRoomID, pair);
                AppendRelevant(c, pair.SecondRoomID, pair);
            }

            // 6. routes + portal-index native arrays: append-grow. old entries in
            // Dictionary_0/Dictionary_2 keep their offsets because the old contents are
            // copied to the same positions — only the tail is new.
            int addIdx = 0;
            foreach (var (_, route, _) in newPairs) addIdx += route.Length;

            var oldR = c.NativeArray_0;
            int oldRLen = oldR.IsCreated ? oldR.Length : 0;
            var newR = new NativeArray<AudioRouteData>(oldRLen + newPairs.Count, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            if (oldRLen > 0) NativeArray<AudioRouteData>.Copy(oldR, newR, oldRLen);

            var oldI = c.NativeArray_1;
            int oldILen = oldI.IsCreated ? oldI.Length : 0;
            var newI = new NativeArray<int>(oldILen + addIdx, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            if (oldILen > 0) NativeArray<int>.Copy(oldI, newI, oldILen);

            int ro = oldRLen, io = oldILen, unresolvable = 0;
            foreach (var (pair, route, dist) in newPairs)
            {
                int n = route.Length;
                var idx = new int[n];
                bool ok = true;
                for (int i = 0; i < n; i++)
                {
                    idx[i] = c.Gclass1124_0.GetIndex(route[i]);
                    if (idx[i] < 0) ok = false;
                }
                if (!ok) { n = 0; unresolvable++; }   // 0-portal route = direct segment, lenient but never max-occluded

                newR[ro] = new AudioRouteData
                {
                    PortalDataLength = (short)n,
                    HeuristicCost = dist,
                    StartIndex = 0,       // offsets are relative to THIS pair's subarrays
                    NodeStartIndex = 0
                };
                c.Dictionary_0[pair.ID] = new GClass1138.Struct207 { startIndex = ro, endExclusive = ro + 1 };
                ro++;

                c.Dictionary_2[pair.ID] = new GClass1138.Struct207 { startIndex = io, endExclusive = io + n };
                for (int i = 0; i < n; i++) newI[io++] = idx[i];
            }
            if (oldR.IsCreated) oldR.Dispose();
            c.NativeArray_0 = newR;
            if (oldI.IsCreated) oldI.Dispose();
            c.NativeArray_1 = newI;

            // 7. rebuild the (emitter,listener) index so lookups see our pairs
            c.BuildRoomPairIndex();

            Plugin.Log.LogInfo($"[LabsBoiler] BAKE MERGE: {ourRooms.Count} room(s) + {portalsAdded} portal(s) registered, " +
                               $"{newPairs.Count} new room pair(s) with routes (depth<={maxDepth}" +
                               $"{(unresolvable > 0 ? $", {unresolvable} route(s) degraded to direct" : "")}) — " +
                               "office is now a full member of the baked graph");
        }

        private static void AppendRelevant(GClass1138 c, short roomId, RoomPair pair)
        {
            if (c.RelevantRoomPairsByRoomID.TryGetValue(roomId, out var arr) && arr != null)
            {
                Array.Resize(ref arr, arr.Length + 1);
                arr[arr.Length - 1] = pair;
                c.RelevantRoomPairsByRoomID[roomId] = arr;
                return;
            }
            c.RelevantRoomPairsByRoomID[roomId] = new[] { pair };
        }

        // clone of GClass1138.method_0 minus the two things that break on our staged
        // portals: collider.bounds (invalid while inactive -> world center via
        // TransformPoint) and FrontRoom/BackRoom derefs (synthetic portals have 1 room)
        private static RoomPair.AudioPortalData BuildPortalData(BaseSpatialAudioPortal p)
        {
            var t = p.transform;
            Vector3 center = t.position;
            var box = p.portalCollider;
            if (box != null) center = box.transform.TransformPoint(box.center);
            var front = p.FrontRoom;
            var back = p.BackRoom;
            float2 half;
            try { half = p.GetPortalHalfSize(); }
            catch { half = new float2(0.75f, 1.1f); }
            return new RoomPair.AudioPortalData
            {
                portalNormal = t.forward,
                portalRight = t.right,
                portalUp = t.up,
                portalHalfSize = half,
                closureLevel = 1f,
                traversalMaxCost = p.traversalMaxCost,
                depth = p.Depth,
                center = center,
                frontRoomWallOcclusion = front != null ? front.WallOcclusion : 0.5f,
                backRoomWallOcclusion = back != null ? back.WallOcclusion : 0.5f,
                isFrontRoomOutdoor = front != null && front.IsOutdoor,
                isBackRoomOutdoor = back != null && back.IsOutdoor
            };
        }
    }
}
