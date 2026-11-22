using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The smallest JSON reader the content packs need: objects become <see cref="Dictionary{TKey,TValue}"/> of string
    /// to object, arrays <see cref="List{T}"/> of object, and values string, bool, long, double or null. Written here
    /// instead of taking a dependency on a JSON assembly, because the hooks run inside the game's Mono runtime where a
    /// missing or mismatched assembly would fail the whole patch. Throws <see cref="FormatException"/> on bad input.
    /// </summary>
    internal static class MiniJson
    {
        public static object Parse(string text)
        {
            int index = 0;
            object value = Value(text, ref index);
            Space(text, ref index);
            if (index != text.Length) throw Error("trailing data", text, index);
            return value;
        }

        private static object Value(string text, ref int index)
        {
            Space(text, ref index);
            if (index >= text.Length) throw Error("unexpected end", text, index);
            return text[index] switch
            {
                '{' => Object(text, ref index),
                '[' => Array(text, ref index),
                '"' => String(text, ref index),
                't' => Literal(text, ref index, "true", true),
                'f' => Literal(text, ref index, "false", false),
                'n' => Literal(text, ref index, "null", null),
                _ => Number(text, ref index),
            };
        }

        private static Dictionary<string, object> Object(string text, ref int index)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            index++; // {
            Space(text, ref index);
            if (index < text.Length && text[index] == '}') { index++; return result; }
            while (true)
            {
                Space(text, ref index);
                if (index >= text.Length || text[index] != '"') throw Error("expected a key", text, index);
                string key = String(text, ref index);
                Space(text, ref index);
                if (index >= text.Length || text[index] != ':') throw Error("expected ':'", text, index);
                index++;
                result[key] = Value(text, ref index);
                Space(text, ref index);
                if (index >= text.Length) throw Error("unexpected end", text, index);
                if (text[index] == ',') { index++; continue; }
                if (text[index] == '}') { index++; return result; }
                throw Error("expected ',' or '}'", text, index);
            }
        }

        private static List<object> Array(string text, ref int index)
        {
            var result = new List<object>();
            index++; // [
            Space(text, ref index);
            if (index < text.Length && text[index] == ']') { index++; return result; }
            while (true)
            {
                result.Add(Value(text, ref index));
                Space(text, ref index);
                if (index >= text.Length) throw Error("unexpected end", text, index);
                if (text[index] == ',') { index++; continue; }
                if (text[index] == ']') { index++; return result; }
                throw Error("expected ',' or ']'", text, index);
            }
        }

        private static string String(string text, ref int index)
        {
            index++; // opening quote
            var builder = new StringBuilder();
            while (true)
            {
                if (index >= text.Length) throw Error("unterminated string", text, index);
                char c = text[index++];
                if (c == '"') return builder.ToString();
                if (c != '\\') { builder.Append(c); continue; }
                if (index >= text.Length) throw Error("unterminated escape", text, index);
                char escape = text[index++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (index + 4 > text.Length) throw Error("truncated \\u escape", text, index);
                        builder.Append((char)ushort.Parse(text.AsSpan(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        index += 4;
                        break;
                    default: throw Error($"bad escape '\\{escape}'", text, index);
                }
            }
        }

        private static object Literal(string text, ref int index, string literal, object value)
        {
            if (string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0) throw Error($"expected {literal}", text, index);
            index += literal.Length;
            return value;
        }

        private static object Number(string text, ref int index)
        {
            int start = index;
            if (index < text.Length && (text[index] == '-' || text[index] == '+')) index++;
            bool fraction = false;
            while (index < text.Length)
            {
                char c = text[index];
                if (c >= '0' && c <= '9') { index++; continue; }
                if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { fraction |= c == '.' || c == 'e' || c == 'E'; index++; continue; }
                break;
            }
            string slice = text.Substring(start, index - start);
            if (slice.Length == 0) throw Error("expected a value", text, start);
            if (!fraction && long.TryParse(slice, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer)) return integer;
            if (double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out double real)) return real;
            throw Error($"bad number '{slice}'", text, start);
        }

        private static void Space(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        }

        private static FormatException Error(string why, string text, int index)
        {
            int from = Math.Max(0, index - 20), length = Math.Min(40, text.Length - from);
            return new FormatException($"{why} at {index}: '{(length > 0 ? text.Substring(from, length) : "")}'");
        }
    }
}
