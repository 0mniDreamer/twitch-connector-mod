using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MelonLoader;

namespace TwitchConnectorMod
{
    /// <summary>
    /// Handles Twitch OAuth using the Implicit Grant Flow with a local loopback
    /// redirect. The user clicks "Connect with Twitch" on a local page, approves on
    /// twitch.tv, and the token is captured automatically.
    ///
    /// Why implicit: it requires NO client secret (the Twitch app is registered as a
    /// Public client), so nothing sensitive ships in the mod at all. The tradeoff is
    /// that implicit tokens cannot be refreshed - when the token eventually expires,
    /// the mod simply re-runs this one-click browser flow.
    ///
    /// Mechanics note: Twitch returns the token in the URL *fragment*
    /// (#access_token=...), which browsers never send to a server. The local page
    /// therefore includes a small script that reads the fragment and forwards it to
    /// the listener as /token?access_token=...
    /// </summary>
    public class TwitchAuth
    {
        private const string AuthorizeEndpoint = "https://id.twitch.tv/oauth2/authorize";
        private const string ValidateEndpoint = "https://id.twitch.tv/oauth2/validate";

        // Scopes required to read from and write to chat over IRC.
        private const string Scopes = "chat:read chat:edit";

        private static readonly HttpClient Http = new HttpClient();

        public string ClientId;
        public int RedirectPort;

        public TwitchAuth(string clientId, int redirectPort)
        {
            this.ClientId = clientId;
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
            if (string.IsNullOrEmpty(ClientId))
            {
                Log("Twitch ClientId not set, cannot authorize. See the README.");
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
                        "). The port must be free and must match the redirect URL registered on Twitch.");
                    return null;
                }

                string state = Guid.NewGuid().ToString("N");
                string authUrl = AuthorizeEndpoint +
                    "?response_type=token" +
                    "&client_id=" + Uri.EscapeDataString(ClientId) +
                    "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                    "&scope=" + Uri.EscapeDataString(Scopes) +
                    "&state=" + state;

                // --- Step 2: open the browser to OUR local landing page, not Twitch.
                // The page asks the user to click "Connect with Twitch" so nothing
                // happens without an explicit user action.
                Log("Opening your browser to connect to Twitch...");
                Log("If it doesn't open automatically, paste this URL into any browser:");
                Log("  " + RedirectUri + "/");
                OpenBrowser(RedirectUri + "/");

                // --- Step 3: wait for the user to click Connect, authorize on Twitch,
                // and have the page forward the token back to our listener ---
                Dictionary<string, string> p = WaitForToken(listener, authUrl, 300);
                if (p == null)
                {
                    Log("Timed out waiting for Twitch authorization. Restart Audica to try again.");
                    return null;
                }

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

                string accessToken;
                if (!p.TryGetValue("access_token", out accessToken) || string.IsNullOrEmpty(accessToken))
                {
                    Log("Twitch did not return an access token.");
                    return null;
                }

                Log("Twitch authorization successful!");
                return new TokenResult { AccessToken = accessToken };
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
                    result.Login = MiniJson.ExtractString(body, "login");
                    return result;
                }
            }
            catch
            {
                return result;
            }
        }

        /// <summary>
        /// Serves a small local site while waiting for authorization:
        ///   - A combined landing/capture page: if the URL has no token fragment it
        ///     shows a "Connect with Twitch" button (explicit user consent); if Twitch
        ///     has redirected back with a token in the fragment, a small script
        ///     forwards it to /token so the mod can read it.
        ///   - Success page once the token arrives; declined page if the user cancels.
        /// Returns the parsed token/error parameters, or null on timeout.
        /// </summary>
        private Dictionary<string, string> WaitForToken(TcpListener listener, string authUrl, int timeoutSeconds)
        {
            // & must be entity-escaped inside an HTML attribute.
            string authHref = authUrl.Replace("&", "&amp;");

            const string pageStyle =
                "<style>body{font-family:sans-serif;background:#0e0e10;color:#efeff1;" +
                "display:flex;align-items:center;justify-content:center;height:100vh;margin:0}" +
                ".card{background:#18181b;padding:2.5em 3em;border-radius:12px;text-align:center;max-width:26em}" +
                "h2{margin-top:0}p{color:#adadb8}" +
                ".btn{display:inline-block;margin-top:1em;padding:0.8em 1.6em;background:#9147ff;" +
                "color:#fff;text-decoration:none;border-radius:6px;font-weight:bold}" +
                ".btn:hover{background:#772ce8}</style>";

            // The landing page doubles as the fragment-capture page. On load, if the
            // URL fragment contains a token or an error, it is forwarded to /token
            // (fragments are never sent to servers, so this hop is required). If not,
            // the Connect button is revealed.
            string landingPage =
                "<html><head><title>Connect to Twitch</title>" + pageStyle + "</head><body><div class='card'>" +
                "<div id='wait'><h2>One moment...</h2></div>" +
                "<div id='landing' style='display:none'>" +
                "<h2>Audica Twitch Connector</h2>" +
                "<p>This mod would like to connect to your Twitch account to read and send chat messages.</p>" +
                "<a class='btn' href='" + authHref + "'>Connect with Twitch</a>" +
                "<p style='font-size:0.85em'>You'll be taken to twitch.tv to approve. Close this tab to skip for now.</p>" +
                "</div>" +
                "<script>(function(){var h=location.hash;" +
                "if(h&&(h.indexOf('access_token=')>=0||h.indexOf('error=')>=0)){" +
                "location.replace('/token?'+h.substring(1));}else{" +
                "document.getElementById('wait').style.display='none';" +
                "document.getElementById('landing').style.display='block';}})();</script>" +
                "</div></body></html>";

            string successPage =
                "<html><head><title>Connected</title>" + pageStyle + "</head><body><div class='card'>" +
                "<h2>Audica is connecting to Twitch.</h2>" +
                "<p>You can close this tab and return to the game.</p>" +
                "</div></body></html>";

            string declinedPage =
                "<html><head><title>Authorization declined</title>" + pageStyle + "</head><body><div class='card'>" +
                "<h2>Authorization declined</h2>" +
                "<p>No problem - the mod won't connect to Twitch. Restart Audica if you change your mind.</p>" +
                "</div></body></html>";

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
                    // Don't let a stalled/broken connection hang the auth thread.
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;

                    // First line is what we need: "GET /token?access_token=... HTTP/1.1"
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    string requestLine = null;
                    try { requestLine = reader.ReadLine(); }
                    catch { /* stalled or bogus connection - skip it, keep waiting */ }

                    // Drain the remaining request headers before responding - some
                    // browsers abort if the server replies before the request is
                    // fully sent.
                    try
                    {
                        string headerLine;
                        while (!string.IsNullOrEmpty(headerLine = reader.ReadLine())) { }
                    }
                    catch { /* timeout/disconnect while draining; proceed */ }

                    string path = null;
                    string query = null;
                    if (!string.IsNullOrEmpty(requestLine))
                    {
                        string[] tokens = requestLine.Split(' ');
                        if (tokens.Length >= 2)
                        {
                            path = tokens[1];
                            int q = path.IndexOf('?');
                            if (q >= 0)
                            {
                                query = path.Substring(q + 1);
                                path = path.Substring(0, q);
                            }
                        }
                    }

                    // Twitch can also deliver a decline in the query string of the
                    // redirect itself (before any fragment forwarding).
                    bool isTokenRoute = path == "/token" && query != null;
                    bool queryHasError = query != null && query.Contains("error=");

                    Dictionary<string, string> parsed = null;
                    string body;
                    if (isTokenRoute || queryHasError)
                    {
                        parsed = ParseQuery(query);
                        body = parsed.ContainsKey("access_token") ? successPage : declinedPage;
                    }
                    else
                    {
                        body = landingPage;
                    }

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

                    if (parsed != null)
                        return parsed;
                    // otherwise (landing page / favicon) keep waiting
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

        private static void Log(string msg)
        {
            Melon<TwitchConnectorMod>.Logger.Msg(msg);
        }
    }
}
