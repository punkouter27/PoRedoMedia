namespace PoRedoMedia.Movie.Script;

// Finding passages, and what a picture shows: its lettering, its host, its scene and whether it still fits.
internal static partial class ScriptSchemas
{
    /// <summary>Turns a lesson into the words its passages would be found by in the official repositories.</summary>
    public const string SearchSystem = $"""
        You help find the passages in the Unitree, MuJoCo and Isaac Lab repositories that bear on one lesson taught to
        the Unitree R1 humanoid robot. Give up to eight search terms: single words or short names as they would appear
        in the code, the configuration or the documentation (for example "terrain", "curriculum", "height_scan",
        "stairs", "reward"). Leave out the robot's own name and everyday words.

        {Fence}
        """;

    public const string Search = """
        { "type": "object", "properties": { "terms": { "type": "array", "items": { "type": "string" } } }, "required": ["terms"], "additionalProperties": false }
        """;

    /// <summary>Looks for words drawn into a picture, which picture models add, misspelt, whatever they are told.</summary>
    public const string LetteringSystem = """
        You are shown one picture made for a video. Answer whether any text is drawn in it: letters, words, numbers,
        labels, signs or speech bubbles with writing. Logos too small to read, and marks that are not letters, do not count.
        - lettered: true when there is text that can be read or nearly read.
        - text: the text as it appears, misspellings and all, a few words at most; empty when there is none.
        """;

    public const string Lettering = """
        { "type": "object", "properties": { "lettered": { "type": "boolean" }, "text": { "type": "string" } }, "required": ["lettered", "text"], "additionalProperties": false }
        """;

    /// <summary>Compares a picture of the host with its locked design.</summary>
    public const string HostCheckSystem = """
        You are shown two pictures. The first is the design sheet of a cartoon robot character. The second is a new
        picture that should show the same character. Answer whether the character in the second picture keeps the
        design: its shape, colours, face and proportions. A different pose, angle, setting or lighting does not count.
        - same: true when it keeps the design, or when no robot is in the picture to judge.
        - difference: one short plain sentence on what differs; empty when it is the same.
        """;

    public const string HostCheck = """
        { "type": "object", "properties": { "same": { "type": "boolean" }, "difference": { "type": "string" } }, "required": ["same", "difference"], "additionalProperties": false }
        """;

    /// <summary>What each code-drawn kind adds to <see cref="SceneSystem"/>.</summary>
    public static string SceneKind(VisualKind kind) => kind switch
    {
        VisualKind.Chart => "This scene is a chart. Draw its axes or frame first, then grow each bar, line or slice to its value, labelling each. Plot only the values the request gives.",
        VisualKind.KineticText => "This scene is animated text. Show only the words the request gives, very large, arriving a word or a phrase at a time; a figure may count up to its value.",
        _ => "This scene is an animated diagram or visual metaphor. Introduce its parts one at a time, in the order the narration mentions them.",
    };

    public const string SceneSystem = $$"""
        You draw one animated scene for an explainer video, as an SVG picture moved by JavaScript. Give:
        - svg: one <svg viewBox="0 0 1920 1080" xmlns="http://www.w3.org/2000/svg"> element holding every shape and
          label, with an id on whatever moves. Its first child is a rectangle that fills the frame.
        - script: JavaScript that defines function render(t, duration). t is the time in seconds since the scene began
          and duration is its whole length. render sets attributes and styles so the picture is right for that instant.

        render is called once for every frame and must work from t alone: keep nothing between calls, and use no
        timers, no animation frames, no Math.random and no dates. Clamp and ease every movement. The scene builds until
        about 85% of its length and then holds, with something still gently moving.

        In the svg use no script, image, animate or foreignObject elements, no CSS animations or transitions, and
        nothing loaded from elsewhere. Use the font family "Segoe UI".

        Make it read at a glance: a few large shapes, forty elements at most, and labels of one to four words at least
        44 pixels tall. Keep everything inside the frame, and keep the bottom 220 pixels free of labels, because
        captions are laid over it. Use only facts and figures that appear in the narration.

        {{Fence}}
        """;

    public const string Scene = """
        { "type": "object", "properties": { "svg": { "type": "string" }, "script": { "type": "string" } }, "required": ["svg", "script"], "additionalProperties": false }
        """;

    /// <summary>Looks at a drawn scene once it has built, for the faults code cannot see in itself.</summary>
    public const string SceneCheckSystem = """
        You check one frame of an animated scene for an explainer video, taken once the scene has built. The video is
        1920 by 1080 and captions are laid over its bottom 220 pixels. Answer whether it has any of these faults:
        - a label or shape cut off by the edge of the frame;
        - labels or shapes lying over one another so that a label cannot be read;
        - a label inside the bottom 220 pixels;
        - text too small to read on a phone, or the frame nearly empty.
        Give fine: true when it has none. Otherwise give fine: false and problem: one or two short sentences saying what
        to move or change, as a designer would tell the person who drew it. Leave problem empty when it is fine.
        """;

    public const string SceneCheck = """
        { "type": "object", "properties": { "fine": { "type": "boolean" }, "problem": { "type": "string" } }, "required": ["fine", "problem"], "additionalProperties": false }
        """;

    public const string DriftSystem = $"""
        You compare two versions of a narration line from an explainer video. Answer whether the picture drawn for the
        first version would still fit the second. It no longer fits only when the core action, the tool in use, or the
        physical subject has changed. Rewording, tone, pacing and added detail about the same thing do not count.

        {Fence}
        """;

    public const string Drift = """
        { "type": "object", "properties": { "coreChanged": { "type": "boolean" } }, "required": ["coreChanged"], "additionalProperties": false }
        """;
}
