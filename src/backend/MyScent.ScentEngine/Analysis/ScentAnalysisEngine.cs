using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyScent.ScentEngine.Specifications;

namespace MyScent.ScentEngine.Analysis;

public sealed class ScentAnalysisEngine
{
    private static readonly string[] AttributeKeys =
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

    private static readonly string[] StageKeys = ["top", "heart", "base"];

    private readonly ScentSpecificationBundle _specifications;
    private readonly Dictionary<string, ScentBlockDefinition> _blocks;
    private readonly double _gamma;
    private readonly double _minimumPositiveQuantity;
    private readonly int _roundingDigits;
    private readonly double _insufficientStageShare;
    private readonly double _maskedSubjectThreshold;

    public ScentAnalysisEngine(ScentSpecificationBundle specifications)
    {
        ArgumentNullException.ThrowIfNull(specifications);
        _specifications = specifications;
        _blocks = specifications.Blocks.Blocks.ToDictionary(static block => block.Id, StringComparer.Ordinal);

        _gamma = ReadDoubleSection(specifications.Engine, "contribution", "gamma");
        _minimumPositiveQuantity = ReadDoubleSection(
            specifications.Engine,
            "contribution",
            "minimum_positive_quantity");
        _roundingDigits = ReadIntSection(specifications.Engine, "numeric_policy", "api_rounding_digits");
        _insufficientStageShare = ReadDoubleSection(
            specifications.Engine,
            "stages",
            "insufficient_stage_share");
        _maskedSubjectThreshold = ReadNestedDoubleSection(
            specifications.Engine,
            "pair_relation_policy",
            "masking",
            "masked_subject_problem_threshold");
    }

    public ScentAnalysisResult Analyze(ScentAnalysisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var components = BuildRuntimeComponents(request.Components);
        if (components.Count == 0)
        {
            throw new ArgumentException("At least one positive-quantity component is required.", nameof(request));
        }

        CalculateInitialContributions(components);
        var activeRelations = ApplyMasking(components);
        RenormalizePerceivedShares(components);

        var attributes = CalculateBaseAttributes(components);
        var bridgeEffects = new List<double>();
        var conflictPenalty = ApplyNonMaskingRelations(
            components,
            attributes,
            activeRelations,
            bridgeEffects);
        ClampAttributes(attributes);

        var stages = CalculateStages(components);
        var familyContributions = CalculateFamilyContributions(components);
        var continuity = CalculateContinuity(components, stages, bridgeEffects, conflictPenalty);
        var activeGroups = EvaluateGroupRules(components, attributes);
        var complexity = CalculateComplexity(
            components,
            familyContributions.Count,
            activeRelations.Count,
            activeGroups.Sum(static group => group.ComplexityDelta));
        var clarity = CalculateClarity(
            components,
            familyContributions,
            stages,
            continuity,
            conflictPenalty,
            activeGroups.Sum(static group => group.ClarityDelta));
        var balance = CalculateBalance(
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
        var warnings = components.Any(static component => component.IsExternal)
            ? new[] { "not_platform_verified" }
            : [];

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
        var analyzedComponents = components
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
        var roundedFamilyContributions = familyContributions.ToDictionary(
            static pair => pair.Key,
            pair => Round(pair.Value),
            StringComparer.Ordinal);
        var formulaHash = CalculateFormulaHash(components);
        var numericOutputHash = CalculateNumericOutputHash(
            roundedAttributes,
            roundedStages,
            primary,
            metrics,
            activeRelations,
            problems,
            confidence);

        return new ScentAnalysisResult
        {
            EngineVersion = _specifications.Engine.EngineVersion,
            BlockLibraryVersion = _specifications.Blocks.SchemaVersion,
            RelationLibraryVersion = _specifications.Relations.SchemaVersion,
            FormulaHash = formulaHash,
            CanonicalNumericOutputHash = numericOutputHash,
            Components = analyzedComponents,
            Attributes = roundedAttributes,
            Stages = roundedStages,
            PrimaryComponents = primary,
            FamilyContributions = roundedFamilyContributions,
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

    private List<RuntimeComponent> BuildRuntimeComponents(IReadOnlyList<FormulaComponentInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var components = new List<RuntimeComponent>(inputs.Count);
        var componentIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var input in inputs)
        {
            if (!double.IsFinite(input.Quantity))
            {
                throw new ArgumentException("Component quantity must be finite.", nameof(inputs));
            }

            if (input.Quantity < _minimumPositiveQuantity)
            {
                continue;
            }

            RuntimeComponent component;
            if (input.ExternalMaterial is not null)
            {
                component = BuildExternalComponent(input);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(input.BlockId)
                    || !_blocks.TryGetValue(input.BlockId, out var block))
                {
                    throw new ArgumentException($"Unknown scent block: {input.BlockId}.", nameof(inputs));
                }

                component = RuntimeComponent.FromBlock(block, input.Quantity);
            }

            if (!componentIds.Add(component.ComponentId))
            {
                throw new ArgumentException(
                    $"Duplicate active component id: {component.ComponentId}.",
                    nameof(inputs));
            }

            components.Add(component);
        }

        return components;
    }

    private RuntimeComponent BuildExternalComponent(FormulaComponentInput input)
    {
        var external = input.ExternalMaterial
            ?? throw new InvalidOperationException("External material payload is required.");
        if (string.IsNullOrWhiteSpace(external.ExternalMaterialId))
        {
            throw new ArgumentException("External material id is required.", nameof(input));
        }

        ValidateExactKeys(external.AttributeVector.Keys, AttributeKeys, "external attribute vector");
        ValidateExactKeys(external.StageWeights.Keys, StageKeys, "external stage weights");
        ValidateUnitRange(external.AttributeVector.Values, "external attribute vector");
        ValidateUnitRange(external.StageWeights.Values, "external stage weights");
        if (Math.Abs(external.StageWeights.Values.Sum() - 1.0) > 1e-9)
        {
            throw new ArgumentException("External stage weights must sum to 1.0.", nameof(input));
        }

        var mappedBlock = external.MappedBlockIds
            .Select(mappedId => _blocks.GetValueOrDefault(mappedId))
            .FirstOrDefault(static block => block is not null);
        var relationIdentity = mappedBlock?.Id ?? external.ExternalMaterialId;
        var family = mappedBlock?.Family ?? "external";
        var overloadGroups = mappedBlock?.OverloadGroups ?? [];

        return new RuntimeComponent
        {
            ComponentId = external.ExternalMaterialId,
            RelationIdentity = relationIdentity,
            Family = family,
            Quantity = input.Quantity,
            AttributeVector = new Dictionary<string, double>(external.AttributeVector, StringComparer.Ordinal),
            Intensity = external.Intensity,
            StageWeights = new Dictionary<string, double>(external.StageWeights, StringComparer.Ordinal),
            DataQuality = Clamp(external.DataQuality),
            MappingQuality = Clamp(external.MappingQuality),
            OverloadGroups = overloadGroups,
            IsExternal = true,
            ExternalMissingFields = external.MissingFields,
        };
    }

    private void CalculateInitialContributions(List<RuntimeComponent> components)
    {
        var totalQuantity = components.Sum(static component => component.Quantity);
        var totalEffectiveStrength = 0.0;

        foreach (var component in components)
        {
            component.NormalizedShare = component.Quantity / totalQuantity;
            component.EffectiveStrength = Math.Pow(component.NormalizedShare, _gamma) * component.Intensity;
            totalEffectiveStrength += component.EffectiveStrength;
        }

        if (!double.IsFinite(totalEffectiveStrength) || totalEffectiveStrength <= 0)
        {
            throw new InvalidOperationException("Effective component strength is invalid.");
        }

        foreach (var component in components)
        {
            component.PreMaskShare = component.EffectiveStrength / totalEffectiveStrength;
            component.PerceivedShare = component.PreMaskShare;
        }
    }

    private List<ActiveRelation> ApplyMasking(List<RuntimeComponent> components)
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

    private static void RenormalizePerceivedShares(List<RuntimeComponent> components)
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

    private static Dictionary<string, double> CalculateBaseAttributes(List<RuntimeComponent> components)
    {
        var attributes = AttributeKeys.ToDictionary(static key => key, static _ => 0.0, StringComparer.Ordinal);
        foreach (var component in components)
        {
            foreach (var key in AttributeKeys)
            {
                attributes[key] += component.PerceivedShare * component.AttributeVector[key];
            }
        }

        return attributes;
    }

    private double ApplyNonMaskingRelations(
        List<RuntimeComponent> components,
        Dictionary<string, double> attributes,
        List<ActiveRelation> activeRelations,
        List<double> bridgeEffects)
    {
        var conflictPenalty = 0.0;
        foreach (var relationType in new[] { "synergy", "bridge", "conflict" })
        {
            foreach (var rule in _specifications.Relations.PairRules.Where(
                         rule => string.Equals(rule.Relation, relationType, StringComparison.Ordinal)))
            {
                var source = FindComponent(components, rule.Source);
                var target = FindComponent(components, rule.Target);
                if (source is null || target is null)
                {
                    continue;
                }

                var minimumShare = Math.Min(source.PerceivedShare, target.PerceivedShare);
                if (minimumShare < rule.Threshold)
                {
                    continue;
                }

                var effect = minimumShare * rule.Strength;
                activeRelations.Add(new ActiveRelation(
                    rule.RuleId,
                    rule.Relation,
                    source.ComponentId,
                    target.ComponentId,
                    effect));

                switch (relationType)
                {
                    case "synergy":
                        foreach (var metric in rule.AffectedMetrics.Where(attributes.ContainsKey))
                        {
                            attributes[metric] += effect;
                        }

                        break;
                    case "bridge":
                        bridgeEffects.Add(effect);
                        break;
                    case "conflict":
                        conflictPenalty += effect;
                        foreach (var metric in rule.AffectedMetrics.Where(attributes.ContainsKey))
                        {
                            attributes[metric] -= effect * 0.5;
                        }

                        break;
                }
            }
        }

        return conflictPenalty;
    }

    private static void ClampAttributes(Dictionary<string, double> attributes)
    {
        foreach (var key in AttributeKeys)
        {
            attributes[key] = Clamp(attributes[key]);
        }
    }

    private static Dictionary<string, StageAnalysis> CalculateStages(List<RuntimeComponent> components)
    {
        var rawByStage = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var stage in StageKeys)
        {
            rawByStage[stage] = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var component in components)
            {
                rawByStage[stage][component.ComponentId] =
                    component.PerceivedShare * component.StageWeights[stage];
            }
        }

        var stageTotals = rawByStage.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Values.Sum(),
            StringComparer.Ordinal);
        var allStageTotal = stageTotals.Values.Sum();
        var result = new Dictionary<string, StageAnalysis>(StringComparer.Ordinal);

        foreach (var stage in StageKeys)
        {
            var stageTotal = stageTotals[stage];
            var stageComponents = rawByStage[stage]
                .Select(pair => new ComponentContribution(
                    pair.Key,
                    stageTotal <= 0 ? 0 : pair.Value / stageTotal))
                .OrderByDescending(static component => component.Share)
                .ThenBy(static component => component.ComponentId, StringComparer.Ordinal)
                .ToArray();
            var primary = stageComponents.FirstOrDefault();
            result[stage] = new StageAnalysis
            {
                Share = allStageTotal <= 0 ? 0 : stageTotal / allStageTotal,
                Components = stageComponents,
                PrimaryComponentId = primary?.ComponentId,
                PrimaryShare = primary?.Share ?? 0,
            };
        }

        return result;
    }

    private static Dictionary<string, double> CalculateFamilyContributions(List<RuntimeComponent> components)
    {
        return components
            .GroupBy(static component => component.Family, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Sum(static component => component.PerceivedShare),
                StringComparer.Ordinal);
    }

    private static double CalculateContinuity(
        List<RuntimeComponent> components,
        Dictionary<string, StageAnalysis> stages,
        List<double> bridgeEffects,
        double conflictPenalty)
    {
        var familyShares = CalculateStageFamilyShares(components);
        var bridgeBonus = Clamp(bridgeEffects.Sum() * 4);
        var topHeartLink = Clamp(
            0.75 * CalculateFamilyOverlap(familyShares["top"], familyShares["heart"])
            + 0.25 * bridgeBonus);
        var heartBaseLink = Clamp(
            0.75 * CalculateFamilyOverlap(familyShares["heart"], familyShares["base"])
            + 0.25 * bridgeBonus);
        var middleSupport = Clamp(stages["heart"].Share / 0.30);

        return Clamp(0.40 * topHeartLink + 0.40 * heartBaseLink + 0.20 * middleSupport - conflictPenalty);
    }

    private static Dictionary<string, Dictionary<string, double>> CalculateStageFamilyShares(
        List<RuntimeComponent> components)
    {
        var result = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var stage in StageKeys)
        {
            var raw = components
                .GroupBy(static component => component.Family, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    group => group.Sum(component => component.PerceivedShare * component.StageWeights[stage]),
                    StringComparer.Ordinal);
            var total = raw.Values.Sum();
            result[stage] = raw.ToDictionary(
                static pair => pair.Key,
                pair => total <= 0 ? 0 : pair.Value / total,
                StringComparer.Ordinal);
        }

        return result;
    }

    private static double CalculateFamilyOverlap(
        Dictionary<string, double> left,
        Dictionary<string, double> right)
    {
        return left.Keys
            .Union(right.Keys, StringComparer.Ordinal)
            .Sum(key => Math.Min(left.GetValueOrDefault(key), right.GetValueOrDefault(key)));
    }

    private List<ActiveGroupRule> EvaluateGroupRules(
        List<RuntimeComponent> components,
        Dictionary<string, double> attributes)
    {
        var active = new List<ActiveGroupRule>();
        foreach (var rule in _specifications.Relations.GroupRules)
        {
            var members = components
                .Where(component => component.OverloadGroups.Contains(rule.Group, StringComparer.Ordinal))
                .ToArray();
            if (members.Length < rule.MinActiveComponents)
            {
                continue;
            }

            var combinedShare = members.Sum(static component => component.PerceivedShare);
            if (combinedShare < rule.CombinedEffectiveShareThreshold
                || !ConditionsMatch(rule.Conditions, attributes))
            {
                continue;
            }

            active.Add(new ActiveGroupRule(
                rule.RuleId,
                rule.ProblemCode,
                rule.Effects.GetValueOrDefault("clarity_delta"),
                rule.Effects.GetValueOrDefault("complexity_delta")));
        }

        return active;
    }

    private static bool ConditionsMatch(
        IReadOnlyDictionary<string, double> conditions,
        Dictionary<string, double> attributes)
    {
        foreach (var (key, value) in conditions)
        {
            if (key.EndsWith("_min", StringComparison.Ordinal))
            {
                var attribute = key[..^4];
                if (!attributes.TryGetValue(attribute, out var actual) || actual < value)
                {
                    return false;
                }
            }
            else if (key.EndsWith("_max", StringComparison.Ordinal))
            {
                var attribute = key[..^4];
                if (!attributes.TryGetValue(attribute, out var actual) || actual > value)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static double CalculateComplexity(
        List<RuntimeComponent> components,
        int activeFamilyCount,
        int activeRelationCount,
        double groupDelta)
    {
        var entropy = 0.0;
        if (components.Count > 1)
        {
            entropy = -components.Sum(component =>
                    component.PerceivedShare <= 0
                        ? 0
                        : component.PerceivedShare * Math.Log(component.PerceivedShare))
                / Math.Log(components.Count);
        }

        var familyDiversity = Math.Min(1, activeFamilyCount / 5.0);
        var allPairs = Math.Max(1, components.Count * (components.Count - 1) / 2.0);
        var interactionDensity = Math.Min(1, activeRelationCount / allPairs);
        return Clamp(0.55 * entropy + 0.30 * familyDiversity + 0.15 * interactionDensity + groupDelta);
    }

    private static double CalculateClarity(
        List<RuntimeComponent> components,
        Dictionary<string, double> familyContributions,
        Dictionary<string, StageAnalysis> stages,
        double continuity,
        double conflictPenalty,
        double groupDelta)
    {
        var orderedShares = components
            .Select(static component => component.PerceivedShare)
            .OrderDescending()
            .ToArray();
        var primary = orderedShares[0];
        var secondary = orderedShares.Length > 1 ? orderedShares[1] : 0;
        var dominance = Clamp((primary - secondary) / 0.20);
        var familyCoherence = familyContributions.Values.Max();
        var stageBalance = Clamp(3 * StageKeys.Min(stage => stages[stage].Share));
        var maskingLoss = components.Sum(component => Math.Max(0, component.PreMaskShare - component.PerceivedShare));
        var lowMasking = Clamp(1 - 3 * maskingLoss);
        var themePenalty = Math.Max(0, familyContributions.Count - 5) * 0.04;

        return Clamp(
            0.30 * dominance
            + 0.20 * familyCoherence
            + 0.20 * continuity
            + 0.15 * stageBalance
            + 0.15 * lowMasking
            - conflictPenalty
            - themePenalty
            + groupDelta);
    }

    private static double CalculateBalance(
        Dictionary<string, StageAnalysis> stages,
        double conflictPenalty,
        double overloadPenalty)
    {
        var stageBalance = Clamp(3 * StageKeys.Min(stage => stages[stage].Share));
        return Clamp(
            0.50 * stageBalance
            + 0.25 * (1 - Clamp(conflictPenalty * 2))
            + 0.25 * (1 - Clamp(overloadPenalty * 2)));
    }

    private List<string> DetectProblems(
        List<RuntimeComponent> components,
        Dictionary<string, double> attributes,
        Dictionary<string, StageAnalysis> stages,
        ScentDerivedMetrics metrics,
        List<ActiveRelation> activeRelations,
        List<ActiveGroupRule> activeGroups,
        double conflictPenalty)
    {
        var problems = activeGroups.Select(static group => group.ProblemCode).ToList();
        var sweetHeavyShare = components
            .Where(static component => component.OverloadGroups.Contains("sweet_heavy", StringComparer.Ordinal))
            .Sum(static component => component.PerceivedShare);

        if (!problems.Contains("too_sweet_heavy", StringComparer.Ordinal)
            && attributes["sweetness"] >= 0.78
            && sweetHeavyShare >= 0.35)
        {
            problems.Add("too_sweet");
        }

        if (!problems.Contains("dark_heavy_overload", StringComparer.Ordinal)
            && attributes["weight"] >= 0.78
            && attributes["transparency"] <= 0.28)
        {
            problems.Add("too_heavy");
        }

        if (stages["heart"].Share < _insufficientStageShare || stages["heart"].PrimaryShare < 0.25)
        {
            problems.Add("middle_missing");
        }

        if (stages["base"].Share < _insufficientStageShare || attributes["longevity"] < 0.35)
        {
            problems.Add("tail_insufficient");
        }

        if (activeRelations.Any(relation =>
                string.Equals(relation.RelationType, "masking", StringComparison.Ordinal)
                && relation.Effect >= _maskedSubjectThreshold))
        {
            problems.Add("masked_subject");
        }

        var familyCount = components.Select(static component => component.Family).Distinct(StringComparer.Ordinal).Count();
        if (familyCount >= 5 || (familyCount >= 4 && metrics.Clarity < 0.45 && metrics.Complexity > 0.65))
        {
            problems.Add("too_many_themes");
        }

        if (activeRelations.Any(static relation => string.Equals(relation.RelationId, "REL-027", StringComparison.Ordinal))
            || (attributes["freshness"] >= 0.65
                && attributes["warmth"] >= 0.65
                && conflictPenalty > 0))
        {
            problems.Add("cold_warm_tension");
        }

        if (activeRelations.Any(static relation =>
                string.Equals(relation.RelationType, "conflict", StringComparison.Ordinal)))
        {
            problems.Add("pair_conflict");
        }

        return problems.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<ScentSuggestion> GenerateSuggestions(
        List<RuntimeComponent> components,
        IReadOnlyList<string> problems)
    {
        var suggestions = new List<ScentSuggestion>();
        var primary = components
            .OrderByDescending(static component => component.PerceivedShare)
            .First();

        if (problems.Contains("too_sweet", StringComparer.Ordinal)
            || problems.Contains("too_sweet_heavy", StringComparer.Ordinal))
        {
            suggestions.Add(new ScentSuggestion("too_sweet", "decrease_component", primary.ComponentId));
            suggestions.Add(new ScentSuggestion("too_sweet", "add_or_increase_bitter_fresh_bridge", null));
            suggestions.Add(new ScentSuggestion("too_sweet", "add_or_increase_dry_wood_support", null));
        }

        if (problems.Contains("middle_missing", StringComparer.Ordinal))
        {
            suggestions.Add(new ScentSuggestion("middle_missing", "add_or_increase_heart_bridge", null));
        }

        if (problems.Contains("tail_insufficient", StringComparer.Ordinal))
        {
            suggestions.Add(new ScentSuggestion("tail_insufficient", "add_or_increase_base_support", null));
        }

        if (problems.Contains("masked_subject", StringComparer.Ordinal))
        {
            suggestions.Add(new ScentSuggestion("masked_subject", "decrease_masking_component", null));
        }

        return suggestions;
    }

    private ScentConfidence CalculateConfidence(List<RuntimeComponent> components)
    {
        var dataCompleteness = components.Sum(component => component.PerceivedShare * component.DataQuality);
        var mappingQuality = components.Sum(component => component.PerceivedShare * component.MappingQuality);
        var allPairs = Math.Max(1, components.Count * (components.Count - 1) / 2);
        var coveredPairs = 0;
        for (var leftIndex = 0; leftIndex < components.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < components.Count; rightIndex++)
            {
                if (HasDefinedRelation(components[leftIndex], components[rightIndex]))
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
        var score = Clamp(
            0.30 * dataCompleteness
            + 0.25 * ruleCoverage
            + 0.20 * mappingQuality
            + 0.15 * inDomain
            + 0.10 * 0.75);
        var reasons = components.Any(static component =>
            component.IsExternal && component.ExternalMissingFields.Count > 0)
            ? new[] { "external_material_incomplete" }
            : [];
        var level = score >= 0.80 ? "high" : score >= 0.55 ? "medium" : "low";
        return new ScentConfidence(Round(score), level, reasons);
    }

    private bool HasDefinedRelation(RuntimeComponent left, RuntimeComponent right)
    {
        return _specifications.Relations.PairRules.Any(rule =>
            (string.Equals(rule.Source, left.RelationIdentity, StringComparison.Ordinal)
             && string.Equals(rule.Target, right.RelationIdentity, StringComparison.Ordinal))
            || (string.Equals(rule.Source, right.RelationIdentity, StringComparison.Ordinal)
                && string.Equals(rule.Target, left.RelationIdentity, StringComparison.Ordinal)));
    }

    private static RuntimeComponent? FindComponent(List<RuntimeComponent> components, string relationIdentity)
    {
        return components.FirstOrDefault(component =>
            string.Equals(component.RelationIdentity, relationIdentity, StringComparison.Ordinal));
    }

    private string CalculateFormulaHash(List<RuntimeComponent> components)
    {
        var canonical = new StringBuilder();
        canonical.Append(_specifications.Engine.EngineVersion).Append('|')
            .Append(_specifications.Blocks.SchemaVersion).Append('|')
            .Append(_specifications.Relations.SchemaVersion);

        foreach (var component in components.OrderBy(static component => component.ComponentId, StringComparer.Ordinal))
        {
            canonical.Append('|').Append(component.ComponentId)
                .Append(':').Append(component.Quantity.ToString("R", CultureInfo.InvariantCulture))
                .Append(':').Append(component.RelationIdentity)
                .Append(':').Append(component.IsExternal ? '1' : '0');
            if (component.IsExternal)
            {
                foreach (var key in AttributeKeys)
                {
                    canonical.Append(':').Append(component.AttributeVector[key].ToString("R", CultureInfo.InvariantCulture));
                }

                foreach (var stage in StageKeys)
                {
                    canonical.Append(':').Append(component.StageWeights[stage].ToString("R", CultureInfo.InvariantCulture));
                }

                canonical.Append(':').Append(component.Intensity.ToString("R", CultureInfo.InvariantCulture))
                    .Append(':').Append(component.DataQuality.ToString("R", CultureInfo.InvariantCulture))
                    .Append(':').Append(component.MappingQuality.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        return Hash(canonical.ToString());
    }

    private static string CalculateNumericOutputHash(
        Dictionary<string, double> attributes,
        Dictionary<string, StageAnalysis> stages,
        IReadOnlyList<ComponentContribution> primary,
        ScentDerivedMetrics metrics,
        List<ActiveRelation> relations,
        IReadOnlyList<string> problems,
        ScentConfidence confidence)
    {
        var canonical = new StringBuilder();
        foreach (var key in AttributeKeys)
        {
            canonical.Append(key).Append('=').Append(attributes[key].ToString("F4", CultureInfo.InvariantCulture)).Append('|');
        }

        foreach (var stage in StageKeys)
        {
            canonical.Append(stage).Append('=').Append(stages[stage].Share.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
        }

        foreach (var component in primary)
        {
            canonical.Append(component.ComponentId).Append('=').Append(component.Share.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
        }

        canonical.Append(metrics.Clarity.ToString("F4", CultureInfo.InvariantCulture)).Append('|')
            .Append(metrics.Complexity.ToString("F4", CultureInfo.InvariantCulture)).Append('|')
            .Append(metrics.Continuity.ToString("F4", CultureInfo.InvariantCulture)).Append('|')
            .Append(metrics.Balance.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
        foreach (var relation in relations)
        {
            canonical.Append(relation.RelationId).Append('|');
        }

        foreach (var problem in problems)
        {
            canonical.Append(problem).Append('|');
        }

        canonical.Append(confidence.Score.ToString("F4", CultureInfo.InvariantCulture));
        return Hash(canonical.ToString());
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

    private double Round(double value)
    {
        return Math.Round(value, _roundingDigits, MidpointRounding.AwayFromZero);
    }

    private static double Clamp(double value)
    {
        return Math.Clamp(value, 0, 1);
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static double ReadDoubleSection(
        ScentEngineSpecification engine,
        string section,
        string property)
    {
        return GetSection(engine, section).GetProperty(property).GetDouble();
    }

    private static int ReadIntSection(
        ScentEngineSpecification engine,
        string section,
        string property)
    {
        return GetSection(engine, section).GetProperty(property).GetInt32();
    }

    private static double ReadNestedDoubleSection(
        ScentEngineSpecification engine,
        string section,
        string nestedSection,
        string property)
    {
        return GetSection(engine, section)
            .GetProperty(nestedSection)
            .GetProperty(property)
            .GetDouble();
    }

    private static JsonElement GetSection(ScentEngineSpecification engine, string section)
    {
        if (!engine.AdditionalSections.TryGetValue(section, out var element))
        {
            throw new InvalidDataException($"Engine specification section is missing: {section}.");
        }

        return element;
    }

    private static void ValidateExactKeys(
        IEnumerable<string> actualKeys,
        IEnumerable<string> expectedKeys,
        string label)
    {
        var actual = actualKeys.ToHashSet(StringComparer.Ordinal);
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
        {
            throw new ArgumentException($"{label} does not contain the required keys.");
        }
    }

    private static void ValidateUnitRange(IEnumerable<double> values, string label)
    {
        if (values.Any(static value => !double.IsFinite(value) || value is < 0 or > 1))
        {
            throw new ArgumentException($"{label} values must be finite and between zero and one.");
        }
    }

    private sealed class RuntimeComponent
    {
        public required string ComponentId { get; init; }
        public required string RelationIdentity { get; init; }
        public required string Family { get; init; }
        public double Quantity { get; init; }
        public required IReadOnlyDictionary<string, double> AttributeVector { get; init; }
        public double Intensity { get; init; }
        public required IReadOnlyDictionary<string, double> StageWeights { get; init; }
        public double DataQuality { get; init; }
        public double MappingQuality { get; init; }
        public required IReadOnlyList<string> OverloadGroups { get; init; }
        public bool IsExternal { get; init; }
        public IReadOnlyList<string> ExternalMissingFields { get; init; } = [];
        public double NormalizedShare { get; set; }
        public double EffectiveStrength { get; set; }
        public double PreMaskShare { get; set; }
        public double PerceivedShare { get; set; }

        public static RuntimeComponent FromBlock(ScentBlockDefinition block, double quantity)
        {
            return new RuntimeComponent
            {
                ComponentId = block.Id,
                RelationIdentity = block.Id,
                Family = block.Family,
                Quantity = quantity,
                AttributeVector = block.AttributeVector,
                Intensity = block.Intensity,
                StageWeights = block.StageWeights,
                DataQuality = block.DataQuality,
                MappingQuality = 0.85,
                OverloadGroups = block.OverloadGroups,
                IsExternal = false,
            };
        }
    }

    private sealed record ActiveGroupRule(
        string RuleId,
        string ProblemCode,
        double ClarityDelta,
        double ComplexityDelta);
}
