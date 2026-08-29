using JetBrains.Annotations;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace Manimal.LabsBoiler.Server;

[UsedImplicitly]
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.manimal.labsboiler";
    public string Name { get; init; } = "ManimalLabsBoiler";
    public string Author { get; init; } = "Manimal";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new(typeof(ModMetadata).Assembly.GetName().Version!.ToString(3));
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.3");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        ["com.wtt.commonlib"] = new("~3.0.6"),
        ["com.wtt.contentbackport"] = new("~2.0.1")
    };
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}
