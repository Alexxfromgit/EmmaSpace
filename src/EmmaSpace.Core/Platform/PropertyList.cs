using System.Globalization;
using System.Xml;

namespace EmmaSpace.Core.Platform;

/// <summary>
/// Reads an XML property list (what macOS tools print with <c>-plist</c>) into plain .NET values: dictionaries become
/// <see cref="Dictionary{TKey,TValue}"/> of string to object, arrays <see cref="List{T}"/> of object, and the scalars
/// string, long, double and bool. Dates and data are kept as their text. DTDs are never fetched.
/// </summary>
internal static class PropertyList
{
    /// <summary>The root value; throws <see cref="FormatException"/> when the text is not a property list.</summary>
    public static object? Parse(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true, IgnoreComments = true });
            reader.MoveToContent();
            if (reader.Name != "plist") throw new FormatException("Not a property list.");
            if (reader.IsEmptyElement) return null;
            reader.Read();
            return reader.NodeType == XmlNodeType.EndElement ? null : ReadValue(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException("Not a property list.", ex);
        }
    }

    // Reads the element the reader is on and leaves it after that element's end.
    private static object? ReadValue(XmlReader reader)
    {
        if (reader.NodeType != XmlNodeType.Element) throw new FormatException($"Unexpected {reader.NodeType} in a property list.");
        var name = reader.Name;
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return name switch
            {
                "true" => true,
                "false" => false,
                "dict" => new Dictionary<string, object?>(),
                "array" => new List<object?>(),
                "string" or "data" or "date" => "",
                _ => throw new FormatException($"Unknown property list element <{name}/>."),
            };
        }
        switch (name)
        {
            case "dict":
            {
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                reader.Read();
                while (reader.NodeType != XmlNodeType.EndElement)
                {
                    if (reader.Name != "key") throw new FormatException("A dictionary entry has no <key>.");
                    var key = reader.ReadElementContentAsString();
                    dict[key] = ReadValue(reader);
                }
                reader.Read();
                return dict;
            }
            case "array":
            {
                var list = new List<object?>();
                reader.Read();
                while (reader.NodeType != XmlNodeType.EndElement) list.Add(ReadValue(reader));
                reader.Read();
                return list;
            }
            case "string" or "data" or "date":
                return reader.ReadElementContentAsString();
            case "integer":
            {
                var text = reader.ReadElementContentAsString().Trim();
                // diskutil prints sizes that can exceed long.MaxValue in theory; keep them as unsigned when they do.
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
                if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var u)) return u;
                throw new FormatException($"Bad integer \"{text}\" in a property list.");
            }
            case "real":
                return double.Parse(reader.ReadElementContentAsString(), NumberStyles.Float, CultureInfo.InvariantCulture);
            default:
                throw new FormatException($"Unknown property list element <{name}>.");
        }
    }

    public static IReadOnlyDictionary<string, object?>? Dict(object? value, string key) =>
        value is Dictionary<string, object?> d && d.TryGetValue(key, out var v) ? v as Dictionary<string, object?> : null;

    public static IEnumerable<Dictionary<string, object?>> Dicts(object? value, string key) =>
        value is Dictionary<string, object?> d && d.TryGetValue(key, out var v) && v is List<object?> list
            ? list.OfType<Dictionary<string, object?>>()
            : [];

    public static IEnumerable<string> Strings(object? value, string key) =>
        value is Dictionary<string, object?> d && d.TryGetValue(key, out var v) && v is List<object?> list ? list.OfType<string>() : [];

    public static string? String(object? value, string key) =>
        value is Dictionary<string, object?> d && d.TryGetValue(key, out var v) ? v as string : null;

    public static bool Bool(object? value, string key) =>
        value is Dictionary<string, object?> d && d.TryGetValue(key, out var v) && v is true;

    public static ulong UInt(object? value, string key) =>
        value is Dictionary<string, object?> d && d.TryGetValue(key, out var v)
            ? v switch { long l and >= 0 => (ulong)l, ulong u => u, _ => 0 }
            : 0;
}
