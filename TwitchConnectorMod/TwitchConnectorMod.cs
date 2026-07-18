using System;
using System.Threading;
using MelonLoader;

namespace TwitchConnectorMod
{
    public class TwitchConnectorMod : MelonMod
    {
        // The Client ID of the Twitch app (registered as a PUBLIC client - see README).
        // A Client ID is a public identifier by design, so it's safe to have here in
        // the clear. The implicit grant flow needs no client secret at all, so nothing
        // sensitive ships in this mod.
        // RedirectPort must match the redirect URL registered on Twitch (http://localhost:<port>).
        private const string ClientId = "ysvczvyml8q1qenwgs9w3sjfubcdrl";
        private const int RedirectPort = 3000;

        internal static TwitchIRC IRC = new TwitchIRC();

        private MelonPreferences_Category twitchConnectorPrefs;
        private MelonPreferences_Entry<string> oauthToken;     // optional manual override
        private MelonPreferences_Entry<string> channel;
        private MelonPreferences_Entry<string> username;
        private MelonPreferences_Entry<bool> logTwitchMessages;
        private MelonPreferences_Entry<bool> logRawTwitchMessages;

        // Tokens are persisted to UserData/TwitchConnector.json, not the cfg.
        private TwitchTokenStore.Tokens tokens;

        private TwitchAuth auth;

        public override void OnInitializeMelon()
        {
            twitchConnectorPrefs = MelonPreferences.CreateCategory("TwitchConnector");

            username = twitchConnectorPrefs.CreateEntry<string>("Username", "");
            username.Comment = "Your twitch username. Optional - detected automatically after you authorize.";

            // Kept for backward compatibility: if you already have a chat token from
            // another source, paste it here and it will be used directly. Otherwise
            // leave it blank and the mod will obtain a token for you in the browser.
            oauthToken = twitchConnectorPrefs.CreateEntry<string>("OAuthToken", "");
            oauthToken.Comment = "Optional. Leave blank to log in via the browser. Only set this if pasting a chat token from another generator.";

           
            channel = twitchConnectorPrefs.CreateEntry<string>("Channel", "");
            channel.Comment = "The twitch channel to join - typically the same as your username.";

            logTwitchMessages = twitchConnectorPrefs.CreateEntry<bool>("LogTwitchMessages", false);
            logTwitchMessages.Comment = "If set to true, all received twitch messages are written to the console log.";

            logRawTwitchMessages = twitchConnectorPrefs.CreateEntry<bool>("LogRawTwitchMessages", false);
            logRawTwitchMessages.Comment = "If set to true, all raw received twitch messages are written to the console log.";

            // Resolving/obtaining a token can block (network calls + waiting for the
            // user to authorize in a browser), so do it off the main thread.
            Thread connectThread = new Thread(ConnectToTwitch);
            connectThread.IsBackground = true;
            connectThread.Start();
        }

        private void ConnectToTwitch()
        {
            try
            {
                tokens = TwitchTokenStore.Load();
                auth = new TwitchAuth(ClientId, RedirectPort);

                string token = ResolveAccessToken();
                if (string.IsNullOrEmpty(token))
                {
                    LoggerInstance.Msg("Could not obtain a Twitch token, not connecting. See the log above for details.");
                    return;
                }

                // If the user didn't set a username, use the login name from the token.
                if (string.IsNullOrEmpty(username.Value))
                {
                    var validation = auth.Validate(token);
                    if (validation.Valid && !string.IsNullOrEmpty(validation.Login))
                    {
                        username.Value = validation.Login;
                        SavePrefs();
                    }
                }

                if (string.IsNullOrEmpty(username.Value))
                {
                    LoggerInstance.Msg("Twitch username could not be determined. Set Username in MelonPreferences.cfg under [TwitchConnector].");
                    return;
                }

                if (string.IsNullOrEmpty(channel.Value))
                {
                    LoggerInstance.Msg("Twitch channel not set, defaulting to twitch username.");
                    channel.Value = username.Value;
                    SavePrefs();
                }

                LoggerInstance.Msg("Starting Connection");
                IRC.oauth = token;                 // raw token; IRC adds the "oauth:" prefix
                IRC.channelName = channel.Value;
                IRC.nickName = username.Value;
                IRC.AuthFailedCallback = OnTwitchAuthRejected;

                AddChatMsgReceivedEventHandler(OnChatMsgReceived);
                if (logRawTwitchMessages.Value)
                    IRC.logRawMessages = true;

                IRC.Enable();
            }
            catch (Exception ex)
            {
                LoggerInstance.Msg("Error while connecting to Twitch: " + ex.Message);
            }
        }

        /// <summary>
        /// Returns a usable chat access token, in priority order:
        ///   1. A manually pasted OAuthToken (backward compatible).
        ///   2. A stored access token that still validates.
        ///   3. A refreshed token from a stored refresh token (silent).
        ///   4. A brand new token via the browser authorization flow.
        /// Returns null if none of these can produce a token.
        /// </summary>
        private string ResolveAccessToken()
        {
            // 1. Manual override.
            if (!string.IsNullOrEmpty(oauthToken.Value))
            {
                LoggerInstance.Msg("Using the OAuthToken set in MelonPreferences.cfg.");
                return NormalizeToken(oauthToken.Value);
            }

            // 2. Stored access token - reuse it if it's still valid.
            if (!string.IsNullOrEmpty(tokens.AccessToken) && auth.Validate(tokens.AccessToken).Valid)
            {
                LoggerInstance.Msg("Reusing stored Twitch token.");
                return tokens.AccessToken;
            }

            // 3. Full browser authorization (the one-click "Connect with Twitch" step).
            // Implicit-grant tokens can't be refreshed, so when the stored token
            // expires this flow simply runs again.
            if (string.IsNullOrEmpty(ClientId))
            {
                LoggerInstance.Msg("No Twitch ClientId baked into this build. Either paste an OAuthToken in MelonPreferences.cfg or rebuild with a ClientId set (see README).");
                return null;
            }

            TwitchAuth.TokenResult result = auth.RunBrowserAuth();
            if (result != null)
            {
                StoreTokens(result);
                return result.AccessToken;
            }

            return null;
        }

        /// <summary>
        /// Called by the IRC client if Twitch rejects our login. We clear the stored
        /// access token so the next launch refreshes or re-authorizes cleanly.
        /// </summary>
        private void OnTwitchAuthRejected()
        {
            LoggerInstance.Msg("Twitch rejected the login token. Clearing it - restart Audica to re-authorize.");
            tokens.AccessToken = "";
            TwitchTokenStore.Save(tokens.AccessToken, "");
        }

        private void StoreTokens(TwitchAuth.TokenResult result)
        {
            tokens.AccessToken = result.AccessToken ?? "";
            TwitchTokenStore.Save(tokens.AccessToken, "");
        }

        private void SavePrefs()
        {
            try { MelonPreferences.Save(); }
            catch (Exception ex) { LoggerInstance.Msg("Could not save MelonPreferences: " + ex.Message); }
        }

        // Accepts either "oauth:xxxx" or "xxxx" and returns the bare token.
        private static string NormalizeToken(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            raw = raw.Trim();
            if (raw.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring("oauth:".Length);
            return raw;
        }

        public static void SendMessage(string message)
        {
            if (IRC != null)
                IRC.SendMsg(message);
        }

        public static void AddChatMsgReceivedEventHandler(TwitchIRC.MessageReceivedEventHandler eventHandler)
        {
            if (IRC != null)
                IRC.MessageReceived += eventHandler;
        }

        private void OnChatMsgReceived(Object sender, ParsedTwitchMessage eventArgs)
        {
            if (logTwitchMessages.Value)
                Melon<TwitchConnectorMod>.Logger.Msg($"Twitch Message: {eventArgs.Message}");
        }

        public override void OnUpdate()
        {
            IRC.Update();
        }
    }
}
