namespace MyScent.ScentEngine.Specifications;

public static class ScentSpecificationValidator
{
    private static readonly HashSet<string> RequiredAttributes =
    [
        "sweetness",
        "freshness",
        "warmth",
        "weight",
        "softness",
        "transparency",
        "diffusion",
        "longevity",
    ];

    private static readonly HashSet<string> RequiredStages = ["top", "heart", "base"];
    private static readonly HashSet<string> RelationTypes = ["synergy", "bridge", "masking", "conflict"];

    public static IReadOnlyList<string> Validate(ScentSpecificationBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var errors = new List<string>();
        ValidateBlocks(bundle.Blocks, errors);
        ValidateRelations(bundle.Relations, bundle.Blocks, errors);
        ValidateEngine(bundle.Engine, errors);
        return errors;
    }

    private static void ValidateBlocks(ScentBlockCatalog catalog, List<string> errors)
    {
        if (catalog.Blocks.Count != 24)
        {
            errors.Add($"Expected 24 scent blocks, found {catalog.Blocks.Count}.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in catalog.Blocks)
        {
            var prefix = $"Block {block.Id}";
            if (string.IsNullOrWhiteSpace(block.Id))
            {
                errors.Add("A scent block has an empty id.");
                continue;
            }

            if (!seen.Add(block.Id))
            {
                errors.Add($"Duplicate scent block id: {block.Id}.");
            }

            if (string.IsNullOrWhiteSpace(block.DisplayName))
            {
                errors.Add($"{prefix}: display name is required.");
            }

            if (string.IsNullOrWhiteSpace(block.Family))
            {
                errors.Add($"{prefix}: family is required.");
            }

            ValidateExactKeys(block.AttributeVector.Keys, RequiredAttributes, $"{prefix} attributes", errors);
            foreach (var (key, value) in block.AttributeVector)
            {
                ValidateFiniteRange(value, 0, 1, $"{prefix} attribute {key}", errors);
            }

            ValidateFiniteRange(block.Intensity, 0.30, 1.50, $"{prefix} intensity", errors);
            ValidateExactKeys(block.StageWeights.Keys, RequiredStages, $"{prefix} stage weights", errors);
            foreach (var (key, value) in block.StageWeights)
            {
                ValidateFiniteRange(value, 0, 1, $"{prefix} stage weight {key}", errors);
            }

            if (block.StageWeights.Count == 3)
            {
                var sum = block.StageWeights.Values.Sum();
                if (Math.Abs(sum - 1.0) > 1e-9)
                {
                    errors.Add($"{prefix}: stage weights must sum to 1.0, found {sum:R}.");
                }
            }

            ValidateFiniteRange(block.DataQuality, 0, 1, $"{prefix} data quality", errors);

            if (string.IsNullOrWhiteSpace(block.RealMaterialDirection))
            {
                errors.Add($"{prefix}: real material direction is required.");
            }
        }
    }

    private static void ValidateRelations(
        ScentRelationCatalog catalog,
        ScentBlockCatalog blocks,
        List<string> errors)
    {
        var blockIds = blocks.Blocks
            .Select(static block => block.Id)
            .ToHashSet(StringComparer.Ordinal);
        var overloadGroups = blocks.Blocks
            .SelectMany(static block => block.OverloadGroups)
            .ToHashSet(StringComparer.Ordinal);
        var ruleIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in catalog.PairRules)
        {
            var prefix = $"Pair rule {rule.RuleId}";
            if (!ruleIds.Add(rule.RuleId))
            {
                errors.Add($"Duplicate relation rule id: {rule.RuleId}.");
            }

            if (!blockIds.Contains(rule.Source))
            {
                errors.Add($"{prefix}: unknown source block {rule.Source}.");
            }

            if (!blockIds.Contains(rule.Target))
            {
                errors.Add($"{prefix}: unknown target block {rule.Target}.");
            }

            if (!RelationTypes.Contains(rule.Relation))
            {
                errors.Add($"{prefix}: unsupported relation type {rule.Relation}.");
            }

            ValidateFiniteRange(rule.Strength, 0, 1.50, $"{prefix} strength", errors);
            ValidateFiniteRange(rule.Threshold, 0, 1, $"{prefix} threshold", errors);

            if (rule.AffectedMetrics.Count == 0)
            {
                errors.Add($"{prefix}: affected metrics are required.");
            }
        }

        foreach (var rule in catalog.GroupRules)
        {
            var prefix = $"Group rule {rule.RuleId}";
            if (!ruleIds.Add(rule.RuleId))
            {
                errors.Add($"Duplicate relation rule id: {rule.RuleId}.");
            }

            if (!overloadGroups.Contains(rule.Group))
            {
                errors.Add($"{prefix}: group {rule.Group} is not referenced by any scent block.");
            }

            if (rule.MinActiveComponents < 1)
            {
                errors.Add($"{prefix}: min active components must be at least one.");
            }

            ValidateFiniteRange(
                rule.CombinedEffectiveShareThreshold,
                0,
                1,
                $"{prefix} combined share threshold",
                errors);
        }
    }

    private static void ValidateEngine(ScentEngineSpecification engine, List<string> errors)
    {
        if (!string.Equals(engine.EngineVersion, "0.1.0", StringComparison.Ordinal))
        {
            errors.Add($"Expected engine version 0.1.0, found {engine.EngineVersion}.");
        }

        if (!string.Equals(
                engine.Dependencies.BlockLibrary,
                "scent-blocks.v0.1.json",
                StringComparison.Ordinal))
        {
            errors.Add("Engine block-library dependency does not match scent-blocks.v0.1.json.");
        }

        if (!string.Equals(
                engine.Dependencies.RelationLibrary,
                "scent-relations.v0.1.json",
                StringComparison.Ordinal))
        {
            errors.Add("Engine relation-library dependency does not match scent-relations.v0.1.json.");
        }

        ValidateExactKeys(engine.Attributes.Keys, RequiredAttributes, "Engine attribute keys", errors);

        if (!engine.Canonicalization.FormulaHashFields.Contains("components", StringComparer.Ordinal))
        {
            errors.Add("Engine formula hash must include components.");
        }

        if (!engine.Canonicalization.ExcludeFromHash.Contains("scene_id", StringComparer.Ordinal))
        {
            errors.Add("Engine formula hash must exclude scene_id.");
        }
    }

    private static void ValidateExactKeys(
        IEnumerable<string> actual,
        IReadOnlySet<string> expected,
        string label,
        List<string> errors)
    {
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        if (!actualSet.SetEquals(expected))
        {
            errors.Add(
                $"{label}: expected [{string.Join(", ", expected.Order())}], "
                + $"found [{string.Join(", ", actualSet.Order())}].");
        }
    }

    private static void ValidateFiniteRange(
        double value,
        double minimum,
        double maximum,
        string label,
        List<string> errors)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            errors.Add($"{label} must be finite and in range {minimum}..{maximum}; found {value:R}.");
        }
    }
}
