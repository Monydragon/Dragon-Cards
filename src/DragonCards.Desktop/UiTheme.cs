using Microsoft.Xna.Framework;

namespace DragonCards.Desktop;

[Flags]
internal enum UiControlState
{
    None = 0,
    Hovered = 1 << 0,
    Pressed = 1 << 1,
    Focused = 1 << 2,
    Selected = 1 << 3,
    Disabled = 1 << 4
}

internal readonly record struct UiControlPalette(Color Fill, Color Border, Color Text, Color Accent);

internal readonly record struct AspectFitViewport(Rectangle Rectangle, float Scale)
{
    public Point ToVirtual(Point point) => new(
        (int)MathF.Round((point.X - Rectangle.X) / Scale),
        (int)MathF.Round((point.Y - Rectangle.Y) / Scale));

    public static AspectFitViewport Calculate(int backBufferWidth, int backBufferHeight, int virtualWidth, int virtualHeight)
    {
        backBufferWidth = Math.Max(1, backBufferWidth);
        backBufferHeight = Math.Max(1, backBufferHeight);
        virtualWidth = Math.Max(1, virtualWidth);
        virtualHeight = Math.Max(1, virtualHeight);
        var scale = Math.Min(backBufferWidth / (float)virtualWidth, backBufferHeight / (float)virtualHeight);
        var width = (int)MathF.Round(virtualWidth * scale);
        var height = (int)MathF.Round(virtualHeight * scale);
        return new AspectFitViewport(
            new Rectangle((backBufferWidth - width) / 2, (backBufferHeight - height) / 2, width, height),
            scale);
    }
}

internal static class UiTheme
{
    public const int MinimumTargetSize = 44;
    public const int CompactSpacing = 8;
    public const int StandardSpacing = 12;
    public const int SectionSpacing = 20;
    public const int PanelPadding = 20;
    public const int ScrollBarWidth = 12;
    public const int MinimumScrollThumbLength = 20;

    public const float CompactTextScale = 0.52f;
    public const float BodyTextScale = 0.68f;
    public const float MenuTextScale = 0.82f;
    public const float HeadingTextScale = 1.08f;

    public static readonly Color Canvas = new(10, 14, 21);
    public static readonly Color Panel = new(19, 25, 34);
    public static readonly Color PanelRaised = new(27, 35, 47);
    public static readonly Color PanelInset = new(14, 19, 27);
    public static readonly Color Border = new(67, 82, 101);
    public static readonly Color BorderStrong = new(109, 130, 154);
    public static readonly Color Text = new(226, 233, 242);
    public static readonly Color TextMuted = new(166, 179, 196);
    public static readonly Color TextDisabled = new(101, 112, 127);
    public static readonly Color DragonGold = new(224, 172, 73);
    public static readonly Color Focus = new(118, 193, 255);
    public static readonly Color Selection = new(63, 111, 143);
    public static readonly Color Danger = new(210, 90, 82);
    public static readonly Color Success = new(88, 177, 123);
    public static readonly Color ScrollTrack = new(43, 53, 67);
    public static readonly Color ScrollThumb = new(133, 151, 174);
    public static readonly Color ScrollThumbHovered = new(171, 191, 216);

    public static Rectangle MinimumHitTarget(Rectangle rect)
    {
        var width = Math.Max(MinimumTargetSize, rect.Width);
        var height = Math.Max(MinimumTargetSize, rect.Height);
        return new Rectangle(
            rect.Center.X - width / 2,
            rect.Center.Y - height / 2,
            width,
            height);
    }

    public static UiControlPalette ControlPalette(UiControlState state)
    {
        if ((state & UiControlState.Disabled) != 0)
        {
            return new UiControlPalette(PanelInset, Border, TextDisabled, TextDisabled);
        }

        var fill = (state & UiControlState.Pressed) != 0
            ? new Color(43, 75, 96)
            : (state & UiControlState.Selected) != 0
                ? Selection
                : (state & UiControlState.Hovered) != 0
                    ? new Color(39, 51, 67)
                    : PanelRaised;
        var border = (state & UiControlState.Focused) != 0
            ? Focus
            : (state & UiControlState.Selected) != 0
                ? DragonGold
                : Border;
        var accent = (state & (UiControlState.Focused | UiControlState.Selected)) != 0
            ? DragonGold
            : BorderStrong;
        return new UiControlPalette(fill, border, Text, accent);
    }
}
