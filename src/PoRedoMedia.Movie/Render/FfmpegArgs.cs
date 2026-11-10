using System.Globalization;

namespace PoRedoMedia.Movie.Render;

public sealed record ExportPreset(int Width, int Height, int Fps)
{
    public static readonly ExportPreset Hd30 = new(1920, 1080, 30);
    public static readonly ExportPreset Hd60 = new(1920, 1080, 60);
    public static readonly ExportPreset Uhd30 = new(3840, 2160, 30);
    public static readonly ExportPreset Uhd60 = new(3840, 2160, 60);

    /// <summary>Upright, for teasers on phone-first video sites.</summary>
    public static readonly ExportPreset Upright = new(1080, 1920, 30);

    /// <summary>A teaser small enough to draw in a moment, to watch on its card before rendering it in full.</summary>
    public static readonly ExportPreset UprightDraft = new(360, 640, 30);

    public bool Portrait => Height > Width;
}

/// <summary>One moment of a teaser as FFmpeg cuts it: a stretch of a finished clip picture and of that clip's sound.</summary>
public sealed record TeaserCut(string Picture, string Sound, TimeSpan Start, TimeSpan Length);

/// <summary>
/// How the series intro and outro look: the accent colour (#RRGGBB), the kit's logo and mascot pictures (each null when
/// there is none), and where the intro's words and mascot sit.
/// </summary>
/// <param name="Grain">The brand kit's film finish: grain and a soft vignette, as on every clip.</param>
/// <param name="CallToAction">The line under the channel's name in the outro. Kept to plain words: it is drawn as it is.</param>
public sealed record BookendLook(string Accent, string? Logo, string? Mascot, IntroLayout Layout = IntroLayout.Side, bool Grain = false, string CallToAction = BookendLook.Subscribe)
{
    public const string Subscribe = "Subscribe for the next lesson";

    public const string KidsSubscribe = "See you next playtime";
    public const string GeneralSubscribe = "Thanks for watching";
}

/// <summary>
/// What a clip gets besides its picture: the series colour (for a title card's moving background and the light that
/// sweeps in a new beat), the brand kit's film finish, and for the first clip of a beat its streak of light and the
/// frosted glass behind its strap.
/// </summary>
/// <param name="NewBeat">The clip opens a beat, so a streak of light crosses its first half second.</param>
/// <param name="Strap">The beat's name on the strap burned in with the captions, which sizes the glass behind it; null for none.</param>
public sealed record Polish(string Accent = "#FFD400", bool Grain = false, bool NewBeat = false, string? Strap = null);

/// <summary>
/// The owner's film as the robot's own camera might see it: brightness read as depth in false colour, edges traced in
/// light, scan lines, and a readout naming the moment. A film from a beat where it went wrong also glitches.
/// </summary>
/// <param name="LabelFile">A text file in the working folder with the readout's words; they are drawn, never run.</param>
public sealed record RobotEye(string LabelFile, bool Fault);

/// <summary>Video holds its last frame when the narration runs longer; Loop, for short generated footage, plays again from the start.</summary>
public enum ClipSource { TitleCard, Image, Video, Loop }

/// <summary>Where a clip sits on the episode timeline, and how long its picture runs.</summary>
public sealed record Slot(TimeSpan Start, TimeSpan VideoLength);

/// <summary>
/// Builds FFmpeg command lines. Pure functions, so the exact arguments are snapshot-tested.
/// Each clip is encoded once, complete with its captions and fades, and the finished clips are then joined by
/// copying. Encoding the whole episode a second time to join it took several times longer than everything else.
/// </summary>
public static partial class FfmpegArgs
{
    public static readonly TimeSpan AudioCrossfade = TimeSpan.FromMilliseconds(150);

    /// <summary>Each clip fades up from black and back down over this long, which reads as a dip between clips.</summary>
    public static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(250);
    private const double TargetLufs = -16;

    /// <summary>Font the title card reads from the working folder. This FFmpeg build has no font lookup by name.</summary>
    public const string TitleFontFile = "title-font.ttf";

    /// <summary>
    /// Narration drives the timeline. Each join overlaps the audio by <see cref="AudioCrossfade"/>, and each picture
    /// runs exactly its share, so the pictures laid end to end stay in step with the sound.
    /// </summary>
    public static IReadOnlyList<Slot> Timeline(IReadOnlyList<TimeSpan> narration)
    {
        var slots = new List<Slot>(narration.Count);
        var start = TimeSpan.Zero;
        for (var i = 0; i < narration.Count; i++)
        {
            var share = i == narration.Count - 1 ? narration[i] : narration[i] - AudioCrossfade;
            slots.Add(new Slot(start, share));
            start += share;
        }
        return slots;
    }

    /// <summary>Renders one finished clip picture (no sound): sized, faded, and with its captions burned in.</summary>
    /// <param name="input">Image or video path, or for a title card the text file holding the title.</param>
    /// <param name="captionsFile">Caption file timed from the start of this clip, read from the working folder; null for none.</param>
    /// <param name="move">Which camera move a still picture gets; see <see cref="PanZoom"/>.</param>
    /// <param name="eye">For footage, the robot's-eye look; null to show it as filmed.</param>
    /// <param name="polish">The series colour, finish and beat touches; the standard ones when null.</param>
    public static IReadOnlyList<string> ClipVideo(ClipSource source, string input, TimeSpan length, ExportPreset preset, string output, string? captionsFile = null, int move = 0,
        RobotEye? eye = null, Polish? polish = null)
    {
        var (w, h, fps) = preset;
        var frames = (int)Math.Round(length.TotalSeconds * fps);
        polish ??= new Polish();
        string[] inputArgs = source switch
        {
            // A band of the series colour turning slowly through the dark, so a chapter break is never a flat slide.
            ClipSource.TitleCard => ["-f", "lavfi", "-i", $"gradients=s={w}x{h}:r={fps}:n=3:c0=0x101828:c1=0x{polish.Accent.TrimStart('#')}:c2=0x101828:x0=0:y0=0:x1={w}:y1={h}:speed=0.004"],
            ClipSource.Loop => ["-stream_loop", "-1", "-i", input],
            _ => ["-i", input],
        };
        var filter = source switch
        {
            ClipSource.Image => PanZoom(preset, frames, move),
            ClipSource.Video or ClipSource.Loop =>
                // A phone's film is often full range; brought to the usual range so every clip is encoded alike for the join.
                $"scale={w}:{h}:force_original_aspect_ratio=decrease:out_range=tv,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,fps={fps}," +
                $"tpad=stop_mode=clone:stop_duration={Seconds(length)}" + (eye is null ? "" : RobotLook(eye)),
            // ponytail: one centred line, no wrapping. Clip titles are short; wrap here if they stop being so.
            _ => $"drawtext=textfile={input}:expansion=none:fontfile={TitleFontFile}:fontcolor=white:fontsize={(preset.Portrait ? "w" : "h")}/12:x=(w-text_w)/2:y=(h-text_h)/2:borderw=3:bordercolor=black@0.35",
        };
        return [.. inputArgs, "-vf", filter + Finish(length, captionsFile, preset, polish), "-t", Seconds(length), "-an", .. Encode(preset), "-y", output];
    }

    // The robot's-eye look, after the film is fitted to the frame. Brightness stands in for depth: there is no depth
    // camera's data in an ordinary film, and near things are usually the lit ones.
    private static string RobotLook(RobotEye eye)
    {
        const string Cyan = "0x00E5FF";
        var hud = $"fontfile={TitleFontFile}:fontsize=h/26:";
        // Four corner brackets, each two short bars, framing the view as a camera's readout does.
        (string X, string Y, bool Right, bool Bottom)[] corners = [("0.04", "0.06", false, false), ("0.96", "0.06", true, false), ("0.04", "0.94", false, true), ("0.96", "0.94", true, true)];
        var brackets = string.Concat(corners.Select(c =>
            $",drawbox=x=iw*{c.X}{(c.Right ? "-iw*0.06" : "")}:y=ih*{c.Y}{(c.Bottom ? "-3" : "")}:w=iw*0.06:h=3:color={Cyan}@0.8:t=fill" +
            $",drawbox=x=iw*{c.X}{(c.Right ? "-3" : "")}:y=ih*{c.Y}{(c.Bottom ? "-ih*0.08" : "")}:w=3:h=ih*0.08:color={Cyan}@0.8:t=fill"));
        return
            ",split[cam][edges];" +
            "[cam]format=gray,format=yuv444p,pseudocolor=preset=turbo,eq=brightness=-0.08:saturation=0.8,format=gbrp[depth];" +
            "[edges]edgedetect=low=0.08:high=0.22,format=gbrp[lines];" +
            "[depth][lines]blend=all_mode=screen,drawgrid=w=iw:h=4:t=1:c=black@0.25,vignette=PI/5" +
            (eye.Fault ? ",rgbashift=rh=-6:bh=6,noise=alls=18:allf=t" : ",noise=alls=6:allf=t") +
            brackets +
            $",drawtext=text='R1 VISION':expansion=none:{hud}fontcolor={Cyan}:x=w*0.06:y=h*0.08" +
            $",drawtext=textfile={eye.LabelFile}:expansion=none:{hud}fontcolor=white:x=w*0.06:y=h*0.13" +
            $",drawtext=text='%{{pts\\:hms}}':{hud}fontcolor={Cyan}:x=w*0.94-text_w:y=h*0.08" + // the clock is the app's own words, so it may be run
            ",drawbox=x=iw*0.94-ih*0.03:y=ih*0.14:w=ih*0.03:h=ih*0.03:color=red@0.9:t=fill:enable='lt(mod(t,1),0.5)'" +
            (eye.Fault ? $",drawtext=text='FAULT':expansion=none:{hud}fontcolor=red:x=w*0.94-text_w:y=h*0.19:enable='lt(mod(t,0.5),0.3)'" : "");
    }

    /// <summary>Renders a panel sequence: each picture gets an equal share of the clip, with its own pan and zoom.</summary>
    public static IReadOnlyList<string> PanelsVideo(IReadOnlyList<string> images, TimeSpan length, ExportPreset preset, string output, string? captionsFile = null, Polish? polish = null)
    {
        var (w, h, fps) = preset;
        var total = (int)Math.Round(length.TotalSeconds * fps);
        var graph = new List<string>();
        for (var i = 0; i < images.Count; i++)
        {
            var frames = total / images.Count + (i < total % images.Count ? 1 : 0); // spare frames go to the first panels
            graph.Add($"[{i}:v]{PanZoom(preset, frames)}[p{i}]");
        }
        graph.Add($"{string.Concat(images.Select((_, i) => $"[p{i}]"))}concat=n={images.Count}:v=1:a=0{Finish(length, captionsFile, preset, polish)}[v]");
        return
        [
            .. images.SelectMany(path => new[] { "-i", path }),
            "-filter_complex", string.Join(';', graph), "-map", "[v]",
            "-t", Seconds(length), "-an", .. Encode(preset), "-y", output,
        ];
    }

    /// <summary>
    /// Renders a layered picture: the background pushes in slowly while the subject, cut out on a transparent
    /// background and a little larger than the frame, slides across in front of it.
    /// </summary>
    public static IReadOnlyList<string> ParallaxVideo(string background, string subject, TimeSpan length, ExportPreset preset, string output, string? captionsFile = null,
        Polish? polish = null)
    {
        var (w, h, fps) = preset;
        var frames = (int)Math.Round(length.TotalSeconds * fps);
        var (fw, fh) = (w * 106 / 100 / 2 * 2, h * 106 / 100 / 2 * 2); // even numbers, as the encoder needs
        var graph =
            $"[0:v]{PanZoom(preset, frames)}[bg];" +
            $"[1:v]format=rgba,scale={fw}:{fh}:force_original_aspect_ratio=increase,crop={fw}:{fh}[fg];" +
            $"[bg][fg]overlay=x='-(w-W)*t/{Seconds(length)}':y='-(h-H)/2'{Finish(length, captionsFile, preset, polish)}[v]";
        return
        [
            "-i", background, "-i", subject,
            "-filter_complex", graph, "-map", "[v]",
            "-t", Seconds(length), "-an", .. Encode(preset), "-y", output,
        ];
    }

    /// <summary>
    /// A slow camera move on a still picture: 0 pushes in, 1 pulls back, 2 and 3 drift right and left. Oversampling
    /// before zoompan avoids the stair-stepping it shows at native size.
    /// </summary>
    private static string PanZoom(ExportPreset p, int frames, int move = 0)
    {
        const string Centre = "iw/2-(iw/zoom/2)";
        var (zoom, x) = move switch
        {
            1 => ($"1.08-0.08*on/{frames}", Centre),
            2 => ("1.08", $"(iw-iw/zoom)*on/{frames}"),
            3 => ("1.08", $"(iw-iw/zoom)*(1-on/{frames})"),
            _ => ($"1+0.08*on/{frames}", Centre),
        };
        return $"scale={2 * p.Width}:{2 * p.Height}:force_original_aspect_ratio=increase,crop={2 * p.Width}:{2 * p.Height}," +
            $"zoompan=z='{zoom}':x='{x}':y='ih/2-(ih/zoom/2)':d={frames}:s={p.Width}x{p.Height}:fps={p.Fps}";
    }


    /// <summary>
    /// The last filters on every clip: the strap's glass and the captions over the picture, the film finish, then the
    /// fades over all of them, and a new beat's streak of light, which crosses even the dip to black.
    /// </summary>
    private static string Finish(TimeSpan length, string? captionsFile, ExportPreset preset, Polish? polish = null) =>
        (polish?.Strap is { Length: > 0 } strap ? Glass(preset, strap) : "") +
        (captionsFile is null ? "" : $",ass={captionsFile}:fontsdir=.") +
        (polish?.Grain == true ? FilmGrain : "") +
        $",fade=t=in:st=0:d={Seconds(Fade)},fade=t=out:st={Seconds(length - Fade)}:d={Seconds(Fade)}" +
        (polish?.NewBeat == true ? LightTrail(preset, polish.Accent) : "") +
        ",format=yuv420p";

    /// <summary>The brand kit's film finish: fine grain that moves from frame to frame, and corners a little darker.</summary>
    private const string FilmGrain = ",noise=alls=7:allf=t,vignette=PI/5";

    /// <summary>How long a new beat's streak of light takes to cross the frame.</summary>
    public static readonly TimeSpan TrailLength = TimeSpan.FromSeconds(0.5);

    // A streak of light in the series colour, white-hot near its soft head and fading along its tail, crossing the frame as a
    // new beat opens. Clips are joined by copying, so it is burned into the beat's first clip instead of a crossfade.
    private static string LightTrail(ExportPreset preset, string accent)
    {
        var (w, h, fps) = preset;
        var colour = Convert.FromHexString(accent.TrimStart('#'));
        var (tw, th) = (w * 3 / 5 / 2 * 2, Math.Max(8, h / 8 / 2 * 2));
        string Hot(byte value) => $"{value}+{255 - value}*pow(X/W,8)";
        return $"[unlit];color=c=black:s={tw}x{th}:r={fps}:d={Seconds(TrailLength)},format=rgba," +
            $"geq=r='{Hot(colour[0])}':g='{Hot(colour[1])}':b='{Hot(colour[2])}':a='255*pow(X/W,3)*(1-pow(X/W,40))*exp(-pow(6*Y/H-3,2))'[streak];" +
            $"[unlit][streak]overlay=x='-w+(W+w)*t/{Seconds(TrailLength)}':y=H*0.42-h/2:eof_action=pass";
    }

    // The strap's frosted pane: the picture behind it blurred, lifted and soft at its edges, coming and going with the
    // strap. Placed from the strap's style in AssCaptions (top left, 80 in at 1920 wide) and a rough width per letter,
    // so it runs a little past the strap's own box whatever the font.
    private static string Glass(ExportPreset preset, string strap)
    {
        var (playWidth, size) = preset.Portrait ? (1080, 52) : (1920, 44);
        var scale = (double)preset.Width / playWidth;
        int Px(double value) => Math.Max(2, (int)Math.Round(value * scale / 2) * 2);
        var (x, y) = (Px(54), Px(54)); // a little outside the strap's box, which starts 66 in
        var (w, h) = (Math.Min(Px(strap.Length * (0.55 * size + 2) + 52), preset.Width - x), Px(1.3 * size + 52));
        var end = AssCaptions.StrapLength;
        return $",split[clear][frost];[frost]trim=end={Seconds(end)},crop={w}:{h}:{x}:{y},gblur=sigma={Px(16)},drawbox=color=white@0.18:t=fill,format=yuva444p," +
            $"geq=lum='p(X,Y)':cb='p(X,Y)':cr='p(X,Y)':a='255*clip(min(min(X,W-1-X),min(Y,H-1-Y))/{Px(12)},0,1)'," +
            $"fade=t=in:d=0.2:alpha=1,fade=t=out:st={Seconds(end - TimeSpan.FromSeconds(0.3))}:d=0.3:alpha=1[pane];[clear][pane]overlay={x}:{y}:eof_action=pass";
    }

    // Identical for every clip: the join copies the pictures, which only works when they are encoded the same way.
    // ponytail: software x264 only. A native (not emulated) FFmpeg build is the bigger win on ARM machines.
    private static string[] Encode(ExportPreset preset) =>
    [
        .. Compression,
        "-r", preset.Fps.ToString(CultureInfo.InvariantCulture), "-video_track_timescale", "90000",
    ];

    /// <summary>How every picture the app makes is compressed. A clip's own frames use it too, before the clip is encoded.</summary>
    public static IReadOnlyList<string> Compression { get; } = ["-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p"];


    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);
}
