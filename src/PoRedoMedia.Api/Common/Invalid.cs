using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace PoRedoMedia.Api.Common;

/// <summary>
/// A request whose fields are wrong answers 400 as a validation problem: <c>errors</c> by field, and the first message
/// as <c>detail</c> too, which is what the client shows (<c>ProblemDetailsParser</c>).
/// </summary>
public static class Invalid
{
    public static ValidationProblem Field(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }, detail: message);

    /// <returns>The validation problem when <paramref name="model"/> breaks a rule of <paramref name="validator"/>, else null.</returns>
    public static ValidationProblem? Check<T>(IValidator<T> validator, T model)
    {
        var result = validator.Validate(model);
        return result.IsValid ? null : TypedResults.ValidationProblem(result.ToDictionary(), detail: result.Errors[0].ErrorMessage);
    }
}
