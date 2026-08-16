# second pass on the boiler-office light carve: the Light components serialize
# intensity 0 BY DESIGN — the authored intensity lives in CullingLightObject
# (_maxLightIntensity + _useLightIntensityFromEditor, verified in the 4.0 assembly:
# CullingLightObject.cs line 83 picks _maxLightIntensity over light.intensity) and
# LampController (on a lamp-root ancestor) drives on/off/flicker. so carve those
# EFT wrappers too or the rebaked lamps stay black. typetree pipeline same as
# extract_doors.py. consumes boiler_lights.json (the position-diffed new-light set).

import json
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

TTH.read_typetree_boost = None

RETAIL_DIR = Path(r"C:\Users\peard\Desktop\LabsBoilerLevels")
MANAGED = Path(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
OUT = Path(__file__).parent / "boiler_lamps.json"

NEW_LIGHTS = json.load(open(Path(__file__).parent / "boiler_lights.json"))["newLights"]
NEW_PATHS = {l["go"] for l in NEW_LIGHTS}

# wrappers on the light GO itself + drivers on ancestors
GO_TARGETS = {"CullingLightObject", "VolumetricLight", "LightFlicker", "Flicker"}
ANCESTOR_TARGETS = {"LampController"}


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


def main():
    print("loading SPT Managed dlls for typetrees...")
    gen = TypeTreeGenerator("2022.3.43f2")
    gen.load_local_dll_folder(str(MANAGED))

    env = UnityPy.load(str(RETAIL_DIR / "globalgamemanagers.assets"), str(RETAIL_DIR / "level114"))
    sf = next(f for k, f in env.files.items() if str(k).endswith("level114"))

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

    path_cache = {}

    def path_of_tid(tid):
        if tid in path_cache:
            return path_cache[tid]
        chain, t, hops = [], tid, 0
        while t in transforms and hops < 64:
            hops += 1
            chain.append(sib_key(t))
            t = parent.get(t, 0)
        res = "/".join(reversed(chain))
        path_cache[tid] = res
        return res

    def path_of(go):
        return path_of_tid(go_tid(go))

    # index MonoBehaviours by owning GO path_id
    go_mbs = {}  # go path_id -> list of (objreader, class name, assembly, namespace)
    for o in sf.objects.values():
        if o.type.name != "MonoBehaviour":
            continue
        try:
            mb = o.read(check_read=False)
            scr = mb.m_Script.read()
            go_mbs.setdefault(mb.m_GameObject.path_id, []).append(
                (o, scr.m_ClassName, scr.m_AssemblyName, scr.m_Namespace))
        except Exception:
            pass

    # ref index for PPtr resolution (transforms + GOs + component class names)
    ref_index = {}
    for pid, tr in transforms.items():
        g = gos.get(tr.m_GameObject.path_id)
        if g is not None:
            ref_index[pid] = (path_of(g), "Transform")
    for pid, g in gos.items():
        ref_index[pid] = (path_of(g), "GameObject")
    for gpid, mbs in go_mbs.items():
        g = gos.get(gpid)
        if g is None:
            continue
        p = path_of(g)
        for o, cls, _, _ in mbs:
            ref_index[o.path_id] = (p, cls)
    # native Light components too — LampController.Lights is a Light PPtr array
    for o in sf.objects.values():
        if o.type.name == "Light" and o.path_id not in ref_index:
            try:
                t = o.read_typetree()
                g = gos.get(t["m_GameObject"]["m_PathID"])
                if g is not None:
                    ref_index[o.path_id] = (path_of(g), "Light")
            except Exception:
                pass

    def path_of_ref(pid):
        return ref_index.get(pid)

    node_cache = {}

    def read_mb(o, cls, asm, ns):
        full = (ns + "." if ns else "") + cls
        key = (asm, full)
        if key not in node_cache:
            node_cache[key] = gen.get_nodes_up(asm, full)
        data = o.read_typetree(node_cache[key], check_read=False)
        return sanitize({k: v for k, v in data.items()
                         if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")},
                        path_of_ref)

    # find the GO path_id for each new light path
    go_by_path = {}
    for pid, g in gos.items():
        go_by_path[path_of(g)] = pid

    results = []
    lamp_cache = {}   # lamp-root tid -> extracted controller (dedupe: one controller drives many lights)
    flagged = []

    for nl in NEW_LIGHTS:
        gpath = nl["go"]
        gpid = go_by_path.get(gpath)
        if gpid is None:
            flagged.append(f"light GO not refound: {gpath}")
            continue
        row = {"go": gpath, "wrappers": {}}
        for o, cls, asm, ns in go_mbs.get(gpid, []):
            if cls not in GO_TARGETS:
                continue
            try:
                row["wrappers"][cls] = read_mb(o, cls, asm, ns)
            except Exception as e:
                row["wrappers"][cls] = {"parse_error": str(e)[:150]}
                flagged.append(f"{gpath} {cls}: {str(e)[:80]}")
        # walk ancestors for the LampController
        tid = go_tid(gos[gpid])
        t = parent.get(tid, 0)
        hops = 0
        while t in transforms and hops < 12:
            hops += 1
            agpid = transforms[t].m_GameObject.path_id
            found = False
            for o, cls, asm, ns in go_mbs.get(agpid, []):
                if cls in ANCESTOR_TARGETS:
                    row["lampRoot"] = path_of_tid(t)
                    if t not in lamp_cache:
                        try:
                            lamp_cache[t] = read_mb(o, cls, asm, ns)
                        except Exception as e:
                            lamp_cache[t] = {"parse_error": str(e)[:150]}
                            flagged.append(f"{row['lampRoot']} LampController: {str(e)[:80]}")
                    found = True
                    break
            if found:
                break
            t = parent.get(t, 0)
        if "lampRoot" not in row:
            row["lampRoot"] = None
        results.append(row)

    controllers = {path_of_tid(t): data for t, data in lamp_cache.items()}

    # sanity: every light needs an intensity source
    n_cull = sum(1 for r in results if "CullingLightObject" in r["wrappers"]
                 and "parse_error" not in r["wrappers"]["CullingLightObject"])
    intens = [r["wrappers"]["CullingLightObject"].get("_maxLightIntensity")
              for r in results if "CullingLightObject" in r["wrappers"]
              and "parse_error" not in r["wrappers"]["CullingLightObject"]]
    bad = [i for i in intens if not isinstance(i, (int, float)) or not (0 <= i <= 1000)]
    print(f"\n{len(results)} lights processed, {n_cull} with parsed CullingLightObject")
    print(f"_maxLightIntensity values: min={min(intens):.2f} max={max(intens):.2f}" if intens else "NO INTENSITIES")
    if bad:
        print(f"SANITY FAIL: implausible intensities: {bad[:8]}")
    print(f"{len(controllers)} distinct LampControllers found")
    for p, c in list(controllers.items())[:6]:
        if "parse_error" in c:
            print(f"  {p}: PARSE ERROR {c['parse_error'][:60]}")
        else:
            nlights = len(c.get("Lights") or [])
            print(f"  {p}: Lights[{nlights}] state-ish keys: LampState={c.get('LampState')}")
    if flagged:
        print(f"\nFLAGGED ({len(flagged)}):")
        for f in flagged[:10]:
            print("  " + f)

    OUT.write_text(json.dumps({
        "source": "retail current level114 wrappers for boiler expansion lights",
        "lights": results,
        "lampControllers": controllers,
    }, indent=1))
    print(f"\nwrote {OUT} ({OUT.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
