# generate the 3 boiler-office container entries the server mod appends to Labs'
# staticContainers. SPT matches a server container entry to the client scene's
# LootableContainer by Id, then rolls loot for it from the map's staticLoot pools
# (labs already has pools for Jacket/Duffle/PC-block). shape mirrors labs'
# staticContainers.json entries (Position 0,0,0 — unused, Id is the link).
import hashlib
import json
from pathlib import Path

HERE = Path(__file__).parent
loot = json.load(open(HERE / "boiler_lootables.json"))

OUT = Path(r"C:\Users\peard\Desktop\ManimalLabsBoiler\server\db\labs_containers.json")
OUT.parent.mkdir(parents=True, exist_ok=True)


def mongo(seed):
    return hashlib.md5(seed.encode()).hexdigest()[:24]


entries = []
for r in loot["components"].get("LootableContainer", []):
    f = r.get("fields", {})
    cid = f.get("Id")
    tpl = f.get("Template")
    if not cid or not tpl:
        continue
    root = mongo(cid)
    entries.append({
        "probability": 1,
        "template": {
            "Id": cid,
            "IsContainer": True,
            "useGravity": False,
            "randomRotation": False,
            "Position": {"x": 0, "y": 0, "z": 0},
            "Rotation": {"x": 0, "y": 0, "z": 0},
            "IsGroupPosition": False,
            "GroupPositions": [],
            "IsAlwaysSpawn": True,
            "Root": root,
            "Items": [
                {"_id": root, "_tpl": tpl, "upd": {"StackObjectsCount": 1}}
            ],
        },
    })

doc = {"staticWeapons": [], "staticContainers": entries, "staticForced": []}
OUT.write_text(json.dumps(doc, indent=2))
print(f"wrote {OUT}: {len(entries)} container entries")
for e in entries:
    t = e["template"]
    print(f"  {t['Id']}  tpl={t['Items'][0]['_tpl']}")
