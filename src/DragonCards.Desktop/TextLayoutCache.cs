namespace DragonCards.Desktop;

internal sealed class TextLayoutCache
{
    private readonly Func<string, float> _measureWidth;
    private readonly Dictionary<TextLayoutKey, string[]> _wrappedLines = [];
    private readonly Queue<TextLayoutKey> _insertionOrder = [];

    public TextLayoutCache(Func<string, float> measureWidth, int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(measureWidth);
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _measureWidth = measureWidth;
        Capacity = capacity;
    }

    public int Capacity { get; }
    public int CachedLayoutCount => _wrappedLines.Count;

    public IReadOnlyList<string> Wrap(string text, float maxWidth, float scale = 1f)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxWidth <= 0 || scale <= 0 || float.IsNaN(maxWidth) || float.IsNaN(scale))
        {
            return [];
        }

        var key = new TextLayoutKey(text, maxWidth, scale);
        if (_wrappedLines.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var lines = WrapCore(text, maxWidth, scale).ToArray();
        while (_wrappedLines.Count >= Capacity && _insertionOrder.TryDequeue(out var oldest))
        {
            _wrappedLines.Remove(oldest);
        }

        _wrappedLines.Add(key, lines);
        _insertionOrder.Enqueue(key);
        return lines;
    }

    public string Ellipsize(string text, float maxWidth, float scale = 1f, string ellipsis = "...")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(ellipsis);
        if (maxWidth <= 0 || scale <= 0 || !Fits(ellipsis, maxWidth, scale))
        {
            return string.Empty;
        }

        if (Fits(text, maxWidth, scale))
        {
            return text;
        }

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var midpoint = (low + high + 1) / 2;
            var candidate = $"{text[..midpoint].TrimEnd()}{ellipsis}";
            if (Fits(candidate, maxWidth, scale))
            {
                low = midpoint;
            }
            else
            {
                high = midpoint - 1;
            }
        }

        return $"{text[..low].TrimEnd()}{ellipsis}";
    }

    public static int VisibleLineCount(int viewportHeight, int lineHeight) =>
        viewportHeight <= 0 || lineHeight <= 0 ? 0 : Math.Max(1, viewportHeight / lineHeight);

    public void Clear()
    {
        _wrappedLines.Clear();
        _insertionOrder.Clear();
    }

    private IEnumerable<string> WrapCore(string text, float maxWidth, float scale)
    {
        var paragraphs = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        foreach (var paragraph in paragraphs)
        {
            if (string.IsNullOrWhiteSpace(paragraph))
            {
                yield return string.Empty;
                continue;
            }

            var currentLine = string.Empty;
            foreach (var word in paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = currentLine.Length == 0 ? word : $"{currentLine} {word}";
                if (Fits(candidate, maxWidth, scale))
                {
                    currentLine = candidate;
                    continue;
                }

                if (currentLine.Length > 0)
                {
                    yield return currentLine;
                    currentLine = string.Empty;
                }

                if (Fits(word, maxWidth, scale))
                {
                    currentLine = word;
                    continue;
                }

                var pieces = BreakWord(word, maxWidth, scale).ToArray();
                for (var index = 0; index < pieces.Length - 1; index++)
                {
                    yield return pieces[index];
                }

                currentLine = pieces[^1];
            }

            if (currentLine.Length > 0)
            {
                yield return currentLine;
            }
        }
    }

    private IEnumerable<string> BreakWord(string word, float maxWidth, float scale)
    {
        var start = 0;
        while (start < word.Length)
        {
            var length = 1;
            while (start + length < word.Length && Fits(word.Substring(start, length + 1), maxWidth, scale))
            {
                length++;
            }

            yield return word.Substring(start, length);
            start += length;
        }
    }

    private bool Fits(string text, float maxWidth, float scale) => _measureWidth(text) * scale <= maxWidth;

    private readonly record struct TextLayoutKey(string Text, float MaxWidth, float Scale);
}
