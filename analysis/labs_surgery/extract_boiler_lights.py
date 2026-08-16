# carve the boiler-office expansion's lights out of retail Laboratory_LIGHT (level114).
# the scene swap keeps SPT's native LIGHT scene, which has no lights for the new
# floors — so those lights must ship INSIDE our swapped scene. carve = retail lights
# within the expansion bounds MINUS lights that already exist in SPT's LIGHT scene
# (position+type match) so floor-1's native lights dont get doubled. also censuses
# lights inside level74 itself on both sides, in case BSG lit the rooms in-scene.
# consumer: an SDK editor script that recreates these as plain Lights under one root.

import json
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH

TTH.read_typetree_boost = None

RETAIL_DIR = Path(r"C:\Users\peard\Desktop\LabsBoilerLevels")
SPT_DATA = Path(r"D:\SPTDev\EscapeFromTarkov_Data")
OUT = Path(__file__).parent / "boiler_lights.json"

BOUNDS = json.load(open(Path(__file__).parent / "boiler_expansion_bounds.json"))
MARGIN = 2.0  # meters — catch lights on the box edge (wall sconces etc.)

LIGHT_TYPES = {0: "Spot", 1: "Directional", 2: "Point", 3: "Rectangle", 4: "Disc"}


def in_bounds(p):
    return (BOUNDS["x"][0] - MARGIN <= p[0] <= BOUNDS["x"][1] + MARGIN
            and BOUNDS["y"][0] - MARGIN <= p[1] <= BOUNDS["y"][1] + MARGIN
            and BOUNDS["z"][0] - MARGIN <= p[2] <= BOUNDS["z"][1] + MARGIN)


def load_scene(ggm_assets, level_path, level_name):
    env = UnityPy.load(str(ggm_assets), str(level_path))
    return next(f for k, f in env.files.items() if str(k).endswith(level_name))


def scene_index(sf):
    gos, transforms = {}, {}
    for o in sf.objects.values():
        try:
            if o.type.name == "GameObject":
                gos[o.path_id] = o.read()
            elif o.type.name in ("Transform", "RectTransform"):
                transforms[o.path_id] = o.read()
        except Exception:
            pass
    parent = {pid: tr.m_Father.path_id for pid, tr in transforms.items()}
    return gos, transforms, parent


def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx,
            aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)


def qrot(q, v):
    qc = (-q[0], -q[1], -q[2], q[3])
    r = qmul(qmul(q, (v[0], v[1], v[2], 0.0)), qc)
    return r[:3]


def make_world(transforms, parent):
    cache = {}

    def world(tid):
        if tid in cache:
            return cache[tid]
        tr = transforms[tid]
        lp = (tr.m_LocalPosition.x, tr.m_LocalPosition.y, tr.m_LocalPosition.z)
        lr = (tr.m_LocalRotation.x, tr.m_LocalRotation.y, tr.m_LocalRotation.z, tr.m_LocalRotation.w)
        ls = (tr.m_LocalScale.x, tr.m_LocalScale.y, tr.m_LocalScale.z)
        par = parent.get(tid, 0)
        if par not in transforms:
            res = (lp, lr, ls)
        else:
            pp, pr, ps = world(par)
            sc = (lp[0]*ps[0], lp[1]*ps[1], lp[2]*ps[2])
            rt = qrot(pr, sc)
            res = ((pp[0]+rt[0], pp[1]+rt[1], pp[2]+rt[2]), qmul(pr, lr),
                   (ps[0]*ls[0], ps[1]*ls[1], ps[2]*ls[2]))
        cache[tid] = res
        return res

    return world


def collect_lights(sf, label):
    gos, transforms, parent = scene_index(sf)
    world = make_world(transforms, parent)

    def go_tid(go):
        for comp in go.m_Component:
            c = comp.component if hasattr(comp, "component") else comp[1]
            if c.path_id in transforms:
                return c.path_id
        return 0

    def sib_key(tid):
        g = gos.get(transforms[tid].m_GameObject.path_id)
        name = g.m_Name if g else "?"
        par = parent.get(tid, 0)
        if par not in transforms:
            return name
        k = 0
        for ch in (transforms[par].m_Children or []):
            if ch.path_id == tid:
                break
            cg = gos.get(transforms[ch.path_id].m_GameObject.path_id) if ch.path_id in transforms else None
            if cg is not None and cg.m_Name == name:
                k += 1
        return name if k == 0 else f"{name}~{k}"

    def path_of(go):
        chain, t, hops = [], go_tid(go), 0
        while t in transforms and hops < 64:
            hops += 1
            chain.append(sib_key(t))
            t = parent.get(t, 0)
        return "/".join(reversed(chain))

    # which EFT wrappers sit on each GO — lamp revival needs to know if a light was
    # LampController/CullingLightObject-driven vs bare
    go_scripts = {}
    for o in sf.objects.values():
        if o.type.name != "MonoBehaviour":
            continue
        try:
            mb = o.read(check_read=False)
            cls = mb.m_Script.read().m_ClassName
            go_scripts.setdefault(mb.m_GameObject.path_id, []).append(cls)
        except Exception:
            pass

    lights = []
    for o in sf.objects.values():
        if o.type.name != "Light":
            continue
        try:
            tree = o.read_typetree()
        except Exception as e:
            lights.append({"error": str(e)[:120]})
            continue
        go = gos.get(tree["m_GameObject"]["m_PathID"])
        tid = go_tid(go) if go else 0
        if tid not in transforms:
            continue
        p, r, _ = world(tid)
        c = tree["m_Color"]
        lights.append({
            "go": path_of(go),
            "name": go.m_Name,
            "active": bool(go.m_IsActive),
            "enabled": bool(tree.get("m_Enabled", 1)),
            "world": [round(v, 3) for v in p],
            "worldRot": [round(v, 6) for v in r],
            "type": LIGHT_TYPES.get(tree["m_Type"], tree["m_Type"]),
            "color": [round(c["r"], 4), round(c["g"], 4), round(c["b"], 4), round(c["a"], 4)],
            "intensity": round(tree["m_Intensity"], 4),
            "range": round(tree["m_Range"], 3),
            "spotAngle": round(tree["m_SpotAngle"], 2),
            "innerSpotAngle": round(tree.get("m_InnerSpotAngle", 0), 2),
            "shadows": tree["m_Shadows"]["m_Type"],
            "renderMode": tree["m_RenderMode"],
            "cullingMask": tree["m_CullingMask"]["m_Bits"],
            "bounceIntensity": round(tree.get("m_BounceIntensity", 1.0), 3),
            "siblingScripts": go_scripts.get(tree["m_GameObject"]["m_PathID"], []),
        })
    print(f"  {label}: {len(lights)} lights total")
    return lights


def main():
    ggm = RETAIL_DIR / "globalgamemanagers.assets"

    print("collecting lights...")
    retail_light = collect_lights(load_scene(ggm, RETAIL_DIR / "level114", "level114"), "retail LIGHT")
    spt_light = collect_lights(load_scene(SPT_DATA / "globalgamemanagers.assets", SPT_DATA / "level114", "level114"), "spt LIGHT")
    retail_l74 = collect_lights(load_scene(ggm, RETAIL_DIR / "level74", "level74"), "retail level74")

    retail_box = [l for l in retail_light if "world" in l and in_bounds(l["world"])]
    spt_box = [l for l in spt_light if "world" in l and in_bounds(l["world"])]
    l74_box = [l for l in retail_l74 if "world" in l and in_bounds(l["world"])]
    print(f"\nin expansion box: retail LIGHT={len(retail_box)}, spt LIGHT={len(spt_box)}, retail level74={len(l74_box)}")

    # a retail light is NEW if no spt light of the same type sits within 10cm
    def matches(a, b):
        return (a["type"] == b["type"]
                and abs(a["world"][0]-b["world"][0]) < 0.1
                and abs(a["world"][1]-b["world"][1]) < 0.1
                and abs(a["world"][2]-b["world"][2]) < 0.1)

    new = [l for l in retail_box if not any(matches(l, s) for s in spt_box)]
    kept_native = len(retail_box) - len(new)
    print(f"carve result: {len(new)} NEW lights to carry, {kept_native} already exist in SPT LIGHT (skipped)")

    by_type = {}
    for l in new:
        by_type[l["type"]] = by_type.get(l["type"], 0) + 1
    print(f"new by type: {by_type}")
    wrappers = {}
    for l in new:
        for s in l["siblingScripts"]:
            wrappers[s] = wrappers.get(s, 0) + 1
    print(f"EFT wrappers on new lights: {wrappers}")

    OUT.write_text(json.dumps({
        "source": "retail current Laboratory_LIGHT level114, diffed vs SPT 4.0",
        "bounds": BOUNDS, "margin": MARGIN,
        "newLights": new,
        "sptLightsInBox": spt_box,
        "retailLevel74LightsInBox": l74_box,
    }, indent=1))
    print(f"wrote {OUT} ({OUT.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
