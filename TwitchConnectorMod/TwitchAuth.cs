using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MelonLoader;

namespace TwitchConnectorMod
{
    /// <summary>
    /// Handles Twitch OAuth using the Authorization Code Grant Flow with a local
    /// loopback redirect. The user clicks a single "Authorize" button in their
    /// browser and the mod captures the result automatically - no codes to type.
    ///
    /// Flow:
    ///   1. Start a tiny local listener on http://localhost:PORT/.
    ///   2. Open the browser to Twitch's /authorize page.
    ///   3. User clicks Authorize; Twitch redirects to localhost with ?code=...
    ///   4. Exchange the code at /oauth2/token for access + refresh tokens.
    ///   5. Refresh silently later using the refresh token.
    ///
    /// NOTE: Twitch does not support PKCE, so the authorization code flow requires a
    /// client secret. Because this mod is distributed, that secret is technically
    /// extractable; rotate it from the Twitch console if it is ever abused.
    /// </summary>
    public class TwitchAuth
    {
        private const string AuthorizeEndpoint = "https://id.twitch.tv/oauth2/authorize";
        private const string TokenEndpoint = "https://id.twitch.tv/oauth2/token";
        private const string ValidateEndpoint = "https://id.twitch.tv/oauth2/validate";

        // Scopes required to read from and write to chat over IRC.
        private const string Scopes = "chat:read chat:edit";

        private static readonly HttpClient Http = new HttpClient();

        public string ClientId;
        public string ClientSecret;
        public int RedirectPort;

        public TwitchAuth(string clientId, string clientSecret, int redirectPort)
        {
            this.ClientId = clientId;
            this.ClientSecret = clientSecret;
            this.RedirectPort = redirectPort > 0 ? redirectPort : 3000;

            // Older Unity/mono runtimes default to a TLS version Twitch rejects.
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
            catch { /* some runtimes don't allow setting this; ignore */ }
        }

        // The redirect URI must EXACTLY match the one registered in the Twitch app.
        public string RedirectUri
        {
            get { return "http://localhost:" + RedirectPort; }
        }

        public class TokenResult
        {
            public string AccessToken;
            public string RefreshToken;
            public int ExpiresIn;
        }

        public class ValidationResult
        {
            public bool Valid;
            public string Login;
        }

        /// <summary>
        /// Runs the full browser authorization. Blocks (call from a background thread)
        /// until the user authorizes, declines, or it times out. Returns null on failure.
        /// </summary>
        public TokenResult RunBrowserAuth()
        {
            if (string.IsNullOrEmpty(ClientId) || string.IsNullOrEmpty(ClientSecret))
            {
                Log("Twitch ClientId/ClientSecret not set, cannot authorize. See the README.");
                return null;
            }

            TcpListener listener = null;
            try
            {
                // --- Step 1: start the local listener BEFORE opening the browser ---
                try
                {
                    listener = new TcpListener(IPAddress.Loopback, RedirectPort);
                    listener.Start();
                }
                catch (Exception ex)
                {
                    Log("Could not start local listener on port " + RedirectPort + " (" + ex.Message +
                        "). Change RedirectPort in MelonPreferences.cfg and make sure it matches the redirect URL registered on Twitch.");
                    return null;
                }

                string state = Guid.NewGuid().ToString("N");
                string authUrl = AuthorizeEndpoint +
                    "?response_type=code" +
                    "&client_id=" + Uri.EscapeDataString(ClientId) +
                    "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                    "&scope=" + Uri.EscapeDataString(Scopes) +
                    "&state=" + state;

                // --- Step 2: open the browser ---
                Log("Opening your browser to authorize with Twitch...");
                Log("If it doesn't open automatically, paste this URL into any browser:");
                Log("  " + authUrl);
                OpenBrowser(authUrl);

                // --- Step 3: wait for Twitch to redirect back to our listener ---
                string query = WaitForRedirect(listener, 180);
                if (query == null)
                {
                    Log("Timed out waiting for Twitch authorization. Restart Audica to try again.");
                    return null;
                }

                Dictionary<string, string> p = ParseQuery(query);

                string returnedState;
                p.TryGetValue("state", out returnedState);
                if (returnedState != state)
                {
                    Log("Twitch authorization state mismatch - ignoring response for safety.");
                    return null;
                }

                string error;
                if (p.TryGetValue("error", out error) && !string.IsNullOrEmpty(error))
                {
                    string desc;
                    p.TryGetValue("error_description", out desc);
                    Log("Twitch authorization was declined or failed: " + error + " " + (desc ?? ""));
                    return null;
                }

                string code;
                if (!p.TryGetValue("code", out code) || string.IsNullOrEmpty(code))
                {
                    Log("Twitch did not return an authorization code.");
                    return null;
                }

                // --- Step 4: exchange the code for access + refresh tokens ---
                string response = PostForm(TokenEndpoint, new Dictionary<string, string>
                {
                    { "client_id", ClientId },
                    { "client_secret", ClientSecret },
                    { "code", code },
                    { "grant_type", "authorization_code" },
                    { "redirect_uri", RedirectUri }
                });

                string accessToken = ExtractString(response, "access_token");
                if (string.IsNullOrEmpty(accessToken))
                {
                    Log("Failed to exchange the authorization code for a token: " + response);
                    return null;
                }

                Log("Twitch authorization successful!");
                return new TokenResult
                {
                    AccessToken = accessToken,
                    RefreshToken = ExtractString(response, "refresh_token"),
                    ExpiresIn = ExtractInt(response, "expires_in", 14400)
                };
            }
            catch (Exception ex)
            {
                Log("Twitch authorization error: " + ex.Message);
                return null;
            }
            finally
            {
                if (listener != null)
                {
                    try { listener.Stop(); } catch { /* ignore */ }
                }
            }
        }

        /// <summary>
        /// Uses a stored refresh token to get a fresh access token (silent reconnect).
        /// Returns null if the refresh failed (caller should re-run the browser flow).
        /// </summary>
        public TokenResult Refresh(string refreshToken)
        {
            if (string.IsNullOrEmpty(refreshToken) || string.IsNullOrEmpty(ClientId) || string.IsNullOrEmpty(ClientSecret))
                return null;

            try
            {
                string response = PostForm(TokenEndpoint, new Dictionary<string, string>
                {
                    { "client_id", ClientId },
                    { "client_secret", ClientSecret },
                    { "grant_type", "refresh_token" },
                    { "refresh_token", refreshToken }
                });

                string accessToken = ExtractString(response, "access_token");
                if (string.IsNullOrEmpty(accessToken))
                {
                    Log("Twitch token refresh failed (will fall back to re-authorization): " + response);
                    return null;
                }

                return new TokenResult
                {
                    AccessToken = accessToken,
                    RefreshToken = ExtractString(response, "refresh_token"),
                    ExpiresIn = ExtractInt(response, "expires_in", 14400)
                };
            }
            catch (Exception ex)
            {
                Log("Twitch token refresh error: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Checks whether an access token is still valid and, if so, returns the
        /// associated login name (handy so the user doesn't have to set a username).
        /// </summary>
        public ValidationResult Validate(string accessToken)
        {
            var result = new ValidationResult { Valid = false, Login = null };
            if (string.IsNullOrEmpty(accessToken))
                return result;

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, ValidateEndpoint))
                {
                    // The /validate endpoint expects "OAuth <token>", not "Bearer".
                    request.Headers.Add("Authorization", "OAuth " + accessToken);
                    HttpResponseMessage response = Http.SendAsync(request).GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                        return result;

                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    result.Valid = true;
                    result.Login = ExtractString(body, "login");
                    return result;
                }
            }
            catch
            {
                return result;
            }
        }

        /// <summary>
        /// Polls the listener until Twitch redirects back with a code/error, responding
        /// to any stray requests (e.g. favicon) with a benign page so the browser tab
        /// isn't left hanging. Returns the raw query string, or null on timeout.
        /// </summary>
        private string WaitForRedirect(TcpListener listener, int timeoutSeconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (!listener.Pending())
                {
                    Thread.Sleep(200);
                    continue;
                }

                using (TcpClient client = listener.AcceptTcpClient())
                using (NetworkStream stream = client.GetStream())
                {
                    // We only need the request line: "GET /?code=...&state=... HTTP/1.1"
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    string requestLine = reader.ReadLine();

                    string query = null;
                    if (!string.IsNullOrEmpty(requestLine))
                    {
                        string[] tokens = requestLine.Split(' ');
                        if (tokens.Length >= 2)
                        {
                            string path = tokens[1];
                            int q = path.IndexOf('?');
                            if (q >= 0)
                                query = path.Substring(q + 1);
                        }
                    }

                    bool hasResult = query != null && (query.Contains("code=") || query.Contains("error="));

                    string body = hasResult
                        ? "<html><body style='font-family:sans-serif;text-align:center;margin-top:3em'>"
                          + "<h2>Audica is connecting to Twitch.</h2><p>You can close this tab and return to the game.</p></body></html>"
                        : "<html><body style='font-family:sans-serif;text-align:center;margin-top:3em'>"
                          + "<h2>Waiting for Twitch authorization...</h2></body></html>";

                    byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                    string headers = "HTTP/1.1 200 OK\r\n" +
                                     "Content-Type: text/html; charset=utf-8\r\n" +
                                     "Content-Length: " + bodyBytes.Length + "\r\n" +
                                     "Connection: close\r\n\r\n";
                    byte[] headerBytes = Encoding.ASCII.GetBytes(headers);

                    try
                    {
                        stream.Write(headerBytes, 0, headerBytes.Length);
                        stream.Write(bodyBytes, 0, bodyBytes.Length);
                        stream.Flush();
                    }
                    catch { /* browser may have closed; ignore */ }

                    if (hasResult)
                        return query;
                    // otherwise keep waiting for the real redirect
                }
            }
            return null;
        }

        private static void OpenBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // Fallback for runtimes where UseShellExecute isn't honored.
                try { Process.Start(url); }
                catch { /* user can copy the URL printed to the log */ }
            }
        }

        private string PostForm(string url, Dictionary<string, string> fields)
        {
            try
            {
                using (var content = new FormUrlEncodedContent(fields))
                {
                    HttpResponseMessage response = Http.PostAsync(url, content).GetAwaiter().GetResult();
                    return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                Log("HTTP request to " + url + " failed: " + ex.Message);
                return null;
            }
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var dict = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(query)) return dict;

            foreach (string pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                if (eq < 0)
                {
                    dict[Uri.UnescapeDataString(pair)] = "";
                    continue;
                }
                string k = Uri.UnescapeDataString(pair.Substring(0, eq));
                string v = Uri.UnescapeDataString(pair.Substring(eq + 1));
                dict[k] = v;
            }
            return dict;
        }

        // --- tiny, dependency-free JSON helpers (the responses here are flat) ---

        private static string ExtractString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            try { return Regex.Unescape(m.Groups[1].Value); }
            catch { return m.Groups[1].Value; }
        }

        private static int ExtractInt(string json, string key, int fallback)
        {
            if (string.IsNullOrEmpty(json)) return fallback;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\\d+)");
            int v;
            return (m.Success && int.TryParse(m.Groups[1].Value, out v)) ? v : fallback;
        }

        private static void Log(string msg)
        {
            Melon<TwitchConnectorMod>.Logger.Msg(msg);
        }
    }
}
