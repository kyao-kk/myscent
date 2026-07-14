using MyScent.ScentEngine.Specifications;
using MyScent.SpecSmokeTests;

try
{
    var specifications = ScentSpecificationLoader.LoadDefault();

    Require(specifications.Blocks.Blocks.Count == 24, "Expected 24 scent blocks.");
    Require(specifications.Relations.PairRules.Count == 27, "Expected 27 pair rules.");
    Require(specifications.Relations.GroupRules.Count == 5, "Expected 5 group rules.");
    Require(
        specifications.Engine.Canonicalization.ExcludeFromHash.Contains("scene_id", StringComparer.Ordinal),
        "scene_id must be excluded from the formula hash.");

    Console.WriteLine("Runtime specification smoke test passed.");
    Console.WriteLine($" - engine: {specifications.Engine.EngineVersion}");
    Console.WriteLine($" - blocks: {specifications.Blocks.Blocks.Count}");
    Console.WriteLine($" - pair rules: {specifications.Relations.PairRules.Count}");
    Console.WriteLine($" - group rules: {specifications.Relations.GroupRules.Count}");

    Console.WriteLine("Running deterministic scent regression fixtures:");
    var scentFailures = new ScentFixtureRegressionRunner(specifications).RunAll();
    if (scentFailures.Count > 0)
    {
        PrintFailures("Scent regression fixtures failed:", scentFailures);
        return 1;
    }

    Console.WriteLine("All scent regression fixtures passed.");
    Console.WriteLine("Running formula aggregate regression tests:");
    var formulaFailures = new FormulaAggregateRegressionRunner(specifications).RunAll();
    if (formulaFailures.Count > 0)
    {
        PrintFailures("Formula aggregate regression tests failed:", formulaFailures);
        return 1;
    }

    Console.WriteLine("All formula aggregate regression tests passed.");
    Console.WriteLine("Running formula application service regression tests:");
    var serviceFailures = new FormulaApplicationServiceRegressionRunner(specifications).RunAll();
    if (serviceFailures.Count > 0)
    {
        PrintFailures("Formula application service regression tests failed:", serviceFailures);
        return 1;
    }

    Console.WriteLine("All formula application service regression tests passed.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Runtime specification, scent, formula, or service regression test failed.");
    Console.Error.WriteLine(exception);
    return 1;
}

static void PrintFailures(string heading, IReadOnlyList<string> failures)
{
    Console.Error.WriteLine(heading);
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($" - {failure}");
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
