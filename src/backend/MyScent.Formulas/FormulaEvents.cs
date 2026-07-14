namespace MyScent.Formulas;

public sealed record FormulaEvent
{
    public required string EventId { get; init; }
    public required string FormulaId { get; init; }
    public required long Revision { get; init; }
    public required string EventType { get; init; }
    public required Guid CommandId { get; init; }
    public required string ActorId { get; init; }
    public required string BeforeStateHash { get; init; }
    public required string AfterStateHash { get; init; }
    public required FormulaStateChange Change { get; init; }
    public string? CompensatesEventId { get; init; }
    public required FormulaEngineVersions EngineVersions { get; init; }
    public required DateTimeOffset ServerTime { get; init; }
}

public sealed record FormulaEngineVersions(
    string EngineVersion,
    string BlockLibraryVersion,
    string RelationLibraryVersion);

public sealed record FormulaStateChange
{
    public IReadOnlyList<FormulaComponent> Added { get; init; } = [];
    public IReadOnlyList<FormulaQuantityChange> Updated { get; init; } = [];
    public IReadOnlyList<FormulaComponent> Removed { get; init; } = [];
    public bool? SealedAfter { get; init; }
    public string? TitleAfter { get; init; }

    public FormulaStateChange Invert(bool sealedBefore, string? titleBefore)
    {
        return new FormulaStateChange
        {
            Added = Removed,
            Updated = Updated
                .Select(static change => new FormulaQuantityChange(
                    change.BlockId,
                    change.AfterQuantity,
                    change.BeforeQuantity))
                .ToArray(),
            Removed = Added,
            SealedAfter = SealedAfter.HasValue ? sealedBefore : null,
            TitleAfter = SealedAfter.HasValue ? titleBefore : null,
        };
    }
}

public sealed record FormulaQuantityChange(
    string BlockId,
    double BeforeQuantity,
    double AfterQuantity);
