# third light pass: BSG AreaLight/TubeLight are MonoBehaviours (custom deferred area
# lights), NOT UnityEngine.Light — the native-Light carve missed them, and the
# office branch of retail's LIGHT scene is full of them (Part1/Part2/AREA_LIGHTS).
# same recipe: extract in-bounds from retail level114, position-diff vs SPT level114,
# ship only the new ones. CullingAdvancedLightObject rides the same GO (the perf
# playbook's area-light instancing is post-0.16 engine work, but the light classes
# themselves exist and render in SPT 4.0 — native Labs uses them today).

import json
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

TTH.read_typetree_boost = None

RETAIL_DIR = Path(r"C:\Users\peard\Desktop\LabsBoilerLevels")
SPT_DATA = Path(r"D:\SPTDev\EscapeFromTarkov_Data")
MANAGED = Path(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
OUT = Path(__file__).parent / "boiler_arealights.json"

BOUNDS = json.load(open(Path(__file__).parent / "boiler_expansion_bounds.json"))
MARGIN = 2.0

TARGETS = {"AreaLight", "TubeLight", "CullingAdvancedLightObject"}


def in_bounds(p):
    return (BOUNDS["x"][0] - MARGIN <= p[0] <= BOUNDS["x"][1] + MARGIN
            and BOUNDS["y"][0] - MARGIN <= p[1] <= BOUNDS["y"][1] + MARGIN
            and BOUNDS["z"][0] - MARGIN <= p[2] <= BOUNDS["z"][1] + MARGIN)


def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx,
            aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)


def qrot(q, v):
    qc = (-q[0], -q[1], -q[2], q[3])
    r = qmul(qmul(q, (v[0], v[1], v[2], 0.0)), qc)
    return r[:3]


def sanitize(v, path_of_ref):
    if isinstance(v, dict):
        if "m_PathID" in v and "m_FileID" in v:
            pid = v["m_PathID"]
            if pid == 0:
                return None
            if v["m_FileID"] != 0:
                return {"externalRef": [v["m_FileID"], pid]}
            info = path_of_ref(pid)
            return {"refPath": info[0], "refType": info[1]} if info else {"unresolvedRef": pid}
        return {k: sanitize(x, path_of_ref) for k, x in v.items()}
    if isinstance(v, (list, tuple)):
        return [sanitize(x, path_of_ref) for x in v]
    if isinstance(v, bool) or isinstance(v, (int, str)) or v is None:
        return v
    if isinstance(v, float):
        return v if v == v and abs(v) != float("inf") else 0.0
    if hasattr(v, "__dict__"):
        return {k: sanitize(x, path_of_ref) for k, x in v.__dict__.items() if not k.startswith("_UnityPy")}
    return str(v)


def collect(gen_or_none, ggm, level_path, label, want_fields):
    env = UnityPy.load(str(ggm), str(level_path))
    sf = next(f for k, f in env.files.items() if str(k).endswith(level_path.name))

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

    node_cache = {}
    rows = {}  # go path_id -> row
    for o in sf.objects.values():
        if o.type.name != "MonoBehaviour":
            continue
        try:
            mb = o.read(check_read=False)
            scr = mb.m_Script.read()
        except Exception:
            continue
        cls = scr.m_ClassName
        if cls not in TARGETS:
            continue
        gpid = mb.m_GameObject.path_id
        go = gos.get(gpid)
        if go is None:
            continue
        tid = go_tid(go)
        if tid not in transforms:
            continue
        p, r, s = world(tid)
        if not in_bounds(p):
            continue
        if gpid not in rows:
            rows[gpid] = {
                "go": path_of(go), "name": go.m_Name, "active": bool(go.m_IsActive),
                "world": [round(v, 3) for v in p],
                "worldRot": [round(v, 6) for v in r],
                "worldScale": [round(v, 4) for v in s],
                "classes": [],
            }
        rows[gpid]["classes"].append(cls)
        if want_fields and gen_or_none is not None:
            full = (scr.m_Namespace + "." if scr.m_Namespace else "") + cls
            key = (scr.m_AssemblyName, full)
            try:
                if key not in node_cache:
                    node_cache[key] = gen_or_none.get_nodes_up(scr.m_AssemblyName, full)
                data = o.read_typetree(node_cache[key], check_read=False)
                rows[gpid][cls] = sanitize(
                    {k: v for k, v in data.items()
                     if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")},
                    lambda pid: None)
                rows[gpid][cls + "_enabled"] = bool(data.get("m_Enabled", 1))
            except Exception as e:
                rows[gpid][cls] = {"parse_error": str(e)[:150]}
    out = list(rows.values())
    print(f"  {label}: {len(out)} area/tube light GOs in box")
    return out


def main():
    print("loading SPT Managed dlls for typetrees...")
    gen = TypeTreeGenerator("2022.3.43f2")
    gen.load_local_dll_folder(str(MANAGED))

    retail = collect(gen, RETAIL_DIR / "globalgamemanagers.assets", RETAIL_DIR / "level114", "retail LIGHT", True)
    spt = collect(None, SPT_DATA / "globalgamemanagers.assets", SPT_DATA / "level114", "spt LIGHT", False)

    def matches(a, b):
        return (abs(a["world"][0]-b["world"][0]) < 0.1
                and abs(a["world"][1]-b["world"][1]) < 0.1
                and abs(a["world"][2]-b["world"][2]) < 0.1)

    new = retail  # ALL in-box: the whole-branch graft carries every retail area light and
    # the native office branch gets DELETED at runtime — every copy needs retail values
    print(f"carve result: {len(new)} area/tube lights (ALL in-box; native branch deleted at runtime)")

    parse_errors = [r["go"] for r in new for c in r["classes"] if isinstance(r.get(c), dict) and "parse_error" in r[c]]
    if parse_errors:
        print(f"PARSE ERRORS on: {parse_errors[:8]}")
    for r in new[:8]:
        al = r.get("AreaLight") or r.get("TubeLight") or {}
        col = al.get("m_Color") or al.get("Color") or {}
        inten = al.get("m_Intensity", al.get("Intensity"))
        print(f"  {'+'.join(r['classes']):40s} int={inten} color~{ {k: round(v,2) for k,v in col.items()} if isinstance(col, dict) else col } {r['name']}")

    OUT.write_text(json.dumps({
        "source": "retail current level114 area/tube lights in boiler expansion box, diffed vs SPT",
        "bounds": BOUNDS, "margin": MARGIN,
        "newAreaLights": new,
        "sptInBox": spt,
    }, indent=1))
    print(f"wrote {OUT} ({OUT.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
