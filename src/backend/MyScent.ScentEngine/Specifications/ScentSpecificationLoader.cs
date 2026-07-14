using System.Security.Cryptography;
using System.Text.Json;

namespace MyScent.ScentEngine.Specifications;

public static class ScentSpecificationLoader
{
    private const string BlocksFile = "scent-blocks.v0.1.json";
    private const string RelationsFile = "scent-relations.v0.1.json";
    private const string EngineFile = "scent-engine.v0.1.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public static ScentSpecificationBundle LoadDefault()
    {
        var specificationDirectory = Path.Combine(AppContext.BaseDirectory, "specs", "scent");
        return Load(specificationDirectory);
    }

    public static ScentSpecificationBundle Load(string specificationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specificationDirectory);

        var blocks = LoadDocument<ScentBlockCatalog>(specificationDirectory, BlocksFile);
        var relations = LoadDocument<ScentRelationCatalog>(specificationDirectory, RelationsFile);
        var engine = LoadDocument<ScentEngineSpecification>(specificationDirectory, EngineFile);

        var bundle = new ScentSpecificationBundle(
            blocks.Value,
            relations.Value,
            engine.Value,
            blocks.Sha256,
            relations.Sha256,
            engine.Sha256);

        var errors = ScentSpecificationValidator.Validate(bundle);
        if (errors.Count > 0)
        {
            throw new ScentSpecificationException(errors);
        }

        return bundle;
    }

    private static LoadedDocument<T> LoadDocument<T>(string directory, string fileName)
        where T : class
    {
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required scent specification was not found: {path}", path);
        }

        var bytes = File.ReadAllBytes(path);
        var value = JsonSerializer.Deserialize<T>(bytes, JsonOptions)
            ?? throw new InvalidDataException($"Scent specification deserialized to null: {path}");

        return new LoadedDocument<T>(value, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private sealed record LoadedDocument<T>(T Value, string Sha256);
}

public sealed class ScentSpecificationException : Exception
{
    public ScentSpecificationException(IReadOnlyList<string> errors)
        : base(CreateMessage(errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }

    private static string CreateMessage(IReadOnlyList<string> errors)
    {
        return "Scent specification validation failed:" + Environment.NewLine
            + string.Join(Environment.NewLine, errors.Select(static error => $" - {error}"));
    }
}
