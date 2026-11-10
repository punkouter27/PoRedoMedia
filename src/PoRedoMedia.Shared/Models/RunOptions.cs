namespace PoRedoMedia.Shared.Models;

/// <summary>The option keys a run request may carry, and their fixed values.</summary>
public static class RunOptions
{
    /// <summary>Model id from the model picker for looking at an image. Absent = the default provider.</summary>
    public const string VisionModel = "Vision.model";

    /// <summary>
    /// A description of the picture written on the user's device. When present the server does not
    /// look at the picture itself.
    /// </summary>
    public const string VisionDescription = "Vision.description";

    /// <summary>Id of a recipe in <see cref="StyleRecipeCatalog"/>. Absent = recreate the picture as it is.</summary>
    public const string RestyleStyle = "Restyle.style";

    /// <summary>A style in the user's own words. Wins over <see cref="RestyleStyle"/>.</summary>
    public const string RestylePrompt = "Restyle.prompt";

    public const string MemeMode = "MemeCaption.mode";
    public const string MemeModeAi = "ai";
    public const string MemeModeText = "text";
    public const string MemeModeTemplate = "template";
    public const string MemeTop = "MemeCaption.top";
    public const string MemeBottom = "MemeCaption.bottom";
    public const string MemeTemplate = "MemeCaption.template";

    public static string MemeZone(int index) => $"MemeCaption.zone{index}";

    /// <summary>What should happen in the clip. Required for Photo to video.</summary>
    public const string VideoPrompt = "PhotoToVideo.prompt";

    /// <summary>The director's persona for Meme-ify, such as "Brainrot". Absent = the default.</summary>
    public const string VideoPersona = "Memeify.persona";

    /// <summary>"original", "9:16" or "1:1". Applies to the whole render.</summary>
    public const string VideoAspect = "Video.aspect";

    /// <summary>Seconds into the video where the result starts and ends. Absent = the whole video.</summary>
    public const string VideoTrimStart = "Video.trimStart";
    public const string VideoTrimEnd = "Video.trimEnd";

    /// <summary>The roast voice: "Comic", "Neural" or "Rap". Absent = the first one the server offers.</summary>
    public const string VideoRoastVoice = "VideoRoast.voice";

    /// <summary>
    /// "1" when what is said in the video is a narrator's voice-over (a Create Movie clip): the roast answers the
    /// narrator, and its lines wait for the narrator's pauses. Absent = the speech is the video's own.
    /// </summary>
    public const string VideoRoastNarrated = "VideoRoast.narrated";

    /// <summary>
    /// "1" when the video's own sound carries a narrator who must stay on top (a Create Movie clip): the source plays at
    /// full level and the meme sounds come in lower. Absent = the usual mix, where the meme sounds cut through.
    /// </summary>
    public const string VideoVoiceFirst = "Video.voiceFirst";

    /// <summary>One style prompt for Bulk styles, index 0 to 9. None sent = the user's saved set.</summary>
    public static string BulkPrompt(int index) => $"BulkStyles.prompt{index}";
}

/// <summary>A meme layout: where each line of text goes. The user's picture is the artwork.</summary>
public sealed record MemeTemplateDto(string Id, string Name, string Description, int RequiredZoneCount, string[] ZoneLabels);
