using MyScent.ScentEngine.Specifications;

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
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Runtime specification smoke test failed.");
    Console.Error.WriteLine(exception);
    return 1;
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
