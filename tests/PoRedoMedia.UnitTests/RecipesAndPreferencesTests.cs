using PoRedoMedia.Client.Services;
using PoRedoMedia.Shared;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.UnitTests;

public sealed class RecipesAndPreferencesTests
{
    [Fact]
    public void Old_recipes_with_removed_functions_load_with_those_steps_skipped()
    {
        // Saved before Upscale and Cutout were removed: reading them as typed JSON fails on the first
        // unknown name, which used to empty the whole list.
        const string stored = """
            {
              "Meme then upscale": { "sourceId": "00000000-0000-0000-0000-000000000000", "functions": ["MemeCaption", "Upscale"], "options": { "MemeCaption.mode": "ai", "Upscale.scale": "4" } },
              "Only gone": { "functions": ["Cutout", "Upscale"] },
              "Roast": { "Functions": ["RapRoast"] },
              "Odd": { "functions": ["3", 7, null, "Restyle"] }
            }
            """;

        var recipes = RecipeBook.Parse(stored);

        Assert.Equal(["Meme then upscale", "Odd", "Roast"], recipes.Keys.Order());
        Assert.Equal([MediaFunction.MemeCaption], recipes["Meme then upscale"].Functions);
        Assert.Equal("ai", recipes["Meme then upscale"].Options![RunOptions.MemeMode]);
        Assert.Equal([MediaFunction.RapRoast], recipes["Roast"].Functions);
        Assert.Equal([MediaFunction.Restyle], recipes["Odd"].Functions);
        Assert.Empty(RecipeBook.Parse("not json"));
        Assert.Empty(RecipeBook.Parse(null));
    }

    [Fact]
    public void The_account_wins_on_sign_in_and_only_synced_keys_are_touched()
    {
        var local = new Dictionary<string, string>
        {
            ["po.adv.costs"] = "on", ["po.adv.recipes"] = "on", ["po.fx"] = "on", ["po.options"] = "{}", ["po.theme"] = "dark",
        };
        var account = new Dictionary<string, string> { ["po.adv.costs"] = "on", ["po.sfx.muted"] = "0", ["po.recipes"] = "{}", ["other"] = "x" };

        var changes = PreferenceSync.Changes(local, account);

        Assert.Equal(new Dictionary<string, string?>
        {
            ["po.sfx.muted"] = "0", ["po.recipes"] = "{}", ["po.adv.recipes"] = null, ["po.fx"] = null,
        }, changes);
    }

    [Fact]
    public void Only_functions_that_describe_the_picture_can_use_the_device()
    {
        foreach (var (functions, mode, expected) in new (MediaFunction[], string?, bool)[]
        {
            ([MediaFunction.Restyle], null, true),
            ([MediaFunction.RapRoast], null, true),
            ([MediaFunction.MemeCaption], null, true),
            ([MediaFunction.MemeCaption], RunOptions.MemeModeText, false),
            ([MediaFunction.PhotoToVideo, MediaFunction.BulkStyles], null, false),
            ([MediaFunction.Memeify], null, false),
        })
        {
            var options = mode is null ? null : new Dictionary<string, string> { [RunOptions.MemeMode] = mode };
            Assert.Equal(expected, AiCatalog.LooksAtPicture(functions, options));
        }
    }
}
