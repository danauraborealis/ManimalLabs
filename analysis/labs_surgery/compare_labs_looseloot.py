# compare retail 1.0 labs loose-loot dumps (raid-start getLocalloot responses)
# against SPT's laboratory looseLoot.json:
#  - union the dumps' loose points (dedupe by position), find which have NO spt
#    spawnpoint nearby = NEW spots (retail 1.0 additions; expansion box flagged)
#  - classify every tpl seen at new spots: in spt db / backported by
#    WTT-ContentBackport / truly unknown
import json, glob, os
from collections import defaultdict

DUMPS = r"G:/downloads/laboratory/localloot"
SPT_LOOSE = r"D:/SPTDev/SPT/SPT_Data/database/locations/laboratory/looseLoot.json"
SPT_ITEMS = r"D:/SPTDev/SPT/SPT_Data/database/templates/items.json"
WTT_DB = r"D:/SPTDev/SPT/user/mods/WTT-ContentBackport/db"
OUT = os.path.join(os.path.dirname(__file__), "labs_looseloot_new.json")

# expansion box (boiler office floors 2-3, from boiler_expansion_bounds)
BOX = dict(xmin=-269.1, xmax=-249.5, ymin=3.6, ymax=7.8, zmin=-381.2, zmax=-358.9)
MATCH_R = 0.5   # metres: dump point matches an spt spawnpoint within this

def key(p, r=0.25):
    return (round(p["x"] / r), round(p["y"] / r), round(p["z"] / r))

# ---- 1. union loose points across dumps
points = {}   # poskey -> {pos, ids, tpls(set), seen(count of dumps)}
dumps = sorted(glob.glob(os.path.join(DUMPS, "*.json")))
for f in dumps:
    d = json.load(open(f))["data"]["locationLoot"]["Loot"]
    seen_this = set()
    for e in d:
        if e.get("IsContainer"):
            continue
        p = e["Position"]
        k = key(p)
        if k not in points:
            points[k] = {"pos": p, "ids": set(), "tpls": set(), "seen": 0}
        pt = points[k]
        pt["ids"].add(e.get("Id", ""))
        root = e.get("Root")
        for it in e.get("Items", []):
            if it["_id"] == root:
                pt["tpls"].add(it["_tpl"])
        if k not in seen_this:
            pt["seen"] += 1
            seen_this.add(k)
print(f"{len(dumps)} dumps -> {len(points)} unique loose points")

# ---- 2. spt spawnpoints
spt = json.load(open(SPT_LOOSE))
spt_pts = []
for section in ("spawnpoints", "spawnpointsForced"):
    for sp in spt.get(section, []):
        tp = sp.get("template", {}).get("Position")
        if tp:
            spt_pts.append((tp["x"], tp["y"], tp["z"]))
print(f"spt looseLoot: {len(spt_pts)} spawnpoints")

# coarse grid for the radius match
grid = defaultdict(list)
for x, y, z in spt_pts:
    grid[(round(x), round(z))].append((x, y, z))

def has_spt_near(p):
    for gx in (round(p["x"]) - 1, round(p["x"]), round(p["x"]) + 1):
        for gz in (round(p["z"]) - 1, round(p["z"]), round(p["z"]) + 1):
            for x, y, z in grid.get((gx, gz), []):
                if (x - p["x"])**2 + (y - p["y"])**2 + (z - p["z"])**2 <= MATCH_R**2:
                    return True
    return False

def in_box(p):
    return (BOX["xmin"] <= p["x"] <= BOX["xmax"] and BOX["ymin"] <= p["y"] <= BOX["ymax"]
            and BOX["zmin"] <= p["z"] <= BOX["zmax"])

new_pts = {k: v for k, v in points.items() if not has_spt_near(v["pos"])}
new_box = {k: v for k, v in new_pts.items() if in_box(v["pos"])}
print(f"NEW points (no spt spawnpoint within {MATCH_R}m): {len(new_pts)} "
      f"({len(new_box)} inside the expansion box)")

# ---- 3. tpl classification
spt_items = json.load(open(SPT_ITEMS, encoding="utf-8"))
wtt_text = ""
for f in glob.glob(os.path.join(WTT_DB, "**", "*.json"), recursive=True):
    try:
        wtt_text += open(f, encoding="utf-8", errors="ignore").read()
    except OSError:
        pass

all_new_tpls = set()
for v in new_pts.values():
    all_new_tpls |= v["tpls"]
cls = {}
for t in sorted(all_new_tpls):
    if t in spt_items:
        cls[t] = "spt"
    elif t in wtt_text:
        cls[t] = "wtt-backport"
    else:
        cls[t] = "UNKNOWN"
n_spt = sum(1 for c in cls.values() if c == "spt")
n_wtt = sum(1 for c in cls.values() if c == "wtt-backport")
n_unk = sum(1 for c in cls.values() if c == "UNKNOWN")
print(f"tpls at new points: {len(cls)} distinct -> {n_spt} spt, {n_wtt} wtt-backport, {n_unk} UNKNOWN")
for t, c in cls.items():
    if c != "spt":
        print(f"  {c}: {t}")

# ---- 4. write result
out = {
    "dumps": len(dumps),
    "sptSpawnpoints": len(spt_pts),
    "uniqueDumpPoints": len(points),
    "newPoints": [
        {
            "pos": v["pos"], "seen": v["seen"], "inExpansionBox": in_box(v["pos"]),
            "ids": sorted(v["ids"]), "tpls": sorted(v["tpls"]),
        }
        for v in sorted(new_pts.values(), key=lambda v: (-v["seen"], v["pos"]["x"]))
    ],
    "tplClassification": cls,
}
json.dump(out, open(OUT, "w"), indent=1)
print(f"wrote {OUT}")
