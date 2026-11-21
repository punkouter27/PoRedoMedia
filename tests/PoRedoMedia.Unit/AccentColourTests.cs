using PoRedoMedia.Client.Features.Movies;

namespace PoRedoMedia.UnitTests;

/// <summary>The series colour from Radzen's colour picker is kept as #RRGGBB, whatever form the picker answers in.</summary>
public sealed class AccentColourTests
{
    [Theory]
    [InlineData("#22cc88", "#22CC88")]
    [InlineData("rgb(34, 204, 136)", "#22CC88")]
    [InlineData("rgba(255,212,0,1)", "#FFD400")]
    [InlineData("rgb(300, 0, 0)", null)]
    [InlineData("hsl(10, 50%, 50%)", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_picker_answer_becomes_a_hex_colour_or_is_ignored(string? picked, string? expected) =>
        Assert.Equal(expected, MovieTemplateStep.Hex(picked));
}
