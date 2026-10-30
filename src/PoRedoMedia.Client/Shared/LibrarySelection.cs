using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Client.Shared;

/// <summary>
/// What the Library's drawer shows, shared between the page and the drawer: a side dialog is drawn
/// by the layout, outside the page, so it cannot take the page's parameters directly. The page
/// changes it; the drawer redraws.
/// </summary>
public sealed class LibrarySelection
{
    public MediaDto? Media { get; private set; }

    /// <summary>Every Library item, for the version tree.</summary>
    public IReadOnlyList<MediaDto> Family { get; private set; } = [];

    /// <summary>The panel on show, so page keys (P, C, Del) reach it.</summary>
    public MediaPanel? Panel { get; set; }

    public event Action? Changed;

    public void Show(MediaDto media, IReadOnlyList<MediaDto> family)
    {
        if (ReferenceEquals(Media, media) && ReferenceEquals(Family, family))
            return;

        (Media, Family) = (media, family);
        Changed?.Invoke();
    }
}
