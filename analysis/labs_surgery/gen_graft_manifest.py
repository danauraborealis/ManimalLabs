# build the graft manifest the Unity editor tool consumes. two entry kinds:
#   branch  - move a whole named subtree (LIGHT's office lamp group: fixtures+lamps)
#   object  - move the subtree rooted at the GO nearest a world position with a
#             name match (scattered interactives/arealights across shared branches)
# positions come straight from the extraction JSONs already produced.
import json
from pathlib import Path

HERE = Path(__file__).parent
lights = json.load(open(HERE / "boiler_lights.json"))
arealights = json.load(open(HERE / "boiler_arealights.json"))
kcsw = json.load(open(HERE / "keycard_switch.json"))
loot = json.load(open(HERE / "boiler_lootables.json"))

DONOR_LIGHT = "Assets/labsboiler_import/Content/Locations/Laboratory/Laboratory_LIGHT.unity"
DONOR_DM = "Assets/labsboiler_dm_import/Content/Locations/Laboratory/Laboratory_DesignMain.unity"
DEST = "Assets/labsboiler_import/Content/Locations/Laboratory/Laboratory_Office_Above_Boiler_Room_floor_1_MX.unity"

# the office lamp group branch (derived from the new-light hierarchy paths — they all
# live under this one branch, and the 3 native in-box lights are in OTHER branches
# (Boiler_Room_floor_1 / relax_zone) so a whole-branch move grabs exactly the new set)
LIGHT_BRANCH = "SBG_Laboratory_Light/OO/LightGroup/Laboratory_Office_Above_Boiler_Room_floor_1"

manifest = {
    "dest": DEST,
    "grafts": [],
}

# 1) LIGHT: one branch graft = all office lamp fixtures + their 33 point/spot lamps
manifest["grafts"].append({
    "kind": "branch",
    "donor": DONOR_LIGHT,
    "path": LIGHT_BRANCH,
    "groupRoot": "__GRAFT_LIGHT_lamps",
    "note": "office lamp group: fixtures + 33 new point/spot lamps (CullingLightObject-driven)",
})

# 2) LIGHT: the 7 new AreaLights (live under shared Part1/Part2 branches — object graft
#    by position so we don't drag the ~38 native area lights with them)
al_objs = []
for a in arealights["newAreaLights"]:
    al_objs.append({"name": a["name"], "world": a["world"]})
manifest["grafts"].append({
    "kind": "objects",
    "donor": DONOR_LIGHT,
    "groupRoot": "__GRAFT_LIGHT_area",
    "matchRadius": 0.25,
    "objects": al_objs,
    "note": "7 new BSG area lights (shader refs rebound at runtime from a native Labs AreaLight)",
})

# 2b) LIGHT: the office EMISSIVE decor (glowing signage/logos/LED/monitors). these
#     live in a SEPARATE branch (SBG_Laboratory_Light/SOO_LOD0/Emissive/, 224 children
#     map-wide) that the lamp-branch graft missed entirely — and SPT's LIGHT scene has
#     NO Emissive branch at all, so nothing native provides them. they emit via emissive
#     materials (rebound at runtime), giving the office its lit look beyond the lamps.
#     object graft by name+position (the branch is map-wide, so we take only the 7 in-box).
emissive_objs = [
    {"name": "Lab_logo_volumed  (1)",       "world": [-260.0, 6.2, -380.8]},
    {"name": "lab_logo",                    "world": [-269.1, 6.6, -375.1]},
    {"name": "terragroup_logo3 (3)",        "world": [-256.6, 6.1, -379.8]},
    {"name": "Lab_Recreation_LED",          "world": [-264.2, 6.2, -372.0]},
    {"name": "Lab_recreation_LED_Big_02",   "world": [-246.0, 4.1, -376.5]},
    {"name": "lab_map_info_C (1)",          "world": [-256.6, 5.2, -374.4]},
    {"name": "lab_monitor (5)",             "world": [-266.1, 5.0, -375.8]},
]
manifest["grafts"].append({
    "kind": "objects",
    "donor": DONOR_LIGHT,
    "groupRoot": "__GRAFT_LIGHT_emissive",
    "matchRadius": 0.6,
    "objects": emissive_objs,
    "note": "7 office emissive decor objects (logos/LED/monitors) — glow via emissive materials",
})

# 3) DesignMain: the WHOLE Quest_doors branch = the office's complete access hardware
#    (office door + safe, each with door/safe body + keypad `security_pass` [# * 0-9] +
#    card reader `security_pass_card` [InteractiveProxy/Lock/KeyGrip]). 254 objects, all
#    6 children in the office box. grafting the branch keeps every door intact instead of
#    orphaning the KeycardDoor component sub-object from its meshes (the piecemeal-graft
#    bug). branch pivot is at map-origin (-120,-2,-437) but worldPositionStays keeps the
#    children at their office positions. parking door is a SEPARATE branch, not included.
manifest["grafts"].append({
    "kind": "branch",
    "donor": DONOR_DM,
    "path": "Security_pass_door/Quest_doors",
    "groupRoot": "__GRAFT_DM_questdoors",
    "note": "office access hardware: door + safe + keypads + card readers (254 objs)",
})

# 4) DesignMain: switches (v1 inert — Id+pos recovered, exfil wiring is v2). graft so
#    the console/power/announcer objects exist in place for later wiring.
sw_objs = []
for r in kcsw["components"]["Switch"]:
    sw_objs.append({"name": r["go"].split("/")[-1], "world": r["world"], "tag": "Switch"})
manifest["grafts"].append({
    "kind": "objects",
    "donor": DONOR_DM,
    "groupRoot": "__GRAFT_DM_switches",
    "matchRadius": 0.5,
    "objects": sw_objs,
    "note": "3 switches — present but inert in v1; wired to native exfil chains in v2",
})

out = HERE / "graft_manifest.json"
out.write_text(json.dumps(manifest, indent=1))
print(f"wrote {out}")
for g in manifest["grafts"]:
    n = 1 if g["kind"] == "branch" else len(g["objects"])
    print(f"  [{g['kind']:7s}] {g['groupRoot']:22s} {n} target(s) <- {Path(g['donor']).stem}")
