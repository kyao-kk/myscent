using System.Collections.Concurrent;
using MyScent.ScentEngine.Analysis;
using MyScent.ScentEngine.Specifications;

namespace MyScent.Formulas;

public sealed class FormulaApplicationService
{
    private readonly ConcurrentDictionary<string, FormulaEntry> _formulas = new(StringComparer.Ordinal);
    private readonly ScentSpecificationBundle _specifications;
    private readonly ScentAnalysisEngine _analysisEngine;
    private readonly string[] _validBlockIds;
    private readonly FormulaEngineVersions _versions;

    public FormulaApplicationService(ScentSpecificationBundle specifications)
    {
        ArgumentNullException.ThrowIfNull(specifications);
        _specifications = specifications;
        _analysisEngine = new ScentAnalysisEngine(specifications);
        _validBlockIds = specifications.Blocks.Blocks.Select(static block => block.Id).ToArray();
        _versions = new FormulaEngineVersions(
            specifications.Engine.EngineVersion,
            specifications.Blocks.SchemaVersion,
            specifications.Relations.SchemaVersion);
    }

    public FormulaView CreateFormula(
        string actorId,
        string sceneId,
        DateTimeOffset serverTime,
        string? requestedFormulaId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);

        var formulaId = string.IsNullOrWhiteSpace(requestedFormulaId)
            ? Guid.NewGuid().ToString("N")
            : requestedFormulaId.Trim();
        var aggregate = new FormulaAggregate(
            formulaId,
            _validBlockIds,
            _versions,
            actorId,
            serverTime);
        var entry = new FormulaEntry(aggregate, sceneId);
        if (!_formulas.TryAdd(formulaId, entry))
        {
            throw new FormulaCommandException("INVALID_STATE", $"Formula {formulaId} already exists.");
        }

        return Snapshot(entry);
    }

    public FormulaView GetFormula(string formulaId)
    {
        var entry = GetEntry(formulaId);
        lock (entry.SyncRoot)
        {
            return Snapshot(entry);
        }
    }

    public FormulaCommandResult ExecuteCommand(
        FormulaCommandEnvelope command,
        DateTimeOffset serverTime)
    {
        ArgumentNullException.ThrowIfNull(command);
        var entry = GetEntry(command.FormulaId);
        lock (entry.SyncRoot)
        {
            var result = entry.Aggregate.Execute(command, serverTime);
            entry.SceneId = command.SceneId;
            return result;
        }
    }

    public FormulaAnalysisView Analyze(
        string formulaId,
        string sceneId,
        DateTimeOffset serverTime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);
        var entry = GetEntry(formulaId);
        FormulaComponentInput[] components;
        long requestedRevision;
        string requestedFormulaHash;
        lock (entry.SyncRoot)
        {
            if (entry.Aggregate.Components.Count == 0)
            {
                throw new FormulaCommandException(
                    "ENGINE_INPUT_INVALID",
                    "At least one active component is required before analysis.");
            }

            components = entry.Aggregate.Components
                .OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => new FormulaComponentInput
                {
                    BlockId = item.Key,
                    Quantity = item.Value,
                })
                .ToArray();
            requestedRevision = entry.Aggregate.Revision;
            requestedFormulaHash = entry.Aggregate.FormulaHash;
        }

        var analysis = _analysisEngine.Analyze(new ScentAnalysisRequest
        {
            Components = components,
            SceneId = sceneId,
        });
        long currentRevision;
        lock (entry.SyncRoot)
        {
            currentRevision = entry.Aggregate.Revision;
            entry.SceneId = sceneId;
        }

        if (!string.Equals(requestedFormulaHash, analysis.FormulaHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Formula and scent-engine canonical hashes diverged.");
        }

        return new FormulaAnalysisView(
            Guid.NewGuid().ToString("N"),
            formulaId,
            requestedRevision,
            currentRevision,
            requestedRevision == currentRevision ? "fresh" : "stale",
            sceneId,
            serverTime,
            analysis);
    }

    public IReadOnlyList<FormulaEvent> GetEvents(string formulaId)
    {
        var entry = GetEntry(formulaId);
        lock (entry.SyncRoot)
        {
            return entry.Aggregate.Events.ToArray();
        }
    }

    private FormulaEntry GetEntry(string formulaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formulaId);
        if (!_formulas.TryGetValue(formulaId, out var entry))
        {
            throw new FormulaCommandException("FORMULA_NOT_FOUND", $"Formula {formulaId} was not found.");
        }

        return entry;
    }

    private static FormulaView Snapshot(FormulaEntry entry)
    {
        var aggregate = entry.Aggregate;
        return new FormulaView(
            aggregate.FormulaId,
            aggregate.Revision,
            aggregate.FormulaHash,
            aggregate.IsSealed ? "sealed" : "editing",
            aggregate.Title,
            entry.SceneId,
            aggregate.Components
                .OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => new FormulaComponent(item.Key, item.Value))
                .ToArray(),
            aggregate.UndoAvailable);
    }

    private sealed class FormulaEntry
    {
        public FormulaEntry(FormulaAggregate aggregate, string sceneId)
        {
            Aggregate = aggregate;
            SceneId = sceneId;
        }

        public object SyncRoot { get; } = new();
        public FormulaAggregate Aggregate { get; }
        public string SceneId { get; set; }
    }
}

public sealed record FormulaView(
    string FormulaId,
    long Revision,
    string FormulaHash,
    string Status,
    string? Title,
    string SceneId,
    IReadOnlyList<FormulaComponent> Components,
    bool UndoAvailable);

public sealed record FormulaAnalysisView(
    string AnalysisId,
    string FormulaId,
    long FormulaRevision,
    long CurrentFormulaRevision,
    string State,
    string SceneId,
    DateTimeOffset CreatedAt,
    ScentAnalysisResult Analysis);
