using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Results;

namespace PoRedoMedia.Shared.Validation;

/// <summary>
/// Validators are registered once by type and injected (API and client), so what a check needs from the moment (the
/// server's movie limits, which publishing sites are connected) travels as FluentValidation root context data, keyed
/// by its type, instead of through a constructor.
/// </summary>
public static class ContextData
{
    /// <summary>Checks <paramref name="model"/> with <paramref name="data"/> available to the rules that need it.</summary>
    public static ValidationResult Validate<T>(this IValidator<T> validator, T model, object data,
        Action<ValidationStrategy<T>>? options = null)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(data);
        var context = options is null ? new ValidationContext<T>(model) : ValidationContext<T>.CreateWithOptions(model, options);
        context.RootContextData[data.GetType().Name] = data;
        return validator.Validate(context);
    }

    /// <summary>The context data of type <typeparamref name="TData"/>; a rule that needs it cannot run without it.</summary>
    public static TData Get<TData, T>(ValidationContext<T> context) where TData : class
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RootContextData.TryGetValue(typeof(TData).Name, out var data) && data is TData typed
            ? typed
            : throw new InvalidOperationException($"{typeof(T).Name} is checked with {typeof(TData).Name}: call Validate(model, {typeof(TData).Name}).");
    }
}
