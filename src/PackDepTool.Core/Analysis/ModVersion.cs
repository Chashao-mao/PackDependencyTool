namespace PackDepTool.Core.Analysis;

/// <summary>
/// Lenient version comparer for mod metadata. Handles semver-ish strings, Forge build
/// suffixes and Minecraft versions without throwing on unexpected input.
/// </summary>
public static class ModVersion
{
    public static int Compare(string? left, string? right)
    {
        var a = Tokenize(left);
        var b = Tokenize(right);
        var count = Math.Max(a.Count, b.Count);
        for (var i = 0; i < count; i++)
        {
            var ta = i < a.Count ? a[i] : default;
            var tb = i < b.Count ? b[i] : default;

            if (ta.IsNumeric && tb.IsNumeric)
            {
                var cmp = ta.Number.CompareTo(tb.Number);
                if (cmp != 0) return Math.Sign(cmp);
                continue;
            }

            // A missing numeric part is lower ("1.0" < "1.0.0"). A missing
            // part compared with a trailing non-numeric token is a release
            // versus a pre-release, so the release sorts higher.
            if (i >= a.Count) return tb.IsNumeric ? -1 : 1;
            if (i >= b.Count) return ta.IsNumeric ? 1 : -1;

            if (ta.IsNumeric != tb.IsNumeric)
                return ta.IsNumeric ? -1 : 1;

            var text = string.Compare(ta.Text, tb.Text, StringComparison.OrdinalIgnoreCase);
            if (text != 0) return Math.Sign(text);
        }

        return 0;
    }

    public static bool TryCompare(string? left, string? right, out int result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        result = Compare(left, right);
        return true;
    }

    private readonly record struct Token(bool IsNumeric, long Number, string Text);

    private static List<Token> Tokenize(string? value)
    {
        var tokens = new List<Token>();
        if (string.IsNullOrWhiteSpace(value)) return tokens;

        var span = value.Trim();
        if (span.StartsWith('v') || span.StartsWith('V')) span = span[1..];

        var i = 0;
        while (i < span.Length)
        {
            var c = span[i];
            if (c is '.' or '-' or '+' or '_' or '~')
            {
                i++;
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < span.Length && char.IsDigit(span[i])) i++;
                var numberText = span[start..i];
                _ = long.TryParse(numberText, out var number);
                tokens.Add(new Token(true, number, numberText));
            }
            else
            {
                var start = i;
                while (i < span.Length && !char.IsDigit(span[i]) && span[i] is not ('.' or '-' or '+' or '_' or '~')) i++;
                tokens.Add(new Token(false, 0, span[start..i]));
            }
        }

        return tokens;
    }
}
