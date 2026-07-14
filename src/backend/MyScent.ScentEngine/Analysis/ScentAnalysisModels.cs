namespace MyScent.ScentEngine.Analysis;

public sealed record ScentAnalysisRequest
{
    public required IReadOnlyList<FormulaComponentInput> Components { get; init; }
    public string? SceneId { get; init; }
}

public sealed record FormulaComponentInput
{
    public string? BlockId { get; init; }
    public double Quantity { get; init; }
    public ExternalMaterialInput? ExternalMaterial { get; init; }
}

public sealed record ExternalMaterialInput
{
    public required string ExternalMaterialId { get; init; }
    public IReadOnlyList<string> MappedBlockIds { get; init; } = [];
    public IReadOnlyDictionary<string, double> AttributeVector { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);
    public double Intensity { get; init; }
    public IReadOnlyDictionary<string, double> StageWeights { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);
    public double DataQuality { get; init; }
    public double MappingQuality { get; init; }
    public IReadOnlyList<string> MissingFields { get; init; } = [];
}

public sealed record ScentAnalysisResult
{
    public required string EngineVersion { get; init; }
    public required string BlockLibraryVersion { get; init; }
    public required string RelationLibraryVersion { get; init; }
    public required string FormulaHash { get; init; }
    public required string CanonicalNumericOutputHash { get; init; }
    public required IReadOnlyList<AnalyzedComponent> Components { get; init; }
    public required IReadOnlyDictionary<string, double> Attributes { get; init; }
    public required IReadOnlyDictionary<string, StageAnalysis> Stages { get; init; }
    public required IReadOnlyList<ComponentContribution> PrimaryComponents { get; init; }
    public required IReadOnlyDictionary<string, double> FamilyContributions { get; init; }
    public required ScentDerivedMetrics Metrics { get; init; }
    public required IReadOnlyList<ActiveRelation> ActiveRelations { get; init; }
    public required IReadOnlyList<string> Problems { get; init; }
    public required IReadOnlyList<ScentSuggestion> Suggestions { get; init; }
    public required ScentConfidence Confidence { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record AnalyzedComponent(
    string ComponentId,
    string RelationIdentity,
    string Family,
    double Quantity,
    double NormalizedShare,
    double PreMaskShare,
    double PerceivedShare,
    bool IsExternal);

public sealed record ComponentContribution(string ComponentId, double Share);

public sealed record StageAnalysis
{
    public double Share { get; init; }
    public required IReadOnlyList<ComponentContribution> Components { get; init; }
    public string? PrimaryComponentId { get; init; }
    public double PrimaryShare { get; init; }
}

public sealed record ScentDerivedMetrics(
    double Clarity,
    double Complexity,
    double Continuity,
    double Balance);

public sealed record ActiveRelation(
    string RelationId,
    string RelationType,
    string SourceComponentId,
    string TargetComponentId,
    double Effect);

public sealed record ScentSuggestion(
    string ProblemCode,
    string ActionType,
    string? TargetComponentId);

public sealed record ScentConfidence
{
    public ScentConfidence(double score, string level, IReadOnlyList<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(reasons);

        Reasons = reasons;
        if (reasons.Contains("external_material_incomplete", StringComparer.Ordinal))
        {
            Score = Math.Min(score, 0.54);
            Level = "low";
        }
        else
        {
            Score = score;
            Level = level;
        }
    }

    public double Score { get; }
    public string Level { get; }
    public IReadOnlyList<string> Reasons { get; }
}
