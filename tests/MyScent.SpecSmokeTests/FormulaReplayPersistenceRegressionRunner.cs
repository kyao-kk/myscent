using System.Diagnostics.CodeAnalysis;
using MyScent.Formulas;
using MyScent.ScentEngine.Specifications;

namespace MyScent.SpecSmokeTests;

internal sealed class FormulaReplayPersistenceRegressionRunner
{
    private readonly string[] _validBlockIds;
    private readonly FormulaEngineVersions _versions;
    private readonly DateTimeOffset _clock = new(2026, 7, 14, 10, 30, 0, TimeSpan.Zero);

    public FormulaReplayPersistenceRegressionRunner(ScentSpecificationBundle specifications)
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
            ("PERSIST-001 replay rebuilds undo stack", ReplayRebuildsUndoStack),
            ("PERSIST-002 seal changes aggregate state hash only", SealChangesAggregateStateHashOnly),
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

    private void ReplayRebuildsUndoStack()
    {
        var aggregate = NewAggregate("replay-undo");
        Execute(aggregate, new AddComponentCommand("SB-CA-01", 4));
        Execute(aggregate, new AddComponentCommand("SB-FL-01", 6));
        var twoComponentHash = aggregate.FormulaHash;
        Execute(aggregate, new AddComponentCommand("SB-MS-01", 2));

        var replayed = FormulaAggregate.Replay(
            aggregate.FormulaId,
            _validBlockIds,
            _versions,
            aggregate.Events);

        Require(replayed.UndoAvailable, "Replayed aggregate lost its undo stack.");
        var previousEventCount = replayed.Events.Count;
        var undo = Execute(replayed, new UndoOperationCommand());

        Require(undo.EventType == "operation_undone", "Replay undo emitted the wrong event type.");
        Require(replayed.FormulaHash == twoComponentHash, "Replay undo did not restore the prior formula hash.");
        Require(!replayed.Components.ContainsKey("SB-MS-01"), "Replay undo retained the latest component.");
        Require(replayed.Events.Count == previousEventCount + 1, "Replay undo did not append a compensation event.");
        Require(
            replayed.Events[^1].CompensatesEventId == aggregate.Events[^1].EventId,
            "Replay undo did not reference the persisted event it compensates.");
    }

    private void SealChangesAggregateStateHashOnly()
    {
        var aggregate = NewAggregate("seal-state-hash");
        Execute(aggregate, new AddComponentCommand("SB-CA-01", 4));
        Execute(aggregate, new AddComponentCommand("SB-FL-01", 6));
        var formulaHashBeforeSeal = aggregate.FormulaHash;

        Execute(
            aggregate,
            new SealFormulaCommand("Morning Light", PrerequisitesSatisfied: true));

        var sealEvent = aggregate.Events[^1];
        Require(
            aggregate.FormulaHash == formulaHashBeforeSeal,
            "Sealing changed the canonical ingredient formula hash.");
        Require(
            sealEvent.BeforeStateHash != sealEvent.AfterStateHash,
            "Sealing did not change the aggregate state hash.");
        Require(aggregate.IsSealed && aggregate.Title == "Morning Light", "Seal state was not persisted in the aggregate.");

        var replayed = FormulaAggregate.Replay(
            aggregate.FormulaId,
            _validBlockIds,
            _versions,
            aggregate.Events);
        Require(replayed.FormulaHash == formulaHashBeforeSeal, "Replayed seal changed the formula hash.");
        Require(replayed.IsSealed && replayed.Title == "Morning Light", "Replay lost seal state or title.");
        Require(!replayed.UndoAvailable, "Replayed sealed aggregate exposed undo across the seal boundary.");
    }

    private FormulaAggregate NewAggregate(string formulaId)
    {
        return new FormulaAggregate(formulaId, _validBlockIds, _versions, "tester", _clock);
    }

    private FormulaCommandResult Execute(
        FormulaAggregate aggregate,
        FormulaCommandPayload payload)
    {
        return aggregate.Execute(
            new FormulaCommandEnvelope
            {
                CommandId = Guid.NewGuid(),
                IdempotencyKey = Guid.NewGuid(),
                FormulaId = aggregate.FormulaId,
                ExpectedRevision = aggregate.Revision,
                ActorId = "tester",
                SessionId = "session-persistence",
                SceneId = "SCENE-LAB-001",
                IssuedAt = _clock,
                Payload = payload,
            },
            _clock.AddSeconds(aggregate.Revision + 1));
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
