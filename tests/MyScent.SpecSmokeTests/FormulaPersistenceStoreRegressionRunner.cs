using System.Diagnostics.CodeAnalysis;
using MyScent.Formulas;
using MyScent.Formulas.Persistence;
using MyScent.Infrastructure.Persistence;
using MyScent.ScentEngine.Specifications;

namespace MyScent.SpecSmokeTests;

internal sealed class FormulaPersistenceStoreRegressionRunner
{
    private readonly string[] _validBlockIds;
    private readonly FormulaEngineVersions _versions;
    private readonly DateTimeOffset _clock = new(2026, 7, 14, 11, 0, 0, TimeSpan.Zero);

    public FormulaPersistenceStoreRegressionRunner(ScentSpecificationBundle specifications)
    {
        ArgumentNullException.ThrowIfNull(specifications);
        _validBlockIds = specifications.Blocks.Blocks
            .Select(static block => block.Id)
            .ToArray();
        _versions = new FormulaEngineVersions(
            specifications.Engine.EngineVersion,
            specifications.Blocks.SchemaVersion,
            specifications.Relations.SchemaVersion);
    }

    public IReadOnlyList<string> RunAll()
    {
        var tests = new (string Name, Action Action)[]
        {
            ("STORE-001 create and load formula projection", CreateAndLoadFormula),
            ("STORE-002 command commit is atomic", CommandCommitIsAtomic),
            ("STORE-003 duplicate idempotency returns original record", DuplicateIdempotencyReturnsOriginal),
            ("STORE-004 revision conflict preserves persisted state", RevisionConflictPreservesState),
            ("STORE-005 invalid commit preserves persisted state", InvalidCommitPreservesState),
            ("STORE-006 analysis input is unique and deterministic", AnalysisInputIsUnique),
        };

        var failures = new List<string>();
        foreach (var test in tests)
        {
            try
            {
                test.Action();
                Console.WriteLine($" - {test.Name}: passed");
            }
            catch (Exception exception)
            {
                failures.Add($"{test.Name}: {exception.Message}");
            }
        }

        return failures;
    }

    private void CreateAndLoadFormula()
    {
        var store = new InMemoryFormulaPersistenceStore();
        var aggregate = NewAggregate("store-create");
        PersistCreation(store, aggregate);

        var loaded = store.LoadFormulaAsync(aggregate.FormulaId).GetAwaiter().GetResult();
        Require(loaded is not null, "Created formula was not loaded.");
        Require(loaded.Revision == 0 && loaded.Events.Count == 1, "Created projection is incomplete.");
        Require(loaded.AggregateStateHash == aggregate.Events[0].AfterStateHash, "Creation state hash differs.");
        Require(store.FormulaCount == 1, "Formula count differs after creation.");
    }

    private void CommandCommitIsAtomic()
    {
        var store = new InMemoryFormulaPersistenceStore();
        var aggregate = NewAggregate("store-commit");
        PersistCreation(store, aggregate);
        var (envelope, result) = Execute(
            aggregate,
            new AddComponentCommand("SB-CA-01", 4));
        var commit = BuildCommit(aggregate, envelope, result);

        var outcome = store.CommitCommandAsync(commit).GetAwaiter().GetResult();
        Require(outcome.Status == FormulaCommandCommitStatus.Committed, "Command was not committed.");

        var loaded = store.LoadFormulaAsync(aggregate.FormulaId).GetAwaiter().GetResult();
        Require(loaded is not null, "Committed formula was not loaded.");
        Require(loaded.Revision == 1, "Committed revision differs.");
        Require(loaded.Components.SequenceEqual([new FormulaComponent("SB-CA-01", 4)]), "Current projection differs.");
        Require(loaded.Events.Count == 2, "Event was not appended with the projection update.");
        Require(store.IdempotencyCount == 1, "Idempotency response was not committed atomically.");
    }

    private void DuplicateIdempotencyReturnsOriginal()
    {
        var store = new InMemoryFormulaPersistenceStore();
        var aggregate = NewAggregate("store-idempotency");
        PersistCreation(store, aggregate);
        var (envelope, result) = Execute(
            aggregate,
            new AddComponentCommand("SB-CA-01", 4));
        var commit = BuildCommit(aggregate, envelope, result);
        store.CommitCommandAsync(commit).GetAwaiter().GetResult();

        var duplicate = store.CommitCommandAsync(commit).GetAwaiter().GetResult();
        Require(
            duplicate.Status == FormulaCommandCommitStatus.DuplicateIdempotency,
            "Duplicate idempotency did not return the duplicate outcome.");
        Require(duplicate.ExistingIdempotency is not null, "Duplicate outcome lacks the original record.");
        Require(
            duplicate.ExistingIdempotency.CommandResult == result,
            "Duplicate outcome did not preserve the original command response.");
        Require(store.IdempotencyCount == 1, "Duplicate request inserted another idempotency record.");

        var loaded = store.LoadFormulaAsync(aggregate.FormulaId).GetAwaiter().GetResult();
        Require(loaded?.Revision == 1 && loaded.Events.Count == 2, "Duplicate request changed formula state.");
    }

    private void RevisionConflictPreservesState()
    {
        var store = new InMemoryFormulaPersistenceStore();
        var persistedAggregate = NewAggregate("store-revision");
        PersistCreation(store, persistedAggregate);
        var (firstEnvelope, firstResult) = Execute(
            persistedAggregate,
            new AddComponentCommand("SB-CA-01", 4));
        store.CommitCommandAsync(BuildCommit(persistedAggregate, firstEnvelope, firstResult))
            .GetAwaiter()
            .GetResult();

        var staleAggregate = NewAggregate("store-revision");
        var (staleEnvelope, staleResult) = Execute(
            staleAggregate,
            new AddComponentCommand("SB-FL-01", 2));
        var conflict = store.CommitCommandAsync(
                BuildCommit(staleAggregate, staleEnvelope, staleResult))
            .GetAwaiter()
            .GetResult();

        Require(conflict.Status == FormulaCommandCommitStatus.RevisionConflict, "Stale commit was not rejected.");
        Require(conflict.CurrentRevision == 1, "Conflict did not return the current revision.");
        var loaded = store.LoadFormulaAsync("store-revision").GetAwaiter().GetResult();
        Require(loaded?.Components.Count == 1, "Revision conflict changed current components.");
        Require(loaded?.Components[0].BlockId == "SB-CA-01", "Revision conflict replaced persisted state.");
        Require(store.IdempotencyCount == 1, "Revision conflict persisted an idempotency success response.");
    }

    private void InvalidCommitPreservesState()
    {
        var store = new InMemoryFormulaPersistenceStore();
        var aggregate = NewAggregate("store-invalid");
        PersistCreation(store, aggregate);
        var (envelope, result) = Execute(
            aggregate,
            new AddComponentCommand("SB-CA-01", 4));
        var valid = BuildCommit(aggregate, envelope, result);
        var invalid = valid with { AggregateStateHash = new string('0', 64) };

        ExpectException<ArgumentException>(
            () => store.CommitCommandAsync(invalid).GetAwaiter().GetResult());
        var loaded = store.LoadFormulaAsync(aggregate.FormulaId).GetAwaiter().GetResult();
        Require(loaded?.Revision == 0 && loaded.Events.Count == 1, "Invalid commit partially changed formula state.");
        Require(store.IdempotencyCount == 0, "Invalid commit inserted an idempotency record.");
    }

    private void AnalysisInputIsUnique()
    {
        var store = new InMemoryFormulaPersistenceStore();
        var aggregate = NewAggregate("store-analysis");
        PersistCreation(store, aggregate);
        var snapshot = new AnalysisSnapshotPersistenceRecord
        {
            AnalysisId = "analysis-001",
            FormulaId = aggregate.FormulaId,
            FormulaRevision = 0,
            CurrentRevisionAtFinish = 0,
            FreshnessState = "fresh",
            FormulaHash = aggregate.FormulaHash,
            NumericOutputHash = new string('a', 64),
            SceneId = "SCENE-LAB-001",
            EngineVersions = _versions,
            PredictionJson = "{}",
            ConfidenceScore = 0.8m,
            CreatedAt = _clock,
        };

        store.SaveAnalysisAsync(snapshot).GetAwaiter().GetResult();
        store.SaveAnalysisAsync(snapshot with { AnalysisId = "analysis-duplicate" })
            .GetAwaiter()
            .GetResult();
        Require(store.AnalysisCount == 1, "Equivalent analysis input created a duplicate snapshot.");

        ExpectException<InvalidOperationException>(() =>
            store.SaveAnalysisAsync(snapshot with
            {
                AnalysisId = "analysis-divergent",
                NumericOutputHash = new string('b', 64),
            }).GetAwaiter().GetResult());
        Require(store.AnalysisCount == 1, "Divergent duplicate changed analysis storage.");
    }

    private FormulaAggregate NewAggregate(string formulaId)
    {
        return new FormulaAggregate(formulaId, _validBlockIds, _versions, "guest-001", _clock);
    }

    private void PersistCreation(
        InMemoryFormulaPersistenceStore store,
        FormulaAggregate aggregate)
    {
        var creationEvent = aggregate.Events[0];
        var formula = new PersistedFormula
        {
            FormulaId = aggregate.FormulaId,
            ActorScope = "guest:guest-001",
            GuestId = "guest-001",
            Status = "editing",
            Revision = 0,
            FormulaHash = aggregate.FormulaHash,
            AggregateStateHash = creationEvent.AfterStateHash,
            CurrentSceneId = "SCENE-LAB-001",
            EngineVersions = _versions,
            Components = [],
            Events = [creationEvent],
            CreatedAt = _clock,
            UpdatedAt = _clock,
        };
        store.CreateFormulaAsync(new FormulaCreationCommit
        {
            Formula = formula,
            CreationEvent = creationEvent,
        }).GetAwaiter().GetResult();
    }

    private (FormulaCommandEnvelope Envelope, FormulaCommandResult Result) Execute(
        FormulaAggregate aggregate,
        FormulaCommandPayload payload)
    {
        var envelope = new FormulaCommandEnvelope
        {
            CommandId = Guid.NewGuid(),
            IdempotencyKey = Guid.NewGuid(),
            FormulaId = aggregate.FormulaId,
            ExpectedRevision = aggregate.Revision,
            ActorId = "guest-001",
            SessionId = "session-store",
            SceneId = "SCENE-LAB-001",
            IssuedAt = _clock,
            Payload = payload,
        };
        var result = aggregate.Execute(envelope, _clock.AddSeconds(aggregate.Revision + 1));
        return (envelope, result);
    }

    private FormulaCommandPersistenceCommit BuildCommit(
        FormulaAggregate aggregate,
        FormulaCommandEnvelope envelope,
        FormulaCommandResult result)
    {
        var @event = aggregate.Events[^1];
        return new FormulaCommandPersistenceCommit
        {
            ActorScope = "guest:guest-001",
            IdempotencyKey = envelope.IdempotencyKey,
            RequestFingerprint = FormulaCommandFingerprint.Calculate(envelope),
            FormulaId = aggregate.FormulaId,
            ExpectedRevision = result.PreviousRevision,
            NewRevision = result.NewRevision,
            FormulaHash = aggregate.FormulaHash,
            AggregateStateHash = @event.AfterStateHash,
            CurrentSceneId = envelope.SceneId,
            Status = aggregate.IsSealed ? "sealed" : "editing",
            Title = aggregate.Title,
            CurrentComponents = aggregate.Components
                .OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => new FormulaComponent(item.Key, item.Value))
                .ToArray(),
            Event = @event,
            CommandResult = result,
            IdempotencyExpiresAt = _clock.AddDays(7),
        };
    }

    private static void ExpectException<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }

    private static void Require(
        [DoesNotReturnIf(false)] bool condition,
        string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
