using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SqlLense.Text
{
    /// <summary>
    /// A deliberately tiny JSON reader/writer. Analyzers run inside the compiler and IDE hosts, where
    /// taking a dependency on System.Text.Json or Newtonsoft.Json risks version conflicts with the
    /// host's own copy, so the schema snapshot format is handled here instead.
    /// </summary>
    internal static class MiniJson
    {
        public static object? Parse(string json)
        {
            var reader = new Reader(json);
            reader.SkipWhitespace();
            var value = reader.ReadValue();
            reader.SkipWhitespace();
            if (!reader.AtEnd)
            {
                throw reader.Error("Unexpected trailing content");
            }

            return value;
        }

        private sealed class Reader
        {
            private readonly string _s;
            private int _i;

            public Reader(string s) => _s = s;

            public bool AtEnd => _i >= _s.Length;

            public FormatException Error(string message) =>
                new FormatException($"{message} at position {_i} of schema snapshot.");

            public void SkipWhitespace()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
                {
                    _i++;
                }
            }

            public object? ReadValue()
            {
                if (AtEnd)
                {
                    throw Error("Unexpected end of input");
                }

                switch (_s[_i])
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ReadNumber();
                }
            }

            private void Expect(string literal)
            {
                if (string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                {
                    throw Error($"Expected '{literal}'");
                }

                _i += literal.Length;
            }

            private Dictionary<string, object?> ReadObject()
            {
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                _i++;
                SkipWhitespace();
                if (_i < _s.Length && _s[_i] == '}')
                {
                    _i++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || _s[_i] != '"')
                    {
                        throw Error("Expected property name");
                    }

                    var key = ReadString();
                    SkipWhitespace();
                    if (AtEnd || _s[_i] != ':')
                    {
                        throw Error("Expected ':'");
                    }

                    _i++;
                    SkipWhitespace();
                    result[key] = ReadValue();
                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw Error("Unterminated object");
                    }

                    if (_s[_i] == ',')
                    {
                        _i++;
                        continue;
                    }

                    if (_s[_i] == '}')
                    {
                        _i++;
                        return result;
                    }

                    throw Error("Expected ',' or '}'");
                }
            }

            private List<object?> ReadArray()
            {
                var result = new List<object?>();
                _i++;
                SkipWhitespace();
                if (_i < _s.Length && _s[_i] == ']')
                {
                    _i++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    result.Add(ReadValue());
                    SkipWhitespace();
                    if (AtEnd)
                    {
                        throw Error("Unterminated array");
                    }

                    if (_s[_i] == ',')
                    {
                        _i++;
                        continue;
                    }

                    if (_s[_i] == ']')
                    {
                        _i++;
                        return result;
                    }

                    throw Error("Expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                _i++; // opening quote
                var start = _i;
                while (_i < _s.Length && _s[_i] != '"' && _s[_i] != '\\')
                {
                    _i++;
                }

                if (_i < _s.Length && _s[_i] == '"')
                {
                    // Fast path: no escapes.
                    var simple = _s.Substring(start, _i - start);
                    _i++;
                    return simple;
                }

                var sb = new StringBuilder(_s, start, _i - start, (_i - start) + 16);
                while (true)
                {
                    if (AtEnd)
                    {
                        throw Error("Unterminated string");
                    }

                    var c = _s[_i++];
                    if (c == '"')
                    {
                        return sb.ToString();
                    }

                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }

                    if (AtEnd)
                    {
                        throw Error("Unterminated escape");
                    }

                    var e = _s[_i++];
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
                            if (_i + 4 > _s.Length)
                            {
                                throw Error("Invalid unicode escape");
                            }

                            sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default:
                            throw Error("Invalid escape");
                    }
                }
            }

            private double ReadNumber()
            {
                var start = _i;
                while (_i < _s.Length && "+-0123456789.eE".IndexOf(_s[_i]) >= 0)
                {
                    _i++;
                }

                if (start == _i)
                {
                    throw Error("Unexpected character");
                }

                return double.Parse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        public static void WriteString(StringBuilder sb, string? value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            foreach (var c in value)
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
    }
}
