using System.Text.Json;
using MyScent.ScentEngine.Specifications;

namespace MyScent.ScentEngine.Analysis;

internal sealed record ScentEngineSettings(
    double Gamma,
    double MinimumPositiveQuantity,
    int RoundingDigits,
    double InsufficientStageShare,
    double MaskedSubjectThreshold)
{
    public static ScentEngineSettings From(ScentEngineSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        return new ScentEngineSettings(
            ReadDouble(specification, "contribution", "gamma"),
            ReadDouble(specification, "contribution", "minimum_positive_quantity"),
            ReadInt(specification, "numeric_policy", "api_rounding_digits"),
            ReadDouble(specification, "stages", "insufficient_stage_share"),
            ReadNestedDouble(
                specification,
                "pair_relation_policy",
                "masking",
                "masked_subject_problem_threshold"));
    }

    private static double ReadDouble(
        ScentEngineSpecification specification,
        string section,
        string property)
    {
        return GetSection(specification, section).GetProperty(property).GetDouble();
    }

    private static int ReadInt(
        ScentEngineSpecification specification,
        string section,
        string property)
    {
        return GetSection(specification, section).GetProperty(property).GetInt32();
    }

    private static double ReadNestedDouble(
        ScentEngineSpecification specification,
        string section,
        string nestedSection,
        string property)
    {
        return GetSection(specification, section)
            .GetProperty(nestedSection)
            .GetProperty(property)
            .GetDouble();
    }

    private static JsonElement GetSection(ScentEngineSpecification specification, string section)
    {
        if (!specification.AdditionalSections.TryGetValue(section, out var value))
        {
            throw new InvalidDataException($"Engine specification section is missing: {section}.");
        }

        return value;
    }
}
