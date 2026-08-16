# build server-mod looseLoot spawnpoints from the retail 1.0 dumps — FULL MAP
# parity: every loose point (map-wide) that SPT's looseLoot doesn't cover within
# 0.5m. probability = raids-seen/raids-total, candidates = observed root items
# (full trees), itemDistribution weighted by observation count.
#
# WTT AUGMENTATION (user request): WTT-ContentBackport adds 1.0 items that never
# got loose-loot placement (27 new jewelry etc.). for an allowlisted set of
# generic-valuable classes, every WTT item of a class already present at a point
# is added as an extra candidate. added candidates share a weight budget of 50%
# of the point's observed total, so dump-observed loot stays dominant.
# keys/keycards and quest-adjacent classes (Info/Flyer) are deliberately NOT
# augmented. UNKNOWN tpls (not spt, not WTT) drop here; the server mod also
# filters against the live db so WTT-less installs degrade gracefully.
import json, glob, os, hashlib
from collections import defaultdict

DUMPS = r"G:/downloads/laboratory/localloot"
SPT_LOOSE = r"D:/SPTDev/SPT/SPT_Data/database/locations/laboratory/looseLoot.json"
SPT_ITEMS = r"D:/SPTDev/SPT/SPT_Data/database/templates/items.json"
WTT_DB = r"D:/SPTDev/SPT/user/mods/WTT-ContentBackport/db/CustomItems"
OUT = r"C:/Users/peard/Desktop/ManimalLabs/labs-server/db/labs_office_looseloot.json"

MATCH_R = 0.5
# MAP-EXCLUSIVE items that must never join Labs pools even though their class
# matches — Icebreaker-unique loot (user call 2026-08-16): no other map gets these
EXCLUDE_TPLS = {
    "69bb4203f94327bc0f0230cd",  # Giga Chad processor
    "69bb424e99f3fda8f107247d",  # Memento Server RAM Module
    "69bb41c03b5fb75517065960",  # Ultra Link
    "699f09de81a6c812900a77b7",  # AMG-10 fluid
    "69bb43df99f3fda8f1072483",  # Nooby Shield tablets
    # user calls (2026-08-16): bitcoin halves out (quest-chain pieces), moreman
    # out; icebreaker models are FINE (ship replicas, map-agnostic souvenirs)
    "68f119c6121d878a2303eee3",  # Right half of a Physical Bitcoin
    "68f11adfcd0babab2c0fb003",  # Left half of a Physical Bitcoin
    "68da4fa81ddecb1cc0077aee",  # Moreman's dogtag
    "69bb435ff609db77390b0e25",  # Aceso Xpress analyzer — icebreaker item
    "68e6394e658d876c930977b1",  # Unknown device fragment — provenance unknown, out
}

# classes safe to pool-augment with WTT items: generic valuables only.
# "Other" (590c745b) DROPPED after the item report: WTT's backports there are
# 24 dogtag variants + Ref's Caches + a crate — nonsense as floor loot.
AUGMENT_CLASSES = {
    "57864a3d24597754843f8721",  # Jewelry
    "57864a66245977548f04a81f",  # Electronics
    "57864c8c245977548867e7f1",  # MedicalSupplies
    "57864e4c24597754843f8723",  # Lubricant
}

def key(p, r=0.25):
    return (round(p["x"] / r), round(p["y"] / r), round(p["z"] / r))

spt_loose = json.load(open(SPT_LOOSE))
spt_pts = [sp["template"]["Position"] for s in ("spawnpoints", "spawnpointsForced")
           for sp in spt_loose.get(s, []) if sp.get("template", {}).get("Position")]
grid = defaultdict(list)
for q in spt_pts:
    grid[(round(q["x"]), round(q["z"]))].append(q)

def spt_has(p):
    for gx in (round(p["x"]) - 1, round(p["x"]), round(p["x"]) + 1):
        for gz in (round(p["z"]) - 1, round(p["z"]), round(p["z"]) + 1):
            for q in grid.get((gx, gz), []):
                if (q["x"]-p["x"])**2 + (q["y"]-p["y"])**2 + (q["z"]-p["z"])**2 <= MATCH_R**2:
                    return True
    return False

spt_items = json.load(open(SPT_ITEMS, encoding="utf-8"))

# WTT items: id -> parent class (CustomItems only — quest items excluded by path).
# augmentation uses THIS structured set; tpl VALIDITY uses the broad text scan of
# the whole WTT db (quest docs live in CustomQuestItems with a different shape).
wtt = {}
for f in glob.glob(os.path.join(WTT_DB, "**", "*.json"), recursive=True):
    try:
        d = json.load(open(f, encoding="utf-8"))
    except Exception:
        continue
    if isinstance(d, dict):
        for k, v in d.items():
            if isinstance(v, dict) and "parentId" in v and len(k) == 24:
                # never pool anything flagged as a quest item (user rule)
                if (v.get("overrideProperties") or {}).get("QuestItem"):
                    continue
                wtt[k] = v["parentId"]

wtt_text = ""
for f in glob.glob(os.path.join(os.path.dirname(WTT_DB), "**", "*.json"), recursive=True):
    try:
        wtt_text += open(f, encoding="utf-8", errors="ignore").read()
    except OSError:
        pass

def cls(t):
    if t in spt_items: return spt_items[t].get("_parent")
    return wtt.get(t)

def tpl_ok(t):
    return t in spt_items or t in wtt_text

wtt_by_class = defaultdict(list)
for i, c in wtt.items():
    if c in AUGMENT_CLASSES and i not in EXCLUDE_TPLS:
        wtt_by_class[c].append(i)

# ---- collect new points from dumps.
# group by (id-prefix, position): position alone MERGED co-located distinct
# points — at the safe, q_card_key/q_master_access sit within the 0.25m grid of
# the lab_krug valuables slots, which turned "bitcoin AND key AND keycard" into
# "bitcoin OR key OR keycard" (user caught it via a Prapor figurine in the safe).
# also dedupe within a raid — the dumps list safe entries twice.
def id_prefix_of(e):
    return e.get("Id", "").split(" [")[0]

points = {}
dumps = sorted(glob.glob(os.path.join(DUMPS, "*.json")))
for f in dumps:
    seen_this = set()
    for e in json.load(open(f))["data"]["locationLoot"]["Loot"]:
        if e.get("IsContainer") or spt_has(e["Position"]):
            continue
        k = (id_prefix_of(e), key(e["Position"]))
        if k in seen_this:
            continue
        seen_this.add(k)
        pt = points.setdefault(k, {"entries": [], "seen": set()})
        pt["entries"].append(e)
        pt["seen"].add(f)

def fake_id(seed):
    return hashlib.md5(seed.encode()).hexdigest()[:24]

spawnpoints, dropped_tpls, augmented_pts, augment_added = [], set(), 0, 0
for k, pt in sorted(points.items()):
    first = pt["entries"][0]
    by_tpl, counts = {}, defaultdict(int)
    for e in pt["entries"]:
        root_item = next((i for i in e["Items"] if i["_id"] == e["Root"]), None)
        if root_item is None:
            continue
        t = root_item["_tpl"]
        if not tpl_ok(t):
            dropped_tpls.add(t)
            continue
        counts[t] += 1
        by_tpl.setdefault(t, e)
    if not by_tpl:
        continue

    # NO-LOOT BUG (raid-verified 2026-08-16): SPT's generator matches
    # itemDistribution.composedKey.key against each item's OWN "composedKey"
    # field (LocationLootGenerator.cs:797/820) — NOT its _id. the raid dumps
    # don't carry composedKey (it's an SPT database-ism), so without stamping it
    # every candidate failed validation and loot generation died. stamp the root
    # item of each candidate with its _id as the key and reference that.
    items, dist = [], []
    for t, e in by_tpl.items():
        for it in e["Items"]:
            it = dict(it)
            if it["_id"] == e["Root"]:
                it["composedKey"] = e["Root"]
            items.append(it)
        dist.append({"composedKey": {"key": e["Root"]}, "relativeProbability": counts[t]})

    # WTT augmentation — POOL points only. curated points (the safe's authored
    # slots, quest spawns, keycard spawns) must never get random figurines:
    # augment only when EVERY observed candidate is an augmentable-class item
    # AND the point isn't a curated id (lab_krug_*, q_*, Labcards, quest*).
    idp = id_prefix_of(first)
    curated = idp.startswith(("lab_krug", "q_", "Labcards", "quest", "item_info"))
    all_augmentable = all(cls(t) in AUGMENT_CLASSES for t in by_tpl)
    pool_classes = {cls(t) for t in by_tpl} & AUGMENT_CLASSES if (all_augmentable and not curated) else set()
    add_tpls = sorted({i for c in pool_classes for i in wtt_by_class[c]} - set(by_tpl))
    if add_tpls:
        # no weight floor: the 0.1 floor let 27 added items reach 73-79% share on
        # single-observation points — budget/n keeps the cap honest everywhere
        budget = 0.5 * sum(d["relativeProbability"] for d in dist)
        w = round(budget / len(add_tpls), 4)
        for t in add_tpls:
            iid = fake_id(f"{first['Id']}|{t}")
            items.append({"composedKey": iid, "_id": iid, "_tpl": t, "upd": {"StackObjectsCount": 1}})
            dist.append({"composedKey": {"key": iid}, "relativeProbability": w})
        augmented_pts += 1
        augment_added += len(add_tpls)

    # retail-forced: present EVERY raid with a single item = guaranteed spawn
    # (intel folder, cardinal key, master keycard, the quest notes). SPT's forced
    # path takes Items.FirstOrDefault() and ignores itemDistribution, so only
    # single-candidate points can be forced; multi-candidate every-raid slots
    # (bitcoin-or-roler) stay pool-drawn at p=1.0.
    forced = len(pt["seen"]) == len(dumps) and len(by_tpl) == 1 and not add_tpls

    spawnpoints.append({
        "locationId": f"({first['Position']['x']:.3f}, {first['Position']['y']:.3f}, {first['Position']['z']:.3f})",
        "probability": round(len(pt["seen"]) / len(dumps), 3),
        "template": {
            "Id": first["Id"],
            "IsContainer": False,
            "useGravity": first.get("useGravity", True),
            "randomRotation": first.get("randomRotation", False),
            "Position": first["Position"],
            "Rotation": first["Rotation"],
            "IsAlwaysSpawn": forced,
            "IsGroupPosition": False,
            "GroupPositions": [],
            "Root": list(by_tpl.values())[0]["Root"],
            "Items": items,
        },
        "itemDistribution": dist,
    })

# ---- Kruglov desk textbook clones (user request): the wiki lists both
# textbooks on Kruglov's desk, but all 7 dumps lack them (that profile never
# rolled the pools). SPT's native Labs RES UNIT desk already carries them in
# its 4 'infoitem' pool points — clone those wholesale (pools, weights, native
# probabilities) onto Kruglov's desk, preserving their relative layout.
# anchor: midpoint of the loot_intel_docs1 (26)/(27) paper piles — those ARE the
# papers flanking the monitor in the SDK (desk surface y=6.0, south wall). the
# clones sit tightly among the papers (user: "around these papers near the
# computer"), two left of the keyboard, two right, instead of the res-unit's
# original 1.8m sprawl.
DESK_ANCHOR = (-264.8, 6.0, -376.85)
DESK_OFFSETS = [(-0.55, 0.1), (-0.3, -0.1), (0.35, -0.05), (0.6, 0.1)]
TB_TPLS = {"6389c8fb46b54c634724d847", "6389c92d52123d5dd17f8876"}
import copy
src_pts = [sp for sp in spt_loose.get("spawnpoints", [])
           if any(i["_tpl"] in TB_TPLS for i in sp.get("template", {}).get("Items", []))]
for n, sp in enumerate(src_pts):
    c = copy.deepcopy(sp)
    t = c["template"]
    ox, oz = DESK_OFFSETS[n % len(DESK_OFFSETS)]
    t["Position"] = {"x": round(DESK_ANCHOR[0] + ox, 3), "y": DESK_ANCHOR[1],
                     "z": round(DESK_ANCHOR[2] + oz, 3)}
    t["Id"] = f"krug_desk_infoitem ({n}) [manimal-labsboiler]"
    c["locationId"] = f"({t['Position']['x']}, {t['Position']['y']}, {t['Position']['z']})"
    spawnpoints.append(c)
print(f"cloned {len(src_pts)} res-unit infoitem point(s) around the Kruglov desk papers (textbook pools, native odds)")


json.dump({"spawnpoints": spawnpoints}, open(OUT, "w"), indent=1)
print(f"{len(dumps)} dumps -> {len(points)} new points map-wide -> {len(spawnpoints)} spawnpoints written")
print(f"WTT augmentation: {augment_added} candidate(s) added across {augmented_pts} point(s)")
print(f"dropped unknown tpls: {sorted(dropped_tpls) or 'none'}")
