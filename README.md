# OpenCode Telegram Bridge

A self-hosted, single-process C#/.NET app that lets you drive [OpenCode](https://opencode.ai)
from Telegram — with a small local web dashboard, and **native MTProxy support** so it still
works when `api.telegram.org` itself is blocked/filtered on your network but a Telegram MTProxy
is reachable.

## Why this exists / how it's different

Most Telegram bot tooling (including the Node-based
[`opencode-remote-telegram`](https://github.com/weisser-dev/opencode-remote-telegram)) talks to
Telegram over the classic **HTTP Bot API**. That API has no concept of Telegram's MTProxy
protocol — MTProxy only works for clients that speak raw **MTProto** (the protocol Telegram's own
apps use), and pure HTTP libraries like `fetch`/`undici`/`grammy` can't tunnel through it.

This project talks to Telegram directly over **MTProto** using
[WTelegramClient](https://github.com/wiz0u/WTelegramClient) (a pure C#/.NET MTProto
implementation), so it can connect via:
- a direct connection (works when Telegram isn't blocked), **or**
- a Telegram **MTProxy** link (`https://t.me/proxy?server=...&port=...&secret=...`) — the same
  kind of proxy link the Telegram app itself uses, which is exactly what a lot of restrictive
  networks still let through even when `api.telegram.org` is firewalled directly.

Everything else — spawning `opencode serve` per project, creating an OpenCode session, sending
the prompt, and reading the streamed response back off `/event` — follows the same approach as
`opencode-remote-telegram`, just re-implemented in C#.

**No external NuGet packages are required.** WTelegramClient is vendored as source under
`vendor/WTelegramClient-src` and built as a plain project reference, so the whole thing compiles
offline once you have the .NET SDK. (One line was patched — see `vendor/README.md` — to compile
under a C# language version prior to 14.)

## Prerequisites

1. **.NET 8 SDK or later.**
   - Ubuntu/WSL: `sudo apt-get install -y dotnet-sdk-8.0` (or a newer version if available).
   - Windows: install from the official .NET site, or run this inside WSL alongside OpenCode.
2. **OpenCode** installed and on `PATH`, wherever you'll run this app. If your OpenCode setup
   lives in WSL (recommended by OpenCode's own docs — native Windows OpenCode has known
   `EPERM`/binary-execution issues), run this bridge in the **same WSL environment**, not on
   Windows directly.
3. A Telegram **api_id** / **api_hash** pair from <https://my.telegram.org/apps> — this is
   required by the MTProto protocol itself (it's how any raw Telegram client identifies itself),
   separate from and in addition to your bot token. Free, takes a minute to create.
4. A **bot token** from [@BotFather](https://t.me/BotFather).
5. (Optional) An **MTProxy link** if Telegram is blocked on this network. This is the same link
   you'd add in Telegram's own app under Settings → Data and Storage → Proxy → Add Proxy. It
   looks like `https://t.me/proxy?server=1.2.3.4&port=443&secret=abcdef...`.

## Build & run

```bash
cd OpenCodeTelegramBridge
dotnet build
dotnet run
```

The dashboard is served at **http://localhost:5080**. Open it, fill in the Settings card
(API ID/Hash, bot token, optional MTProxy link, allowed Telegram user IDs, and the folder(s)
containing your projects), click **Save & reconnect**, and watch the status dot and live log at
the bottom turn green / show "Connected as @yourbot".

To keep it running in the background (so it survives closing the terminal), use `pm2`,
`systemd`, or `nohup dotnet run &` — ask me if you'd like a ready-made systemd unit file.

### Publishing a standalone binary (optional)

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o out
./out/OpenCodeTelegramBridge
```

(swap `linux-x64` for `win-x64` if you ever run this natively on Windows.)

## Using it from Telegram

Once connected, message your bot:

- `/projects` — list every subfolder of your configured project folder(s)
- `/use <name>` — select a project for this chat
- `/models` — list models available via `opencode models`
- `/model <provider/model>` — select a model (optional — omit to use OpenCode's default)
- `/status` — show your current project/model and whether its server is running
- `/stop` — stop the `opencode serve` instance for your selected project
- `/abort` — cancel an in-progress prompt
- anything else you type is sent straight to OpenCode as a prompt; you'll get a "🤔 Thinking…"
  placeholder followed by the final response (long responses are split into multiple messages).

## Where things are stored

Everything lives under `data/`, created next to the executable on first run:
- `data/config.json` — your settings (also editable via the dashboard)
- `data/bridge.session` — WTelegramClient's own encrypted session state, so you don't have to
  re-authenticate the bot on every restart

## Security notes

- Always set **Allowed Telegram user IDs** to your own numeric ID (get it from
  [@userinfobot](https://t.me/userinfobot)) — an empty list lets *anyone* who finds your bot run
  arbitrary prompts (and therefore arbitrary code, indirectly) against your machine.
- The dashboard itself has no login and binds to `0.0.0.0:5080` — fine on a trusted machine/LAN,
  but don't expose port 5080 to the open internet as-is.
- Your bot token and API hash are stored in plain text in `data/config.json` (same as basically
  every self-hosted bot framework). Keep that file private, and revoke/regenerate the bot token
  via BotFather if it ever leaks.

## v1 scope (by design) / ideas for v2

This first version deliberately keeps things minimal and clean rather than trying to match every
feature of `opencode-remote-telegram` on day one:

- **Private chats only** (no groups/channels yet).
- **One active project session at a time per chat** — starting a new prompt while one is running
  cancels the previous one (mirrors the upstream tool's behavior).
- No prompt queueing, no cost/token stats, no OpenCode Desktop integration, no `/diff` or `/undo`.

All of these are natural follow-ups if you want to keep extending it — the `OpenCodeManager` /
`TelegramBridgeService` split was written to make that straightforward (e.g. per-chat prompt
queues would just be a list on `ChatState`, drained after each `session.idle`).
