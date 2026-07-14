using System.Text.Json;
using MyScent.ScentEngine.Analysis;
using MyScent.ScentEngine.Specifications;

namespace MyScent.SpecSmokeTests;

internal sealed class ScentFixtureRegressionRunner
{
    private readonly ScentAnalysisEngine _engine;
    private readonly JsonElement _root;
    private readonly double _tolerance;

    public ScentFixtureRegressionRunner(ScentSpecificationBundle specifications)
    {
        ArgumentNullException.ThrowIfNull(specifications);
        _engine = new ScentAnalysisEngine(specifications);

        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "specs",
            "scent",
            "scent-fixtures.v0.1.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
        _root = document.RootElement.Clone();
        _tolerance = _root.GetProperty("numeric_tolerance").GetDouble();
    }

    public IReadOnlyList<string> RunAll()
    {
        var failures = new List<string>();
        foreach (var fixture in _root.GetProperty("fixtures").EnumerateArray())
        {
            var id = fixture.GetProperty("id").GetString()
                ?? throw new InvalidDataException("Fixture id is required.");
            try
            {
                RunFixture(id, fixture);
                Console.WriteLine($" - {id}: passed");
            }
            catch (Exception exception)
            {
                failures.Add($"{id}: {exception.Message}");
            }
        }

        return failures;
    }

    private void RunFixture(string id, JsonElement fixture)
    {
        switch (id)
        {
            case "T-009":
                RunUndoFixture(fixture);
                return;
            case "T-010":
                RunReplaceFixture(fixture);
                return;
            case "T-011":
                RunCrossSceneFixture(fixture);
                return;
            case "T-012":
                RunExternalMaterialFixture(fixture);
                return;
            case "T-013":
                RunDeterminismFixture(fixture);
                return;
            case "T-015":
                RunVersionReplayFixture(fixture);
                return;
            default:
                RunExpectedAnalysisFixture(fixture);
                return;
        }
    }

    private void RunExpectedAnalysisFixture(JsonElement fixture)
    {
        var result = AnalyzeComponents(fixture.GetProperty("components"));
        AssertExpected(result, fixture.GetProperty("expected"));
    }

    private void RunUndoFixture(JsonElement fixture)
    {
        var components = new List<FormulaComponentInput>();
        ScentAnalysisResult? baseline = null;
        ScentAnalysisResult? restored = null;

        foreach (var operation in fixture.GetProperty("operation_sequence").EnumerateArray())
        {
            var command = operation.GetProperty("command").GetString();
            switch (command)
            {
                case "component.add":
                    components.Add(new FormulaComponentInput
                    {
                        BlockId = operation.GetProperty("block_id").GetString(),
                        Quantity = operation.GetProperty("quantity").GetDouble(),
                    });
                    break;
                case "analysis.request":
                    var analysis = _engine.Analyze(new ScentAnalysisRequest { Components = [.. components] });
                    var capture = operation.GetProperty("capture_as").GetString();
                    if (string.Equals(capture, "baseline", StringComparison.Ordinal))
                    {
                        baseline = analysis;
                    }
                    else if (string.Equals(capture, "restored", StringComparison.Ordinal))
                    {
                        restored = analysis;
                    }

                    break;
                case "operation.undo":
                    components.RemoveAt(components.Count - 1);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported undo fixture command: {command}.");
            }
        }

        Require(baseline is not null && restored is not null, "Undo fixture did not capture both analyses.");
        Require(
            string.Equals(baseline.FormulaHash, restored.FormulaHash, StringComparison.Ordinal),
            "Undo did not restore the formula hash.");
        Require(
            string.Equals(
                baseline.CanonicalNumericOutputHash,
                restored.CanonicalNumericOutputHash,
                StringComparison.Ordinal),
            "Undo did not restore the numeric output hash.");
    }

    private void RunReplaceFixture(JsonElement fixture)
    {
        var initial = ParseComponents(fixture.GetProperty("initial_components"));
        var before = _engine.Analyze(new ScentAnalysisRequest { Components = initial });
        AssertPartialExpected(before, fixture.GetProperty("expected_before"));

        var operation = fixture.GetProperty("operation");
        var from = operation.GetProperty("from_block_id").GetString();
        var to = operation.GetProperty("to_block_id").GetString();
        var index = initial.FindIndex(component => string.Equals(component.BlockId, from, StringComparison.Ordinal));
        Require(index >= 0, "Replace fixture source component is missing.");
        var quantity = initial[index].Quantity;
        initial[index] = new FormulaComponentInput { BlockId = to, Quantity = quantity };

        var after = _engine.Analyze(new ScentAnalysisRequest { Components = initial });
        var expectedAfter = fixture.GetProperty("expected_after");
        AssertPartialExpected(after, expectedAfter);
        foreach (var absentId in expectedAfter.GetProperty("absent_component_ids").EnumerateArray())
        {
            var value = absentId.GetString();
            Require(
                after.Components.All(component => !string.Equals(component.ComponentId, value, StringComparison.Ordinal)),
                $"Replaced component {value} remains in the result.");
        }
    }

    private void RunCrossSceneFixture(JsonElement fixture)
    {
        var components = ParseComponents(fixture.GetProperty("components"));
        var scenes = fixture.GetProperty("scenes").EnumerateArray().Select(static item => item.GetString()).ToArray();
        Require(scenes.Length == 2, "Cross-scene fixture requires two scenes.");

        var first = _engine.Analyze(new ScentAnalysisRequest { Components = components, SceneId = scenes[0] });
        var second = _engine.Analyze(new ScentAnalysisRequest { Components = components, SceneId = scenes[1] });
        Require(first.FormulaHash == second.FormulaHash, "Scene changed the formula hash.");
        Require(
            first.CanonicalNumericOutputHash == second.CanonicalNumericOutputHash,
            "Scene changed the numeric output hash.");
    }

    private void RunExternalMaterialFixture(JsonElement fixture)
    {
        var result = AnalyzeComponents(fixture.GetProperty("components"));
        Require(result.Confidence.Score < 0.55, $"Expected low confidence, found {result.Confidence.Score}.");
        Require(result.Confidence.Level == "low", $"Expected low confidence level, found {result.Confidence.Level}.");
        Require(
            result.Confidence.Reasons.Contains("external_material_incomplete", StringComparer.Ordinal),
            "Missing external-material confidence reason.");
        Require(
            result.Warnings.Contains("not_platform_verified", StringComparer.Ordinal),
            "Missing external-material warning.");
    }

    private void RunDeterminismFixture(JsonElement fixture)
    {
        var components = ParseComponents(fixture.GetProperty("components"));
        var repeatCount = fixture.GetProperty("repeat_count").GetInt32();
        var formulaHashes = new HashSet<string>(StringComparer.Ordinal);
        var numericHashes = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < repeatCount; index++)
        {
            var result = _engine.Analyze(new ScentAnalysisRequest { Components = components });
            formulaHashes.Add(result.FormulaHash);
            numericHashes.Add(result.CanonicalNumericOutputHash);
        }

        Require(formulaHashes.Count == 1, $"Formula hash changed across {repeatCount} runs.");
        Require(numericHashes.Count == 1, $"Numeric output changed across {repeatCount} runs.");
    }

    private void RunVersionReplayFixture(JsonElement fixture)
    {
        var replay = AnalyzeComponents(fixture.GetProperty("components"));
        var referenceFixture = FindFixture("T-002");
        AssertExpected(replay, referenceFixture.GetProperty("expected"));
    }

    private ScentAnalysisResult AnalyzeComponents(JsonElement components)
    {
        return _engine.Analyze(new ScentAnalysisRequest { Components = ParseComponents(components) });
    }

    private static List<FormulaComponentInput> ParseComponents(JsonElement components)
    {
        var result = new List<FormulaComponentInput>();
        foreach (var component in components.EnumerateArray())
        {
            if (component.TryGetProperty("external_material_id", out var externalId))
            {
                result.Add(new FormulaComponentInput
                {
                    Quantity = component.GetProperty("quantity").GetDouble(),
                    ExternalMaterial = new ExternalMaterialInput
                    {
                        ExternalMaterialId = externalId.GetString()
                            ?? throw new InvalidDataException("External material id is required."),
                        MappedBlockIds = ReadStringArray(component.GetProperty("mapped_block_ids")),
                        AttributeVector = ReadDoubleDictionary(component.GetProperty("attribute_vector")),
                        Intensity = component.GetProperty("intensity").GetDouble(),
                        StageWeights = ReadDoubleDictionary(component.GetProperty("stage_weights")),
                        DataQuality = component.GetProperty("data_quality").GetDouble(),
                        MappingQuality = component.GetProperty("mapping_quality").GetDouble(),
                        MissingFields = ReadStringArray(component.GetProperty("missing_fields")),
                    },
                });
            }
            else
            {
                result.Add(new FormulaComponentInput
                {
                    BlockId = component.GetProperty("block_id").GetString(),
                    Quantity = component.GetProperty("quantity").GetDouble(),
                });
            }
        }

        return result;
    }

    private void AssertExpected(ScentAnalysisResult actual, JsonElement expected)
    {
        AssertDoubleDictionary("attributes", actual.Attributes, expected.GetProperty("attributes"));
        AssertStageShares(actual.Stages, expected.GetProperty("stage_shares"));
        AssertPrimary(actual.PrimaryComponents, expected.GetProperty("primary"));
        AssertMetrics(actual, expected.GetProperty("metrics"));
        AssertStringSequence(
            "active relations",
            actual.ActiveRelations.Select(static relation => relation.RelationId),
            expected.GetProperty("active_relations"));
        AssertStringSequence("problems", actual.Problems, expected.GetProperty("problems"));

        if (expected.TryGetProperty("required_suggestion_actions", out var actions))
        {
            foreach (var action in actions.EnumerateArray())
            {
                var value = action.GetString();
                Require(
                    actual.Suggestions.Any(suggestion =>
                        string.Equals(suggestion.ActionType, value, StringComparison.Ordinal)),
                    $"Missing suggestion action {value}.");
            }
        }
    }

    private void AssertPartialExpected(ScentAnalysisResult actual, JsonElement expected)
    {
        AssertDoubleDictionary("attributes", actual.Attributes, expected.GetProperty("attributes"));
        AssertStringSequence(
            "active relations",
            actual.ActiveRelations.Select(static relation => relation.RelationId),
            expected.GetProperty("active_relations"));
    }

    private void AssertDoubleDictionary(
        string label,
        IReadOnlyDictionary<string, double> actual,
        JsonElement expected)
    {
        foreach (var property in expected.EnumerateObject())
        {
            Require(actual.TryGetValue(property.Name, out var actualValue), $"{label} is missing {property.Name}.");
            AssertNear($"{label}.{property.Name}", actualValue, property.Value.GetDouble());
        }
    }

    private void AssertStageShares(
        IReadOnlyDictionary<string, StageAnalysis> actual,
        JsonElement expected)
    {
        foreach (var property in expected.EnumerateObject())
        {
            Require(actual.TryGetValue(property.Name, out var stage), $"Missing stage {property.Name}.");
            AssertNear($"stage.{property.Name}", stage.Share, property.Value.GetDouble());
        }
    }

    private void AssertPrimary(IReadOnlyList<ComponentContribution> actual, JsonElement expected)
    {
        var expectedItems = expected.EnumerateArray().ToArray();
        Require(actual.Count == expectedItems.Length, "Primary component count differs.");
        for (var index = 0; index < expectedItems.Length; index++)
        {
            var expectedId = expectedItems[index].GetProperty("block_id").GetString();
            Require(actual[index].ComponentId == expectedId, $"Primary component {index} differs.");
            AssertNear(
                $"primary.{expectedId}",
                actual[index].Share,
                expectedItems[index].GetProperty("share").GetDouble());
        }
    }

    private void AssertMetrics(ScentAnalysisResult actual, JsonElement expected)
    {
        AssertNear("metrics.clarity", actual.Metrics.Clarity, expected.GetProperty("clarity").GetDouble());
        AssertNear("metrics.complexity", actual.Metrics.Complexity, expected.GetProperty("complexity").GetDouble());
        AssertNear("metrics.continuity", actual.Metrics.Continuity, expected.GetProperty("continuity").GetDouble());
        AssertNear("metrics.balance", actual.Metrics.Balance, expected.GetProperty("balance").GetDouble());
        AssertNear("metrics.confidence", actual.Confidence.Score, expected.GetProperty("confidence").GetDouble());
    }

    private static void AssertStringSequence(
        string label,
        IEnumerable<string> actual,
        JsonElement expected)
    {
        var actualItems = actual.ToArray();
        var expectedItems = expected.EnumerateArray().Select(static item => item.GetString()).ToArray();
        Require(
            actualItems.SequenceEqual(expectedItems, StringComparer.Ordinal),
            $"{label} differ. expected=[{string.Join(",", expectedItems)}] actual=[{string.Join(",", actualItems)}].");
    }

    private void AssertNear(string label, double actual, double expected)
    {
        if (Math.Abs(actual - expected) > _tolerance)
        {
            throw new InvalidOperationException(
                $"{label} expected {expected:R}, found {actual:R}, tolerance {_tolerance:R}.");
        }
    }

    private JsonElement FindFixture(string id)
    {
        foreach (var fixture in _root.GetProperty("fixtures").EnumerateArray())
        {
            if (string.Equals(fixture.GetProperty("id").GetString(), id, StringComparison.Ordinal))
            {
                return fixture;
            }
        }

        throw new InvalidDataException($"Fixture not found: {id}.");
    }

    private static Dictionary<string, double> ReadDoubleDictionary(JsonElement element)
    {
        return element.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => property.Value.GetDouble(),
            StringComparer.Ordinal);
    }

    private static string[] ReadStringArray(JsonElement element)
    {
        return element.EnumerateArray()
            .Select(static item => item.GetString() ?? string.Empty)
            .ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
