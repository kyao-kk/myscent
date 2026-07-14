using System.Diagnostics.CodeAnalysis;
using MyScent.Formulas;
using MyScent.ScentEngine.Analysis;
using MyScent.ScentEngine.Specifications;

namespace MyScent.SpecSmokeTests;

internal sealed class FormulaAggregateRegressionRunner
{
    private readonly ScentSpecificationBundle _specifications;
    private readonly string[] _validBlockIds;
    private readonly FormulaEngineVersions _versions;
    private readonly DateTimeOffset _clock = new(2026, 7, 14, 9, 30, 0, TimeSpan.Zero);

    public FormulaAggregateRegressionRunner(ScentSpecificationBundle specifications)
    {
        ArgumentNullException.ThrowIfNull(specifications);
        _specifications = specifications;
        _validBlockIds = specifications.Blocks.Blocks.Select(static block => block.Id).ToArray();
        _versions = new FormulaEngineVersions(
            specifications.Engine.EngineVersion,
            specifications.Blocks.SchemaVersion,
            specifications.Relations.SchemaVersion);
    }

    public IReadOnlyList<string> RunAll()
    {
        var tests = new (string Name, Action Action)[]
        {
            ("A2-001 add increments revision and matches engine hash", AddIncrementsRevisionAndMatchesEngineHash),
            ("A2-002 idempotent retry returns original result", IdempotentRetryReturnsOriginalResult),
            ("A2-003 duplicate idempotency mismatch is rejected", DuplicateIdempotencyMismatchIsRejected),
            ("A2-004 revision conflict preserves state", RevisionConflictPreservesState),
            ("A2-005 replace is atomic", ReplaceIsAtomic),
            ("A2-006 undo appends compensation and restores hash", UndoRestoresHash),
            ("A2-007 event replay rebuilds state", EventReplayRebuildsState),
            ("A2-008 final component removal is blocked", FinalRemovalIsBlocked),
            ("A2-009 scene does not affect formula hash", SceneDoesNotAffectHash),
            ("A2-010 seal creates a mutation boundary", SealBlocksFurtherMutation),
            ("A2-011 invalid command is fully atomic", InvalidCommandIsAtomic),
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

    private void AddIncrementsRevisionAndMatchesEngineHash()
    {
        var aggregate = NewAggregate("formula-add");
        var result = Execute(aggregate, new AddComponentCommand("SB-CA-01", 4));

        Require(result.PreviousRevision == 0 && result.NewRevision == 1, "Add did not increment revision once.");
        Require(aggregate.Revision == 1, "Aggregate revision is incorrect after add.");
        Require(aggregate.Events.Count == 2, "Add should append exactly one event after creation.");
        Require(aggregate.Components["SB-CA-01"] == 4, "Added component quantity is incorrect.");

        var analysis = new ScentAnalysisEngine(_specifications).Analyze(new ScentAnalysisRequest
        {
            Components = [new FormulaComponentInput { BlockId = "SB-CA-01", Quantity = 4 }],
        });
        Require(result.FormulaHash == analysis.FormulaHash, "Formula aggregate hash differs from scent engine hash.");
    }

    private void IdempotentRetryReturnsOriginalResult()
    {
        var aggregate = NewAggregate("formula-idempotent");
        var idempotencyKey = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var command = Envelope(
            aggregate,
            new AddComponentCommand("SB-CA-01", 2),
            commandId,
            idempotencyKey);

        var first = aggregate.Execute(command, _clock);
        var second = aggregate.Execute(command, _clock.AddSeconds(5));
        Require(first == second, "Idempotent retry did not return the original result.");
        Require(aggregate.Revision == 1, "Idempotent retry incremented revision.");
        Require(aggregate.Events.Count == 2, "Idempotent retry appended a duplicate event.");
    }

    private void DuplicateIdempotencyMismatchIsRejected()
    {
        var aggregate = NewAggregate("formula-idempotency-mismatch");
        var idempotencyKey = Guid.NewGuid();
        aggregate.Execute(
            Envelope(aggregate, new AddComponentCommand("SB-CA-01", 2), Guid.NewGuid(), idempotencyKey),
            _clock);

        var exception = ExpectError(
            () => aggregate.Execute(
                Envelope(
                    aggregate,
                    new AddComponentCommand("SB-CA-02", 2),
                    Guid.NewGuid(),
                    idempotencyKey,
                    expectedRevision: 0),
                _clock.AddSeconds(1)),
            "DUPLICATE_COMMAND_MISMATCH");
        Require(exception.Code == "DUPLICATE_COMMAND_MISMATCH", "Wrong idempotency mismatch code.");
        Require(aggregate.Revision == 1 && aggregate.Events.Count == 2, "Mismatch changed aggregate state.");
    }

    private void RevisionConflictPreservesState()
    {
        var aggregate = NewAggregate("formula-revision");
        Execute(aggregate, new AddComponentCommand("SB-CA-01", 2));
        var hash = aggregate.FormulaHash;
        var events = aggregate.Events.Count;

        ExpectError(
            () => aggregate.Execute(
                Envelope(
                    aggregate,
                    new AddComponentCommand("SB-FL-01", 2),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    expectedRevision: 0),
                _clock),
            "REVISION_CONFLICT");
        Require(aggregate.FormulaHash == hash, "Revision conflict changed formula hash.");
        Require(aggregate.Events.Count == events && aggregate.Revision == 1, "Revision conflict mutated history.");
    }

    private void ReplaceIsAtomic()
    {
        var aggregate = NewAggregate("formula-replace");
        Execute(aggregate, new AddComponentCommand("SB-CA-01", 4));
        Execute(aggregate, new AddComponentCommand("SB-MS-01", 2));
        var eventCount = aggregate.Events.Count;
        var result = Execute(
            aggregate,
            new ReplaceComponentCommand("SB-CA-01", "SB-FL-01", PreserveQuantity: true));

        Require(result.NewRevision == 3, "Replace did not increment exactly one revision.");
        Require(aggregate.Events.Count == eventCount + 1, "Replace appended more than one event.");
        Require(!aggregate.Components.ContainsKey("SB-CA-01"), "Replace retained source component.");
        Require(aggregate.Components["SB-FL-01"] == 4, "Replace did not preserve quantity.");
        Require(result.ComponentDelta.Added.Count == 1, "Replace delta lacks target addition.");
        Require(result.ComponentDelta.Removed.SequenceEqual(["SB-CA-01"], StringComparer.Ordinal), "Replace delta lacks source removal.");
    }

    private void UndoRestoresHash()
    {
        var aggregate = NewAggregate("formula-undo");
        Execute(aggregate, new AddComponentCommand("SB-CA-01", 4));
        Execute(aggregate, new AddComponentCommand("SB-FL-01", 6));
        var baselineHash = aggregate.FormulaHash;
        Execute(aggregate, new AddComponentCommand("SB-MS-01", 2));
        var addedEvent = aggregate.Events[^1];
        var eventCount = aggregate.Events.Count;

        var undo = Execute(aggregate, new UndoOperationCommand());
        Require(aggregate.FormulaHash == baselineHash, "Undo did not restore formula hash.");
        Require(aggregate.Events.Count == eventCount + 1, "Undo did not append an event.");
        Require(undo.EventType == "operation_undone", "Undo event type is incorrect.");
        Require(aggregate.Events[^1].CompensatesEventId == addedEvent.EventId, "Undo does not reference compensated event.");
        Require(!aggregate.Components.ContainsKey("SB-MS-01"), "Undo retained the added component.");
    }

    private void EventReplayRebuildsState()
    {
        var aggregate = NewAggregate("formula-replay");
        Execute(aggregate, new AddComponentCommand("SB-CA-01", 4));
        Execute(aggregate, new AddComponentCommand("SB-FL-01", 6));
        Execute(aggregate, new IncreaseComponentCommand("SB-CA-01", 1.5));
        Execute(aggregate, new ReplaceComponentCommand("SB-FL-01", "SB-FL-02", PreserveQuantity: true));

        var replayed = FormulaAggregate.Replay(
            aggregate.FormulaId,
            _validBlockIds,
            _versions,
            aggregate.Events);
        Require(replayed.Revision == aggregate.Revision, "Replay revision differs.");
        Require(replayed.FormulaHash == aggregate.FormulaHash, "Replay formula hash differs.");
        Require(
            replayed.Components.OrderBy(static item => item.Key, StringComparer.Ordinal)
                .SequenceEqual(aggregate.Components.OrderBy(static item => item.Key, StringComparer.Ordinal)),
            "Replay components differ.");
    }

    private void FinalRemovalIsBlocked()
    {
        var aggregate = NewAggregate("formula-remove");
        Execute(aggregate, new AddComponentCommand("SB-CA-01", 4));
        var hash = aggregate.FormulaHash;
        ExpectError(() => Execute(aggregate, new RemoveComponentCommand("SB-CA-01")), "LAST_COMPONENT_REMOVAL_BLOCKED");
        Require(aggregate.FormulaHash == hash && aggregate.Revision == 1, "Blocked removal changed state.");
    }

    private void SceneDoesNotAffectHash()
    {
        var laboratory = NewAggregate("formula-scene-a");
        var alchemy = NewAggregate("formula-scene-b");
        Execute(laboratory, new AddComponentCommand("SB-CA-03", 2), sceneId: "SCENE-LAB-001");
        Execute(alchemy, new AddComponentCommand("SB-CA-03", 2), sceneId: "SCENE-ALCHEMY-001");
        Execute(laboratory, new AddComponentCommand("SB-GH-03", 3), sceneId: "SCENE-LAB-001");
        Execute(alchemy, new AddComponentCommand("SB-GH-03", 3), sceneId: "SCENE-ALCHEMY-001");
        Require(laboratory.FormulaHash == alchemy.FormulaHash, "Scene changed canonical formula hash.");
    }

    private void SealBlocksFurtherMutation()
    {
        var aggregate = NewAggregate("formula-seal");
        Execute(aggregate, new AddComponentCommand("SB-CA-01", 4));
        Execute(aggregate, new SealFormulaCommand("Morning Light", PrerequisitesSatisfied: true));
        Require(aggregate.IsSealed && aggregate.Title == "Morning Light", "Seal did not update aggregate state.");
        Require(!aggregate.UndoAvailable, "Seal did not clear undo boundary.");
        ExpectError(() => Execute(aggregate, new AddComponentCommand("SB-FL-01", 2)), "FORMULA_SEALED");
    }

    private void InvalidCommandIsAtomic()
    {
        var aggregate = NewAggregate("formula-atomic");
        Execute(aggregate, new AddComponentCommand("SB-CA-01", 4));
        var revision = aggregate.Revision;
        var hash = aggregate.FormulaHash;
        var events = aggregate.Events.Count;
        var components = aggregate.Components.ToArray();

        ExpectError(
            () => Execute(
                aggregate,
                new ReplaceComponentCommand(
                    "SB-CA-01",
                    "SB-NOT-FOUND",
                    PreserveQuantity: true)),
            "BLOCK_NOT_FOUND");
        Require(aggregate.Revision == revision, "Invalid replace changed revision.");
        Require(aggregate.FormulaHash == hash, "Invalid replace changed hash.");
        Require(aggregate.Events.Count == events, "Invalid replace appended an event.");
        Require(aggregate.Components.SequenceEqual(components), "Invalid replace changed components.");
    }

    private FormulaAggregate NewAggregate(string formulaId)
    {
        return new FormulaAggregate(formulaId, _validBlockIds, _versions, "tester", _clock);
    }

    private FormulaCommandResult Execute(
        FormulaAggregate aggregate,
        FormulaCommandPayload payload,
        string sceneId = "SCENE-LAB-001")
    {
        return aggregate.Execute(
            Envelope(
                aggregate,
                payload,
                Guid.NewGuid(),
                Guid.NewGuid(),
                sceneId: sceneId),
            _clock.AddSeconds(aggregate.Revision + 1));
    }

    private FormulaCommandEnvelope Envelope(
        FormulaAggregate aggregate,
        FormulaCommandPayload payload,
        Guid commandId,
        Guid idempotencyKey,
        long? expectedRevision = null,
        string sceneId = "SCENE-LAB-001")
    {
        return new FormulaCommandEnvelope
        {
            CommandId = commandId,
            IdempotencyKey = idempotencyKey,
            FormulaId = aggregate.FormulaId,
            ExpectedRevision = expectedRevision ?? aggregate.Revision,
            ActorId = "tester",
            SessionId = "session-001",
            SceneId = sceneId,
            IssuedAt = _clock,
            Payload = payload,
        };
    }

    private static FormulaCommandException ExpectError(Action action, string expectedCode)
    {
        try
        {
            action();
        }
        catch (FormulaCommandException exception)
        {
            Require(exception.Code == expectedCode, $"Expected {expectedCode}, found {exception.Code}.");
            return exception;
        }

        throw new InvalidOperationException($"Expected formula command error {expectedCode}.");
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
