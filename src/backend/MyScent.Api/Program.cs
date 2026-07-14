using MyScent.Api;
using MyScent.Formulas;
using MyScent.ScentEngine.Specifications;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(static _ => ScentSpecificationLoader.LoadDefault());
builder.Services.AddSingleton<FormulaApplicationService>();

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

app.MapPost("/api/v1/formulas", (
    CreateFormulaRequest request,
    FormulaApplicationService formulas) =>
    FormulaApiResult.Execute(() =>
    {
        var formula = formulas.CreateFormula(
            request.ActorId,
            request.SceneId,
            DateTimeOffset.UtcNow,
            request.RequestedFormulaId);
        return Results.Created($"/api/v1/formulas/{formula.FormulaId}", formula);
    }));

app.MapGet("/api/v1/formulas/{formulaId}", (
    string formulaId,
    FormulaApplicationService formulas) =>
    FormulaApiResult.Execute(() => Results.Ok(formulas.GetFormula(formulaId))));

app.MapPost("/api/v1/formulas/{formulaId}/commands", (
    string formulaId,
    FormulaCommandRequest request,
    FormulaApplicationService formulas) =>
    FormulaApiResult.Execute(() =>
    {
        var command = FormulaCommandMapper.Map(formulaId, request);
        return Results.Ok(formulas.ExecuteCommand(command, DateTimeOffset.UtcNow));
    }));

app.MapPost("/api/v1/formulas/{formulaId}/analyses", (
    string formulaId,
    AnalyzeFormulaRequest request,
    FormulaApplicationService formulas) =>
    FormulaApiResult.Execute(() =>
        Results.Ok(formulas.Analyze(formulaId, request.SceneId, DateTimeOffset.UtcNow))));

app.MapGet("/api/v1/formulas/{formulaId}/events", (
    string formulaId,
    FormulaApplicationService formulas) =>
    FormulaApiResult.Execute(() => Results.Ok(formulas.GetEvents(formulaId))));

app.Run();

public partial class Program
{
}
