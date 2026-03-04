using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

/// <summary>
/// Minimal JSON serializer/parser for the simple formats used in this project.
/// </summary>
internal static class SimpleJson
{
    #region Serialization

    /// <summary>Serialize an object to a JSON string.</summary>
    public static string Serialize(object obj)
    {
        if (obj == null) return "null";
        if (obj is bool b) return b ? "true" : "false";
        if (obj is string s) return "\"" + EscapeString(s) + "\"";
        if (obj is int i) return i.ToString(CultureInfo.InvariantCulture);
        if (obj is long l) return l.ToString(CultureInfo.InvariantCulture);
        if (obj is double d) return d.ToString("R", CultureInfo.InvariantCulture);
        if (obj is float f) return f.ToString("R", CultureInfo.InvariantCulture);
        if (obj is IDictionary dict)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (DictionaryEntry entry in dict)
            {
                if (!first) sb.Append(',');
                sb.Append('"').Append(EscapeString(entry.Key.ToString())).Append("\":").Append(Serialize(entry.Value));
                first = false;
            }
            sb.Append('}');
            return sb.ToString();
        }
        if (obj is IEnumerable items)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var item in items)
            {
                if (!first) sb.Append(',');
                sb.Append(Serialize(item));
                first = false;
            }
            sb.Append(']');
            return sb.ToString();
        }
        // Handle anonymous types and plain objects via reflection
        var type = obj.GetType();
        var props = type.GetProperties();
        if (props.Length > 0)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var prop in props)
            {
                if (!first) sb.Append(',');
                sb.Append('"').Append(EscapeString(prop.Name)).Append("\":").Append(Serialize(prop.GetValue(obj, null)));
                first = false;
            }
            sb.Append('}');
            return sb.ToString();
        }
        return "\"" + EscapeString(obj.ToString()) + "\"";
    }

    /// <summary>Serialize {"texts": [...]} from a string array.</summary>
    public static string SerializeTexts(string[] texts)
    {
        var dict = new Dictionary<string, object> { { "texts", texts } };
        return Serialize(dict);
    }

    private static string EscapeString(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    #endregion

    #region Parsing

    /// <summary>Parse {"texts": [...]} and return the string array.</summary>
    public static string[] ParseTexts(string json)
    {
        try
        {
            int pos = SkipWhitespace(json, 0);
            var obj = ParseObject(json, ref pos);
            if (obj == null || !obj.ContainsKey("texts")) return new string[0];
            var texts = obj["texts"] as List<object>;
            if (texts == null) return new string[0];
            return texts.Select(t => (t as string) ?? "").ToArray();
        }
        catch
        {
            return new string[0];
        }
    }

    /// <summary>Parse a JSON object into a Dictionary. Supports nested objects and arrays.</summary>
    public static Dictionary<string, object> ParseModelParams(string json)
    {
        try
        {
            if (string.IsNullOrEmpty(json)) return new Dictionary<string, object>();
            int pos = SkipWhitespace(json, 0);
            return ParseObject(json, ref pos);
        }
        catch
        {
            return new Dictionary<string, object>();
        }
    }

    /// <summary>Extract choices[0].delta.content from an SSE stream chunk.</summary>
    public static string ParseSseContent(string json)
    {
        try
        {
            int pos = SkipWhitespace(json, 0);
            var obj = ParseObject(json, ref pos);
            if (obj == null) return null;
            var choices = obj.ContainsKey("choices") ? obj["choices"] as List<object> : null;
            if (choices == null || choices.Count == 0) return null;
            var choice = choices[0] as Dictionary<string, object>;
            if (choice == null) return null;
            var delta = choice.ContainsKey("delta") ? choice["delta"] as Dictionary<string, object> : null;
            if (delta == null) return null;
            return delta.ContainsKey("content") ? delta["content"] as string : null;
        }
        catch
        {
            return null;
        }
    }

    private static int SkipWhitespace(string s, int pos)
    {
        while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\n' || s[pos] == '\r'))
            pos++;
        return pos;
    }

    private static string ReadString(string s, ref int pos)
    {
        pos++; // skip opening "
        var sb = new StringBuilder();
        while (pos < s.Length && s[pos] != '"')
        {
            if (s[pos] == '\\' && pos + 1 < s.Length)
            {
                pos++;
                switch (s[pos])
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 <= s.Length)
                        {
                            var hex = s.Substring(pos + 1, 4);
                            sb.Append((char)Convert.ToInt32(hex, 16));
                            pos += 4;
                        }
                        break;
                    default: sb.Append(s[pos]); break;
                }
            }
            else
            {
                sb.Append(s[pos]);
            }
            pos++;
        }
        if (pos < s.Length) pos++; // skip closing "
        return sb.ToString();
    }

    private static object ReadNumber(string s, ref int pos)
    {
        int start = pos;
        bool isFloat = false;
        if (pos < s.Length && s[pos] == '-') pos++;
        while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E' || ((s[pos] == '+' || s[pos] == '-') && pos > start && (s[pos - 1] == 'e' || s[pos - 1] == 'E'))))
        {
            if (s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E') isFloat = true;
            pos++;
        }
        var numStr = s.Substring(start, pos - start);
        if (isFloat)
            return double.Parse(numStr, CultureInfo.InvariantCulture);
        if (long.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lval))
            return lval;
        return double.Parse(numStr, CultureInfo.InvariantCulture);
    }

    private static object ParseValue(string s, ref int pos)
    {
        pos = SkipWhitespace(s, pos);
        if (pos >= s.Length) return null;
        char c = s[pos];
        if (c == '"') return ReadString(s, ref pos);
        if (c == '{') return ParseObject(s, ref pos);
        if (c == '[') return ParseArray(s, ref pos);
        if (c == 't' && pos + 4 <= s.Length && s.Substring(pos, 4) == "true") { pos += 4; return true; }
        if (c == 'f' && pos + 5 <= s.Length && s.Substring(pos, 5) == "false") { pos += 5; return false; }
        if (c == 'n' && pos + 4 <= s.Length && s.Substring(pos, 4) == "null") { pos += 4; return null; }
        return ReadNumber(s, ref pos);
    }

    private static Dictionary<string, object> ParseObject(string s, ref int pos)
    {
        pos++; // skip {
        var result = new Dictionary<string, object>();
        pos = SkipWhitespace(s, pos);
        while (pos < s.Length && s[pos] != '}')
        {
            if (s[pos] != '"') { pos++; continue; }
            var key = ReadString(s, ref pos);
            pos = SkipWhitespace(s, pos);
            if (pos >= s.Length || s[pos] != ':') break;
            pos++;
            var value = ParseValue(s, ref pos);
            result[key] = value;
            pos = SkipWhitespace(s, pos);
            if (pos < s.Length && s[pos] == ',') pos++;
            pos = SkipWhitespace(s, pos);
        }
        if (pos < s.Length) pos++; // skip }
        return result;
    }

    private static List<object> ParseArray(string s, ref int pos)
    {
        pos++; // skip [
        var items = new List<object>();
        pos = SkipWhitespace(s, pos);
        while (pos < s.Length && s[pos] != ']')
        {
            items.Add(ParseValue(s, ref pos));
            pos = SkipWhitespace(s, pos);
            if (pos < s.Length && s[pos] == ',') pos++;
            pos = SkipWhitespace(s, pos);
        }
        if (pos < s.Length) pos++; // skip ]
        return items;
    }

    #endregion
}
