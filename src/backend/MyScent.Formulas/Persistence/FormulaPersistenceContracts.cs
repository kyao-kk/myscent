namespace MyScent.Formulas.Persistence;

public interface IFormulaPersistenceStore
{
    Task<PersistedFormula?> LoadFormulaAsync(
        string formulaId,
        CancellationToken cancellationToken = default);

    Task CreateFormulaAsync(
        FormulaCreationCommit creation,
        CancellationToken cancellationToken = default);

    Task<PersistedIdempotencyRecord?> FindIdempotencyAsync(
        string actorScope,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<FormulaCommandCommitOutcome> CommitCommandAsync(
        FormulaCommandPersistenceCommit commit,
        CancellationToken cancellationToken = default);

    Task SaveAnalysisAsync(
        AnalysisSnapshotPersistenceRecord snapshot,
        CancellationToken cancellationToken = default);
}

public sealed record PersistedFormula
{
    public required string FormulaId { get; init; }
    public required string ActorScope { get; init; }
    public string? OwnerId { get; init; }
    public string? GuestId { get; init; }
    public string? Title { get; init; }
    public required string Status { get; init; }
    public required long Revision { get; init; }
    public required string FormulaHash { get; init; }
    public required string AggregateStateHash { get; init; }
    public required string CurrentSceneId { get; init; }
    public required FormulaEngineVersions EngineVersions { get; init; }
    public required IReadOnlyList<FormulaComponent> Components { get; init; }
    public required IReadOnlyList<FormulaEvent> Events { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record FormulaCreationCommit
{
    public required PersistedFormula Formula { get; init; }
    public required FormulaEvent CreationEvent { get; init; }
}

public sealed record FormulaCommandPersistenceCommit
{
    public required string ActorScope { get; init; }
    public required Guid IdempotencyKey { get; init; }
    public required string RequestFingerprint { get; init; }
    public required string FormulaId { get; init; }
    public required long ExpectedRevision { get; init; }
    public required long NewRevision { get; init; }
    public required string FormulaHash { get; init; }
    public required string AggregateStateHash { get; init; }
    public required string CurrentSceneId { get; init; }
    public required string Status { get; init; }
    public string? Title { get; init; }
    public required IReadOnlyList<FormulaComponent> CurrentComponents { get; init; }
    public required FormulaEvent Event { get; init; }
    public required FormulaCommandResult CommandResult { get; init; }
    public required DateTimeOffset IdempotencyExpiresAt { get; init; }
}

public sealed record FormulaCommandCommitOutcome
{
    public required FormulaCommandCommitStatus Status { get; init; }
    public long? CurrentRevision { get; init; }
    public PersistedIdempotencyRecord? ExistingIdempotency { get; init; }

    public static FormulaCommandCommitOutcome Committed() =>
        new() { Status = FormulaCommandCommitStatus.Committed };

    public static FormulaCommandCommitOutcome RevisionConflict(long currentRevision) =>
        new()
        {
            Status = FormulaCommandCommitStatus.RevisionConflict,
            CurrentRevision = currentRevision,
        };

    public static FormulaCommandCommitOutcome DuplicateIdempotency(
        PersistedIdempotencyRecord existing) =>
        new()
        {
            Status = FormulaCommandCommitStatus.DuplicateIdempotency,
            ExistingIdempotency = existing,
        };
}

public enum FormulaCommandCommitStatus
{
    Committed = 0,
    RevisionConflict = 1,
    DuplicateIdempotency = 2,
}

public sealed record PersistedIdempotencyRecord
{
    public required string ActorScope { get; init; }
    public required Guid IdempotencyKey { get; init; }
    public required string FormulaId { get; init; }
    public required Guid CommandId { get; init; }
    public required string RequestFingerprint { get; init; }
    public required FormulaCommandResult CommandResult { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed record AnalysisSnapshotPersistenceRecord
{
    public required string AnalysisId { get; init; }
    public required string FormulaId { get; init; }
    public required long FormulaRevision { get; init; }
    public required long CurrentRevisionAtFinish { get; init; }
    public required string FreshnessState { get; init; }
    public required string FormulaHash { get; init; }
    public required string NumericOutputHash { get; init; }
    public required string SceneId { get; init; }
    public required FormulaEngineVersions EngineVersions { get; init; }
    public required string PredictionJson { get; init; }
    public required decimal ConfidenceScore { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public static class FormulaCommandFingerprint
{
    public static string Calculate(FormulaCommandEnvelope command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Hash(
            $"{command.FormulaId}|{command.ExpectedRevision}|{command.ActorId}|{command.Payload.SemanticFingerprint}");
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }
}
