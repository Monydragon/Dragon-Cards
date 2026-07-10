namespace DragonCards.Desktop;

internal enum UiBackResultKind
{
    None,
    OverlayClosed,
    ScreenChanged
}

internal readonly record struct UiBackResult<TScreen, TOverlay>(
    UiBackResultKind Kind,
    TScreen Screen,
    TOverlay Overlay);

internal sealed class UiNavigationState<TScreen, TOverlay>
    where TScreen : notnull
    where TOverlay : notnull
{
    private readonly Stack<TScreen> _origins = [];
    private readonly Stack<TOverlay> _overlays = [];

    public UiNavigationState(TScreen initialScreen)
    {
        CurrentScreen = initialScreen;
    }

    public TScreen CurrentScreen { get; private set; }
    public int OriginCount => _origins.Count;
    public int OverlayCount => _overlays.Count;
    public bool HasOverlay => _overlays.Count > 0;
    public TOverlay? CurrentOverlay => _overlays.TryPeek(out var overlay) ? overlay : default;

    public void NavigateTo(TScreen destination, bool rememberOrigin = true)
    {
        if (EqualityComparer<TScreen>.Default.Equals(CurrentScreen, destination))
        {
            return;
        }

        if (rememberOrigin)
        {
            _origins.Push(CurrentScreen);
        }

        CurrentScreen = destination;
        _overlays.Clear();
    }

    public void ReplaceWith(TScreen destination)
    {
        CurrentScreen = destination;
        _overlays.Clear();
    }

    public void OpenOverlay(TOverlay overlay) => _overlays.Push(overlay);

    public bool TryCloseOverlay(out TOverlay overlay) => _overlays.TryPop(out overlay!);

    public UiBackResult<TScreen, TOverlay> Back()
    {
        if (_overlays.TryPop(out var overlay))
        {
            return new UiBackResult<TScreen, TOverlay>(UiBackResultKind.OverlayClosed, CurrentScreen, overlay);
        }

        if (_origins.TryPop(out var origin))
        {
            CurrentScreen = origin;
            return new UiBackResult<TScreen, TOverlay>(UiBackResultKind.ScreenChanged, CurrentScreen, default!);
        }

        return new UiBackResult<TScreen, TOverlay>(UiBackResultKind.None, CurrentScreen, default!);
    }

    public void Reset(TScreen screen)
    {
        CurrentScreen = screen;
        _origins.Clear();
        _overlays.Clear();
    }
}
