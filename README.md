# TwitchConnectorMod - A basic Twitch IRC implementation for Audica

This mod connects to twitch chat and exposes two-way twitch chat communication to other Audica mods.

## Prerequisites

This requires MelonLoader 0.5.3 or later be installed on your Audica installation.  Check out https://melonwiki.xyz for installation instructions.

## What changed (v1.5.0)

The old setup told you to grab an OAuth token from `https://twitchapps.com/tmi/`. **That site has been shut down, so that method no longer works.** Login now happens in your browser using Twitch's implicit grant flow: you start Audica, a local page opens asking if you want to connect, you click **Connect with Twitch**, approve on twitch.tv, and the mod connects automatically.

This flow needs **no client secret at all** - nothing sensitive ships in the mod. The tradeoff is that these tokens can't be silently refreshed: when a token eventually expires, the mod just shows the one-click connect page again.

## Installation (end users)

1. Download the latest TwitchConnectorMod.zip from the releases section of this repository and unzip it into your Audica installation folder.

2. Start Audica. Your browser will open to a local page asking if you want to connect the mod to Twitch. Nothing happens until you click **Connect with Twitch** - you'll then be taken to twitch.tv, where you click **Authorize**. The page will confirm and you can close the tab. In the MelonLoader console you'll see:

   ```
   Twitch authorization successful!
   Connected to Twitch.
   ```

The mod stores the token and reconnects silently on later launches. When the token eventually expires, the connect page simply appears again - one click and you're back.

If your browser doesn't open automatically, the URL is also printed in the MelonLoader console - just paste it into any browser.

### Optional settings

These live under `[TwitchConnector]` in `UserData\MelonPreferences.cfg`. The login token is stored separately in `UserData\TwitchConnector.json`:

- `Username` - your twitch username. Optional; detected automatically after you authorize.
- `Channel` - the channel to join. Defaults to your username.
- `OAuthToken` - optional manual override. Leave blank to use the browser login. Only set this if you already have a chat token from another generator; `oauth:` prefix optional.
- `LogTwitchMessages` / `LogRawTwitchMessages` - logging toggles.

If Twitch ever rejects the stored token, the mod clears it and shows the connect page again on the next launch.

> Upgrading from an older build? You can delete any `ClientId`, `ClientSecret`, `RedirectPort`, `AccessToken`, and `RefreshToken` lines left in your `MelonPreferences.cfg` - they're no longer used.

## Building the mod (maintainers)

The Twitch app's Client ID is a constant at the top of `TwitchConnectorMod.cs` (a Client ID is a public identifier - it is not a secret and needs no protection). To create the Twitch app it comes from:

1. Go to https://dev.twitch.tv/console/apps and click **Register Your Application**.
2. **Name:** anything (e.g. "Audica Twitch Connector"). **OAuth Redirect URLs:** `http://localhost:3000` (must match `RedirectPort` exactly). **Category:** Game Integration. **Client Type:** **Public** (the implicit grant flow uses no secret; Public is correct and Twitch will show no "New Secret" button - that's expected).
3. Click **Create**, then **Manage**, and copy the **Client ID** into the constant.
4. Build.

## Usage with Mods

This section applies to modders that want to send/receive messages from Twitch chat.

### Receiving Twitch messages

Create a method to process incoming messages:

```csharp
void OnChatMessage(Object sender, TwitchConnectorMod.ParsedTwitchMessage eventArgs)
{
    // process the message how you see fit.
}
```

See the ParsedTwitchMessage class for details on structure.

Register the event handler with the TwitchConnectorMod:

```csharp
TwitchConnectorMod.TwitchConnectorMod.AddChatMsgReceivedEventHandler(OnChatMessage);
```

### Sending Twitch messages

Call the SendMessage method:

```csharp
TwitchConnectorMod.TwitchConnectorMod.SendMessage("Greetings!");
```

Note that this is a VERY basic IRC client, and does not currently use Twitch Pub/Sub or any other Twitch features.
