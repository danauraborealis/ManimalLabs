# KeycardDoor + Switch recovery from retail level516. these OVERRUN the plain-door
# drift surgery (their subclass fields land in the ~308B drifted tail after
# _occlusionPortal). KEY INSIGHT: everything a keycard door needs to FUNCTION
# (KeyId=keycard tpl, Id, OpenAngle/CloseAngle, _doorState, interactPositions,
# LockHandle) sits in the shared WorldInteractiveObject base BEFORE the drift zone
# (_mboitRenderers). so parse only the clean PREFIX (cut after NoInteractionsAllowed)
# and stop — no tail, no overrun. Switch's exfil-wiring fields ARE in the drifted
# tail (v2 concern; v1 ships switches inert) so we only recover its Id+position here.
import json
from pathlib import Path

import UnityPy
import UnityPy.helpers.TypeTreeHelper as TTH
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

TTH.read_typetree_boost = None

LEVELS_DIR = Path(r"C:\Users\peard\Desktop\LabsBoilerLevels")
MANAGED = Path(r"D:\SPTDev\EscapeFromTarkov_Data\Managed")
OUT = Path(__file__).parent / "keycard_switch.json"

XR = (-276, -239); ZR = (-391, -349)  # office tower box
# cut the typetree right after this field — the last clean base field before the
# 1.0 drift zone (_mboitRenderers/DoorKeyOpenInteraction/Operatable)
CUT_AFTER = "NoInteractionsAllowed"


def flat(n, lvl=0, out=None):
    if out is None:
        out = []
    out.append([lvl, n.m_Type, n.m_Name, n.m_MetaFlag or 0])
    for c in (n.m_Children or []):
        flat(c, lvl + 1, out)
    return out


def to_tree(fl):
    from UnityPy.helpers.TypeTreeNode import TypeTreeNode
    return TypeTreeNode.from_list([TypeTreeNode(l, t, n, 0, 0, m_MetaFlag=m) for l, t, n, m in fl])


def prefix_tree(fl, cut_after):
    # keep rows through the cut_after level-1 field (and its children), drop the rest
    out = []
    i, n = 0, len(fl)
    while i < n:
        out.append(fl[i])
        if fl[i][0] == 1 and fl[i][2] == cut_after:
            j = i + 1
            while j < n and fl[j][0] > 1:
                out.append(fl[j])
                j += 1
            break
        i += 1
    return out


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

    env = UnityPy.load(str(LEVELS_DIR / "globalgamemanagers.assets"), str(LEVELS_DIR / "level516"))
    sf = next(f for k, f in env.files.items() if str(k).endswith("level516"))

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

    def qmul(a, b):
        ax, ay, az, aw = a; bx, by, bz, bw = b
        return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx,
                aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)

    def qrot(q, v):
        qc = (-q[0], -q[1], -q[2], q[3])
        return qmul(qmul(q, (v[0], v[1], v[2], 0.0)), qc)[:3]

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
            sc = (lp[0]*ps[0], lp[1]*ps[1], lp[2]*ps[2]); rt = qrot(pr, sc)
            res = ((pp[0]+rt[0], pp[1]+rt[1], pp[2]+rt[2]), qmul(pr, lr),
                   (ps[0]*ls[0], ps[1]*ls[1], ps[2]*ls[2]))
        cache[tid] = res
        return res

    ref_index = {}
    for pid, tr in transforms.items():
        g = gos.get(tr.m_GameObject.path_id)
        if g is not None:
            ref_index[pid] = (path_of(g), "Transform")
    for pid, g in gos.items():
        ref_index[pid] = (path_of(g), "GameObject")
    for o in sf.objects.values():
        if o.type.name == "MonoBehaviour" and o.path_id not in ref_index:
            try:
                mb2 = o.read(check_read=False)
                g2 = gos.get(mb2.m_GameObject.path_id)
                if g2 is not None:
                    ref_index[o.path_id] = (path_of(g2), mb2.m_Script.read().m_ClassName)
            except Exception:
                pass

    def path_of_ref(pid):
        return ref_index.get(pid)

    node_cache = {}
    results = {"KeycardDoor": [], "Switch": []}
    for o in sf.objects.values():
        if o.type.name != "MonoBehaviour":
            continue
        try:
            mb = o.read(check_read=False)
            scr = mb.m_Script.read()
        except Exception:
            continue
        cls = scr.m_ClassName
        if cls not in ("KeycardDoor", "Switch"):
            continue
        go = gos.get(mb.m_GameObject.path_id)
        tid = go_tid(go) if go else 0
        if tid not in transforms:
            continue
        p, r, _ = world(tid)
        if not (XR[0] <= p[0] <= XR[1] and ZR[0] <= p[2] <= ZR[1]):
            continue
        full = (scr.m_Namespace + "." if scr.m_Namespace else "") + cls
        key = (scr.m_AssemblyName, full)
        if key not in node_cache:
            fl = flat(gen.get_nodes_up(scr.m_AssemblyName, full))
            node_cache[key] = to_tree(prefix_tree(fl, CUT_AFTER))
        row = {"go": path_of(go), "world": [round(v, 3) for v in p],
               "worldRot": [round(v, 6) for v in r], "class": full}
        try:
            data = o.read_typetree(node_cache[key], check_read=False)
            row["fields"] = sanitize({k: v for k, v in data.items()
                                      if k not in ("m_GameObject", "m_Enabled", "m_Script", "m_Name")},
                                     path_of_ref)
            row["parse"] = "clean-prefix (base fields only; drifted tail intentionally not read)"
        except Exception as e:
            row["parse_error"] = str(e)[:200]
        results[cls].append(row)

    OUT.write_text(json.dumps({"source": "retail level516 office-box keycards+switches, clean-prefix parse",
                               "components": results}, indent=1))
    print(f"wrote {OUT}")
    for cls, rows in results.items():
        ok = sum(1 for r in rows if "fields" in r)
        print(f"\n=== {cls}: {len(rows)} in box, {ok} parsed clean ===")
        for r in rows:
            f = r.get("fields", {})
            if cls == "KeycardDoor":
                print(f"  KeyId={f.get('KeyId')!r} Id={f.get('Id')!r} Open={f.get('OpenAngle')} "
                      f"Close={f.get('CloseAngle')} state={f.get('_doorState')} "
                      f"lock={'Y' if f.get('LockHandle') else 'n'}  {r['go'].split('/')[-1]}")
            else:
                print(f"  Id={f.get('Id')!r} @{r['world']}  {r['go'].split('/')[-1]}")


if __name__ == "__main__":
    main()
