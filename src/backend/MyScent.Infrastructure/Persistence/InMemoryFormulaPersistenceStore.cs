using System.Collections.Concurrent;
using MyScent.Formulas.Persistence;

namespace MyScent.Infrastructure.Persistence;

public sealed class InMemoryFormulaPersistenceStore : IFormulaPersistenceStore
{
    private readonly object _transactionLock = new();
    private readonly ConcurrentDictionary<string, PersistedFormula> _formulas = new(StringComparer.Ordinal);
    private readonly Dictionary<IdempotencyKey, PersistedIdempotencyRecord> _idempotency = [];
    private readonly Dictionary<string, AnalysisSnapshotPersistenceRecord> _analyses = new(StringComparer.Ordinal);
    private readonly Dictionary<AnalysisInputKey, string> _analysisInputs = [];

    public Task<PersistedFormula?> LoadFormulaAsync(
        string formulaId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(formulaId);
        lock (_transactionLock)
        {
            return Task.FromResult(
                _formulas.TryGetValue(formulaId, out var formula)
                    ? CloneFormula(formula)
                    : null);
        }
    }

    public Task CreateFormulaAsync(
        FormulaCreationCommit creation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(creation);
        ValidateCreation(creation);

        lock (_transactionLock)
        {
            if (_formulas.ContainsKey(creation.Formula.FormulaId))
            {
                throw new InvalidOperationException(
                    $"Formula {creation.Formula.FormulaId} already exists in the persistence store.");
            }

            _formulas[creation.Formula.FormulaId] = CloneFormula(creation.Formula);
        }

        return Task.CompletedTask;
    }

    public Task<PersistedIdempotencyRecord?> FindIdempotencyAsync(
        string actorScope,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorScope);
        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));
        }

        lock (_transactionLock)
        {
            return Task.FromResult(
                _idempotency.GetValueOrDefault(new IdempotencyKey(actorScope, idempotencyKey)));
        }
    }

    public Task<FormulaCommandCommitOutcome> CommitCommandAsync(
        FormulaCommandPersistenceCommit commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(commit);
        ValidateCommandCommit(commit);

        lock (_transactionLock)
        {
            var idempotencyKey = new IdempotencyKey(commit.ActorScope, commit.IdempotencyKey);
            if (_idempotency.TryGetValue(idempotencyKey, out var existing))
            {
                return Task.FromResult(FormulaCommandCommitOutcome.DuplicateIdempotency(existing));
            }

            if (!_formulas.TryGetValue(commit.FormulaId, out var current))
            {
                throw new InvalidOperationException(
                    $"Formula {commit.FormulaId} does not exist in the persistence store.");
            }

            if (current.Revision != commit.ExpectedRevision)
            {
                return Task.FromResult(
                    FormulaCommandCommitOutcome.RevisionConflict(current.Revision));
            }

            var events = current.Events.Concat([commit.Event]).ToArray();
            var updated = current with
            {
                Title = commit.Title,
                Status = commit.Status,
                Revision = commit.NewRevision,
                FormulaHash = commit.FormulaHash,
                AggregateStateHash = commit.AggregateStateHash,
                CurrentSceneId = commit.CurrentSceneId,
                Components = commit.CurrentComponents.ToArray(),
                Events = events,
                UpdatedAt = commit.Event.ServerTime,
            };
            var idempotency = new PersistedIdempotencyRecord
            {
                ActorScope = commit.ActorScope,
                IdempotencyKey = commit.IdempotencyKey,
                FormulaId = commit.FormulaId,
                CommandId = commit.CommandResult.CommandId,
                RequestFingerprint = commit.RequestFingerprint,
                CommandResult = commit.CommandResult,
                CreatedAt = commit.Event.ServerTime,
                ExpiresAt = commit.IdempotencyExpiresAt,
            };

            // Both mutations occur while holding the same transaction lock. Any validation
            // exception above leaves current state and idempotency unchanged.
            _formulas[commit.FormulaId] = CloneFormula(updated);
            _idempotency[idempotencyKey] = idempotency;
            return Task.FromResult(FormulaCommandCommitOutcome.Committed());
        }
    }

    public Task SaveAnalysisAsync(
        AnalysisSnapshotPersistenceRecord snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateAnalysis(snapshot);

        lock (_transactionLock)
        {
            if (!_formulas.ContainsKey(snapshot.FormulaId))
            {
                throw new InvalidOperationException(
                    $"Formula {snapshot.FormulaId} does not exist for analysis persistence.");
            }

            var inputKey = new AnalysisInputKey(
                snapshot.FormulaId,
                snapshot.FormulaRevision,
                snapshot.EngineVersions.EngineVersion,
                snapshot.EngineVersions.BlockLibraryVersion,
                snapshot.EngineVersions.RelationLibraryVersion);
            if (_analysisInputs.TryGetValue(inputKey, out var existingAnalysisId))
            {
                var existing = _analyses[existingAnalysisId];
                if (!string.Equals(
                        existing.NumericOutputHash,
                        snapshot.NumericOutputHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The same formula revision and engine versions produced a different numeric output hash.");
                }

                return Task.CompletedTask;
            }

            if (!_analyses.TryAdd(snapshot.AnalysisId, snapshot))
            {
                throw new InvalidOperationException(
                    $"Analysis {snapshot.AnalysisId} already exists.");
            }

            _analysisInputs[inputKey] = snapshot.AnalysisId;
        }

        return Task.CompletedTask;
    }

    public int FormulaCount
    {
        get
        {
            lock (_transactionLock)
            {
                return _formulas.Count;
            }
        }
    }

    public int IdempotencyCount
    {
        get
        {
            lock (_transactionLock)
            {
                return _idempotency.Count;
            }
        }
    }

    public int AnalysisCount
    {
        get
        {
            lock (_transactionLock)
            {
                return _analyses.Count;
            }
        }
    }

    private static void ValidateCreation(FormulaCreationCommit creation)
    {
        var formula = creation.Formula;
        var @event = creation.CreationEvent;
        if (!string.Equals(formula.FormulaId, @event.FormulaId, StringComparison.Ordinal)
            || @event.Revision != 0
            || !string.Equals(@event.EventType, "formula_created", StringComparison.Ordinal)
            || formula.Revision != 0
            || formula.Events.Count != 1
            || formula.Events[0] != @event)
        {
            throw new ArgumentException(
                "Formula creation persistence data is internally inconsistent.",
                nameof(creation));
        }

        if (!string.Equals(
                formula.AggregateStateHash,
                @event.AfterStateHash,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Creation event aggregate state hash does not match the formula projection.",
                nameof(creation));
        }
    }

    private static void ValidateCommandCommit(FormulaCommandPersistenceCommit commit)
    {
        if (commit.IdempotencyKey == Guid.Empty
            || string.IsNullOrWhiteSpace(commit.ActorScope)
            || string.IsNullOrWhiteSpace(commit.RequestFingerprint)
            || commit.RequestFingerprint.Length != 64)
        {
            throw new ArgumentException("Command persistence idempotency data is invalid.", nameof(commit));
        }

        if (commit.NewRevision != commit.ExpectedRevision + 1
            || commit.Event.Revision != commit.NewRevision
            || commit.CommandResult.NewRevision != commit.NewRevision
            || commit.CommandResult.PreviousRevision != commit.ExpectedRevision
            || commit.CommandResult.EventId != commit.Event.EventId
            || commit.CommandResult.FormulaId != commit.FormulaId
            || commit.Event.FormulaId != commit.FormulaId)
        {
            throw new ArgumentException("Command persistence revisions or identities are inconsistent.", nameof(commit));
        }

        if (!string.Equals(commit.Event.AfterStateHash, commit.AggregateStateHash, StringComparison.Ordinal)
            || !string.Equals(commit.CommandResult.FormulaHash, commit.FormulaHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("Command persistence hashes are inconsistent.", nameof(commit));
        }
    }

    private static void ValidateAnalysis(AnalysisSnapshotPersistenceRecord snapshot)
    {
        if (snapshot.FormulaRevision < 0
            || snapshot.CurrentRevisionAtFinish < 0
            || snapshot.ConfidenceScore is < 0 or > 1
            || snapshot.FreshnessState is not ("fresh" or "stale")
            || snapshot.FormulaHash.Length != 64
            || snapshot.NumericOutputHash.Length != 64)
        {
            throw new ArgumentException("Analysis persistence data is invalid.", nameof(snapshot));
        }
    }

    private static PersistedFormula CloneFormula(PersistedFormula formula)
    {
        return formula with
        {
            Components = formula.Components.ToArray(),
            Events = formula.Events.ToArray(),
        };
    }

    private readonly record struct IdempotencyKey(string ActorScope, Guid Key);

    private readonly record struct AnalysisInputKey(
        string FormulaId,
        long Revision,
        string EngineVersion,
        string BlockLibraryVersion,
        string RelationLibraryVersion);
}
