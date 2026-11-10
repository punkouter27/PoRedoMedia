namespace PoRedoMedia.Shared.Models;

// Wire types for Create Movie. The movie engine's own enums (series, shape, look, picture type) travel as their
// names, and the server lists the choices with their labels, so the browser never has to know them.

/// <summary>One choice in a list: the value sent back, the words shown, and a line of help.</summary>
public sealed record MovieOption(string Value, string Label, string? Hint = null);

/// <summary>One series a movie can belong to, with the shapes it can take and what its form calls things.</summary>
/// <param name="Structures">Its shapes; each hint is the shape's beats in order.</param>
/// <param name="FilmsRequired">The series is built on the owner's films, so a script is not written without one.</param>
public sealed record MovieShowDto(
    string Value, string Label, string Hint, MovieOption[] Structures,
    string TopicLabel, string TopicPlaceholder, string SecondLabel, string SecondPlaceholder, bool FilmsRequired);

/// <summary>What Create Movie can do on this server, and the choices it offers.</summary>
/// <param name="Ffmpeg">FFmpeg was found. Without it nothing can be drawn, previewed or rendered.</param>
/// <param name="Script">A script model is connected; otherwise scripts are stand-in words.</param>
/// <param name="Voice">A voice service is connected; otherwise the voice is a stand-in tone.</param>
/// <param name="Pictures">Pictures can be generated.</param>
/// <param name="DraftPictures">The picture model draws cheaper drafts for the asking.</param>
/// <param name="Notice">What is simulated or missing, in words fit to show; null when everything is connected.</param>
/// <param name="Memes">Meme-ify runs here: every film and AI video clip goes through it before the movie is rendered.</param>
/// <param name="MemePrice">About what Meme-ify costs for one clip, in dollars.</param>
/// <param name="ServerSpeech">The server can hear what is said in a film; when false, the browser's own speech model does it.</param>
public sealed record MovieConfigDto(
    bool Ffmpeg, bool Script, bool Voice, bool Pictures, bool DraftPictures, bool YouTube,
    MovieShowDto[] Shows, MovieOption[] Looks, MovieOption[] Kinds, MovieOption[] CaptionPresets, MovieOption[] Layouts,
    MovieOption[] ExportPresets, MovieIntroStart[] IntroStarts, string? Notice, int MaxFilms, int MaxTopicLength,
    bool Memes = false, decimal MemePrice = 0, int MaxClipSeconds = 60, int MaxMovieSeconds = 600, bool ServerSpeech = true);

/// <summary>A ready-made colour and layout for the intro.</summary>
public sealed record MovieIntroStart(string Name, string Accent, string Layout);

/// <summary>A series' brand kit: what every movie of the series shares.</summary>
/// <param name="LogoUrl">The kit's own picture or sound, when it has one; null means the app's made-up one is used.</param>
public sealed record MovieKitDto(
    string Show, string ChannelName, string Tagline, int OutroSeconds, bool Grain, string Structure, string Look, string Layout,
    string CaptionPreset, string Accent, int FontSize, int Stroke,
    string? LogoUrl, string? MascotUrl, string? StingUrl, string? MusicUrl, string? MusicName, string? StingName,
    int NextNumber, bool HasHostDesign);

/// <summary>The kit's editable fields, sent whole.</summary>
public sealed record MovieKitUpdate(
    string ChannelName, string Tagline, int OutroSeconds, bool Grain, string Structure, string Look, string Layout,
    string CaptionPreset, string Accent, int FontSize, int Stroke);

/// <summary>One saved movie as the list shows it.</summary>
public sealed record MovieSummaryDto(string Id, string Title, string Show, string Details, string? ThumbUrl, DateTimeOffset Saved, int Number, bool Damaged);

public sealed record MovieCaptionsDto(string Preset, string Accent, int FontSize, int Stroke);

/// <summary>One clip of a movie, with everything its card and the inspector show.</summary>
/// <param name="Tiers">The depths the clip has words for already; another is written when first asked for.</param>
/// <param name="Seconds">How long it runs: measured when <paramref name="Measured"/>, estimated from its words otherwise.</param>
/// <param name="Review">The editor's note on its words, when the read of the script left one.</param>
/// <param name="FilmUrl">The owner's own film, when the clip shows one.</param>
/// <param name="EarlierTakes">A picture for each earlier take that can be gone back to, oldest first.</param>
public sealed record MovieClipDto(
    Guid Id, int Number, string Title, string? Beat, string Tier, string[] Tiers, string Dialogue, string VisualPrompt,
    string Kind, string KindLabel, bool KindByHand, bool HostVisible, bool HasFilm, bool OwnSound, bool RobotView,
    bool Stale, bool Draft, double Seconds, bool Measured, string Summary, string[] Warnings, string? Review,
    string? ThumbUrl, string? FilmUrl, string[] EarlierTakes, bool HasPicture, bool Memed = false, bool CanMeme = false, bool MemesOn = true);

/// <summary>A moment of a finished video where a clip or a bookend starts.</summary>
public sealed record MovieMarkDto(string Title, double Start, bool Bookend);

/// <summary>One word of the narration and when it is said, from the start of the video.</summary>
public sealed record MovieWordDto(string Text, double Start, double Length);

/// <summary>The quick preview: the small video, where each clip starts in it, and its words for captions drawn live.</summary>
public sealed record MoviePreviewDto(string Url, MovieMarkDto[] Marks, MovieWordDto[] Words, double Seconds);

public sealed record MovieTeaserMomentDto(int Clip, double Start, double Length);

/// <summary>A ten-second upright teaser. <paramref name="Clip"/> numbers count from 1.</summary>
public sealed record MovieTeaserDto(string Hook, bool Keep, bool Fill, MovieTeaserMomentDto[] Moments);

/// <summary>What goes with the finished video when it is uploaded.</summary>
public sealed record MoviePublishDto(
    string? MasterUrl, string? MasterName, string? MasterSize, string Title, string Description, string Tags, string Chapters,
    string? ThumbnailUrl, string[] ThumbnailChoices, string[] TeaserUrls, string? VideoLink, Guid? LibraryId);

/// <summary>A movie as it is now: its script, its clips, and what has been made of it.</summary>
/// <param name="PicturesToMake">Clips still waiting for a picture.</param>
/// <param name="OffSeriesLook">Its look, captions or music have moved away from the series' own.</param>
public sealed record MovieDto(
    string Id, string Title, string Topic, int Number, string Show, string Structure, string Look, MovieCaptionsDto Captions,
    MovieClipDto[] Clips, double RuntimeSeconds, string Meta, bool CanUndo, bool CanRedo, string? MusicName,
    int PicturesToMake, bool HasDraftPictures, bool OffSeriesLook, MoviePreviewDto? Preview, MoviePublishDto Publish,
    MovieTeaserDto[] Teasers, string[] SpendLines);

/// <summary>The one long job a user has under way, or the one that just ended.</summary>
/// <param name="State">"running", "done", "stopped" or "failed".</param>
/// <param name="ClipProgress">While drawing or rendering, how far each clip has got, 0 to 1, in the movie's order.</param>
/// <param name="GoTo">The step to show once the job is done, when it leads somewhere.</param>
public sealed record MovieJobDto(
    int Id, string Title, string Detail, double Fraction, string Time, string State, string? MovieId, string? Message, string? Error,
    string[] Skeletons, double[]? ClipProgress, string? GoTo);

/// <summary>What comes back from anything that changes a movie: the movie, and what to tell the user.</summary>
public sealed record MovieReply(MovieDto? Movie, string? Status = null, string? Error = null, string? Url = null);

/// <summary>One of the owner's films, picked from the Library, with what it shows.</summary>
public sealed record MovieFilmDto(Guid MediaId, string Note, bool OwnSound);

/// <summary>What a new movie is written from.</summary>
public sealed record MovieLessonRequest(
    string Show, string Structure, string Topic, string Second, int Number, MovieFilmDto[] Films, string? ExportPreset = null);

/// <summary>What a model saw in one film.</summary>
public sealed record MovieWatchReply(string? Note, string? Timeline, string? Error);

/// <summary>What a job would make and cost, shown before anything is paid for.</summary>
/// <param name="Dollars">Null when the price is not known.</param>
/// <param name="Refusal">Why it cannot go ahead (the month's budget), or null.</param>
public sealed record MovieEstimateDto(string Summary, decimal? Dollars, bool NothingToDo, string? Refusal);

/// <summary>
/// One change to a movie. <paramref name="Op"/> names it; the other fields are those that change needs. Every edit
/// is undoable.
/// </summary>
public sealed record MovieEdit(
    string Op, Guid? ClipId = null, string? Text = null, bool? Flag = null, int? Index = null, Guid[]? Order = null,
    MovieCaptionsDto? Captions = null);

public sealed record MoviePrefsDto(decimal MonthlyBudget, bool DraftPictures, string ExportPreset, decimal MonthSpent);

public sealed record MovieUploadRequest(string Title, string Description, string Tags, string Privacy, DateTimeOffset? PublishAt, bool Shorts);

public sealed record MovieTeasersUpdate(MovieTeaserDto[] Teasers);
