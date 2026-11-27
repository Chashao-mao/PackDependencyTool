using System.Buffers.Binary;
using System.Text;

namespace PackDepTool.Core.Analysis;

/// <summary>
/// Minimal class-file reader for Mixin annotations. It intentionally parses only
/// the constant pool and class-level runtime annotations, not the full class.
/// </summary>
public static class MixinAnnotationReader
{
    private const string MixinDescriptor = "Lorg/spongepowered/asm/mixin/Mixin;";

    public static IReadOnlyList<string> ReadTargets(byte[] classBytes)
    {
        if (classBytes.Length < 10 || ReadU4(classBytes, 0) != 0xCAFEBABE) return [];

        var offset = 8;
        var count = ReadU2(classBytes, offset);
        offset += 2;
        var utf8 = new Dictionary<int, string>();
        var classNames = new Dictionary<int, int>();
        for (var i = 1; i < count; i++)
        {
            var tag = classBytes[offset++];
            switch (tag)
            {
                case 1:
                    var length = ReadU2(classBytes, offset);
                    offset += 2;
                    utf8[i] = Encoding.UTF8.GetString(classBytes, offset, length);
                    offset += length;
                    break;
                case 3:
                case 4:
                    offset += 4;
                    break;
                case 5:
                case 6:
                    offset += 8;
                    i++;
                    break;
                case 7:
                    classNames[i] = ReadU2(classBytes, offset);
                    offset += 2;
                    break;
                case 8:
                case 16:
                case 19:
                case 20:
                    offset += 2;
                    break;
                case 9:
                case 10:
                case 11:
                case 12:
                case 17:
                case 18:
                    offset += 4;
                    break;
                case 15:
                    offset += 3;
                    break;
                default:
                    return [];
            }
        }

        offset += 6; // access, this, super
        var interfaces = ReadU2(classBytes, offset);
        offset += 2 + interfaces * 2;

        for (var member = 0; member < 2; member++)
        {
            var fields = ReadU2(classBytes, offset);
            offset += 2;
            for (var i = 0; i < fields; i++)
            {
                offset += 6; // access, name, descriptor
                offset = SkipAttributes(classBytes, offset);
            }
        }

        var resolvedClasses = classNames.ToDictionary(
            pair => pair.Key,
            pair => utf8.GetValueOrDefault(pair.Value, string.Empty));

        var attributes = ReadU2(classBytes, offset);
        offset += 2;
        var targets = new List<string>();
        for (var i = 0; i < attributes; i++)
        {
            var nameIndex = ReadU2(classBytes, offset);
            var length = (int)ReadU4(classBytes, offset + 2);
            var start = offset + 6;
            var name = utf8.GetValueOrDefault(nameIndex, string.Empty);
            if (name is "RuntimeVisibleAnnotations" or "RuntimeInvisibleAnnotations")
                ReadAnnotations(classBytes, start, start + length, utf8, resolvedClasses, targets);
            offset = start + length;
        }
        return targets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int SkipAttributes(byte[] data, int offset)
    {
        var count = ReadU2(data, offset);
        offset += 2;
        for (var i = 0; i < count; i++)
        {
            var length = (int)ReadU4(data, offset + 2);
            offset += 6 + length;
        }
        return offset;
    }

    private static void ReadAnnotations(
        byte[] data,
        int start,
        int end,
        Dictionary<int, string> utf8,
        Dictionary<int, string> classNames,
        List<string> targets)
    {
        var offset = start;
        if (end - offset < 2) return;
        var count = ReadU2(data, offset);
        offset += 2;
        for (var i = 0; i < count && offset + 4 <= end; i++)
            offset = ReadAnnotation(data, offset, end, utf8, classNames, targets, isRoot: true);
    }

    private static int ReadAnnotation(
        byte[] data,
        int offset,
        int end,
        Dictionary<int, string> utf8,
        Dictionary<int, string> classNames,
        List<string> targets,
        bool isRoot)
    {
        var typeIndex = ReadU2(data, offset);
        var type = utf8.GetValueOrDefault(typeIndex, string.Empty);
        offset += 2;
        var pairs = ReadU2(data, offset);
        offset += 2;
        var isMixin = string.Equals(type, MixinDescriptor, StringComparison.Ordinal);
        for (var i = 0; i < pairs && offset + 3 <= end; i++)
        {
            var name = utf8.GetValueOrDefault(ReadU2(data, offset), string.Empty);
            offset += 2;
            offset = ReadElement(data, offset, end, utf8, classNames, targets,
                isMixin && name is "targets" or "value");
        }
        return offset;
    }

    private static int ReadElement(
        byte[] data,
        int offset,
        int end,
        Dictionary<int, string> utf8,
        Dictionary<int, string> classNames,
        List<string> targets,
        bool collect)
    {
        if (offset >= end) return end;
        var tag = (char)data[offset++];
        switch (tag)
        {
            case 's':
                if (collect) AddTarget(utf8.GetValueOrDefault(ReadU2(data, offset), string.Empty), targets);
                return offset + 2;
            case 'c':
                if (collect) AddTarget(classNames.GetValueOrDefault(ReadU2(data, offset), string.Empty), targets);
                return offset + 2;
            case 'e':
                return offset + 4;
            case '@':
                return ReadAnnotation(data, offset, end, utf8, classNames, targets, isRoot: false);
            case '[':
                var count = ReadU2(data, offset);
                offset += 2;
                for (var i = 0; i < count; i++)
                    offset = ReadElement(data, offset, end, utf8, classNames, targets, collect);
                return offset;
            default:
                return offset + 2;
        }
    }

    private static void AddTarget(string value, List<string> targets)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var normalized = value.Trim();
        if (normalized.StartsWith('L') && normalized.EndsWith(';'))
            normalized = normalized[1..^1];
        normalized = normalized.Replace('/', '.');
        if (normalized.Length > 0 && !targets.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            targets.Add(normalized);
    }

    private static ushort ReadU2(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

    private static uint ReadU4(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
}
