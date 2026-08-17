using JetBrains.Annotations;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SysPath = System.IO.Path;

namespace Manimal.LabsBoiler.Server;

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 100), UsedImplicitly]
public class LabsBoilerServerMod(
    DatabaseService databaseService,
    JsonUtil jsonUtil,
    ISptLogger<LabsBoilerServerMod> logger)
    : IOnLoad
{
    public Task OnLoad()
    {
        var labs = databaseService.GetLocations().Laboratory;
        if (labs.StaticContainers is null)
        {
            logger.Warning("[LabsBoiler] Laboratory.StaticContainers missing — office containers not registered");
            return Task.CompletedTask;
        }

        var modDir = SysPath.GetDirectoryName(typeof(LabsBoilerServerMod).Assembly.Location)!;
        var jsonPath = SysPath.Combine(modDir, "db", "labs_containers.json");
        if (!File.Exists(jsonPath))
        {
            logger.Warning($"[LabsBoiler] {jsonPath} missing — office containers not registered");
            return Task.CompletedTask;
        }
        var json = File.ReadAllText(jsonPath);

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

        labs.StaticContainers.AddTransformer(sc =>
        {
            if (sc?.StaticContainers is null) return sc;
            
            var mine = jsonUtil.Deserialize<StaticContainerDetails>(json);
            if (mine?.StaticContainers is null) return sc;
            
            var have = new HashSet<string>(sc.StaticContainers
                .Where(c => c.Template?.Id is not null)
                .Select(c => c.Template!.Id!));
            
            var add = mine.StaticContainers
                .Where(c => c.Template?.Id is not null && !have.Contains(c.Template!.Id!))
                .ToList();
            
            if (add.Count > 0)
            {
                sc.StaticContainers = [.. sc.StaticContainers, .. add];
            }
            return sc;
        });

        logger.Success($"[LabsBoiler] registered {wantIds.Count} office container(s) into Labs static loot: "
                       + string.Join(", ", wantIds));

        RegisterLooseLoot(labs, modDir);
        return Task.CompletedTask;
    }

    private void RegisterLooseLoot(Location labs, string modDir)
    {
        if (labs.LooseLoot is null)
        {
            logger.Warning("[LabsBoiler] Laboratory.LooseLoot missing — office floor loot not registered");
            return;
        }
        
        var loosePath = SysPath.Combine(modDir, "db", "labs_office_looseloot.json");
        if (!File.Exists(loosePath))
        {
            logger.Warning($"[LabsBoiler] {loosePath} missing — office floor loot not registered");
            return;
        }
        
        var looseJson = File.ReadAllText(loosePath);
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
            
            var itemDb = databaseService.GetItems();

            var have = new HashSet<string>(ll.Spawnpoints
                .Select(s => s.Template?.Id)
                .Where(id => id is not null).Cast<string>());
            
            var add = new List<Spawnpoint>();
            foreach (var sp in mine.Spawnpoints)
            {
                if (sp.Template?.Id is null || sp.Template.Items is null || have.Contains(sp.Template.Id)) continue;
                var all = sp.Template.Items.ToList();
                var kids = all.Where(i => i.ParentId is not null)
                              .GroupBy(i => i.ParentId!)
                              .ToDictionary(g => g.Key, g => g.ToList());

                var keptItems = new List<SptLootItem>();
                var keptDist = new List<LooseLootItemDistribution>();
                foreach (var dist in sp.ItemDistribution ?? [])
                {
                    var rootId = dist.ComposedKey?.Key;
                    var root = all.FirstOrDefault(i => i.ParentId is null && i.Id.ToString() == rootId);
                    if (root is null) { continue; }
                    
                    var tree = Tree(root, kids);
                    if (tree.Any(i => !itemDb.ContainsKey(i.Template))) { continue; }
                    
                    keptItems.AddRange(tree);
                    keptDist.Add(dist);
                }
                
                if (keptDist.Count == 0) { continue; }
                sp.Template.Items = keptItems;
                sp.ItemDistribution = keptDist;
                if (!keptDist.Any(d => d.ComposedKey?.Key == sp.Template.Root))
                {
                    sp.Template.Root = keptDist[0].ComposedKey?.Key;
                }
                add.Add(sp);
            }
            if (add.Count > 0)
            {
                ll.Spawnpoints = [.. ll.Spawnpoints, .. add];
            }
            return ll;
        });

        logger.Success($"[LabsBoiler] registered {count} loose-loot spawnpoint(s) — retail 1.0 full-map parity + WTT-augmented pools (unknown-tpl candidates drop per-install)");
    }

    private static List<SptLootItem> Tree(SptLootItem root, Dictionary<string, List<SptLootItem>>? kids)
    {
        var list = new List<SptLootItem> { root };
        if (kids == null || !kids.TryGetValue(root.Id.ToString(), out var ch))
        {
            return list;
        }

        foreach (var c in ch) list.AddRange(Tree(c, kids));
        return list;
    }
}
