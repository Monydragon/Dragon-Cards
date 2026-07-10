using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace DragonCards.Desktop;

[Flags]
internal enum UiAction : uint
{
    None = 0,
    NavigateUp = 1u << 0,
    NavigateDown = 1u << 1,
    NavigateLeft = 1u << 2,
    NavigateRight = 1u << 3,
    FocusPrevious = 1u << 4,
    FocusNext = 1u << 5,
    Confirm = 1u << 6,
    Back = 1u << 7,
    PagePrevious = 1u << 8,
    PageNext = 1u << 9,
    MoveToStart = 1u << 10,
    MoveToEnd = 1u << 11,
    Secondary = 1u << 12,
    Tertiary = 1u << 13,
    History = 1u << 14
}

internal readonly struct UiActionFrame
{
    private readonly IReadOnlyDictionary<UiAction, int>? _triggerCounts;

    internal UiActionFrame(
        UiAction down,
        UiAction pressed,
        UiAction released,
        UiAction repeated,
        IReadOnlyDictionary<UiAction, int> triggerCounts)
    {
        Down = down;
        Pressed = pressed;
        Released = released;
        Repeated = repeated;
        _triggerCounts = triggerCounts;
    }

    public UiAction Down { get; }
    public UiAction Pressed { get; }
    public UiAction Released { get; }
    public UiAction Repeated { get; }

    public bool IsDown(UiAction action) => (Down & action) == action;
    public bool WasPressed(UiAction action) => (Pressed & action) == action;
    public bool WasReleased(UiAction action) => (Released & action) == action;
    public bool Triggered(UiAction action) => TriggerCount(action) > 0;

    public int TriggerCount(UiAction action) =>
        _triggerCounts is not null && _triggerCounts.TryGetValue(action, out var count) ? count : 0;
}

internal sealed class UiInputRepeater
{
    private static readonly UiAction[] RepeatableActions =
    [
        UiAction.NavigateUp,
        UiAction.NavigateDown,
        UiAction.NavigateLeft,
        UiAction.NavigateRight,
        UiAction.FocusPrevious,
        UiAction.FocusNext,
        UiAction.PagePrevious,
        UiAction.PageNext
    ];

    private readonly Dictionary<UiAction, double> _repeatCountdowns = [];
    private UiAction _previousDown;

    public UiInputRepeater(double initialDelaySeconds = 0.36, double repeatIntervalSeconds = 0.085)
    {
        if (initialDelaySeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelaySeconds));
        }

        if (repeatIntervalSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(repeatIntervalSeconds));
        }

        InitialDelaySeconds = initialDelaySeconds;
        RepeatIntervalSeconds = repeatIntervalSeconds;
    }

    public double InitialDelaySeconds { get; }
    public double RepeatIntervalSeconds { get; }

    public UiActionFrame Update(KeyboardState keyboard, GamePadState gamePad, double elapsedSeconds) =>
        Update(UiInputMapper.Map(keyboard, gamePad), elapsedSeconds);

    public UiActionFrame Update(UiAction down, double elapsedSeconds)
    {
        if (elapsedSeconds < 0 || double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        }

        var pressed = down & ~_previousDown;
        var released = _previousDown & ~down;
        var repeated = UiAction.None;
        var triggerCounts = new Dictionary<UiAction, int>();

        AddPressedTriggers(pressed, triggerCounts);

        foreach (var action in RepeatableActions)
        {
            if ((down & action) == 0)
            {
                _repeatCountdowns.Remove(action);
                continue;
            }

            if ((pressed & action) != 0)
            {
                _repeatCountdowns[action] = InitialDelaySeconds;
                continue;
            }

            var remaining = _repeatCountdowns.GetValueOrDefault(action, InitialDelaySeconds) - elapsedSeconds;
            if (remaining <= 0)
            {
                var repeatCount = 1 + (int)Math.Floor(-remaining / RepeatIntervalSeconds);
                triggerCounts[action] = repeatCount;
                repeated |= action;
                remaining += repeatCount * RepeatIntervalSeconds;
            }

            _repeatCountdowns[action] = remaining;
        }

        _previousDown = down;
        return new UiActionFrame(down, pressed, released, repeated, triggerCounts);
    }

    public void Reset()
    {
        _previousDown = UiAction.None;
        _repeatCountdowns.Clear();
    }

    private static void AddPressedTriggers(UiAction pressed, IDictionary<UiAction, int> triggerCounts)
    {
        for (var bit = 0; bit < 32; bit++)
        {
            var action = (UiAction)(1u << bit);
            if ((pressed & action) != 0)
            {
                triggerCounts[action] = 1;
            }
        }
    }
}

internal static class UiInputMapper
{
    private const float StickThreshold = 0.55f;

    public static UiAction Map(KeyboardState keyboard, GamePadState gamePad)
    {
        var actions = UiAction.None;
        var leftStick = gamePad.ThumbSticks.Left;

        Add(ref actions, UiAction.NavigateUp,
            keyboard.IsKeyDown(Keys.Up) || keyboard.IsKeyDown(Keys.W) ||
            gamePad.IsButtonDown(Buttons.DPadUp) || leftStick.Y >= StickThreshold);
        Add(ref actions, UiAction.NavigateDown,
            keyboard.IsKeyDown(Keys.Down) || keyboard.IsKeyDown(Keys.S) ||
            gamePad.IsButtonDown(Buttons.DPadDown) || leftStick.Y <= -StickThreshold);
        Add(ref actions, UiAction.NavigateLeft,
            keyboard.IsKeyDown(Keys.Left) || keyboard.IsKeyDown(Keys.A) ||
            gamePad.IsButtonDown(Buttons.DPadLeft) || leftStick.X <= -StickThreshold);
        Add(ref actions, UiAction.NavigateRight,
            keyboard.IsKeyDown(Keys.Right) || keyboard.IsKeyDown(Keys.D) ||
            gamePad.IsButtonDown(Buttons.DPadRight) || leftStick.X >= StickThreshold);

        var tabDown = keyboard.IsKeyDown(Keys.Tab);
        var shiftDown = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
        Add(ref actions, UiAction.FocusPrevious, tabDown && shiftDown);
        Add(ref actions, UiAction.FocusNext, tabDown && !shiftDown);
        Add(ref actions, UiAction.Confirm,
            keyboard.IsKeyDown(Keys.Enter) || keyboard.IsKeyDown(Keys.Space) || gamePad.IsButtonDown(Buttons.A));
        Add(ref actions, UiAction.Back,
            keyboard.IsKeyDown(Keys.Escape) || gamePad.IsButtonDown(Buttons.B));
        Add(ref actions, UiAction.PagePrevious,
            keyboard.IsKeyDown(Keys.PageUp) || gamePad.IsButtonDown(Buttons.LeftShoulder));
        Add(ref actions, UiAction.PageNext,
            keyboard.IsKeyDown(Keys.PageDown) || gamePad.IsButtonDown(Buttons.RightShoulder));
        Add(ref actions, UiAction.MoveToStart, keyboard.IsKeyDown(Keys.Home));
        Add(ref actions, UiAction.MoveToEnd, keyboard.IsKeyDown(Keys.End));
        Add(ref actions, UiAction.Secondary, gamePad.IsButtonDown(Buttons.X));
        Add(ref actions, UiAction.Tertiary, gamePad.IsButtonDown(Buttons.Y));
        Add(ref actions, UiAction.History,
            keyboard.IsKeyDown(Keys.L) || gamePad.IsButtonDown(Buttons.Back));
        return actions;
    }

    private static void Add(ref UiAction actions, UiAction action, bool isDown)
    {
        if (isDown)
        {
            actions |= action;
        }
    }
}

internal readonly record struct UiPointerFrame(
    Point Position,
    int WheelDelta,
    bool PrimaryDown,
    bool PrimaryPressed,
    bool PrimaryReleased)
{
    public static UiPointerFrame From(MouseState current, MouseState previous) =>
        new(
            current.Position,
            current.ScrollWheelValue - previous.ScrollWheelValue,
            current.LeftButton == ButtonState.Pressed,
            current.LeftButton == ButtonState.Pressed && previous.LeftButton == ButtonState.Released,
            current.LeftButton == ButtonState.Released && previous.LeftButton == ButtonState.Pressed);
}
