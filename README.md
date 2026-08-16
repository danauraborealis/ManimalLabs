# ManimalLabs

Runtime **map surgery** on SPT 4.x's native Labs: the retail 1.0 expansion
(the Office-Above-Boiler-Room floors 2-3) rebuilt in the WTT SDK and
hot-swapped into the live map at raid load. Native Labs keeps doing 95% of the
work — one scene in its multi-scene load is replaced with ours.

- `labs-client/` — BepInEx client plugin (`Manimal-LabsBoiler`): scene swap,
  shader rebind, area-light rebuild, spatial-audio bake merge, keycard proxies
- `labs-server/` — SPT server mod: expansion containers + retail 1.0 loose-loot
  parity (dump-authored, WTT-ContentBackport-aware pools)
- `analysis/` — extraction scripts + component-value JSONs from the retail rip
- `docs/` — working notes

The scene bundle (`manimal_labs_boiler.bundle`) is built in the WTT SDK
(`Labs Boiler` menu: graft → rebake → wire → build) and ships beside the
client DLL — no StreamingAssets writes, Forge-compliant layout.
