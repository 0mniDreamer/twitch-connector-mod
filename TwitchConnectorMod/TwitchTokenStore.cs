using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using MelonLoader;

namespace TwitchConnectorMod
{
    /// <summary>
    /// Persists the mod's runtime Twitch tokens to a small JSON file in the game's
    /// UserData folder (UserData/TwitchConnector.json) instead of MelonPreferences.cfg,
    /// so the auth tokens stay out of the shared preferences file.
    /// </summary>
    public static class TwitchTokenStore
    {
        private const string FileName = "TwitchConnector.json";

        public class Tokens
        {
            public string AccessToken = "";
            public string RefreshToken = "";
        }

        public static Tokens Load()
        {
            var tokens = new Tokens();
            try
            {
                string path = FilePath();
                if (!File.Exists(path))
                    return tokens;

                string json = File.ReadAllText(path);
                tokens.AccessToken = ExtractString(json, "accessToken") ?? "";
                tokens.RefreshToken = ExtractString(json, "refreshToken") ?? "";
            }
            catch (Exception ex)
            {
                Melon<TwitchConnectorMod>.Logger.Msg("Could not read token file: " + ex.Message);
            }
            return tokens;
        }

        public static void Save(string accessToken, string refreshToken)
        {
            try
            {
                string path = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                string json =
                    "{\r\n" +
                    "  \"accessToken\": \"" + Escape(accessToken) + "\",\r\n" +
                    "  \"refreshToken\": \"" + Escape(refreshToken) + "\"\r\n" +
                    "}\r\n";

                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Melon<TwitchConnectorMod>.Logger.Msg("Could not write token file: " + ex.Message);
            }
        }

        // Resolves <GameDir>/UserData (next to MelonPreferences.cfg), independent of
        // the current working directory.
        private static string FilePath()
        {
            string userDataDir = null;
            try
            {
                string loc = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    // The mod DLL lives in <GameDir>/Mods, so UserData is one level up.
                    string modsDir = Path.GetDirectoryName(loc);
                    userDataDir = Path.GetFullPath(Path.Combine(modsDir, "..", "UserData"));
                }
            }
            catch { /* fall through to cwd-based path */ }

            if (string.IsNullOrEmpty(userDataDir))
                userDataDir = Path.Combine(Directory.GetCurrentDirectory(), "UserData");

            return Path.Combine(userDataDir, FileName);
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string ExtractString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            return m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }
}
