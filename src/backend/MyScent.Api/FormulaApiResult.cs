using MyScent.Formulas;

namespace MyScent.Api;

public static class FormulaApiResult
{
    public static IResult Execute(Func<IResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            return action();
        }
        catch (FormulaCommandException exception)
        {
            var statusCode = MapStatusCode(exception.Code);
            return Results.Problem(
                statusCode: statusCode,
                title: "Formula command rejected",
                detail: exception.Message,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = exception.Code,
                });
        }
        catch (ArgumentException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request",
                detail: exception.Message,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "INVALID_REQUEST",
                });
        }
    }

    private static int MapStatusCode(string code)
    {
        return code switch
        {
            "FORMULA_NOT_FOUND" => StatusCodes.Status404NotFound,
            "COMPONENT_NOT_FOUND" => StatusCodes.Status404NotFound,
            "REVISION_CONFLICT" => StatusCodes.Status409Conflict,
            "DUPLICATE_COMMAND_MISMATCH" => StatusCodes.Status409Conflict,
            "COMPONENT_ALREADY_EXISTS" => StatusCodes.Status409Conflict,
            "LAST_COMPONENT_REMOVAL_BLOCKED" => StatusCodes.Status409Conflict,
            "UNDO_EMPTY" => StatusCodes.Status409Conflict,
            "UNDO_BOUNDARY_REACHED" => StatusCodes.Status409Conflict,
            "FORMULA_SEALED" => StatusCodes.Status409Conflict,
            "INVALID_STATE" => StatusCodes.Status409Conflict,
            "ENGINE_INPUT_INVALID" => StatusCodes.Status422UnprocessableEntity,
            "BLOCK_NOT_FOUND" => StatusCodes.Status400BadRequest,
            "INVALID_QUANTITY" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status400BadRequest,
        };
    }
}
