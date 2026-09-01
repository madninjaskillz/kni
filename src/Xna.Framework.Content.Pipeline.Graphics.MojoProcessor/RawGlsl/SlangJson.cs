// Copyright (C)2026 - Slang effect pipeline extension.
//
// A minimal JSON reader, for one job: slangc's -reflection-json output.
//
// Hand-rolled rather than referenced, because this assembly multi-targets
// net40 (see the .csproj) where there is no System.Text.Json, and pulling
// Newtonsoft into the content pipeline for one file of reflection data would
// be a heavier dependency than the parser it replaces. Scope is deliberately
// tiny: enough to walk objects, arrays, strings and numbers. No streaming, no
// error recovery, no unicode escapes beyond \\uXXXX.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Microsoft.Xna.Framework.Content.Pipeline.Processors
{
    internal class SlangJsonValue
    {
        public Dictionary<string, SlangJsonValue> Object;
        public List<SlangJsonValue> Array;
        public string String;
        public double? Number;
        public bool? Bool;

        public SlangJsonValue this[string key]
        {
            get
            {
                SlangJsonValue value;
                if (Object != null && Object.TryGetValue(key, out value))
                    return value;
                return null;
            }
        }

        public string AsString { get { return String; } }

        public int AsInt(int fallback)
        {
            return Number.HasValue ? (int)Number.Value : fallback;
        }
    }

    internal static class SlangJson
    {
        public static SlangJsonValue Parse(string text)
        {
            int i = 0;
            SlangJsonValue value = ParseValue(text, ref i);
            return value;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n'))
                i++;
        }

        private static SlangJsonValue ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length)
                return new SlangJsonValue();

            char c = s[i];
            if (c == '{')
                return ParseObject(s, ref i);
            if (c == '[')
                return ParseArray(s, ref i);
            if (c == '"')
                return new SlangJsonValue { String = ParseString(s, ref i) };

            if (s.Length - i >= 4 && s.Substring(i, 4) == "true")
            {
                i += 4;
                return new SlangJsonValue { Bool = true };
            }

            if (s.Length - i >= 5 && s.Substring(i, 5) == "false")
            {
                i += 5;
                return new SlangJsonValue { Bool = false };
            }

            if (s.Length - i >= 4 && s.Substring(i, 4) == "null")
            {
                i += 4;
                return new SlangJsonValue();
            }

            int start = i;
            while (i < s.Length && "+-.eE0123456789".IndexOf(s[i]) >= 0)
                i++;

            double parsed;
            double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
            return new SlangJsonValue { Number = parsed };
        }

        private static SlangJsonValue ParseObject(string s, ref int i)
        {
            var result = new SlangJsonValue { Object = new Dictionary<string, SlangJsonValue>() };
            i++; // '{'

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] == '}')
                {
                    i++;
                    return result;
                }

                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ':')
                    i++;

                result.Object[key] = ParseValue(s, ref i);

                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',')
                    i++;
            }
        }

        private static SlangJsonValue ParseArray(string s, ref int i)
        {
            var result = new SlangJsonValue { Array = new List<SlangJsonValue>() };
            i++; // '['

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] == ']')
                {
                    i++;
                    return result;
                }

                result.Array.Add(ParseValue(s, ref i));

                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',')
                    i++;
            }
        }

        private static string ParseString(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length || s[i] != '"')
                return string.Empty;

            i++; // opening quote
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else
                {
                    sb.Append(s[i++]);
                }
            }

            i++; // closing quote
            return sb.ToString();
        }
    }
}
