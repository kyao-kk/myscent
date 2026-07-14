using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MyScent.Formulas;

public sealed class FormulaAggregate
{
    private const double MinimumPositiveQuantity = 0.000001;
    private const double MaximumComponentQuantity = 1000000.0;
    private const int StoragePrecision = 6;

    private readonly HashSet<string> _validBlockIds;
    private readonly Dictionary<string, double> _components = new(StringComparer.Ordinal);
    private readonly List<FormulaEvent> _events = [];
    private readonly Stack<UndoEntry> _undoStack = [];
    private readonly Dictionary<Guid, IdempotencyEntry> _idempotency = [];
    private readonly FormulaEngineVersions _versions;

    public FormulaAggregate(
        string formulaId,
        IEnumerable<string> validBlockIds,
        FormulaEngineVersions versions,
        string creatorActorId,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formulaId);
        ArgumentNullException.ThrowIfNull(validBlockIds);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentException.ThrowIfNullOrWhiteSpace(creatorActorId);

        FormulaId = formulaId;
        _validBlockIds = validBlockIds.ToHashSet(StringComparer.Ordinal);
        _versions = versions;

        var stateHash = CalculateAggregateStateHash();
        _events.Add(new FormulaEvent
        {
            EventId = NewEventId(),
            FormulaId = formulaId,
            Revision = 0,
            EventType = "formula_created",
            CommandId = Guid.Empty,
            ActorId = creatorActorId,
            BeforeStateHash = stateHash,
            AfterStateHash = stateHash,
            Change = new FormulaStateChange(),
            EngineVersions = versions,
            ServerTime = createdAt,
        });
    }

    public string FormulaId { get; }
    public long Revision { get; private set; }
    public bool IsSealed { get; private set; }
    public string? Title { get; private set; }
    public IReadOnlyDictionary<string, double> Components => _components;
    public IReadOnlyList<FormulaEvent> Events => _events;
    public string FormulaHash => CalculateFormulaHash();
    public bool UndoAvailable => _undoStack.Count > 0 && !IsSealed;

    public FormulaCommandResult Execute(
        FormulaCommandEnvelope command,
        DateTimeOffset serverTime)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateEnvelope(command);

        var fingerprint = CalculateCommandFingerprint(command);
        if (_idempotency.TryGetValue(command.IdempotencyKey, out var prior))
        {
            if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw Error(
                    "DUPLICATE_COMMAND_MISMATCH",
                    "The idempotency key was already used for a different semantic command.");
            }

            return prior.Result;
        }

        if (command.ExpectedRevision != Revision)
        {
            throw Error(
                "REVISION_CONFLICT",
                $"Expected revision {command.ExpectedRevision}, current revision is {Revision}.");
        }

        if (IsSealed)
        {
            throw Error("FORMULA_SEALED", "A sealed formula cannot be edited.");
        }

        var beforeStateHash = CalculateAggregateStateHash();
        var sealedBefore = IsSealed;
        var titleBefore = Title;
        var previousRevision = Revision;
        var operation = PrepareOperation(command.Payload);

        ApplyChange(operation.Change);
        Revision++;
        var afterStateHash = CalculateAggregateStateHash();
        var eventId = NewEventId();
        var @event = new FormulaEvent
        {
            EventId = eventId,
            FormulaId = FormulaId,
            Revision = Revision,
            EventType = operation.EventType,
            CommandId = command.CommandId,
            ActorId = command.ActorId,
            BeforeStateHash = beforeStateHash,
            AfterStateHash = afterStateHash,
            Change = operation.Change,
            CompensatesEventId = operation.CompensatesEventId,
            EngineVersions = _versions,
            ServerTime = serverTime,
        };
        _events.Add(@event);

        if (operation.IsUndo)
        {
            _undoStack.Pop();
        }
        else if (operation.CreatesUndoEntry)
        {
            _undoStack.Push(new UndoEntry(
                eventId,
                operation.Change.Invert(sealedBefore, titleBefore)));
        }

        if (command.Payload is SealFormulaCommand)
        {
            _undoStack.Clear();
        }

        var result = new FormulaCommandResult
        {
            CommandId = command.CommandId,
            FormulaId = FormulaId,
            PreviousRevision = previousRevision,
            NewRevision = Revision,
            EventId = eventId,
            EventType = operation.EventType,
            FormulaHash = CalculateFormulaHash(),
            ComponentDelta = ToComponentDelta(operation.Change),
            UndoAvailable = UndoAvailable,
            ServerTime = serverTime,
        };
        _idempotency[command.IdempotencyKey] = new IdempotencyEntry(fingerprint, result);
        return result;
    }

    public static FormulaAggregate Replay(
        string formulaId,
        IEnumerable<string> validBlockIds,
        FormulaEngineVersions versions,
        IReadOnlyList<FormulaEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0
            || !string.Equals(events[0].EventType, "formula_created", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Formula replay requires a formula_created event.");
        }

        var aggregate = new FormulaAggregate(
            formulaId,
            validBlockIds,
            versions,
            events[0].ActorId,
            events[0].ServerTime);
        aggregate._events.Clear();

        foreach (var @event in events)
        {
            if (!string.Equals(@event.FormulaId, formulaId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Event formula id does not match the replay target.");
            }

            if (@event.Revision == 0)
            {
                aggregate.ValidateCreationEvent(@event);
                aggregate._events.Add(@event);
                continue;
            }

            if (@event.Revision != aggregate.Revision + 1)
            {
                throw new InvalidDataException("Formula event revisions are not contiguous.");
            }

            var beforeHash = aggregate.CalculateAggregateStateHash();
            if (!string.Equals(beforeHash, @event.BeforeStateHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Formula replay before-state hash mismatch.");
            }

            var sealedBefore = aggregate.IsSealed;
            var titleBefore = aggregate.Title;
            aggregate.ApplyChange(@event.Change);
            aggregate.Revision = @event.Revision;
            var afterHash = aggregate.CalculateAggregateStateHash();
            if (!string.Equals(afterHash, @event.AfterStateHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Formula replay after-state hash mismatch.");
            }

            aggregate.RebuildUndoState(@event, sealedBefore, titleBefore);
            aggregate._events.Add(@event);
        }

        return aggregate;
    }

    private void ValidateCreationEvent(FormulaEvent @event)
    {
        if (@event.Revision != 0)
        {
            throw new InvalidDataException("The formula_created event must have revision zero.");
        }

        var stateHash = CalculateAggregateStateHash();
        if (!string.Equals(@event.BeforeStateHash, stateHash, StringComparison.Ordinal)
            || !string.Equals(@event.AfterStateHash, stateHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Formula creation event state hash mismatch.");
        }
    }

    private void RebuildUndoState(
        FormulaEvent @event,
        bool sealedBefore,
        string? titleBefore)
    {
        if (string.Equals(@event.EventType, "operation_undone", StringComparison.Ordinal))
        {
            if (_undoStack.Count == 0
                || !string.Equals(
                    _undoStack.Peek().EventId,
                    @event.CompensatesEventId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Formula replay undo stack does not match compensation event.");
            }

            _undoStack.Pop();
            return;
        }

        if (IsUndoableEventType(@event.EventType))
        {
            _undoStack.Push(new UndoEntry(
                @event.EventId,
                @event.Change.Invert(sealedBefore, titleBefore)));
        }

        if (string.Equals(@event.EventType, "formula_sealed", StringComparison.Ordinal))
        {
            _undoStack.Clear();
        }
    }

    private PreparedOperation PrepareOperation(FormulaCommandPayload payload)
    {
        return payload switch
        {
            AddComponentCommand value => PrepareAdd(value),
            IncreaseComponentCommand value => PrepareIncrease(value),
            DecreaseComponentCommand value => PrepareDecrease(value),
            SetComponentQuantityCommand value => PrepareSet(value),
            RemoveComponentCommand value => PrepareRemove(value),
            ReplaceComponentCommand value => PrepareReplace(value),
            UndoOperationCommand => PrepareUndo(),
            SealFormulaCommand value => PrepareSeal(value),
            _ => throw Error("INVALID_STATE", $"Unsupported command type: {payload.CommandType}."),
        };
    }

    private PreparedOperation PrepareAdd(AddComponentCommand command)
    {
        ValidateBlock(command.BlockId);
        var quantity = ValidateQuantity(command.Quantity);
        if (_components.ContainsKey(command.BlockId))
        {
            throw Error("COMPONENT_ALREADY_EXISTS", $"Component {command.BlockId} is already active.");
        }

        return PreparedOperation.Undoable(
            "component_added",
            new FormulaStateChange { Added = [new FormulaComponent(command.BlockId, quantity)] });
    }

    private PreparedOperation PrepareIncrease(IncreaseComponentCommand command)
    {
        var current = GetComponent(command.BlockId);
        var delta = ValidateQuantity(command.DeltaQuantity);
        var next = ValidateQuantity(current + delta);
        return PreparedOperation.Undoable(
            "component_increased",
            new FormulaStateChange
            {
                Updated = [new FormulaQuantityChange(command.BlockId, current, next)],
            });
    }

    private PreparedOperation PrepareDecrease(DecreaseComponentCommand command)
    {
        var current = GetComponent(command.BlockId);
        var delta = ValidateQuantity(command.DeltaQuantity);
        var next = current - delta;
        if (next < MinimumPositiveQuantity)
        {
            throw Error("INVALID_QUANTITY", "Decrease would make the component inactive.");
        }

        next = RoundQuantity(next);
        return PreparedOperation.Undoable(
            "component_decreased",
            new FormulaStateChange
            {
                Updated = [new FormulaQuantityChange(command.BlockId, current, next)],
            });
    }

    private PreparedOperation PrepareSet(SetComponentQuantityCommand command)
    {
        var current = GetComponent(command.BlockId);
        var next = ValidateQuantity(command.Quantity);
        return PreparedOperation.Undoable(
            "component_quantity_set",
            new FormulaStateChange
            {
                Updated = [new FormulaQuantityChange(command.BlockId, current, next)],
            });
    }

    private PreparedOperation PrepareRemove(RemoveComponentCommand command)
    {
        var current = GetComponent(command.BlockId);
        if (_components.Count == 1)
        {
            throw Error(
                "LAST_COMPONENT_REMOVAL_BLOCKED",
                "Removing the final component is not allowed in protocol v0.1.");
        }

        return PreparedOperation.Undoable(
            "component_removed",
            new FormulaStateChange
            {
                Removed = [new FormulaComponent(command.BlockId, current)],
            });
    }

    private PreparedOperation PrepareReplace(ReplaceComponentCommand command)
    {
        var sourceQuantity = GetComponent(command.FromBlockId);
        ValidateBlock(command.ToBlockId);
        if (_components.ContainsKey(command.ToBlockId))
        {
            throw Error("COMPONENT_ALREADY_EXISTS", $"Component {command.ToBlockId} is already active.");
        }

        var targetQuantity = command.PreserveQuantity
            ? sourceQuantity
            : ValidateQuantity(command.NewQuantity
                ?? throw Error(
                    "INVALID_QUANTITY",
                    "A new quantity is required when preserve_quantity is false."));
        return PreparedOperation.Undoable(
            "component_replaced",
            new FormulaStateChange
            {
                Added = [new FormulaComponent(command.ToBlockId, targetQuantity)],
                Removed = [new FormulaComponent(command.FromBlockId, sourceQuantity)],
            });
    }

    private PreparedOperation PrepareUndo()
    {
        if (_undoStack.Count == 0)
        {
            throw Error("UNDO_EMPTY", "No undoable operation remains.");
        }

        var entry = _undoStack.Peek();
        return new PreparedOperation(
            "operation_undone",
            entry.CompensatingChange,
            CreatesUndoEntry: false,
            IsUndo: true,
            CompensatesEventId: entry.EventId);
    }

    private static PreparedOperation PrepareSeal(SealFormulaCommand command)
    {
        if (!command.PrerequisitesSatisfied)
        {
            throw Error("INVALID_STATE", "Seal prerequisites are not satisfied.");
        }

        if (string.IsNullOrWhiteSpace(command.Title))
        {
            throw Error("INVALID_STATE", "A sealed formula requires a title.");
        }

        return new PreparedOperation(
            "formula_sealed",
            new FormulaStateChange
            {
                SealedAfter = true,
                TitleAfter = command.Title.Trim(),
            },
            CreatesUndoEntry: false,
            IsUndo: false,
            CompensatesEventId: null);
    }

    private void ApplyChange(FormulaStateChange change)
    {
        foreach (var removed in change.Removed)
        {
            if (!_components.Remove(removed.BlockId))
            {
                throw new InvalidDataException($"Cannot apply removal for missing component {removed.BlockId}.");
            }
        }

        foreach (var updated in change.Updated)
        {
            if (!_components.TryGetValue(updated.BlockId, out var current)
                || Math.Abs(current - updated.BeforeQuantity) > 0.0000005)
            {
                throw new InvalidDataException($"Cannot apply quantity change for {updated.BlockId}.");
            }

            _components[updated.BlockId] = updated.AfterQuantity;
        }

        foreach (var added in change.Added)
        {
            if (!_components.TryAdd(added.BlockId, added.Quantity))
            {
                throw new InvalidDataException($"Cannot apply duplicate component addition {added.BlockId}.");
            }
        }

        if (change.SealedAfter.HasValue)
        {
            IsSealed = change.SealedAfter.Value;
            Title = change.TitleAfter;
        }
    }

    private void ValidateEnvelope(FormulaCommandEnvelope command)
    {
        if (!string.Equals(command.FormulaId, FormulaId, StringComparison.Ordinal))
        {
            throw Error("FORMULA_NOT_FOUND", "The command targets a different formula aggregate.");
        }

        if (command.CommandId == Guid.Empty || command.IdempotencyKey == Guid.Empty)
        {
            throw Error("INVALID_STATE", "Command and idempotency identifiers are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command.ActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SceneId);
    }

    private void ValidateBlock(string blockId)
    {
        if (!_validBlockIds.Contains(blockId))
        {
            throw Error("BLOCK_NOT_FOUND", $"Unknown scent block {blockId}.");
        }
    }

    private double GetComponent(string blockId)
    {
        if (!_components.TryGetValue(blockId, out var quantity))
        {
            throw Error("COMPONENT_NOT_FOUND", $"Component {blockId} is not active.");
        }

        return quantity;
    }

    private static double ValidateQuantity(double value)
    {
        if (!double.IsFinite(value)
            || value < MinimumPositiveQuantity
            || value > MaximumComponentQuantity)
        {
            throw Error(
                "INVALID_QUANTITY",
                $"Quantity must be finite and in range {MinimumPositiveQuantity}..{MaximumComponentQuantity}.");
        }

        return RoundQuantity(value);
    }

    private static double RoundQuantity(double value)
    {
        return Math.Round(value, StoragePrecision, MidpointRounding.AwayFromZero);
    }

    private static FormulaComponentDelta ToComponentDelta(FormulaStateChange change)
    {
        return new FormulaComponentDelta
        {
            Added = change.Added,
            Updated = change.Updated
                .Select(static item => new FormulaComponent(item.BlockId, item.AfterQuantity))
                .ToArray(),
            Removed = change.Removed.Select(static item => item.BlockId).ToArray(),
        };
    }

    private string CalculateFormulaHash()
    {
        var canonical = new StringBuilder()
            .Append(_versions.EngineVersion).Append('|')
            .Append(_versions.BlockLibraryVersion).Append('|')
            .Append(_versions.RelationLibraryVersion);
        foreach (var component in _components.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            canonical.Append('|').Append(component.Key)
                .Append(':').Append(component.Value.ToString("R", CultureInfo.InvariantCulture))
                .Append(':').Append(component.Key)
                .Append(":0");
        }

        return Hash(canonical.ToString());
    }

    private string CalculateAggregateStateHash()
    {
        return Hash(
            $"{CalculateFormulaHash()}|sealed:{IsSealed}|title:{Title ?? string.Empty}");
    }

    private static string CalculateCommandFingerprint(FormulaCommandEnvelope command)
    {
        return Hash(
            $"{command.FormulaId}|{command.ExpectedRevision}|{command.ActorId}|{command.Payload.SemanticFingerprint}");
    }

    private static bool IsUndoableEventType(string eventType)
    {
        return eventType is
            "component_added"
            or "component_increased"
            or "component_decreased"
            or "component_quantity_set"
            or "component_removed"
            or "component_replaced";
    }

    private static string NewEventId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static FormulaCommandException Error(string code, string message)
    {
        return new FormulaCommandException(code, message);
    }

    private sealed record PreparedOperation(
        string EventType,
        FormulaStateChange Change,
        bool CreatesUndoEntry,
        bool IsUndo,
        string? CompensatesEventId)
    {
        public static PreparedOperation Undoable(string eventType, FormulaStateChange change) =>
            new(eventType, change, CreatesUndoEntry: true, IsUndo: false, CompensatesEventId: null);
    }

    private sealed record UndoEntry(string EventId, FormulaStateChange CompensatingChange);
    private sealed record IdempotencyEntry(string Fingerprint, FormulaCommandResult Result);
}
