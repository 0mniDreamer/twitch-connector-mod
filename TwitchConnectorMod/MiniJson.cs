using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TwitchConnectorMod
{
    /// <summary>
    /// Tiny dependency-free JSON field extractor for the flat objects Twitch's auth
    /// endpoints return. Shared by TwitchAuth and TwitchTokenStore so the parsing
    /// logic lives in one place. Regexes are cached per key since the same few keys
    /// are extracted repeatedly (e.g. while polling).
    /// </summary>
    internal static class MiniJson
    {
        private static readonly Dictionary<string, Regex> StringPatterns = new Dictionary<string, Regex>();
        private static readonly Dictionary<string, Regex> IntPatterns = new Dictionary<string, Regex>();
        private static readonly object Lock = new object();

        internal static string ExtractString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;

            Regex re;
            lock (Lock)
            {
                if (!StringPatterns.TryGetValue(key, out re))
                {
                    re = new Regex("\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
                    StringPatterns[key] = re;
                }
            }

            Match m = re.Match(json);
            if (!m.Success) return null;
            try { return Regex.Unescape(m.Groups[1].Value); }
            catch { return m.Groups[1].Value; }
        }

        internal static int ExtractInt(string json, string key, int fallback)
        {
            if (string.IsNullOrEmpty(json)) return fallback;

            Regex re;
            lock (Lock)
            {
                if (!IntPatterns.TryGetValue(key, out re))
                {
                    re = new Regex("\"" + Regex.Escape(key) + "\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
                    IntPatterns[key] = re;
                }
            }

            Match m = re.Match(json);
            int v;
            return (m.Success && int.TryParse(m.Groups[1].Value, out v)) ? v : fallback;
        }
    }
}
