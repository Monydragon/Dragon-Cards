using System;

namespace DragonCards.Desktop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var captureScreens = args.Contains("--capture-screens", StringComparer.OrdinalIgnoreCase);
        var captureDirectory = GetOption(args, "--capture-dir");
        using var game = new DragonCardsGame(captureScreens, captureDirectory);
        game.Run();
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
