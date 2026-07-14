using MyScent.ScentEngine.Specifications;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(static _ => ScentSpecificationLoader.LoadDefault());

var app = builder.Build();

app.UseExceptionHandler();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "myscent-api",
}));

app.MapGet("/api/v1/spec-manifest", (ScentSpecificationBundle specifications) =>
    Results.Ok(new
    {
        engine = new
        {
            version = specifications.Engine.EngineVersion,
            sha256 = specifications.EngineSha256,
            status = specifications.Engine.Status,
        },
        blocks = new
        {
            version = specifications.Blocks.SchemaVersion,
            sha256 = specifications.BlocksSha256,
            count = specifications.Blocks.Blocks.Count,
            status = specifications.Blocks.Status,
        },
        relations = new
        {
            version = specifications.Relations.SchemaVersion,
            sha256 = specifications.RelationsSha256,
            pairRuleCount = specifications.Relations.PairRules.Count,
            groupRuleCount = specifications.Relations.GroupRules.Count,
            status = specifications.Relations.Status,
        },
    }));

app.Run();

public partial class Program
{
}
