using System.Diagnostics.CodeAnalysis;
using MyScent.Formulas;
using MyScent.ScentEngine.Specifications;

namespace MyScent.SpecSmokeTests;

internal sealed class FormulaApplicationServiceRegressionRunner
{
    private readonly ScentSpecificationBundle _specifications;
    private readonly DateTimeOffset _clock = new(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);

    public FormulaApplicationServiceRegressionRunner(ScentSpecificationBundle specifications)
    {
        ArgumentNullException.ThrowIfNull(specifications);
        _specifications = specifications;
    }

    public IReadOnlyList<string> RunAll()
    {
        var tests = new (string Name, Action Action)[]
        {
            ("B3-001 create and read formula", CreateAndReadFormula),
            ("B3-002 command and analysis share canonical hash", CommandAndAnalysisShareHash),
            ("B3-003 events expose append-only history", EventsExposeHistory),
            ("B3-004 empty formula analysis is rejected", EmptyFormulaAnalysisIsRejected),
            ("B3-005 missing formula returns stable code", MissingFormulaReturnsStableCode),
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

    private void CreateAndReadFormula()
    {
        var service = NewService();
        var created = service.CreateFormula(
            "tester",
            "SCENE-LAB-001",
            _clock,
            "service-create");
        var loaded = service.GetFormula(created.FormulaId);
        Require(created == loaded, "Created and loaded formula views differ.");
        Require(created.Revision == 0 && created.Components.Count == 0, "New formula state is invalid.");
    }

    private void CommandAndAnalysisShareHash()
    {
        var service = NewService();
        var formula = service.CreateFormula("tester", "SCENE-LAB-001", _clock, "service-analysis");
        var add = service.ExecuteCommand(
            Envelope(
                formula.FormulaId,
                0,
                new AddComponentCommand("SB-CA-01", 4)),
            _clock.AddSeconds(1));
        service.ExecuteCommand(
            Envelope(
                formula.FormulaId,
                1,
                new AddComponentCommand("SB-FL-01", 6)),
            _clock.AddSeconds(2));

        var analysis = service.Analyze(formula.FormulaId, "SCENE-ALCHEMY-001", _clock.AddSeconds(3));
        Require(analysis.State == "fresh", "Synchronous analysis should be fresh.");
        Require(analysis.FormulaRevision == 2 && analysis.CurrentFormulaRevision == 2, "Analysis revision is incorrect.");
        Require(analysis.Analysis.FormulaHash == service.GetFormula(formula.FormulaId).FormulaHash, "Analysis and formula hashes differ.");
        Require(add.NewRevision == 1, "First command response revision is incorrect.");
    }

    private void EventsExposeHistory()
    {
        var service = NewService();
        var formula = service.CreateFormula("tester", "SCENE-LAB-001", _clock, "service-events");
        service.ExecuteCommand(
            Envelope(formula.FormulaId, 0, new AddComponentCommand("SB-CA-03", 2)),
            _clock.AddSeconds(1));
        service.ExecuteCommand(
            Envelope(formula.FormulaId, 1, new AddComponentCommand("SB-GH-03", 3)),
            _clock.AddSeconds(2));

        var events = service.GetEvents(formula.FormulaId);
        Require(events.Count == 3, "Event history should contain create and two mutations.");
        Require(events[0].EventType == "formula_created", "First event is not formula_created.");
        Require(events[1].Revision == 1 && events[2].Revision == 2, "Event revisions are not contiguous.");
        Require(events[1].AfterStateHash == events[2].BeforeStateHash, "Event hash chain is broken.");
    }

    private void EmptyFormulaAnalysisIsRejected()
    {
        var service = NewService();
        var formula = service.CreateFormula("tester", "SCENE-LAB-001", _clock, "service-empty");
        ExpectError(
            () => service.Analyze(formula.FormulaId, "SCENE-LAB-001", _clock),
            "ENGINE_INPUT_INVALID");
    }

    private void MissingFormulaReturnsStableCode()
    {
        var service = NewService();
        ExpectError(() => service.GetFormula("missing-formula"), "FORMULA_NOT_FOUND");
    }

    private FormulaApplicationService NewService() => new(_specifications);

    private FormulaCommandEnvelope Envelope(
        string formulaId,
        long revision,
        FormulaCommandPayload payload)
    {
        return new FormulaCommandEnvelope
        {
            CommandId = Guid.NewGuid(),
            IdempotencyKey = Guid.NewGuid(),
            FormulaId = formulaId,
            ExpectedRevision = revision,
            ActorId = "tester",
            SessionId = "session-service",
            SceneId = "SCENE-LAB-001",
            IssuedAt = _clock,
            Payload = payload,
        };
    }

    private static void ExpectError(Action action, string expectedCode)
    {
        try
        {
            action();
        }
        catch (FormulaCommandException exception)
        {
            Require(exception.Code == expectedCode, $"Expected {expectedCode}, found {exception.Code}.");
            return;
        }

        throw new InvalidOperationException($"Expected formula error {expectedCode}.");
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
