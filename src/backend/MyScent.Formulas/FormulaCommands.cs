namespace MyScent.Formulas;

public sealed record FormulaCommandEnvelope
{
    public required Guid CommandId { get; init; }
    public required Guid IdempotencyKey { get; init; }
    public required string FormulaId { get; init; }
    public required long ExpectedRevision { get; init; }
    public required string ActorId { get; init; }
    public required string SessionId { get; init; }
    public required string SceneId { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required FormulaCommandPayload Payload { get; init; }
}

public abstract record FormulaCommandPayload
{
    public abstract string CommandType { get; }
}

public sealed record AddComponentCommand(string BlockId, double Quantity) : FormulaCommandPayload
{
    public override string CommandType => "component.add";
}

public sealed record IncreaseComponentCommand(string BlockId, double DeltaQuantity) : FormulaCommandPayload
{
    public override string CommandType => "component.increase";
}

public sealed record DecreaseComponentCommand(string BlockId, double DeltaQuantity) : FormulaCommandPayload
{
    public override string CommandType => "component.decrease";
}

public sealed record SetComponentQuantityCommand(string BlockId, double Quantity) : FormulaCommandPayload
{
    public override string CommandType => "component.set_quantity";
}

public sealed record RemoveComponentCommand(string BlockId) : FormulaCommandPayload
{
    public override string CommandType => "component.remove";
}

public sealed record ReplaceComponentCommand(
    string FromBlockId,
    string ToBlockId,
    bool PreserveQuantity,
    double? NewQuantity = null) : FormulaCommandPayload
{
    public override string CommandType => "component.replace";
}

public sealed record UndoOperationCommand : FormulaCommandPayload
{
    public override string CommandType => "operation.undo";
}

public sealed record SealFormulaCommand(string Title, bool PrerequisitesSatisfied) : FormulaCommandPayload
{
    public override string CommandType => "formula.seal";
}

public sealed record FormulaCommandResult
{
    public required Guid CommandId { get; init; }
    public required string FormulaId { get; init; }
    public required long PreviousRevision { get; init; }
    public required long NewRevision { get; init; }
    public required string EventId { get; init; }
    public required string EventType { get; init; }
    public required string FormulaHash { get; init; }
    public required FormulaComponentDelta ComponentDelta { get; init; }
    public required bool UndoAvailable { get; init; }
    public required DateTimeOffset ServerTime { get; init; }
}

public sealed record FormulaComponentDelta
{
    public IReadOnlyList<FormulaComponent> Added { get; init; } = [];
    public IReadOnlyList<FormulaComponent> Updated { get; init; } = [];
    public IReadOnlyList<string> Removed { get; init; } = [];
}

public sealed record FormulaComponent(string BlockId, double Quantity);

public sealed class FormulaCommandException : Exception
{
    public FormulaCommandException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
