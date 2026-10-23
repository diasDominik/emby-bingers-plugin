using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Emby.Bingers.Api
{
    /// <summary>
    /// A tiny JSON reader and writer, so the plugin carries no serializer dependency and
    /// can read responses whose exact shape the Bingers spec leaves open.
    /// Objects parse to Dictionary&lt;string, object&gt;, arrays to List&lt;object&gt;,
    /// numbers to double, plus string, bool and null.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var reader = new Reader(text ?? string.Empty);
            reader.SkipWhitespace();
            var value = reader.ReadValue();
            reader.SkipWhitespace();
            if (!reader.AtEnd)
            {
                throw new FormatException("Unexpected trailing characters in JSON.");
            }

            return value;
        }

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        // ---- Convenience accessors for parsed trees ----

        public static Dictionary<string, object> Obj(object node, string key)
            => Get(node, key) as Dictionary<string, object>;

        public static List<object> Arr(object node, string key)
            => Get(node, key) as List<object>;

        public static string Str(object node, string key)
        {
            var v = Get(node, key);
            switch (v)
            {
                case null: return null;
                case string s: return s;
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case bool b: return b ? "true" : "false";
                default: return null;
            }
        }

        public static int? Int(object node, string key)
        {
            var v = Get(node, key);
            if (v is double d)
            {
                return (int)d;
            }

            if (v is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            {
                return i;
            }

            return null;
        }

        public static bool Bool(object node, string key) => Get(node, key) is bool b && b;

        public static DateTimeOffset? Date(object node, string key)
        {
            var s = Str(node, key);
            if (string.IsNullOrEmpty(s))
            {
                return null;
            }

            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
                ? d
                : (DateTimeOffset?)null;
        }

        public static object Get(object node, string key)
        {
            if (node is Dictionary<string, object> dict && dict.TryGetValue(key, out var v))
            {
                return v;
            }

            return null;
        }

        // ---- Writer ----

        private static void Write(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    break;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    break;
                case double d:
                    sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case DateTimeOffset dto:
                    WriteString(sb, dto.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                    break;
                case IDictionary<string, object> dict:
                    sb.Append('{');
                    var first = true;
                    foreach (var kv in dict)
                    {
                        if (kv.Value == null)
                        {
                            continue; // Bingers rejects explicit nulls on typed fields; omit them.
                        }

                        if (!first)
                        {
                            sb.Append(',');
                        }

                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }

                    sb.Append('}');
                    break;
                case System.Collections.IEnumerable list:
                    sb.Append('[');
                    var firstItem = true;
                    foreach (var item in list)
                    {
                        if (!firstItem)
                        {
                            sb.Append(',');
                        }

                        firstItem = false;
                        Write(sb, item);
                    }

                    sb.Append(']');
                    break;
                default:
                    WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
        }

        // ---- Reader ----

        private sealed class Reader
        {
            private readonly string text;
            private int pos;

            public Reader(string text)
            {
                this.text = text;
            }

            public bool AtEnd => this.pos >= this.text.Length;

            public void SkipWhitespace()
            {
                while (!this.AtEnd && char.IsWhiteSpace(this.text[this.pos]))
                {
                    this.pos++;
                }
            }

            public object ReadValue()
            {
                if (this.AtEnd)
                {
                    throw new FormatException("Unexpected end of JSON.");
                }

                var c = this.text[this.pos];
                switch (c)
                {
                    case '{': return this.ReadObject();
                    case '[': return this.ReadArray();
                    case '"': return this.ReadString();
                    case 't': this.Expect("true"); return true;
                    case 'f': this.Expect("false"); return false;
                    case 'n': this.Expect("null"); return null;
                    default: return this.ReadNumber();
                }
            }

            private Dictionary<string, object> ReadObject()
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                this.pos++; // {
                this.SkipWhitespace();
                if (this.Peek() == '}')
                {
                    this.pos++;
                    return result;
                }

                while (true)
                {
                    this.SkipWhitespace();
                    var key = this.ReadString();
                    this.SkipWhitespace();
                    this.ExpectChar(':');
                    this.SkipWhitespace();
                    result[key] = this.ReadValue();
                    this.SkipWhitespace();
                    var c = this.Next();
                    if (c == '}')
                    {
                        return result;
                    }

                    if (c != ',')
                    {
                        throw new FormatException("Expected ',' or '}' in JSON object.");
                    }
                }
            }

            private List<object> ReadArray()
            {
                var result = new List<object>();
                this.pos++; // [
                this.SkipWhitespace();
                if (this.Peek() == ']')
                {
                    this.pos++;
                    return result;
                }

                while (true)
                {
                    this.SkipWhitespace();
                    result.Add(this.ReadValue());
                    this.SkipWhitespace();
                    var c = this.Next();
                    if (c == ']')
                    {
                        return result;
                    }

                    if (c != ',')
                    {
                        throw new FormatException("Expected ',' or ']' in JSON array.");
                    }
                }
            }

            private string ReadString()
            {
                this.ExpectChar('"');
                var sb = new StringBuilder();
                while (true)
                {
                    var c = this.Next();
                    if (c == '"')
                    {
                        return sb.ToString();
                    }

                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }

                    var e = this.Next();
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (this.pos + 4 > this.text.Length)
                            {
                                throw new FormatException("Bad \\u escape in JSON string.");
                            }

                            sb.Append((char)int.Parse(this.text.Substring(this.pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            this.pos += 4;
                            break;
                        default:
                            throw new FormatException("Bad escape in JSON string.");
                    }
                }
            }

            private double ReadNumber()
            {
                var start = this.pos;
                while (!this.AtEnd && "+-0123456789.eE".IndexOf(this.text[this.pos]) >= 0)
                {
                    this.pos++;
                }

                if (start == this.pos)
                {
                    throw new FormatException("Unexpected character '" + this.text[start] + "' in JSON.");
                }

                return double.Parse(this.text.Substring(start, this.pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(this.text, this.pos, word, 0, word.Length) != 0)
                {
                    throw new FormatException("Unexpected token in JSON.");
                }

                this.pos += word.Length;
            }

            private void ExpectChar(char c)
            {
                if (this.Next() != c)
                {
                    throw new FormatException("Expected '" + c + "' in JSON.");
                }
            }

            private char Peek() => this.AtEnd ? '\0' : this.text[this.pos];

            private char Next()
            {
                if (this.AtEnd)
                {
                    throw new FormatException("Unexpected end of JSON.");
                }

                return this.text[this.pos++];
            }
        }
    }
}
