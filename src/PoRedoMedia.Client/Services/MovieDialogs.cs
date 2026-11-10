using System.Globalization;
using PoRedoMedia.Shared.Models;
using Radzen;

namespace PoRedoMedia.Client.Services;

/// <summary>The two questions Create Movie asks before it acts: "are you sure?", and "this costs money, go ahead?".</summary>
public static class MovieDialogs
{
    public static string Money(decimal dollars) => "$" + dollars.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Asks before something that cannot be taken back. Cancel is the answer unless the user says otherwise.</summary>
    public static async Task<bool> ConfirmAsync(DialogService dialogs, string title, string message, string action) =>
        await dialogs.Confirm(message, title, new ConfirmOptions { OkButtonText = action, CancelButtonText = "Cancel", CloseDialogOnEsc = true, Width = "min(32rem, calc(100vw - 1rem))" }) == true;

    /// <summary>The same, for something that is paid for: with a budget set, it says what the month has spent and what this adds.</summary>
    public static Task<bool> ConfirmSpendAsync(DialogService dialogs, MovieStore store, string title, string message, string action, decimal dollars)
    {
        if (store.Prefs.MonthlyBudget > 0)
            message += $" This month so far: {Money(store.Prefs.MonthSpent)} of {Money(store.Prefs.MonthlyBudget)}{(dollars > 0 ? $"; this adds about {Money(dollars)}" : "")}.";
        return ConfirmAsync(dialogs, title, message, action);
    }

    /// <summary>What a movie's render does to the day's runs, as a sentence; empty when runs are not counted here.</summary>
    public static async Task<string> RunsLeftAsync(RunApi runs)
    {
        try
        {
            return await runs.GetQuotaAsync() is { Unlimited: false } quota
                ? $" Making the movie uses one of your runs for today: {quota.Remaining} of {quota.Limit} left. It is given back if no video comes of it."
                : "";
        }
        catch (HttpRequestException)
        {
            return "";
        }
    }

    /// <summary>
    /// Shows what the movie's missing pictures would cost (or, with <paramref name="finals"/>, drawing its drafts
    /// again at full quality), and draws them once that is agreed to.
    /// </summary>
    public static async Task GeneratePicturesAsync(DialogService dialogs, MovieStore store, MovieApi api, bool finals)
    {
        if (store.Movie is not { } movie) return;
        var flag = finals ? "true" : "false";
        if (await api.EstimateAsync(movie.Id, $"pictures/estimate?finals={flag}") is not { } estimate) return;
        if (estimate.NothingToDo || estimate.Refusal is not null)
        {
            store.Say(estimate.NothingToDo ? estimate.Summary : null, estimate.Refusal);
            return;
        }
        if (await ConfirmSpendAsync(dialogs, store, finals ? "Draw the final pictures?" : "Generate pictures?", estimate.Summary, finals ? "Draw them" : "Generate", estimate.Dollars ?? 0))
            await store.DoAsync($"pictures?finals={flag}", job: true);
    }
}
