using System.Globalization;
using System.Text;
using MyScent.ScentEngine.Specifications;

namespace MyScent.ScentEngine.Analysis;

public sealed class ScentAnalysisEngine
{
    private readonly ScentSpecificationBundle _specifications;
    private readonly Dictionary<string, ScentBlockDefinition> _blocks;
    private readonly ScentEngineSettings _settings;

    public ScentAnalysisEngine(ScentSpecificationBundle specifications)
    {
        ArgumentNullException.ThrowIfNull(specifications);
        _specifications = specifications;
        _blocks = specifications.Blocks.Blocks.ToDictionary(static block => block.Id, StringComparer.Ordinal);
        _settings = ScentEngineSettings.From(specifications.Engine);
    }

    public ScentAnalysisResult Analyze(ScentAnalysisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var components = BuildComponents(request.Components);
        if (components.Count == 0)
        {
            throw new ArgumentException("At least one positive-quantity component is required.", nameof(request));
        }

        CalculateInitialShares(components);
        var activeRelations = ApplyMasking(components);
        RenormalizePerceivedShares(components);

        var attributes = ScentAnalysisMath.CalculateBaseAttributes(components);
        var bridgeEffects = new List<double>();
        var conflictPenalty = ApplyNonMaskingRelations(
            components,
            attributes,
            activeRelations,
            bridgeEffects);
        ClampAttributes(attributes);

        var stages = ScentAnalysisMath.CalculateStages(components);
        var families = ScentAnalysisMath.CalculateFamilyContributions(components);
        var continuity = ScentAnalysisMath.CalculateContinuity(
            components,
            stages,
            bridgeEffects,
            conflictPenalty);
        var activeGroups = EvaluateGroupRules(components, attributes);
        var complexity = ScentAnalysisMath.CalculateComplexity(
            components,
            families.Count,
            activeRelations.Count,
            activeGroups.Sum(static group => group.ComplexityDelta));
        var clarity = ScentAnalysisMath.CalculateClarity(
            components,
            families,
            stages,
            continuity,
            conflictPenalty,
            activeGroups.Sum(static group => group.ClarityDelta));
        var balance = ScentAnalysisMath.CalculateBalance(
            stages,
            conflictPenalty,
            activeGroups.Sum(static group => Math.Abs(group.ClarityDelta)));

        var metrics = new ScentDerivedMetrics(
            Round(clarity),
            Round(complexity),
            Round(continuity),
            Round(balance));
        var problems = DetectProblems(
            components,
            attributes,
            stages,
            metrics,
            activeRelations,
            activeGroups,
            conflictPenalty);
        var suggestions = GenerateSuggestions(components, problems);
        var confidence = CalculateConfidence(components);

        var roundedAttributes = attributes.ToDictionary(
            static pair => pair.Key,
            pair => Round(pair.Value),
            StringComparer.Ordinal);
        var roundedStages = stages.ToDictionary(
            static pair => pair.Key,
            pair => RoundStage(pair.Value),
            StringComparer.Ordinal);
        var primary = components
            .OrderByDescending(static component => component.PerceivedShare)
            .ThenBy(static component => component.ComponentId, StringComparer.Ordinal)
            .Take(2)
            .Select(component => new ComponentContribution(component.ComponentId, Round(component.PerceivedShare)))
            .ToArray();
        var analyzed = components
            .OrderBy(static component => component.ComponentId, StringComparer.Ordinal)
            .Select(component => new AnalyzedComponent(
                component.ComponentId,
                component.RelationIdentity,
                component.Family,
                component.Quantity,
                Round(component.NormalizedShare),
                Round(component.PreMaskShare),
                Round(component.PerceivedShare),
                component.IsExternal))
            .ToArray();
        var roundedFamilies = families.ToDictionary(
            static pair => pair.Key,
            pair => Round(pair.Value),
            StringComparer.Ordinal);
        var formulaHash = CalculateFormulaHash(components);
        var numericHash = CalculateNumericHash(
            roundedAttributes,
            roundedStages,
            primary,
            metrics,
            activeRelations,
            problems,
            confidence);
        IReadOnlyList<string> warnings = components.Any(static component => component.IsExternal)
            ? ["not_platform_verified"]
            : [];

        return new ScentAnalysisResult
        {
            EngineVersion = _specifications.Engine.EngineVersion,
            BlockLibraryVersion = _specifications.Blocks.SchemaVersion,
            RelationLibraryVersion = _specifications.Relations.SchemaVersion,
            FormulaHash = formulaHash,
            CanonicalNumericOutputHash = numericHash,
            Components = analyzed,
            Attributes = roundedAttributes,
            Stages = roundedStages,
            PrimaryComponents = primary,
            FamilyContributions = roundedFamilies,
            Metrics = metrics,
            ActiveRelations = activeRelations
                .Select(relation => relation with { Effect = Round(relation.Effect) })
                .ToArray(),
            Problems = problems,
            Suggestions = suggestions,
            Confidence = confidence,
            Warnings = warnings,
        };
    }

    private List<ScentRuntimeComponent> BuildComponents(IReadOnlyList<FormulaComponentInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var result = new List<ScentRuntimeComponent>(inputs.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var input in inputs)
        {
            if (!double.IsFinite(input.Quantity))
            {
                throw new ArgumentException("Component quantity must be finite.", nameof(inputs));
            }

            if (input.Quantity < _settings.MinimumPositiveQuantity)
            {
                continue;
            }

            var component = input.ExternalMaterial is null
                ? BuildBlockComponent(input)
                : BuildExternalComponent(input);
            if (!ids.Add(component.ComponentId))
            {
                throw new ArgumentException($"Duplicate active component id: {component.ComponentId}.", nameof(inputs));
            }

            result.Add(component);
        }

        return result;
    }

    private ScentRuntimeComponent BuildBlockComponent(FormulaComponentInput input)
    {
        if (string.IsNullOrWhiteSpace(input.BlockId)
            || !_blocks.TryGetValue(input.BlockId, out var block))
        {
            throw new ArgumentException($"Unknown scent block: {input.BlockId}.", nameof(input));
        }

        return ScentRuntimeComponent.FromBlock(block, input.Quantity);
    }

    private ScentRuntimeComponent BuildExternalComponent(FormulaComponentInput input)
    {
        var external = input.ExternalMaterial
            ?? throw new InvalidOperationException("External material payload is required.");
        if (string.IsNullOrWhiteSpace(external.ExternalMaterialId))
        {
            throw new ArgumentException("External material id is required.", nameof(input));
        }

        ValidateKeys(external.AttributeVector.Keys, ScentAnalysisMath.AttributeKeys, "external attributes");
        ValidateKeys(external.StageWeights.Keys, ScentAnalysisMath.StageKeys, "external stages");
        ValidateUnitValues(external.AttributeVector.Values, "external attributes");
        ValidateUnitValues(external.StageWeights.Values, "external stages");
        if (Math.Abs(external.StageWeights.Values.Sum() - 1) > 1e-9)
        {
            throw new ArgumentException("External stage weights must sum to 1.0.", nameof(input));
        }

        var mapped = external.MappedBlockIds
            .Select(id => _blocks.GetValueOrDefault(id))
            .FirstOrDefault(static block => block is not null);
        return new ScentRuntimeComponent
        {
            ComponentId = external.ExternalMaterialId,
            RelationIdentity = mapped?.Id ?? external.ExternalMaterialId,
            Family = mapped?.Family ?? "external",
            Quantity = input.Quantity,
            AttributeVector = new Dictionary<string, double>(external.AttributeVector, StringComparer.Ordinal),
            Intensity = external.Intensity,
            StageWeights = new Dictionary<string, double>(external.StageWeights, StringComparer.Ordinal),
            DataQuality = ScentAnalysisMath.Clamp(external.DataQuality),
            MappingQuality = ScentAnalysisMath.Clamp(external.MappingQuality),
            OverloadGroups = mapped?.OverloadGroups ?? [],
            IsExternal = true,
            ExternalMissingFields = external.MissingFields,
        };
    }

    private void CalculateInitialShares(List<ScentRuntimeComponent> components)
    {
        var quantityTotal = components.Sum(static component => component.Quantity);
        var effective = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            component.NormalizedShare = component.Quantity / quantityTotal;
            effective[component.ComponentId] =
                Math.Pow(component.NormalizedShare, _settings.Gamma) * component.Intensity;
        }

        var effectiveTotal = effective.Values.Sum();
        if (!double.IsFinite(effectiveTotal) || effectiveTotal <= 0)
        {
            throw new InvalidOperationException("Effective component strength is invalid.");
        }

        foreach (var component in components)
        {
            component.PreMaskShare = effective[component.ComponentId] / effectiveTotal;
            component.PerceivedShare = component.PreMaskShare;
        }
    }

    private List<ActiveRelation> ApplyMasking(List<ScentRuntimeComponent> components)
    {
        var factors = components.ToDictionary(
            static component => component.ComponentId,
            static _ => 1.0,
            StringComparer.Ordinal);
        var active = new List<ActiveRelation>();

        foreach (var rule in _specifications.Relations.PairRules.Where(
                     static rule => string.Equals(rule.Relation, "masking", StringComparison.Ordinal)))
        {
            var source = FindComponent(components, rule.Source);
            var target = FindComponent(components, rule.Target);
            if (source is null || target is null || source.PreMaskShare < rule.Threshold)
            {
                continue;
            }

            var effect = Math.Max(0, source.PreMaskShare - rule.Threshold) * rule.Strength;
            if (effect <= 0)
            {
                continue;
            }

            factors[target.ComponentId] *= 1 - effect;
            active.Add(new ActiveRelation(
                rule.RuleId,
                rule.Relation,
                source.ComponentId,
                target.ComponentId,
                effect));
        }

        foreach (var component in components)
        {
            component.PerceivedShare = component.PreMaskShare * factors[component.ComponentId];
        }

        return active;
    }

    private static void RenormalizePerceivedShares(List<ScentRuntimeComponent> components)
    {
        var total = components.Sum(static component => component.PerceivedShare);
        if (!double.IsFinite(total) || total <= 0)
        {
            throw new InvalidOperationException("Perceived component strength is invalid.");
        }

        foreach (var component in components)
        {
            component.PerceivedShare /= total;
        }
    }

    private double ApplyNonMaskingRelations(
        List<ScentRuntimeComponent> components,
        Dictionary<string, double> attributes,
        List<ActiveRelation> active,
        List<double> bridgeEffects)
    {
        var conflictPenalty = 0.0;
        foreach (var type in new[] { "synergy", "bridge", "conflict" })
        {
            foreach (var rule in _specifications.Relations.PairRules.Where(
                         rule => string.Equals(rule.Relation, type, StringComparison.Ordinal)))
            {
                var source = FindComponent(components, rule.Source);
                var target = FindComponent(components, rule.Target);
                if (source is null || target is null)
                {
                    continue;
                }

                var minimum = Math.Min(source.PerceivedShare, target.PerceivedShare);
                if (minimum < rule.Threshold)
                {
                    continue;
                }

                var effect = minimum * rule.Strength;
                active.Add(new ActiveRelation(
                    rule.RuleId,
                    rule.Relation,
                    source.ComponentId,
                    target.ComponentId,
                    effect));
                if (string.Equals(type, "synergy", StringComparison.Ordinal))
                {
                    foreach (var metric in rule.AffectedMetrics.Where(attributes.ContainsKey))
                    {
                        attributes[metric] += effect;
                    }
                }
                else if (string.Equals(type, "bridge", StringComparison.Ordinal))
                {
                    bridgeEffects.Add(effect);
                }
                else
                {
                    conflictPenalty += effect;
                    foreach (var metric in rule.AffectedMetrics.Where(attributes.ContainsKey))
                    {
                        attributes[metric] -= effect * 0.5;
                    }
                }
            }
        }

        return conflictPenalty;
    }

    private List<ActiveGroupRule> EvaluateGroupRules(
        List<ScentRuntimeComponent> components,
        Dictionary<string, double> attributes)
    {
        var result = new List<ActiveGroupRule>();
        foreach (var rule in _specifications.Relations.GroupRules)
        {
            var members = components
                .Where(component => component.OverloadGroups.Contains(rule.Group, StringComparer.Ordinal))
                .ToArray();
            if (members.Length < rule.MinActiveComponents
                || members.Sum(static component => component.PerceivedShare)
                    < rule.CombinedEffectiveShareThreshold
                || !ScentAnalysisMath.ConditionsMatch(rule.Conditions, attributes))
            {
                continue;
            }

            result.Add(new ActiveGroupRule(
                rule.RuleId,
                rule.ProblemCode,
                rule.Effects.GetValueOrDefault("clarity_delta"),
                rule.Effects.GetValueOrDefault("complexity_delta")));
        }

        return result;
    }

    private List<string> DetectProblems(
        List<ScentRuntimeComponent> components,
        Dictionary<string, double> attributes,
        Dictionary<string, StageAnalysis> stages,
        ScentDerivedMetrics metrics,
        List<ActiveRelation> relations,
        List<ActiveGroupRule> groups,
        double conflictPenalty)
    {
        var result = new List<string>();
        foreach (var problem in groups.Select(static group => group.ProblemCode))
        {
            AddUnique(result, problem);
        }

        var sweetShare = components
            .Where(static component => component.OverloadGroups.Contains("sweet_heavy", StringComparer.Ordinal))
            .Sum(static component => component.PerceivedShare);
        if (!result.Contains("too_sweet_heavy", StringComparer.Ordinal)
            && attributes["sweetness"] >= 0.78
            && sweetShare >= 0.35)
        {
            AddUnique(result, "too_sweet");
        }

        if (!result.Contains("dark_heavy_overload", StringComparer.Ordinal)
            && attributes["weight"] >= 0.78
            && attributes["transparency"] <= 0.28)
        {
            AddUnique(result, "too_heavy");
        }

        if (stages["heart"].Share < _settings.InsufficientStageShare
            || stages["heart"].PrimaryShare < 0.25)
        {
            AddUnique(result, "middle_missing");
        }

        if (stages["base"].Share < _settings.InsufficientStageShare
            || attributes["longevity"] < 0.35)
        {
            AddUnique(result, "tail_insufficient");
        }

        if (relations.Any(relation =>
                string.Equals(relation.RelationType, "masking", StringComparison.Ordinal)
                && relation.Effect >= _settings.MaskedSubjectThreshold))
        {
            AddUnique(result, "masked_subject");
        }

        var familyCount = components
            .Select(static component => component.Family)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (familyCount >= 5
            || (familyCount >= 4 && metrics.Clarity < 0.45 && metrics.Complexity > 0.65))
        {
            AddUnique(result, "too_many_themes");
        }

        if (relations.Any(static relation =>
                string.Equals(relation.RelationId, "REL-027", StringComparison.Ordinal))
            || (attributes["freshness"] >= 0.65
                && attributes["warmth"] >= 0.65
                && conflictPenalty > 0))
        {
            AddUnique(result, "cold_warm_tension");
        }

        if (relations.Any(static relation =>
                string.Equals(relation.RelationType, "conflict", StringComparison.Ordinal)))
        {
            AddUnique(result, "pair_conflict");
        }

        return result;
    }

    private static List<ScentSuggestion> GenerateSuggestions(
        List<ScentRuntimeComponent> components,
        List<string> problems)
    {
        var result = new List<ScentSuggestion>();
        var primary = components.OrderByDescending(static component => component.PerceivedShare).First();
        if (problems.Contains("too_sweet", StringComparer.Ordinal)
            || problems.Contains("too_sweet_heavy", StringComparer.Ordinal))
        {
            result.Add(new ScentSuggestion("too_sweet", "decrease_component", primary.ComponentId));
            result.Add(new ScentSuggestion("too_sweet", "add_or_increase_bitter_fresh_bridge", null));
            result.Add(new ScentSuggestion("too_sweet", "add_or_increase_dry_wood_support", null));
        }

        if (problems.Contains("middle_missing", StringComparer.Ordinal))
        {
            result.Add(new ScentSuggestion("middle_missing", "add_or_increase_heart_bridge", null));
        }

        if (problems.Contains("tail_insufficient", StringComparer.Ordinal))
        {
            result.Add(new ScentSuggestion("tail_insufficient", "add_or_increase_base_support", null));
        }

        if (problems.Contains("masked_subject", StringComparer.Ordinal))
        {
            result.Add(new ScentSuggestion("masked_subject", "decrease_masking_component", null));
        }

        return result;
    }

    private ScentConfidence CalculateConfidence(List<ScentRuntimeComponent> components)
    {
        var dataCompleteness = components.Sum(component => component.PerceivedShare * component.DataQuality);
        var mappingQuality = components.Sum(component => component.PerceivedShare * component.MappingQuality);
        var allPairs = Math.Max(1, components.Count * (components.Count - 1) / 2);
        var coveredPairs = 0;
        for (var left = 0; left < components.Count; left++)
        {
            for (var right = left + 1; right < components.Count; right++)
            {
                if (HasDefinedRelation(components[left], components[right]))
                {
                    coveredPairs++;
                }
            }
        }

        var ruleCoverage = 0.60 + 0.40 * Math.Min(1, coveredPairs / (double)allPairs);
        var inDomain = components.Count switch
        {
            <= 6 => 1.0,
            <= 8 => 0.75,
            _ => 0.50,
        };
        var score = ScentAnalysisMath.Clamp(
            0.30 * dataCompleteness
            + 0.25 * ruleCoverage
            + 0.20 * mappingQuality
            + 0.15 * inDomain
            + 0.10 * 0.75);
        IReadOnlyList<string> reasons = components.Any(static component =>
            component.IsExternal && component.ExternalMissingFields.Count > 0)
            ? ["external_material_incomplete"]
            : [];
        var level = score >= 0.80 ? "high" : score >= 0.55 ? "medium" : "low";
        return new ScentConfidence(Round(score), level, reasons);
    }

    private bool HasDefinedRelation(ScentRuntimeComponent left, ScentRuntimeComponent right)
    {
        return _specifications.Relations.PairRules.Any(rule =>
            (string.Equals(rule.Source, left.RelationIdentity, StringComparison.Ordinal)
             && string.Equals(rule.Target, right.RelationIdentity, StringComparison.Ordinal))
            || (string.Equals(rule.Source, right.RelationIdentity, StringComparison.Ordinal)
                && string.Equals(rule.Target, left.RelationIdentity, StringComparison.Ordinal)));
    }

    private string CalculateFormulaHash(List<ScentRuntimeComponent> components)
    {
        var canonical = new StringBuilder()
            .Append(_specifications.Engine.EngineVersion).Append('|')
            .Append(_specifications.Blocks.SchemaVersion).Append('|')
            .Append(_specifications.Relations.SchemaVersion);
        foreach (var component in components.OrderBy(static component => component.ComponentId, StringComparer.Ordinal))
        {
            canonical.Append('|').Append(component.ComponentId)
                .Append(':').Append(component.Quantity.ToString("R", CultureInfo.InvariantCulture))
                .Append(':').Append(component.RelationIdentity)
                .Append(':').Append(component.IsExternal ? '1' : '0');
            if (!component.IsExternal)
            {
                continue;
            }

            foreach (var key in ScentAnalysisMath.AttributeKeys)
            {
                canonical.Append(':')
                    .Append(component.AttributeVector[key].ToString("R", CultureInfo.InvariantCulture));
            }

            foreach (var stage in ScentAnalysisMath.StageKeys)
            {
                canonical.Append(':')
                    .Append(component.StageWeights[stage].ToString("R", CultureInfo.InvariantCulture));
            }

            canonical.Append(':').Append(component.Intensity.ToString("R", CultureInfo.InvariantCulture))
                .Append(':').Append(component.DataQuality.ToString("R", CultureInfo.InvariantCulture))
                .Append(':').Append(component.MappingQuality.ToString("R", CultureInfo.InvariantCulture));
        }

        return ScentAnalysisMath.Hash(canonical.ToString());
    }

    private static string CalculateNumericHash(
        Dictionary<string, double> attributes,
        Dictionary<string, StageAnalysis> stages,
        IReadOnlyList<ComponentContribution> primary,
        ScentDerivedMetrics metrics,
        List<ActiveRelation> relations,
        List<string> problems,
        ScentConfidence confidence)
    {
        var canonical = new StringBuilder();
        foreach (var key in ScentAnalysisMath.AttributeKeys)
        {
            canonical.Append(key).Append('=').Append(ScentAnalysisMath.Format(attributes[key])).Append('|');
        }

        foreach (var stage in ScentAnalysisMath.StageKeys)
        {
            canonical.Append(stage).Append('=').Append(ScentAnalysisMath.Format(stages[stage].Share)).Append('|');
        }

        foreach (var component in primary)
        {
            canonical.Append(component.ComponentId).Append('=').Append(ScentAnalysisMath.Format(component.Share)).Append('|');
        }

        canonical.Append(ScentAnalysisMath.Format(metrics.Clarity)).Append('|')
            .Append(ScentAnalysisMath.Format(metrics.Complexity)).Append('|')
            .Append(ScentAnalysisMath.Format(metrics.Continuity)).Append('|')
            .Append(ScentAnalysisMath.Format(metrics.Balance)).Append('|');
        foreach (var relation in relations)
        {
            canonical.Append(relation.RelationId).Append('|');
        }

        foreach (var problem in problems)
        {
            canonical.Append(problem).Append('|');
        }

        canonical.Append(ScentAnalysisMath.Format(confidence.Score));
        return ScentAnalysisMath.Hash(canonical.ToString());
    }

    private StageAnalysis RoundStage(StageAnalysis stage)
    {
        return new StageAnalysis
        {
            Share = Round(stage.Share),
            Components = stage.Components
                .Select(component => component with { Share = Round(component.Share) })
                .ToArray(),
            PrimaryComponentId = stage.PrimaryComponentId,
            PrimaryShare = Round(stage.PrimaryShare),
        };
    }

    private double Round(double value) => ScentAnalysisMath.Round(value, _settings.RoundingDigits);

    private static void ClampAttributes(Dictionary<string, double> attributes)
    {
        foreach (var key in ScentAnalysisMath.AttributeKeys)
        {
            attributes[key] = ScentAnalysisMath.Clamp(attributes[key]);
        }
    }

    private static ScentRuntimeComponent? FindComponent(
        List<ScentRuntimeComponent> components,
        string relationIdentity)
    {
        return components.FirstOrDefault(component =>
            string.Equals(component.RelationIdentity, relationIdentity, StringComparison.Ordinal));
    }

    private static void AddUnique(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.Ordinal))
        {
            values.Add(value);
        }
    }

    private static void ValidateKeys(
        IEnumerable<string> actualKeys,
        IEnumerable<string> expectedKeys,
        string label)
    {
        var actual = actualKeys.ToHashSet(StringComparer.Ordinal);
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
        {
            throw new ArgumentException($"{label} do not contain the required keys.");
        }
    }

    private static void ValidateUnitValues(IEnumerable<double> values, string label)
    {
        if (values.Any(static value => !double.IsFinite(value) || value is < 0 or > 1))
        {
            throw new ArgumentException($"{label} must be finite and between zero and one.");
        }
    }
}
