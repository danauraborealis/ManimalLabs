using JetBrains.Annotations;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace Manimal.LabsBoiler.Server;

[UsedImplicitly]
public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.manimal.labsboiler";
    public override string Name { get; init; } = "ManimalLabsBoiler";
    public override string Author { get; init; } = "Manimal";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new(typeof(ModMetadata).Assembly.GetName().Version!.ToString(3));
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "MIT";
}