using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace PoRedoMedia.Client.Layout;

/// <summary>
/// Puts a FluentValidation validator from Shared (the one the API endpoint runs too) into a form's
/// <see cref="EditContext"/>, so <c>ValidationMessage</c>/<c>RadzenValidationMessage</c> show each field's message
/// beside it. The same bridge as PoTraffic's. <see cref="Request"/> builds what is checked when the form's model is
/// not itself the request (a mutable form model that becomes a positional record on send); its property names must
/// match the form model's, so each message lands on its field.
/// </summary>
public sealed class FluentValidationValidator<TModel> : ComponentBase, IDisposable where TModel : class
{
    private ValidationMessageStore? _messages;
    private EditContext? _subscribed;

    [CascadingParameter] private EditContext? CurrentEditContext { get; set; }

    [Parameter, EditorRequired] public IValidator<TModel> Validator { get; set; } = default!;

    /// <summary>What to check. Empty: the form's model, which must then be a <typeparamref name="TModel"/>.</summary>
    [Parameter] public Func<TModel>? Request { get; set; }

    protected override void OnInitialized()
    {
        if (CurrentEditContext is null)
            return;

        _subscribed = CurrentEditContext;
        _messages = new ValidationMessageStore(_subscribed);
        _subscribed.OnValidationRequested += OnValidationRequested;
        _subscribed.OnFieldChanged += OnFieldChanged;
    }

    private TModel? Checked() => Request?.Invoke() ?? _subscribed?.Model as TModel;

    private void OnValidationRequested(object? sender, ValidationRequestedEventArgs e)
    {
        if (_subscribed is null || _messages is null || Checked() is not { } model)
            return;

        _messages.Clear();
        foreach (ValidationFailure failure in Validator.Validate(model).Errors)
            _messages.Add(_subscribed.Field(failure.PropertyName), failure.ErrorMessage);

        _subscribed.NotifyValidationStateChanged();
    }

    private void OnFieldChanged(object? sender, FieldChangedEventArgs e)
    {
        if (_subscribed is null || _messages is null || Checked() is not { } model)
            return;

        _messages.Clear(e.FieldIdentifier);
        var result = Validator.Validate(model, options => options.IncludeProperties(e.FieldIdentifier.FieldName));
        foreach (ValidationFailure failure in result.Errors)
            _messages.Add(e.FieldIdentifier, failure.ErrorMessage);

        _subscribed.NotifyValidationStateChanged();
    }

    public void Dispose()
    {
        if (_subscribed is null)
            return;

        _subscribed.OnValidationRequested -= OnValidationRequested;
        _subscribed.OnFieldChanged -= OnFieldChanged;
    }
}
