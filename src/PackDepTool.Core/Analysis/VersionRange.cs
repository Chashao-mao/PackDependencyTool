using System.Globalization;

namespace PackDepTool.Core.Analysis;

/// <summary>
/// Evaluates the version range dialects used by Fabric/Quilt (<c>&gt;=1.0 &lt;2.0</c>,
/// <c>~1.2</c>, <c>^1.2.3</c>, <c>1.2.x</c>) and Forge/NeoForge/Maven
/// (<c>[1.20,1.21)</c>, <c>[1.20]</c>, <c>[1.20,)</c>).
/// Returns null ("unknown") instead of guessing when the range cannot be parsed,
/// so the validator never reports a fabricated mismatch.
/// </summary>
public static class VersionRange
{
    public static bool? Satisfies(string? range, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        if (version.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            version.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.IsNullOrWhiteSpace(range)) return true;

        var text = range.Trim();
        if (text.Contains("${", StringComparison.Ordinal) ||
            text.Equals("TODO", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("TBD", StringComparison.OrdinalIgnoreCase))
            return null;
        if (text is "*" or "x" or "X" or "any" or "latest" or "-")
            return true;

        // OR groups: "1.20.x || 1.21.x"
        if (text.Contains("||", StringComparison.Ordinal))
        {
            bool? any = false;
            foreach (var part in text.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var result = Satisfies(part, version);
                if (result == true) return true;
                if (result == null) any = null;
            }
            return any;
        }

        if (text[0] is '[' or '(')
            return SatisfiesMavenList(text, version);

        // "1.20.1,1.20.2" style alternative lists (some metadata uses commas).
        if (text.Contains(',') && !HasComparisonOperator(text))
        {
            bool? any = false;
            foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var result = Satisfies(part, version);
                if (result == true) return true;
                if (result == null) any = null;
            }
            return any;
        }

        if (text[0] is '~' or '^' or '<' or '>' or '=' or '!')
            return SatisfiesPredicates(text, version);

        // A plain version behaves like an exact/prefix requirement.
        return SatisfiesPlain(text, version);
    }

    /// <summary>
    /// Minecraft-specific evaluation. NeoForge/Forge metadata often uses a patch-level
    /// exclusive upper bound such as [1.21,1.21.1) for a jar actually built for 1.21.1.
    /// Treat that exact upper bound as inclusive so the UI does not report a false mismatch.
    /// Dependency mod version ranges still use the strict <see cref="Satisfies"/>.
    /// </summary>
    public static bool? SatisfiesMinecraft(string? range, string? version)
    {
        var result = Satisfies(range, version);
        if (result != false) return result;
        if (!TryGetPatchExclusiveUpper(range, out var lower, out var upper)) return false;
        if (!IsPatchVersion(version) || !IsPatchVersion(upper)) return false;
        if (Compare(version, upper) != 0) return false;
        return Compare(lower, upper) < 0;
    }

    private static bool TryGetPatchExclusiveUpper(string? range, out string lower, out string upper)
    {
        lower = string.Empty;
        upper = string.Empty;
        if (string.IsNullOrWhiteSpace(range)) return false;
        var text = range.Trim();
        if (text.Length < 5 || text[0] is not ('[' or '(') || text[^1] != ')') return false;
        if (text.Contains("||", StringComparison.Ordinal)) return false;

        var inner = text[1..^1];
        var comma = inner.IndexOf(',');
        if (comma < 0) return false;
        lower = inner[..comma].Trim();
        upper = inner[(comma + 1)..].Trim();
        return lower.Length > 0 && upper.Length > 0 && !upper.Contains(',');
    }

    private static bool IsPatchVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.TrimStart('v', 'V').Split('.');
        return parts.Length >= 3 &&
               int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
               int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }

    private static bool? SatisfiesPredicates(string text, string version)
    {
        var parts = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;

        bool? aggregate = true;
        foreach (var raw in parts)
        {
            var result = SatisfiesPredicate(raw, version);
            if (result == false) return false;
            if (result == null) aggregate = null;
        }
        return aggregate;
    }

    private static bool? SatisfiesPredicate(string token, string version)
    {
        token = token.Trim();
        if (token.Length == 0) return true;

        if (token[0] is '[' or '(')
            return SatisfiesMavenList(token, version);

        if (token.StartsWith(">=", StringComparison.Ordinal))
            return Compare(version, token[2..]) is int ge && ge >= 0;
        if (token.StartsWith("<=", StringComparison.Ordinal))
            return Compare(version, token[2..]) is int le && le <= 0;
        if (token.StartsWith("!=", StringComparison.Ordinal))
            return Compare(version, token[2..]) is int ne && ne != 0;
        if (token.StartsWith("==", StringComparison.Ordinal))
            return SatisfiesPlain(token[2..], version);
        if (token.StartsWith(">", StringComparison.Ordinal))
            return Compare(version, token[1..]) is int gt && gt > 0;
        if (token.StartsWith("<", StringComparison.Ordinal))
            return Compare(version, token[1..]) is int lt && lt < 0;
        if (token.StartsWith("=", StringComparison.Ordinal))
            return SatisfiesPlain(token[1..], version);
        if (token.StartsWith("^", StringComparison.Ordinal))
            return SatisfiesCaret(token[1..], version);
        if (token.StartsWith("~", StringComparison.Ordinal))
            return SatisfiesTilde(token[1..], version);

        return SatisfiesPlain(token, version);
    }

    private static bool? SatisfiesCaret(string baseVersion, string version)
    {
        baseVersion = baseVersion.Trim();
        if (!TryParseCore(baseVersion, out var major, out var minor, out var patch))
            return null;

        string upper = major > 0
            ? $"{major + 1}.0.0"
            : minor > 0
                ? $"0.{minor + 1}.0"
                : $"0.0.{patch + 1}";

        var lowerCmp = Compare(version, baseVersion);
        var upperCmp = Compare(version, upper);
        if (lowerCmp is null || upperCmp is null) return null;
        return lowerCmp >= 0 && upperCmp < 0;
    }

    private static bool? SatisfiesTilde(string baseVersion, string version)
    {
        baseVersion = baseVersion.Trim();
        if (!TryParseCore(baseVersion, out var major, out var minor, out _))
            return null;

        var upper = minor > 0 ? $"{major}.{minor + 1}.0" : $"{major + 1}.0.0";
        var lowerCmp = Compare(version, baseVersion);
        var upperCmp = Compare(version, upper);
        if (lowerCmp is null || upperCmp is null) return null;
        return lowerCmp >= 0 && upperCmp < 0;
    }

    private static bool? SatisfiesPlain(string requirement, string version)
    {
        requirement = requirement.Trim();
        if (requirement.Length == 0) return true;

        var wildcard = requirement.IndexOfAny(['x', 'X', '*']);
        if (wildcard >= 0)
        {
            var prefix = requirement[..wildcard].TrimEnd('.', '-', '_');
            if (prefix.Length == 0) return true;
            var parts = prefix.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var normalized = string.Join('.', parts);
            var versionNormalized = version.TrimStart('v', 'V');
            if (versionNormalized.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return true;
            return versionNormalized.StartsWith(normalized + ".", StringComparison.OrdinalIgnoreCase);
        }

        var cmp = Compare(version, requirement);
        if (cmp is null) return null;
        if (cmp == 0) return true;

        // Accept build/pre-release suffixes of the same version ("1.20.1" ~ "1.20.1-47.2.0").
        var req = requirement.TrimStart('v', 'V');
        var ver = version.TrimStart('v', 'V');
        if (ver.Length > req.Length && ver.StartsWith(req, StringComparison.OrdinalIgnoreCase))
        {
            var separator = ver[req.Length];
            if (separator is '-' or '+' or '_' or '.') return true;
        }

        return false;
    }

    private static bool? SatisfiesMavenList(string text, string version)
    {
        var index = 0;
        bool? aggregate = false;
        var consumed = false;

        while (index < text.Length)
        {
            while (index < text.Length && (char.IsWhiteSpace(text[index]) || text[index] == ',')) index++;
            if (index >= text.Length) break;

            if (text[index] is '[' or '(')
            {
                var open = text[index];
                var closeIndex = FindRangeEnd(text, index);
                if (closeIndex < 0) return null;
                var close = text[closeIndex];
                var inner = text[(index + 1)..closeIndex];
                var result = EvaluateMavenRange(open, close, inner, version);
                if (result == true) return true;
                if (result == null) aggregate = null;
                consumed = true;
                index = closeIndex + 1;
                continue;
            }

            // A bare token inside a comma separated list.
            var next = text.IndexOfAny([',', ' '], index);
            var raw = next < 0 ? text[index..] : text[index..next];
            var plain = SatisfiesPlain(raw, version);
            if (plain == true) return true;
            if (plain == null) aggregate = null;
            consumed = true;
            index = next < 0 ? text.Length : next + 1;
        }

        if (!consumed) return null;
        return aggregate;
    }

    private static int FindRangeEnd(string text, int openIndex)
    {
        for (var i = openIndex + 1; i < text.Length; i++)
        {
            if (text[i] is ']' or ')') return i;
        }
        return -1;
    }

    private static bool? EvaluateMavenRange(char open, char close, string inner, string version)
    {
        var bounds = inner.Split(',');
        if (bounds.Length == 1)
        {
            var exact = bounds[0].Trim();
            if (exact.Length == 0) return null;
            return open == '[' && close == ']' ? SatisfiesPlain(exact, version) : null;
        }

        if (bounds.Length != 2) return null;

        var lower = bounds[0].Trim();
        var upper = bounds[1].Trim();

        if (lower.Length > 0)
        {
            var cmp = Compare(version, lower);
            if (cmp is null) return null;
            var lowerInclusive = open == '[';
            if (lowerInclusive ? cmp < 0 : cmp <= 0) return false;
        }

        if (upper.Length > 0)
        {
            var cmp = Compare(version, upper);
            if (cmp is null) return null;
            var upperInclusive = close == ']';
            if (upperInclusive ? cmp > 0 : cmp >= 0) return false;
        }

        return true;
    }

    private static bool HasComparisonOperator(string text) =>
        text.IndexOfAny(['<', '>', '=', '~', '^', '*', 'x', 'X']) >= 0;

    private static int? Compare(string version, string requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement)) return null;
        return ModVersion.Compare(version, requirement);
    }

    private static bool TryParseCore(string value, out int major, out int minor, out int patch)
    {
        major = minor = patch = 0;
        value = value.TrimStart('v', 'V');
        if (value.Length == 0) return false;

        var parts = value.Split(['.', '-', '+', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major))
            return false;
        if (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minor))
            minor = 0;
        if (parts.Length > 2 && !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out patch))
            patch = 0;
        return true;
    }
}
