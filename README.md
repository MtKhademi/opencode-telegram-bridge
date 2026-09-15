# OpenCode Telegram Bridge

A self-hosted, single-process C#/.NET app that lets you drive [OpenCode](https://opencode.ai)
from Telegram — with a small local web dashboard. It uses the standard Telegram **Bot API**
(via the [Telegram.Bot](https://github.com/TelegramBots/Telegram.Bot) NuGet package), so all you
need is a bot token — no `api_id`/`api_hash` from my.telegram.org. When `api.telegram.org` itself
is blocked/filtered on your network, just paste a **VLESS proxy link** into the dashboard — the
app parses it, downloads [xray-core](https://github.com/XTLS/Xray-core) automatically on first
use, spins up a local tunnel, and routes Telegram traffic through it. No manual xray-core setup,
no manual SOCKS5 configuration — it's all automatic.

Only **VLESS** links are supported for now (not VMess/Trojan/Shadowsocks). xray-core is
downloaded from [XTLS/Xray-core](https://github.com/XTLS/Xray-core)'s GitHub releases the first
time you set a proxy link, so that first save needs outbound internet access once; after that
it's cached under `data/bin/xray` and reused.

## Why this exists / how it's different

Everything here — spawning `opencode serve` per project, creating an OpenCode session, sending
the prompt, and reading the streamed response back off `/event` — follows the same approach as
the Node-based [`opencode-remote-telegram`](https://github.com/weisser-dev/opencode-remote-telegram),
just re-implemented in C# on top of the standard Telegram.Bot library, plus a built-in VLESS
tunnel manager for when Telegram is blocked directly on this network.

## Prerequisites

1. **.NET SDK** matching this project's `<TargetFramework>` (see `OpenCodeTelegramBridge.csproj`).
   - Ubuntu/WSL: `sudo apt-get install -y dotnet-sdk-8.0` (or whichever version matches).
   - Windows: install from the official .NET site, or run this inside WSL alongside OpenCode.
2. **OpenCode** installed and on `PATH`, wherever you'll run this app. If your OpenCode setup
   lives in WSL (recommended by OpenCode's own docs — native Windows OpenCode has known
   `EPERM`/binary-execution issues), run this bridge in the **same WSL environment**, not on
   Windows directly.
3. A **bot token** from [@BotFather](https://t.me/BotFather).
4. (Optional) If Telegram is blocked directly on this network, a **VLESS subscription link**
   (`vless://uuid@host:port?...`) from your proxy provider. Just paste it into the dashboard —
   everything else (downloading xray-core, generating its config, spinning up the local tunnel)
   is handled automatically. Note: automatic xray-core download currently only supports
   linux-x64 (i.e. WSL/Linux, which is how this app is meant to run anyway).

## Build & run

```bash
cd OpenCodeTelegramBridge
dotnet build
dotnet run
```

The dashboard is served at **http://localhost:5080**. Open it, fill in the Settings card
(bot token, optional VLESS proxy link, allowed Telegram user IDs, and the folder(s) containing
your projects), click **Save & reconnect**, and watch the status dot and live log at the bottom
turn green / show "Connected as @yourbot". If you pasted a proxy link, the live log will also
show xray-core being downloaded (first time only) and the local tunnel starting up.

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
- `data/bin/xray` — the downloaded xray-core binary, cached after the first proxy link save
- `data/xray-config-<port>.json` — the generated xray-core config for the currently running
  tunnel (deleted when the tunnel stops)

## Security notes

- Always set **Allowed Telegram user IDs** to your own numeric ID (get it from
  [@userinfobot](https://t.me/userinfobot)) — an empty list lets *anyone* who finds your bot run
  arbitrary prompts (and therefore arbitrary code, indirectly) against your machine.
- The dashboard itself has no login and binds to `0.0.0.0:5080` — fine on a trusted machine/LAN,
  but don't expose port 5080 to the open internet as-is.
- Your bot token — and proxy link, if set, which embeds your VLESS UUID — are stored in plain
  text in `data/config.json` (same as basically every self-hosted bot framework). Keep that file
  private, and revoke/regenerate the bot token via BotFather (or rotate the VLESS UUID with your
  proxy provider) if it ever leaks.

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
