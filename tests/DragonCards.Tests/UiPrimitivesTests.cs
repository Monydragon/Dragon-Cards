using DragonCards.Core;
using DragonCards.Desktop;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace DragonCards.Tests;

public sealed class UiPrimitivesTests
{
    [Fact]
    public void ScrollStateClampsEmptySingleAndOverflowCollections()
    {
        var scroll = new ScrollState();

        scroll.Configure(0, 5);
        Assert.Equal(0, scroll.Offset);
        Assert.Equal(0, scroll.MaxOffset);
        Assert.Equal(new VisibleRange(0, 0), scroll.VisibleRange);

        scroll.Configure(1, 5);
        Assert.False(scroll.ScrollBy(1));
        Assert.Equal(new VisibleRange(0, 1), scroll.VisibleRange);

        scroll.Configure(20, 5);
        Assert.True(scroll.SetOffset(500));
        Assert.Equal(15, scroll.Offset);
        Assert.Equal(new VisibleRange(15, 5), scroll.VisibleRange);
    }

    [Fact]
    public void ManualScrollIsPreservedUntilFocusMovementExplicitlyRequestsVisibility()
    {
        var scroll = new ScrollState();
        scroll.Configure(30, 6);
        scroll.ScrollBy(12);

        scroll.Configure(30, 6);
        Assert.Equal(12, scroll.Offset);
        Assert.Equal(12, scroll.VisibleRange.Clamp(2, 30));
        Assert.Equal(17, scroll.VisibleRange.Clamp(25, 30));

        Assert.True(scroll.EnsureVisible(2));
        Assert.Equal(2, scroll.Offset);
        Assert.True(scroll.VisibleRange.Contains(2));
    }

    [Fact]
    public void FocusStateOnlyAutoScrollsForKeyboardOrControllerMovement()
    {
        var scroll = new ScrollState();
        scroll.Configure(30, 5);
        var focus = new UiFocusState();
        focus.Configure(30);
        focus.Set(12, UiFocusOrigin.Programmatic);
        scroll.SetOffset(20);

        focus.Set(13, UiFocusOrigin.Pointer, scroll);
        Assert.Equal(20, scroll.Offset);

        focus.Move(1, UiFocusOrigin.Controller, scroll);
        Assert.Equal(14, focus.Index);
        Assert.True(scroll.VisibleRange.Contains(focus.Index));
    }

    [Fact]
    public void ScrollBarSupportsThumbDraggingAndTrackPaging()
    {
        var scroll = new ScrollState();
        scroll.Configure(100, 10);
        scroll.SetOffset(45);
        var bar = scroll.GetScrollBar(trackStart: 10, trackLength: 200, minimumThumbLength: 20);

        Assert.True(bar.CanScroll);
        Assert.Equal(20, bar.ThumbLength);
        var grabPoint = bar.ThumbStart + 5;
        Assert.True(scroll.BeginThumbDrag(grabPoint, bar));
        Assert.True(scroll.DragThumb(bar.TrackEnd - bar.ThumbLength + 5, bar));
        Assert.Equal(scroll.MaxOffset, scroll.Offset);
        scroll.EndThumbDrag();

        var endBar = scroll.GetScrollBar(10, 200, 20);
        Assert.True(scroll.ClickTrack(endBar.ThumbStart - 1, endBar));
        Assert.Equal(scroll.MaxOffset - scroll.ViewportLineCount, scroll.Offset);
    }

    [Fact]
    public void ListViewReturnsOnlyRealRowsAndRejectsRowGaps()
    {
        var scroll = new ScrollState();
        var layout = ListViewLayout.Create(new Rectangle(10, 20, 300, 150), 10, 44, 6, scroll);

        Assert.Equal(new VisibleRange(0, 3), layout.VisibleItems);
        Assert.True(layout.TryGetItemAt(new Point(20, 25), out var first));
        Assert.Equal(0, first);
        Assert.False(layout.TryGetItemAt(new Point(20, 65), out _));
        Assert.Equal(new Rectangle(10, 70, 300, 44), layout.ItemBounds(1));
    }

    [Fact]
    public void GridViewVirtualizesRowsAndKeepsControllerFocusVisible()
    {
        var scroll = new ScrollState();
        var viewport = new Rectangle(0, 0, 650, 210);
        var layout = GridViewLayout.Create(viewport, 50, 6, 100, 100, 10, 10, scroll);
        scroll.SetOffset(3);
        layout = GridViewLayout.Create(viewport, 50, 6, 100, 100, 10, 10, scroll);

        Assert.Equal(18, layout.FirstVisibleItem);
        Assert.Equal(12, layout.VisibleItemCount);
        Assert.Equal(new Rectangle(0, 0, 100, 100), layout.ItemBounds(18));
        Assert.Equal(17, layout.MoveFocus(11, 0, 1));

        Assert.True(layout.EnsureItemVisible(49));
        Assert.True(scroll.VisibleRange.Contains(49 / 6));
    }

    [Fact]
    public void HorizontalStripMapsVisibleSlotsBackToRealHandIndexes()
    {
        var scroll = new ScrollState();
        var viewport = new Rectangle(100, 200, 490, 120);
        var layout = HorizontalStripLayout.Create(viewport, 12, 50, 5, scroll, maximumVisibleItems: 9);

        Assert.True(layout.EnsureItemVisible(11));
        Assert.Equal(3, scroll.Offset);
        Assert.Equal(new Rectangle(100, 200, 50, 120), layout.ItemBounds(3));
        Assert.True(layout.TryGetItemAt(new Point(110, 220), out var realHandIndex));
        Assert.Equal(3, realHandIndex);
        Assert.False(layout.TryGetItemAt(new Point(152, 220), out _));
    }

    [Fact]
    public void InputRepeaterEmitsInitialPressThenHeldRepeats()
    {
        var repeater = new UiInputRepeater(initialDelaySeconds: 0.3, repeatIntervalSeconds: 0.1);

        var pressed = repeater.Update(UiAction.NavigateDown | UiAction.Confirm, 0);
        Assert.Equal(1, pressed.TriggerCount(UiAction.NavigateDown));
        Assert.Equal(1, pressed.TriggerCount(UiAction.Confirm));

        var waiting = repeater.Update(UiAction.NavigateDown | UiAction.Confirm, 0.2);
        Assert.False(waiting.Triggered(UiAction.NavigateDown));
        Assert.False(waiting.Triggered(UiAction.Confirm));

        var repeating = repeater.Update(UiAction.NavigateDown | UiAction.Confirm, 0.31);
        Assert.Equal(3, repeating.TriggerCount(UiAction.NavigateDown));
        Assert.False(repeating.Triggered(UiAction.Confirm));
        Assert.True((repeating.Repeated & UiAction.NavigateDown) != 0);

        var released = repeater.Update(UiAction.None, 0.01);
        Assert.True(released.WasReleased(UiAction.NavigateDown));
        Assert.True(released.WasReleased(UiAction.Confirm));

        repeater.Reset();
        Assert.Equal(1, repeater.Update(UiAction.PageNext, 0).TriggerCount(UiAction.PageNext));
        Assert.False(repeater.Update(UiAction.PageNext, 0.2).Triggered(UiAction.PageNext));
        Assert.Equal(3, repeater.Update(UiAction.PageNext, 0.31).TriggerCount(UiAction.PageNext));
    }

    [Fact]
    public void InputMapperCoversKeyboardNavigationAndHistory()
    {
        var keyboard = new KeyboardState(Keys.W, Keys.Tab, Keys.LeftShift, Keys.PageDown, Keys.L);

        var actions = UiInputMapper.Map(keyboard, default);

        Assert.True((actions & UiAction.NavigateUp) != 0);
        Assert.True((actions & UiAction.FocusPrevious) != 0);
        Assert.True((actions & UiAction.FocusNext) == 0);
        Assert.True((actions & UiAction.PageNext) != 0);
        Assert.True((actions & UiAction.History) != 0);
    }

    [Fact]
    public void InputMapperCoversGamepadNavigationActionsAndHistory()
    {
        var gamePad = new GamePadState(
            Vector2.Zero,
            Vector2.Zero,
            0f,
            0f,
            Buttons.DPadDown,
            Buttons.A,
            Buttons.RightShoulder,
            Buttons.Back);

        var actions = UiInputMapper.Map(default, gamePad);

        Assert.True((actions & UiAction.NavigateDown) != 0);
        Assert.True((actions & UiAction.Confirm) != 0);
        Assert.True((actions & UiAction.PageNext) != 0);
        Assert.True((actions & UiAction.History) != 0);
    }

    [Fact]
    public void NavigationBackClosesOverlayBeforeReturningToOrigin()
    {
        var navigation = new UiNavigationState<TestScreen, TestOverlay>(TestScreen.Store);
        navigation.NavigateTo(TestScreen.PackOpening);
        navigation.OpenOverlay(TestOverlay.CardDetail);

        var overlayBack = navigation.Back();
        Assert.Equal(UiBackResultKind.OverlayClosed, overlayBack.Kind);
        Assert.Equal(TestOverlay.CardDetail, overlayBack.Overlay);
        Assert.Equal(TestScreen.PackOpening, navigation.CurrentScreen);

        var screenBack = navigation.Back();
        Assert.Equal(UiBackResultKind.ScreenChanged, screenBack.Kind);
        Assert.Equal(TestScreen.Store, navigation.CurrentScreen);

        var noReverseLoop = navigation.Back();
        Assert.Equal(UiBackResultKind.None, noReverseLoop.Kind);
        Assert.Equal(TestScreen.Store, navigation.CurrentScreen);
    }

    [Fact]
    public void TextLayoutWrapsLongTokensAndCachesLayouts()
    {
        var layout = new TextLayoutCache(text => text.Length * 10f, capacity: 2);

        var first = layout.Wrap("alpha beta\nabcdefgh", maxWidth: 50);
        var cached = layout.Wrap("alpha beta\nabcdefgh", maxWidth: 50);

        Assert.Same(first, cached);
        Assert.Equal(["alpha", "beta", "abcde", "fgh"], first);
        Assert.Equal(1, layout.CachedLayoutCount);
        Assert.Equal("al...", layout.Ellipsize("alphabet", maxWidth: 50, ellipsis: "..."));
    }

    [Fact]
    public void ThemeDefinesAccessibleTargetAndDistinctInteractiveStates()
    {
        Assert.True(UiTheme.MinimumTargetSize >= 44);
        var target = UiTheme.MinimumHitTarget(new Rectangle(100, 100, 24, 30));
        Assert.True(target.Width >= 44);
        Assert.True(target.Height >= 44);
        Assert.Equal(new Point(112, 115), target.Center);
        Assert.NotEqual(
            UiTheme.ControlPalette(UiControlState.None),
            UiTheme.ControlPalette(UiControlState.Hovered));
        Assert.NotEqual(
            UiTheme.ControlPalette(UiControlState.Focused),
            UiTheme.ControlPalette(UiControlState.Disabled));
    }

    [Fact]
    public void StoreFiltersPreserveCatalogOrderAndSupportSearchElementRaritySetAndEmptyStates()
    {
        var data = GameData.LoadDefault();
        var catalog = ShopCatalogService.CreateCatalog(data);
        var expected = catalog
            .Where(item => item.Kind == ShopItemKind.SingleCard &&
                data.CardsById.TryGetValue(item.CardId, out var card) &&
                card.Elements.Contains("Fire", StringComparer.OrdinalIgnoreCase) &&
                card.Rarity.Equals(CardRarities.Common, StringComparison.OrdinalIgnoreCase) &&
                card.SetId.Equals("elemental-ascension", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var filtered = StoreCatalogFilter.Apply(
            catalog,
            data,
            ShopItemKind.SingleCard,
            search: "a",
            element: "Fire",
            rarity: CardRarities.Common,
            setId: "elemental-ascension");

        Assert.Equal(expected.Where(item => item.Name.Contains("a", StringComparison.OrdinalIgnoreCase)).Select(item => item.Id), filtered.Select(item => item.Id));
        Assert.Empty(StoreCatalogFilter.Apply(catalog, data, ShopItemKind.SingleCard, search: "no-card-matches-this"));
        Assert.Equal(4, StoreCatalogFilter.Apply(catalog, data, ShopItemKind.Booster).Count);
    }

    [Theory]
    [InlineData(1280, 720, 0, 0, 1280, 720, 0.8f)]
    [InlineData(1440, 900, 0, 45, 1440, 810, 0.9f)]
    [InlineData(1600, 900, 0, 0, 1600, 900, 1f)]
    [InlineData(1920, 1080, 0, 0, 1920, 1080, 1.2f)]
    public void AuthoredCanvasAspectFitsSupportedWindowSizes(
        int width,
        int height,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight,
        float expectedScale)
    {
        var viewport = AspectFitViewport.Calculate(width, height, 1600, 900);

        Assert.Equal(new Rectangle(expectedX, expectedY, expectedWidth, expectedHeight), viewport.Rectangle);
        Assert.Equal(expectedScale, viewport.Scale, precision: 3);
        Assert.Equal(new Point(800, 450), viewport.ToVirtual(viewport.Rectangle.Center));
    }

    private enum TestScreen
    {
        Store,
        PackOpening
    }

    private enum TestOverlay
    {
        CardDetail
    }
}
