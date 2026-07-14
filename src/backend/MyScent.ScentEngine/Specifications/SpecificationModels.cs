using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyScent.ScentEngine.Specifications;

public sealed record ScentSpecificationBundle(
    ScentBlockCatalog Blocks,
    ScentRelationCatalog Relations,
    ScentEngineSpecification Engine,
    string BlocksSha256,
    string RelationsSha256,
    string EngineSha256);

public sealed record ScentBlockCatalog
{
    public required string SchemaVersion { get; init; }
    public required string Status { get; init; }
    public required string EngineCompatibility { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public IReadOnlyList<ScentBlockDefinition> Blocks { get; init; } = [];
}

public sealed record ScentBlockDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Family { get; init; }
    public IReadOnlyDictionary<string, double> AttributeVector { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);
    public double Intensity { get; init; }
    public IReadOnlyDictionary<string, double> StageWeights { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);
    public IReadOnlyList<string> RoleTags { get; init; } = [];
    public double DataQuality { get; init; }
    public required string RealMaterialDirection { get; init; }
    public IReadOnlyList<string> OverloadGroups { get; init; } = [];
    public required string ProductionMappingStatus { get; init; }
}

public sealed record ScentRelationCatalog
{
    public required string SchemaVersion { get; init; }
    public required string Status { get; init; }
    public required string EngineCompatibility { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public IReadOnlyList<PairRelationDefinition> PairRules { get; init; } = [];
    public IReadOnlyList<GroupRelationDefinition> GroupRules { get; init; } = [];
}

public sealed record PairRelationDefinition
{
    public required string RuleId { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public required string Relation { get; init; }
    public bool Directional { get; init; }
    public double Strength { get; init; }
    public double Threshold { get; init; }
    public IReadOnlyList<string> AffectedMetrics { get; init; } = [];
    public required string Description { get; init; }
    public required string Status { get; init; }
}

public sealed record GroupRelationDefinition
{
    public required string RuleId { get; init; }
    public required string Type { get; init; }
    public required string Group { get; init; }
    public int MinActiveComponents { get; init; }
    public double CombinedEffectiveShareThreshold { get; init; }
    public IReadOnlyDictionary<string, double> Conditions { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, double> Effects { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);
    public required string ProblemCode { get; init; }
    public required string Description { get; init; }
}

public sealed record ScentEngineSpecification
{
    public required string SchemaVersion { get; init; }
    public required string EngineVersion { get; init; }
    public required string Status { get; init; }
    public required string Scope { get; init; }
    public required string Disclaimer { get; init; }
    public required EngineDependencies Dependencies { get; init; }
    public required EngineAttributeSpecification Attributes { get; init; }
    public required EngineCanonicalizationSpecification Canonicalization { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement> AdditionalSections { get; init; }
        = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

public sealed record EngineDependencies
{
    public required string BlockLibrary { get; init; }
    public required string RelationLibrary { get; init; }
}

public sealed record EngineAttributeSpecification
{
    public IReadOnlyList<string> Keys { get; init; } = [];
    public required string BaseFormula { get; init; }
    public required string Finalization { get; init; }
}

public sealed record EngineCanonicalizationSpecification
{
    public required string ComponentOrder { get; init; }
    public IReadOnlyList<string> FormulaHashFields { get; init; } = [];
    public IReadOnlyList<string> ExcludeFromHash { get; init; } = [];
}
