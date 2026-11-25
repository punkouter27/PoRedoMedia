using System.Text.Json;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Features.Render;
using PoRedoMedia.Api.Features.Video;
using PoRedoMedia.Movie.Pictures;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Unit;

public sealed class FeatureUpgradesTests
{
    [Fact]
    public void ApplyVisionBoxDefaults_AimsHeadStickersAtFaceAndGeneralStickersAtPrimary()
    {
        var boxes = VideoStep.ParseVisionBoxes("""
            [
              {"label":"face","x":0.4,"y":0.1,"w":0.2,"h":0.3},
              {"label":"car","x":0.05,"y":0.5,"w":0.8,"h":0.4}
            ]
            """);
        var sound = SoundId.New();
        var hat = new ScriptEntry { EntryId = EntryId.New(), MediaId = MediaId.New(), SoundId = sound, OverlayAssetId = "crown" };
        var eyes = new ScriptEntry { EntryId = EntryId.New(), MediaId = MediaId.New(), SoundId = sound, OverlayAssetId = "laser-eyes" };
        var pow = new ScriptEntry { EntryId = EntryId.New(), MediaId = MediaId.New(), SoundId = sound, OverlayAssetId = "speech-pow" };
        var pinned = new ScriptEntry { EntryId = EntryId.New(), MediaId = MediaId.New(), SoundId = sound, OverlayAssetId = "skull", OverlayX = 0.2, OverlayY = 0.8 };

        VideoStep.ApplyVisionBoxDefaults([hat, eyes, pow, pinned], boxes);

        Assert.Equal(0.5, hat.OverlayX!.Value, 3);
        Assert.Equal(0.136, hat.OverlayY!.Value, 3);
        Assert.Equal(0.5, eyes.OverlayX!.Value, 3);
        Assert.Equal(0.196, eyes.OverlayY!.Value, 3);
        Assert.Equal(0.45, pow.OverlayX!.Value, 3);
        Assert.Equal(0.7, pow.OverlayY!.Value, 3);
        Assert.Equal(0.2, pinned.OverlayX!.Value, 3);
        Assert.Equal(0.8, pinned.OverlayY!.Value, 3);
    }

    [Fact]
    public void DirectorStickers_AllShippedOverlaysExistOnDisk()
    {
        Assert.True(DirectorScripts.Stickers.Length >= 18);
        foreach (var id in DirectorScripts.Stickers)
            Assert.NotNull(FFmpegArgs.ResolveOverlayAssetPath(id));
        Assert.Null(FFmpegArgs.ResolveOverlayAssetPath("../escape"));
    }

    [Theory]
    [InlineData(VisualEffectType.Vhs, "rgbashift")]
    [InlineData(VisualEffectType.ComicHalftone, "unsharp")]
    [InlineData(VisualEffectType.Crt, "vignette")]
    public void ResolveWindowedEffectFilter_SupportsNewShaderEffects(VisualEffectType effect, string expectedToken)
    {
        var filter = FFmpegArgs.ResolveWindowedEffectFilter(effect.ToString());
        Assert.NotNull(filter);
        Assert.Contains(expectedToken, filter, StringComparison.Ordinal);
    }

    [Fact]
    public void WikimediaStock_ExtractsThumbUrlFromQueryPayload()
    {
        using var doc = JsonDocument.Parse("""
            {"query":{"pages":{"101":{"imageinfo":[{"thumburl":"https://upload.wikimedia.org/wikipedia/commons/thumb/a.jpg","url":"https://upload.wikimedia.org/wikipedia/commons/a.jpg"}]}}}}
            """);

        Assert.Equal("https://upload.wikimedia.org/wikipedia/commons/thumb/a.jpg", WikimediaStock.ExtractImageUrl(doc.RootElement, 0));
        Assert.Null(WikimediaStock.ExtractImageUrl(doc.RootElement, 3));
    }

    [Fact]
    public void TenorStock_ExtractsMediumGifUrl()
    {
        using var doc = JsonDocument.Parse("""
            {"results":[{"media_formats":{"mediumgif":{"url":"https://media.tenor.com/abc/demo.gif"}}}]}
            """);

        Assert.Equal("https://media.tenor.com/abc/demo.gif", TenorStock.ExtractMediaUrl(doc.RootElement, 0));
        Assert.Null(TenorStock.ExtractMediaUrl(doc.RootElement, 2));
    }

    [Fact]
    public void WireJson_RoundTripsVisionBoxesAndLocalDirectorDraft()
    {
        var boxes = new[] { new VisionBoxDto("person", 0.1, 0.2, 0.3, 0.4) };
        var boxJson = JsonSerializer.Serialize(boxes, WireJson.Default.VisionBoxDtoArray);
        var backBoxes = JsonSerializer.Deserialize(boxJson, WireJson.Default.VisionBoxDtoArray);
        Assert.NotNull(backBoxes);
        Assert.Single(backBoxes);
        Assert.Equal("person", backBoxes[0].Label);

        var draft = new LocalDirectorDraftDto("TOP", "BOTTOM", "brainrot", "Instant punchline");
        var draftJson = JsonSerializer.Serialize(draft, WireJson.Default.LocalDirectorDraftDto);
        var backDraft = JsonSerializer.Deserialize(draftJson, WireJson.Default.LocalDirectorDraftDto);
        Assert.Equal(draft, backDraft);
    }
}
