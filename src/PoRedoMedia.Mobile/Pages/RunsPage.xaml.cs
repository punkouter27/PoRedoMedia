using PoRedoMedia.Mobile.Services;

namespace PoRedoMedia.Mobile.Pages;

public partial class RunsPage : ContentPage
{
    private readonly PoRedoMediaMobileClient _client;

    public RunsPage(PoRedoMediaMobileClient client)
    {
        _client = client;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    private async void OnRefreshClicked(object? sender, EventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        RunsList.Clear();
        try
        {
            var quota = await _client.GetQuotaAsync();
            QuotaLabel.Text = quota is null
                ? "Quota unavailable"
                : quota.Unlimited
                    ? "Daily Quota: Unlimited"
                    : $"Daily Quota: {quota.Remaining} of {quota.Limit} remaining";

            var runs = await _client.GetRunsAsync();
            foreach (var run in runs.Take(15))
            {
                RunsList.Add(new Label { Text = $"{run.CreatedAt:g} · {string.Join(", ", run.Functions)} · {run.Status}" });
            }
        }
        catch (Exception ex)
        {
            QuotaLabel.Text = $"Error: {ex.Message}";
        }
    }
}
