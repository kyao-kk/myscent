using MyScent.ScentEngine.Specifications;

namespace MyScent.ScentEngine.Analysis;

internal sealed class ScentRuntimeComponent
{
    public required string ComponentId { get; init; }
    public required string RelationIdentity { get; init; }
    public required string Family { get; init; }
    public double Quantity { get; init; }
    public required IReadOnlyDictionary<string, double> AttributeVector { get; init; }
    public double Intensity { get; init; }
    public required IReadOnlyDictionary<string, double> StageWeights { get; init; }
    public double DataQuality { get; init; }
    public double MappingQuality { get; init; }
    public required IReadOnlyList<string> OverloadGroups { get; init; }
    public bool IsExternal { get; init; }
    public IReadOnlyList<string> ExternalMissingFields { get; init; } = [];
    public double NormalizedShare { get; set; }
    public double PreMaskShare { get; set; }
    public double PerceivedShare { get; set; }

    public static ScentRuntimeComponent FromBlock(ScentBlockDefinition block, double quantity)
    {
        return new ScentRuntimeComponent
        {
            ComponentId = block.Id,
            RelationIdentity = block.Id,
            Family = block.Family,
            Quantity = quantity,
            AttributeVector = block.AttributeVector,
            Intensity = block.Intensity,
            StageWeights = block.StageWeights,
            DataQuality = block.DataQuality,
            MappingQuality = 0.85,
            OverloadGroups = block.OverloadGroups,
            IsExternal = false,
        };
    }
}

internal sealed record ActiveGroupRule(
    string RuleId,
    string ProblemCode,
    double ClarityDelta,
    double ComplexityDelta);
