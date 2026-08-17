using System;
using System.Collections.Generic;
using Audio.SpatialSystem;
using HarmonyLib;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Manimal.LabsBoiler;

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
        foreach (var r in LabsBoilerAcoustics.CreatedRooms) if (r) ourRooms.Add(r);
        if (ourRooms.Count == 0) return;

        foreach (var r in ourRooms)
        {
            if (!r.IsInitialized)
            {
                try { r.Initialize(); }
                catch (Exception e) { Plugin.Log.LogWarning($"[LabsBoiler] merge: room {r.name} Initialize failed: {e.Message}"); }
            }
            c.RoomsByID[r.ID] = r;
        }

        var portalsAdded = 0;
        foreach (var p in LabsBoilerAcoustics.CreatedPortals)
        {
            if (!p || c.PortalsByID.ContainsKey(p.ID)) continue;
            p.PortalClosureLevel = 1f;
            c.PortalsByID[p.ID] = p;
            var data = BuildPortalData(p);
            c.Dictionary_1[p.ID] = data;
            c.Gclass1124_0.AddPortal(p.ID, data);
            if (!string.IsNullOrEmpty(p.DoorID)) c.InteractivePortalsByID[p.DoorID] = p;
            portalsAdded++;
        }

        var adj = new Dictionary<short, List<(short portalId, short other, Vector3 pos)>>();
        foreach (var (key, p) in c.PortalsByID)
        {
            if (!p) continue;
            var f = p.FrontRoom; var b = p.BackRoom;
            if (!f || !b || f.ID == b.ID) continue;
            AddEdge(f.ID, b.ID, key, p.transform.position, adj);
            AddEdge(b.ID, f.ID, key, p.transform.position, adj);
        }

        var maxDepth = 4;
        try
        {
            var li = MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated ? MonoBehaviourSingleton<SpatialAudioSystem>.Instance.LocationInfo : null;
            if (li && li.bakeSettings != null)
            {
                maxDepth = Math.Max(2, Convert.ToInt32(li.bakeSettings.maxDepthIndoorToIndoor));
            }
        }
        catch(Exception e)
        {
            Plugin.Log.LogError(e);
        }

        uint nextPairId = 1;
        foreach (var k in c.RoomPairsByID.Keys) if (k >= nextPairId) nextPairId = k + 1;
        var seen = new HashSet<ValueTuple<short, short>>();
        var newPairs = new List<(RoomPair pair, short[] route, float dist)>();

        foreach (var r in ourRooms)
        {
            var best = new Dictionary<short, (List<short> path, float dist)>();
            var frontier = new List<(short room, List<short> path, float dist, Vector3 pos)> { (r.ID, [], 0f, r.transform.position) };
            
            best[r.ID] = ([], 0f);
            for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
            {
                var next = new List<(short, List<short>, float, Vector3)>();
                foreach (var (room, path, dist, pos) in frontier)
                {
                    if (!adj.TryGetValue(room, out var edges)) continue;
                    foreach (var (portalId, other, ppos) in edges)
                    {
                        if (best.ContainsKey(other)) continue;
                        var nPath = new List<short>(path) { portalId };
                        var nDist = dist + Vector3.Distance(pos, ppos);
                        best[other] = (nPath, nDist);
                        next.Add((other, nPath, nDist, ppos));
                    }
                }
                frontier = next;
            }

            foreach (var (x, value) in best)
            {
                if (x == r.ID) continue;
                var key = r.ID < x ? new ValueTuple<short, short>(r.ID, x) : new ValueTuple<short, short>(x, r.ID);
                if (!seen.Add(key)) continue;
                if (c.RoomPairIndex != null && c.RoomPairIndex.ContainsKey(key)) continue;
                if (!c.RoomsByID.TryGetValue(x, out var other) || !other) continue;
                var route = new RoomPair.Route
                {
                    PortalIDs = [.. value.path],
                    HeuristicCost = value.dist,
                    TraverseDistance = value.dist
                };
                var pair = new RoomPair(nextPairId++, r, other, [route], route.PortalIDs.Length);
                newPairs.Add((pair, route.PortalIDs, value.dist));
            }
        }

        if (newPairs.Count == 0)
        {
            Plugin.Log.LogWarning("[LabsBoiler] bake merge: rooms/portals registered but ZERO new pairs computed — check portal connections");
            LabsBoilerAcoustics.StartAmbientFallback();
            return;
        }

        foreach (var (pair, _, _) in newPairs)
        {
            c.RoomPairsByID[pair.ID] = pair;
            AppendRelevant(c, pair.FirstRoomID, pair);
            AppendRelevant(c, pair.SecondRoomID, pair);
        }
        
        var addIdx = 0;
        foreach (var (_, route, _) in newPairs) addIdx += route.Length;

        var oldR = c.NativeArray_0;
        var oldRLen = oldR.IsCreated ? oldR.Length : 0;
        var newR = new NativeArray<AudioRouteData>(oldRLen + newPairs.Count, Allocator.Persistent);
        if (oldRLen > 0) NativeArray<AudioRouteData>.Copy(oldR, newR, oldRLen);

        var oldI = c.NativeArray_1;
        var oldILen = oldI.IsCreated ? oldI.Length : 0;
        var newI = new NativeArray<int>(oldILen + addIdx, Allocator.Persistent);
        if (oldILen > 0) NativeArray<int>.Copy(oldI, newI, oldILen);

        int ro = oldRLen, io = oldILen, unresolvable = 0;
        foreach (var (pair, route, dist) in newPairs)
        {
            var n = route.Length;
            var idx = new int[n];
            var ok = true;
            for (var i = 0; i < n; i++)
            {
                idx[i] = c.Gclass1124_0.GetIndex(route[i]);
                if (idx[i] < 0) ok = false;
            }
            if (!ok) { n = 0; unresolvable++; }

            newR[ro] = new AudioRouteData
            {
                PortalDataLength = (short)n,
                HeuristicCost = dist,
                StartIndex = 0,
                NodeStartIndex = 0
            };
            c.Dictionary_0[pair.ID] = new GClass1138.Struct207 { startIndex = ro, endExclusive = ro + 1 };
            ro++;

            c.Dictionary_2[pair.ID] = new GClass1138.Struct207 { startIndex = io, endExclusive = io + n };
            for (var i = 0; i < n; i++) newI[io++] = idx[i];
        }
        if (oldR.IsCreated) oldR.Dispose();
        c.NativeArray_0 = newR;
        if (oldI.IsCreated) oldI.Dispose();
        c.NativeArray_1 = newI;

        c.BuildRoomPairIndex();

        Plugin.Log.LogInfo($"[LabsBoiler] BAKE MERGE: {ourRooms.Count} room(s) + {portalsAdded} portal(s) registered, " +
                           $"{newPairs.Count} new room pair(s) with routes (depth<={maxDepth}" +
                           $"{(unresolvable > 0 ? $", {unresolvable} route(s) degraded to direct" : "")}) — " +
                           "office is now a full member of the baked graph");
    }

    private static void AddEdge(short a, short b, short portalId, Vector3 pos, Dictionary<short, List<(short portalId, short other, Vector3 pos)>> adj)
    {
        if (!adj.TryGetValue(a, out var l)) adj[a] = l = [];
        l.Add((portalId, b, pos));
    }

    private static void AppendRelevant(GClass1138 c, short roomId, RoomPair pair)
    {
        if (c.RelevantRoomPairsByRoomID.TryGetValue(roomId, out var arr) && arr != null)
        {
            Array.Resize(ref arr, arr.Length + 1);
            arr[^1] = pair;
            c.RelevantRoomPairsByRoomID[roomId] = arr;
            return;
        }
        c.RelevantRoomPairsByRoomID[roomId] = [pair];
    }

    private static RoomPair.AudioPortalData BuildPortalData(BaseSpatialAudioPortal p)
    {
        var t = p.transform;
        var center = t.position;
        var box = p.portalCollider;
        if (box) center = box.transform.TransformPoint(box.center);
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
            frontRoomWallOcclusion = front ? front.WallOcclusion : 0.5f,
            backRoomWallOcclusion = back ? back.WallOcclusion : 0.5f,
            isFrontRoomOutdoor = front && front.IsOutdoor,
            isBackRoomOutdoor = back && back.IsOutdoor
        };
    }
}