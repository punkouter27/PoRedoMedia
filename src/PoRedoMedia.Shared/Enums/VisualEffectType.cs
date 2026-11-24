namespace PoRedoMedia.Shared.Enums;

public enum VisualEffectType
{
    None,
    DeepFry,

    // Retired. SnapZoom cropped to half size and scaled back up, but the filter was added to the
    // base chain instead of the cue's time window, so it cropped the ENTIRE video. Nothing
    // produces or renders it now. The member itself must stay: entries are persisted as JSON via
    // JsonStringEnumConverter, so every already-stored script contains "SnapZoom" and removing the
    // member would throw JsonException when loading those sessions.
    SnapZoom,
    MotionBlur,
    Overlay,

    // Windowed, like DeepFry: only inside the cue's window, so the rest of the video keeps its framing.

    /// <summary>A punch-in on the middle of the picture, for a reaction.</summary>
    Zoom,

    /// <summary>The picture shaken, for an impact: a crash, a fall, a slam.</summary>
    Shake,
}
