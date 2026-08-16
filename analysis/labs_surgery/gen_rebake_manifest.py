# flatten the 5 extraction JSONs into ONE rebake manifest the editor tool applies.
# every retail MonoBehaviour ripped as a husk (Mono-mode rip, no game assembly), so
# each grafted stub needs its serialized values pushed back. matching is by WORLD
# POSITION + component type (graft-invariant — reparenting preserved world pos), not
# hierarchy path. Python does the cross-JSON joins; the C# side stays a dumb setter.
#
# field value encoding the C# setter understands:
#   number            -> float/int/bool field
#   {x,y,z}           -> Vector3      {x,y,z,w} -> Quaternion   {r,g,b,a} -> Color
#   {m_Curve:[...]}   -> AnimationCurve
#   "str"             -> string
#   {"__ref":"light"} -> resolve at bake time to the Light/BaseLight on this GO
#   {"__ref":"handle"}-> resolve to the DoorHandle in this GO's children
# anything unresolvable (externalRef/unresolvedRef/arrays) is dropped here.
import json
from pathlib import Path

HERE = Path(__file__).parent
out = {"rebakes": []}

REF_FIELDS = {"_light": "light", "LockHandle": "handle", "_handle": "handle"}
DROP = {"m_GameObject", "m_Enabled", "m_Script", "m_Name", "_transform"}


def clean(fields):
    o = {}
    for k, v in fields.items():
        if k in DROP:
            continue
        if k in REF_FIELDS:
            o[k] = {"__ref": REF_FIELDS[k]}
            continue
        if isinstance(v, dict):
            if any(t in v for t in ("externalRef", "unresolvedRef", "refPath")):
                continue  # unresolvable cross-object ref — skip
            if not v:
                continue
            o[k] = v  # vector/color/curve pass through
        elif isinstance(v, list):
            continue  # arrays (sounds/triggers/turn-off lists) not needed for v1
        elif v is None:
            continue
        else:
            o[k] = v
    return o


def add(comp_type, world, name, fields):
    f = clean(fields)
    if f:
        out["rebakes"].append({"type": comp_type, "world": [round(x, 3) for x in world],
                               "name": name, "fields": f})


# 1) CullingLightObject — join lamp wrappers (boiler_lamps) to positions (boiler_lights)
lamps = json.load(open(HERE / "boiler_lamps.json"))
lights = json.load(open(HERE / "boiler_lights.json"))
pos_by_go = {l["go"]: l["world"] for l in lights["newLights"]}
name_by_go = {l["go"]: l["name"] for l in lights["newLights"]}
n_cl = 0
for row in lamps["lights"]:
    go = row["go"]
    cl = row["wrappers"].get("CullingLightObject")
    if cl and "parse_error" not in cl and go in pos_by_go:
        add("CullingLightObject", pos_by_go[go], name_by_go.get(go, "?"), cl)
        n_cl += 1

# 2) AreaLight + CullingAdvancedLightObject (boiler_arealights)
al = json.load(open(HERE / "boiler_arealights.json"))
n_al = 0
for row in al["newAreaLights"]:
    w = row["world"]
    if "AreaLight" in row and "parse_error" not in row["AreaLight"]:
        add("AreaLight", w, row["name"], row["AreaLight"])
        n_al += 1
    if "CullingAdvancedLightObject" in row and "parse_error" not in row["CullingAdvancedLightObject"]:
        add("CullingAdvancedLightObject", w, row["name"], row["CullingAdvancedLightObject"])

# 3) KeycardDoor + Switch (keycard_switch, clean-prefix fields)
kcsw = json.load(open(HERE / "keycard_switch.json"))
n_kc = n_sw = 0
for r in kcsw["components"]["KeycardDoor"]:
    if "fields" in r:
        add("KeycardDoor", r["world"], r["go"].split("/")[-1], r["fields"])
        n_kc += 1
for r in kcsw["components"]["Switch"]:
    if "fields" in r:
        add("Switch", r["world"], r["go"].split("/")[-1], r["fields"])
        n_sw += 1

# 3c) level74's OWN door — the office ENTRANCE (door_..._00000). it was never in the
#     rebake before (only DesignMain keycard doors were), so it imported as a husk with
#     no Id and wouldn't open. plain Door, keeps its rip-preserved LockHandle.
doors74 = json.load(open(HERE / "boiler_doors.json"))
n_d74 = 0
for r in doors74["components"].get("Door", []):
    if "fields" in r:
        add("Door", r["world"], r["go"].split("/")[-1], r["fields"])
        n_d74 += 1

# 3d) GripPose + DoorHandle — the LEFT-HAND door animation (user report 2026-08-16).
#     GetClosestGrip only offers grips whose DoorState MASK overlaps the door's
#     current state; husked grips have mask 0 = never match = no hand animation.
#     rebake the scalars (DoorState/Hand/GripType) — finger poses self-heal at
#     runtime (GripPose.Awake CacheAndDestroy rebuilds from the rip-preserved
#     child transforms). DoorHandle gets its authored OpenRotation/OpenAnimation
#     back so the handle actually turns.
dm = json.load(open(HERE / "designmain_doors.json"))
n_gp = n_dh = 0


def in_office(w):
    # the graft scope box — DesignMain rows outside it (parking door etc.) were
    # never grafted and would only MISS-spam the editor tool
    return -275 <= w[0] <= -240 and -390 <= w[2] <= -350


for src in (dm, doors74):
    for r in src["components"].get("GripPose", []):
        if "fields" in r and in_office(r["world"]):
            add("GripPose", r["world"], r["go"].split("/")[-1], r["fields"])
            n_gp += 1
    for r in src["components"].get("DoorHandle", []):
        if "fields" in r and in_office(r["world"]):
            add("DoorHandle", r["world"], r["go"].split("/")[-1], r["fields"])
            n_dh += 1

# 4) LootableContainer (boiler_lootables — these live in level74, already in _MX)
loot = json.load(open(HERE / "boiler_lootables.json"))
n_lc = 0
for r in loot["components"].get("LootableContainer", []):
    if "fields" in r and r.get("world"):
        add("LootableContainer", r["world"], r["go"].split("/")[-1], r["fields"])
        n_lc += 1

outp = HERE / "rebake_manifest.json"
outp.write_text(json.dumps(out, indent=1))
print(f"wrote {outp}: {len(out['rebakes'])} rebake entries")
print(f"  CullingLightObject={n_cl} AreaLight={n_al} CullingAdvancedLightObject={n_al} "
      f"KeycardDoor={n_kc} Switch={n_sw} LootableContainer={n_lc} Door(entrance)={n_d74} "
      f"GripPose={n_gp} DoorHandle={n_dh}")
