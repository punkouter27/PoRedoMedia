namespace PoRedoMedia.Movie.Script;

// After the script is written: fitting a line to its time, reviewing it, the teasers, the shots to film and the upload.
internal static partial class ScriptSchemas
{
    public static string RewriteSystem(Show show = Show.Robot) => $"""
        You rewrite one narration line for {Of(show).Host} so that it takes a different amount of time
        to say. Keep the same subject, facts, voice and depth. To lengthen, add relevant detail on the same subtopic;
        to shorten, summarise. Never add new specific numbers or names. Return only the spoken words.

        {Fence}
        """;

    public const string ReviewSystem = $"""
        You are the editor of an educational video, reading its script before any picture is paid for. The clips are
        numbered. Note only the clips with one of these problems, in one short plain sentence each that says what to change:
        - its opening line would not make a viewer stay;
        - it says what an earlier clip already said (name that clip's number);
        - it does not follow from the title it was given.
        Leave out every clip that is fine. An empty list is a good answer.

        {Fence}
        """;

    /// <summary>The same read, with the passages the script was written from, so a claim they do not back is caught too.</summary>
    public const string CheckedReviewSystem = $"""
        You are the editor of an educational video, reading its script before any picture is paid for. The clips are
        numbered, and the reference passages the script was written from come after it. Note only the clips with one
        of these problems, in one short plain sentence each that says what to change:
        - it names a sensor, part, method, algorithm, number, rate, setting or piece of software that no reference
          passage mentions (say which, and suggest saying it in general terms or cutting it);
        - its opening line would not make a viewer stay;
        - it says what an earlier clip already said (name that clip's number);
        - it does not follow from the title it was given.
        Leave out every clip that is fine. An empty list is a good answer.

        {Fence}
        """;

    public const string Review = """
        { "type": "object", "properties": { "notes": { "type": "array", "items": { "type": "object", "properties": { "clip": { "type": "integer" }, "note": { "type": "string" } }, "required": ["clip", "note"], "additionalProperties": false } } }, "required": ["notes"], "additionalProperties": false }
        """;

    public const string Rewrite = """
        { "type": "object", "properties": { "dialogue": { "type": "string" } }, "required": ["dialogue"], "additionalProperties": false }
        """;

    public static string TeasersSystem(Show show = Show.Robot) => $"""
        You cut teasers for other video sites from a finished episode of {Of(show).SeriesAbout}.
        Each teaser is ten seconds long, upright, and makes someone want to watch the whole episode. The clips are
        numbered and timed. Give three teasers, each different:
        - hook: a line of at most eight words shown over the whole teaser. Plain and specific, no hashtags.
        - moments: one to three stretches of the episode, in the order they play, adding up to ten seconds: each with
          its clip number, where in that clip it starts (seconds from the clip's start) and how long it runs.
        {Of(show).TeaserPrefer} Where a film
        says what was seen in it second by second, start its moment a little before the telling second. Use only what
        the script says happens.

        {Fence}
        """;

    public const string Teasers = """
        { "type": "object", "properties": { "teasers": { "type": "array", "items": { "type": "object", "properties": { "hook": { "type": "string" }, "moments": { "type": "array", "items": { "type": "object", "properties": { "clip": { "type": "integer" }, "start": { "type": "number" }, "length": { "type": "number" } }, "required": ["clip", "start", "length"], "additionalProperties": false } } }, "required": ["hook", "moments"], "additionalProperties": false } } }, "required": ["teasers"], "additionalProperties": false }
        """;

    /// <summary>Plans what to film for a lesson, before it is filmed.</summary>
    public static string ShotsSystem(Show show = Show.Robot) => show switch { Show.Kids => KidsShotsSystem, Show.General => GeneralShotsSystem, _ => RobotShotsSystem };

    private const string GeneralShotsSystem = $"""
        You help someone plan what to film for a short video of their own, before they film it. For each beat the
        request names, give two to four shots, in the order they would be filmed:
        - beat: the beat's name, exactly as the request gives it.
        - shot: one short plain line saying where to point the camera and what should happen, for example "Wide, from
          the gate: the whole garden before anything is planted". Each is one take on a phone, a few seconds to a minute.
        Film in good light and hold the phone still. Where the topic names a moment not to miss, plan a shot of it.
        Use only what the topic says.

        {Fence}
        """;

    private const string KidsShotsSystem = $"""
        You help a parent plan what to film for one episode of a family video series for young children, starring their
        little boy playing, before they film it. For each beat the request names, give two to four shots, in the order
        they would be filmed:
        - beat: the beat's name, exactly as the request gives it.
        - shot: one short plain line saying where to point the camera and what should happen, for example "Low down, at
          his eye level: his face as the lid comes off". Each is one take on a phone, a few seconds to a minute.
        Film at his eye level, in good light, with the toy in view. Keep anything that shows where the family lives out
        of the frame. Use only what the topic says.

        {Fence}
        """;

    private const string RobotShotsSystem = $"""
        You help the owner of a Unitree R1 EDU humanoid robot plan what to film for one episode of their series about
        teaching it a new skill, before they film it. For each beat the request names, give two to four shots, in the
        order they would be filmed:
        - beat: the beat's name, exactly as the request gives it.
        - shot: one short plain line saying where to point the camera and what should happen, for example "From the
          side, low down: its foot lifting onto the first step". Each is one take on a phone, a few seconds to a minute.
        Film the real robot and the place it works in. Where the lesson says something went wrong, plan a shot of that
        moment. Use only what the lesson says.

        {Fence}
        """;

    public const string Shots = """
        { "type": "object", "properties": { "shots": { "type": "array", "items": { "type": "object", "properties": { "beat": { "type": "string" }, "shot": { "type": "string" } }, "required": ["beat", "shot"], "additionalProperties": false } } }, "required": ["shots"], "additionalProperties": false }
        """;

    public static string PublishSystem(Show show = Show.Robot) => show switch { Show.Kids => KidsPublishSystem, Show.General => GeneralPublishSystem, _ => RobotPublishSystem };

    private const string GeneralPublishSystem = $"""
        You write the upload details for a short video from its script. Give:
        - titles: three alternative video titles, each under 70 characters, plain and specific.
        - description: two short paragraphs for the video description, in plain text.
        - tags: ten to fifteen search tags, without a # sign.
        - hashtags: three hashtags, each starting with #.

        Use only facts that appear in the script. Give nobody's surname, address, school or workplace, even when the
        script does.

        {Fence}
        """;

    private const string KidsPublishSystem = $"""
        You write the upload details for a video made for young children, from its script: a little boy playing with
        his toys. Give:
        - titles: three alternative video titles, each under 70 characters, bright and simple.
        - description: two short, cheerful paragraphs for the video description, in plain text, for the parents who read it.
        - tags: ten to fifteen search tags, without a # sign.
        - hashtags: three hashtags, each starting with #.

        Use only facts that appear in the script. Never give the boy's surname, age, school, town or anything else
        that says who or where he is, even when the script does.

        {Fence}
        """;

    private const string RobotPublishSystem = $"""
        You write the upload details for an educational video from its script. Give:
        - titles: three alternative video titles, each under 70 characters.
        - description: two short paragraphs for the video description, in plain text.
        - tags: ten to fifteen search tags, without a # sign.
        - hashtags: three hashtags, each starting with #.

        Use only facts that appear in the script.

        {Fence}
        """;

    public const string Publish = """
        { "type": "object", "properties": { "titles": { "type": "array", "items": { "type": "string" } }, "description": { "type": "string" }, "tags": { "type": "array", "items": { "type": "string" } }, "hashtags": { "type": "array", "items": { "type": "string" } } }, "required": ["titles", "description", "tags", "hashtags"], "additionalProperties": false }
        """;
}
