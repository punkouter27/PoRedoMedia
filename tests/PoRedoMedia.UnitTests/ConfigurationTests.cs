using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Configuration;

namespace PoRedoMedia.UnitTests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Mock_AI_is_enabled_only_in_Test()
    {
        foreach (var (flag, environment, expected) in new[]
        {
            ("true", "Development", false), ("true", "Test", true), ("true", "Production", false), ("false", "Test", false),
        })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Mocks:UseMockAi"] = flag }).Build();
            var env = Substitute.For<IWebHostEnvironment>();
            env.EnvironmentName.Returns(environment);

            Assert.Equal(expected, MockAi.IsEnabled(config, env));
        }
    }

    [Fact]
    public void Only_this_apps_secrets_are_loaded()
    {
        var manager = new PrefixKeyVaultSecretManager("PoRedoMedia");

        Assert.True(manager.Load(SecretModelFactory.SecretProperties(name: "PoRedoMedia--AiFoundry--Key")));
        Assert.Equal("AiFoundry:Key", manager.GetKey(new KeyVaultSecret("PoRedoMedia--AiFoundry--Key", "value")));
        Assert.False(manager.Load(SecretModelFactory.SecretProperties(name: "AzureAd--ClientId")));
        Assert.False(manager.Load(SecretModelFactory.SecretProperties(name: "PoMemeVideo--AiFoundry--Key")));

        // A developer's machine never takes the deployed app's storage account from the vault.
        var storage = SecretModelFactory.SecretProperties(name: "PoRedoMedia--Storage--ConnectionString");
        Assert.True(manager.Load(storage));
        Assert.False(new PrefixKeyVaultSecretManager("PoRedoMedia", localStorage: true).Load(storage));
    }

    [Fact]
    public void The_sweep_removes_stale_uploads_and_expired_items_but_never_a_pinned_one()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        MediaItem Item(MediaStatus status, int ageDays, bool pinned = false) => new()
        {
            Owner = new UserId("u"), Id = MediaId.New(), Kind = PoRedoMedia.Shared.Enums.MediaKind.Image, Status = status, Origin = "Upload",
            Title = "t", ContentType = "image/png", Extension = ".png", Pinned = pinned, CreatedAt = now.AddDays(-ageDays),
        };
        var verdict = (MediaItem item, int days) => PoRedoMedia.Api.Features.Housekeeping.HousekeepingService.Classify(item, now, days).ToString();

        Assert.Equal("Keep", verdict(Item(MediaStatus.Ready, 29), 30));
        Assert.Equal("DeleteExpired", verdict(Item(MediaStatus.Ready, 31), 30));
        Assert.Equal("Keep", verdict(Item(MediaStatus.Ready, 400, pinned: true), 30));
        Assert.Equal("Keep", verdict(Item(MediaStatus.Ready, 400), 0));
        Assert.Equal("DeleteAbandonedUpload", verdict(Item(MediaStatus.Uploading, 2), 0));
        Assert.Equal("Keep", verdict(Item(MediaStatus.Uploading, 0), 30));
    }
}
