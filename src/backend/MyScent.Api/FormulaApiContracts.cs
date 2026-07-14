using System.Text.Json;
using MyScent.Formulas;

namespace MyScent.Api;

public sealed record CreateFormulaRequest
{
    public required string ActorId { get; init; }
    public required string SceneId { get; init; }
    public string? RequestedFormulaId { get; init; }
}

public sealed record FormulaCommandRequest
{
    public required Guid CommandId { get; init; }
    public required Guid IdempotencyKey { get; init; }
    public required long ExpectedRevision { get; init; }
    public required string ActorId { get; init; }
    public required string SessionId { get; init; }
    public required string SceneId { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required string CommandType { get; init; }
    public required JsonElement Payload { get; init; }
}

public sealed record AnalyzeFormulaRequest
{
    public required string SceneId { get; init; }
}

public static class FormulaCommandMapper
{
    public static FormulaCommandEnvelope Map(string formulaId, FormulaCommandRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formulaId);
        ArgumentNullException.ThrowIfNull(request);

        return new FormulaCommandEnvelope
        {
            CommandId = request.CommandId,
            IdempotencyKey = request.IdempotencyKey,
            FormulaId = formulaId,
            ExpectedRevision = request.ExpectedRevision,
            ActorId = request.ActorId,
            SessionId = request.SessionId,
            SceneId = request.SceneId,
            IssuedAt = request.IssuedAt,
            Payload = MapPayload(request.CommandType, request.Payload),
        };
    }

    private static FormulaCommandPayload MapPayload(string commandType, JsonElement payload)
    {
        return commandType switch
        {
            "component.add" => new AddComponentCommand(
                ReadString(payload, "blockId"),
                ReadDouble(payload, "quantity")),
            "component.increase" => new IncreaseComponentCommand(
                ReadString(payload, "blockId"),
                ReadDouble(payload, "deltaQuantity")),
            "component.decrease" => new DecreaseComponentCommand(
                ReadString(payload, "blockId"),
                ReadDouble(payload, "deltaQuantity")),
            "component.set_quantity" => new SetComponentQuantityCommand(
                ReadString(payload, "blockId"),
                ReadDouble(payload, "quantity")),
            "component.remove" => new RemoveComponentCommand(
                ReadString(payload, "blockId")),
            "component.replace" => new ReplaceComponentCommand(
                ReadString(payload, "fromBlockId"),
                ReadString(payload, "toBlockId"),
                ReadBoolean(payload, "preserveQuantity"),
                ReadNullableDouble(payload, "newQuantity")),
            "operation.undo" => new UndoOperationCommand(),
            "formula.seal" => new SealFormulaCommand(
                ReadString(payload, "title"),
                ReadBoolean(payload, "prerequisitesSatisfied")),
            _ => throw new FormulaCommandException(
                "INVALID_STATE",
                $"Unsupported command type {commandType}."),
        };
    }

    private static string ReadString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new FormulaCommandException(
                "INVALID_STATE",
                $"Payload property {propertyName} must be a non-empty string.");
        }

        return property.GetString()!;
    }

    private static double ReadDouble(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetDouble(out var value))
        {
            throw new FormulaCommandException(
                "INVALID_QUANTITY",
                $"Payload property {propertyName} must be numeric.");
        }

        return value;
    }

    private static double? ReadNullableDouble(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var value))
        {
            throw new FormulaCommandException(
                "INVALID_QUANTITY",
                $"Payload property {propertyName} must be numeric or null.");
        }

        return value;
    }

    private static bool ReadBoolean(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new FormulaCommandException(
                "INVALID_STATE",
                $"Payload property {propertyName} must be boolean.");
        }

        return property.GetBoolean();
    }
}
