using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MyScent.ScentEngine.Analysis;

internal static class ScentAnalysisMath
{
    public static readonly string[] AttributeKeys =
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

    public static readonly string[] StageKeys = ["top", "heart", "base"];

    public static double Clamp(double value) => Math.Clamp(value, 0, 1);

    public static double Round(double value, int digits) =>
        Math.Round(value, digits, MidpointRounding.AwayFromZero);

    public static Dictionary<string, double> CalculateBaseAttributes(List<ScentRuntimeComponent> components)
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

    public static Dictionary<string, StageAnalysis> CalculateStages(List<ScentRuntimeComponent> components)
    {
        var result = new Dictionary<string, StageAnalysis>(StringComparer.Ordinal);
        foreach (var stage in StageKeys)
        {
            var raw = components.ToDictionary(
                static component => component.ComponentId,
                component => component.PerceivedShare * component.StageWeights[stage],
                StringComparer.Ordinal);
            var total = raw.Values.Sum();
            var stageComponents = raw
                .Select(pair => new ComponentContribution(
                    pair.Key,
                    total <= 0 ? 0 : pair.Value / total))
                .OrderByDescending(static component => component.Share)
                .ThenBy(static component => component.ComponentId, StringComparer.Ordinal)
                .ToArray();
            var primary = stageComponents.FirstOrDefault();
            result[stage] = new StageAnalysis
            {
                Share = total,
                Components = stageComponents,
                PrimaryComponentId = primary?.ComponentId,
                PrimaryShare = primary?.Share ?? 0,
            };
        }

        var allStageShare = result.Values.Sum(static stage => stage.Share);
        foreach (var stage in StageKeys)
        {
            result[stage] = result[stage] with
            {
                Share = allStageShare <= 0 ? 0 : result[stage].Share / allStageShare,
            };
        }

        return result;
    }

    public static Dictionary<string, double> CalculateFamilyContributions(
        List<ScentRuntimeComponent> components)
    {
        return components
            .GroupBy(static component => component.Family, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Sum(static component => component.PerceivedShare),
                StringComparer.Ordinal);
    }

    public static double CalculateContinuity(
        List<ScentRuntimeComponent> components,
        Dictionary<string, StageAnalysis> stages,
        List<double> bridgeEffects,
        double conflictPenalty)
    {
        var stageFamilies = CalculateStageFamilyShares(components);
        var bridgeBonus = Clamp(bridgeEffects.Sum() * 4);
        var topHeart = Clamp(
            0.75 * CalculateFamilyOverlap(stageFamilies["top"], stageFamilies["heart"])
            + 0.25 * bridgeBonus);
        var heartBase = Clamp(
            0.75 * CalculateFamilyOverlap(stageFamilies["heart"], stageFamilies["base"])
            + 0.25 * bridgeBonus);
        var middleSupport = Clamp(stages["heart"].Share / 0.30);
        return Clamp(0.40 * topHeart + 0.40 * heartBase + 0.20 * middleSupport - conflictPenalty);
    }

    public static double CalculateComplexity(
        List<ScentRuntimeComponent> components,
        int familyCount,
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

        var familyDiversity = Math.Min(1, familyCount / 5.0);
        var pairCount = Math.Max(1, components.Count * (components.Count - 1) / 2.0);
        var interactionDensity = Math.Min(1, activeRelationCount / pairCount);
        return Clamp(0.55 * entropy + 0.30 * familyDiversity + 0.15 * interactionDensity + groupDelta);
    }

    public static double CalculateClarity(
        List<ScentRuntimeComponent> components,
        Dictionary<string, double> familyContributions,
        Dictionary<string, StageAnalysis> stages,
        double continuity,
        double conflictPenalty,
        double groupDelta)
    {
        var shares = components
            .Select(static component => component.PerceivedShare)
            .OrderDescending()
            .ToArray();
        var dominance = Clamp((shares[0] - (shares.Length > 1 ? shares[1] : 0)) / 0.20);
        var familyCoherence = familyContributions.Values.Max();
        var stageBalance = Clamp(3 * StageKeys.Min(stage => stages[stage].Share));
        var maskingLoss = components.Sum(component =>
            Math.Max(0, component.PreMaskShare - component.PerceivedShare));
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

    public static double CalculateBalance(
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

    public static bool ConditionsMatch(
        IReadOnlyDictionary<string, double> conditions,
        Dictionary<string, double> attributes)
    {
        foreach (var (key, expected) in conditions)
        {
            if (key.EndsWith("_min", StringComparison.Ordinal))
            {
                var attribute = key[..^4];
                if (!attributes.TryGetValue(attribute, out var actual) || actual < expected)
                {
                    return false;
                }
            }
            else if (key.EndsWith("_max", StringComparison.Ordinal))
            {
                var attribute = key[..^4];
                if (!attributes.TryGetValue(attribute, out var actual) || actual > expected)
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public static string Format(double value) => value.ToString("F4", CultureInfo.InvariantCulture);

    private static Dictionary<string, Dictionary<string, double>> CalculateStageFamilyShares(
        List<ScentRuntimeComponent> components)
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
}
