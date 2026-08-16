using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;
using SysPath = System.IO.Path;

namespace Manimal.LabsBoiler.Server;

public record ModMetadata : AbstractModMetadata
{
    // matches the client BepInPlugin guid (manimal convention)
    public override string ModGuid { get; init; } = "com.manimal.labsboiler";
    public override string Name { get; init; } = "ManimalLabsBoiler";
    public override string Author { get; init; } = "Manimal";
    public override List<string>? Contributors { get; init; }
    // read version off the assembly so it tracks Directory.Build.props' ModVersion
    public override SemanticVersioning.Version Version { get; init; } =
        new(typeof(ModMetadata).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.1.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    // the scene bundle ships with the CLIENT plugin (BepInEx/plugins), not here
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "MIT";
}

// registers the 3 boiler-office expansion containers into NATIVE Labs' static loot.
// the expansion added a Jacket, a Duffle bag and a PC block to level74 (retail 1.0+),
// which SPT's Labs tables don't know about — so without this they exist in the scene
// but the server never rolls loot for them and they sit empty. we don't touch loose
// loot or the loot POOLS: labs already has pools for these three container types, so
// the entries just need to be present, keyed by the same Ids the client scene carries.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 100)]
public class LabsBoilerServerMod(
    DatabaseService databaseService,
    JsonUtil jsonUtil,
    ISptLogger<LabsBoilerServerMod> logger)
    : IOnLoad
{
    public Task OnLoad()
    {
        var labs = databaseService.GetLocations().Laboratory;
        if (labs?.StaticContainers is null)
        {
            logger.Warning("[LabsBoiler] Laboratory.StaticContainers missing — office containers not registered");
            return Task.CompletedTask;
        }

        var modDir = SysPath.GetDirectoryName(typeof(LabsBoilerServerMod).Assembly.Location)!;
        var jsonPath = SysPath.Combine(modDir, "db", "labs_containers.json");
        if (!System.IO.File.Exists(jsonPath))
        {
            logger.Warning($"[LabsBoiler] {jsonPath} missing — office containers not registered");
            return Task.CompletedTask;
        }
        var json = System.IO.File.ReadAllText(jsonPath);

        // sanity: how many entries we intend to add (for the log)
        var probe = jsonUtil.Deserialize<StaticContainerDetails>(json);
        var wantIds = (probe?.StaticContainers ?? [])
            .Where(c => c.Template?.Id is not null)
            .Select(c => c.Template!.Id!)
            .ToList();
        if (wantIds.Count == 0)
        {
            logger.Warning("[LabsBoiler] labs_containers.json had no valid container entries");
            return Task.CompletedTask;
        }

        // LazyLoad.Value re-deserializes Labs' containers fresh on every access, so a
        // transformer that appends ours runs on a clean base each raid. deserialize a
        // FRESH copy of ours per access too (never share entries the generator might
        // touch), and dedup by Id so a cached base can't accumulate duplicates.
        labs.StaticContainers.AddTransformer(sc =>
        {
            if (sc?.StaticContainers is null) return sc;
            var mine = jsonUtil.Deserialize<StaticContainerDetails>(json);
            if (mine?.StaticContainers is null) return sc;

            var have = new HashSet<string>(
                sc.StaticContainers.Where(c => c.Template?.Id is not null).Select(c => c.Template!.Id!));
            var add = mine.StaticContainers
                .Where(c => c.Template?.Id is not null && !have.Contains(c.Template!.Id!))
                .ToList();
            if (add.Count > 0)
                sc.StaticContainers = sc.StaticContainers.Concat(add).ToList();
            return sc;
        });

        logger.Success($"[LabsBoiler] registered {wantIds.Count} office container(s) into Labs static loot: "
                       + string.Join(", ", wantIds));

        RegisterLooseLoot(labs, modDir);
        return Task.CompletedTask;
    }

    // retail 1.0 loose loot — the gap the playbook called unrecoverable, closed
    // by live-raid getLocalloot dumps (user, 2026-08-16): FULL-MAP parity, every
    // 1.0 loose point SPT's table lacks (office jewelry cluster + safe spots +
    // map-wide 1.0 additions), observed per-raid probabilities, full item trees.
    // pools are AUGMENTED with WTT-ContentBackport's new same-class valuables
    // (27 jewelry etc.) at a capped weight so observed loot stays dominant.
    // candidates whose tpl chain isn't in the LIVE item db are dropped per-access
    // (WTT items vanish gracefully on installs without the backport).
    private void RegisterLooseLoot(Location labs, string modDir)
    {
        if (labs.LooseLoot is null)
        {
            logger.Warning("[LabsBoiler] Laboratory.LooseLoot missing — office floor loot not registered");
            return;
        }
        var loosePath = SysPath.Combine(modDir, "db", "labs_office_looseloot.json");
        if (!System.IO.File.Exists(loosePath))
        {
            logger.Warning($"[LabsBoiler] {loosePath} missing — office floor loot not registered");
            return;
        }
        var looseJson = System.IO.File.ReadAllText(loosePath);
        var count = jsonUtil.Deserialize<LooseLoot>(looseJson)?.Spawnpoints?.Count() ?? 0;
        if (count == 0)
        {
            logger.Warning("[LabsBoiler] labs_office_looseloot.json had no spawnpoints");
            return;
        }

        labs.LooseLoot.AddTransformer(ll =>
        {
            if (ll?.Spawnpoints is null) return ll;
            var mine = jsonUtil.Deserialize<LooseLoot>(looseJson);
            if (mine?.Spawnpoints is null) return ll;
            // live db each access — WTT registers its backported items at its own
            // load step, so checking late means we see them regardless of mod order
            var itemDb = databaseService.GetItems();

            var have = new HashSet<string>(
                ll.Spawnpoints.Select(s => s.Template?.Id).Where(id => id is not null).Cast<string>());
            var add = new List<Spawnpoint>();
            foreach (var sp in mine.Spawnpoints)
            {
                if (sp.Template?.Id is null || sp.Template.Items is null || have.Contains(sp.Template.Id)) continue;
                var all = sp.Template.Items.ToList();
                var kids = all.Where(i => i.ParentId is not null)
                              .GroupBy(i => i.ParentId!)
                              .ToDictionary(g => g.Key, g => g.ToList());
                List<SptLootItem> Tree(SptLootItem root)
                {
                    var list = new List<SptLootItem> { root };
                    if (kids.TryGetValue(root.Id.ToString(), out var ch))
                        foreach (var c in ch) list.AddRange(Tree(c));
                    return list;
                }

                var keptItems = new List<SptLootItem>();
                var keptDist = new List<LooseLootItemDistribution>();
                foreach (var dist in sp.ItemDistribution ?? [])
                {
                    var rootId = dist.ComposedKey?.Key;
                    var root = all.FirstOrDefault(i => i.ParentId is null && i.Id.ToString() == rootId);
                    if (root is null) continue;
                    var tree = Tree(root);
                    if (tree.Any(i => !itemDb.ContainsKey(i.Template))) continue;   // tpl unknown to this install
                    keptItems.AddRange(tree);
                    keptDist.Add(dist);
                }
                if (keptDist.Count == 0) continue;   // nothing this install can spawn here
                sp.Template.Items = keptItems;
                sp.ItemDistribution = keptDist;
                if (!keptDist.Any(d => d.ComposedKey?.Key == sp.Template.Root))
                    sp.Template.Root = keptDist[0].ComposedKey?.Key;
                add.Add(sp);
            }
            if (add.Count > 0)
                ll.Spawnpoints = ll.Spawnpoints.Concat(add).ToList();
            return ll;
        });

        logger.Success($"[LabsBoiler] registered {count} loose-loot spawnpoint(s) — retail 1.0 full-map parity + WTT-augmented pools (unknown-tpl candidates drop per-install)");
    }
}
