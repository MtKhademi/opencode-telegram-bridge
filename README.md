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

For everyday use you don't want to run `dotnet run` by hand and keep a terminal open — see
**"Running it as a service"** below to have it start automatically and stay up persistently,
like an always-on background service.

### Publishing a standalone binary

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o out
./out/OpenCodeTelegramBridge
```

(swap `linux-x64` for `win-x64` if you ever run this natively on Windows.) The published binary
is self-contained (bundles its own .NET runtime) and resolves `data/`, `wwwroot/`, and
`appsettings.json` relative to **its own location** — not the shell's current directory — so it
behaves the same no matter where it's launched from (a systemd service, a different terminal,
etc.). `data/` isn't part of the publish output itself; it's created fresh next to the binary on
first run (or copy an existing one over, e.g. to carry over your saved config).

## Running it as a service (WSL2 + systemd)

The cleanest way to keep this always up in the background — so you never have to remember to
start it, and it survives closing terminals / restarting WSL — is a `systemd` **user-independent**
service, using WSL2's built-in systemd support.

### 1. Enable systemd in WSL (skip if already on)

Check first:

```bash
systemctl --version
```

If that errors with something like *"System has not been booted with systemd"*, enable it:

```bash
sudo tee -a /etc/wsl.conf > /dev/null <<'EOF'
[boot]
systemd=true
EOF
```

Then, **from Windows PowerShell** (not inside WSL):

```powershell
wsl --shutdown
```

Wait a few seconds, then reopen your WSL terminal — `systemctl --version` should now work.

### 2. Publish a self-contained build

```bash
cd OpenCodeTelegramBridge
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ~/apps/opencode-telegram-bridge
```

If you already have a working `data/config.json` from a previous `dotnet run`, copy it over so
the service comes up pre-configured:

```bash
mkdir -p ~/apps/opencode-telegram-bridge/data
cp data/config.json ~/apps/opencode-telegram-bridge/data/config.json
```

### 3. Create the systemd unit

Create `/etc/systemd/system/opencode-telegram-bridge.service` (replace `<username>` with the
output of `whoami`):

```ini
[Unit]
Description=OpenCode Telegram Bridge
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=/home/<username>/apps/opencode-telegram-bridge/OpenCodeTelegramBridge
WorkingDirectory=/home/<username>/apps/opencode-telegram-bridge
Restart=always
RestartSec=5
User=<username>
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

### 4. Enable and start it

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now opencode-telegram-bridge
systemctl status opencode-telegram-bridge   # should say "active (running)"
journalctl -u opencode-telegram-bridge -f   # watch it come up, Ctrl+C when satisfied
```

From now on it starts automatically every time WSL boots — no `dotnet run`, no open terminal
required. Whenever you update the code, re-run the `dotnet publish` command from step 2 and then
`sudo systemctl restart opencode-telegram-bridge`.

### 5. (Optional) Start WSL itself automatically at Windows login

Steps 1–4 make the bridge start as soon as **WSL** boots — but WSL2 itself only boots when
something triggers it (opening a WSL terminal, a VS Code window attached to WSL, or OpenCode
launched via WSL). If you want the bridge running even before you've opened anything in WSL that
session (e.g. right after turning on your PC), have Windows launch WSL automatically at login:

1. Open **Task Scheduler** on Windows.
2. **Create Task…** (not "Basic Task", so you get the full options).
3. **General** tab: name it e.g. "Start WSL", and under Security options pick "Run whether user
   is logged on or not" if you want it to work even without logging in interactively (otherwise
   the default is fine).
4. **Triggers** tab → **New…** → "At log on" (optionally restrict to your user).
5. **Actions** tab → **New…** → Program/script: `wsl.exe`, Add arguments: leave blank (or use
   `-d <DistroName> -e true` to target a specific distro, e.g. `-d Ubuntu -e true`).
6. Save. Next time you log into Windows, WSL boots automatically, systemd starts, and this
   service comes up with it — all before you've opened a single terminal.

## Using it from Telegram

Once connected, message your bot:

- `/start` or `/menu` — open the Persian button-based main menu without changing your current
  project/model selection
- `/projects` — show discovered projects as selectable buttons
- `/use <name>` — select a project for this chat (if multiple projects share the same name, the
  bot shows matching buttons instead of guessing)
- `/models` — show model providers/models as buttons using the live `opencode models` output
- `/model <provider/model>` — select a model by text
- `/model default` — reset back to OpenCode's default model
- `/status` — show your current project/model and whether its server is running
- `/stop` — stop the `opencode serve` instance for your selected project
- `/abort` — cancel the bridge's in-progress request and best-effort call OpenCode's abort
  endpoint when the OpenCode session id is known
- anything else you type is sent straight to OpenCode as a prompt; you'll get a "🤔 Thinking…"
  placeholder followed by the final response (long responses are split into multiple messages).
  The last response message includes a **🏠 منوی اصلی** button.

The menu UI is Persian for user-facing labels/messages, while project names, model identifiers,
and text commands stay unchanged. Project/section selection does not start an OpenCode server; the
server still starts lazily on the first prompt or predefined action for that context.

### Per-project Telegram configuration

A project can opt into section/action buttons by adding `telegram-bridge.json` directly in its
root. Projects without this file keep the legacy behavior: prompts run from the discovered project
root.

Version 1 example:

```json
{
  "version": 1,
  "name": "TenantForge",
  "sections": [
    {
      "id": "backend",
      "title": "بک‌اند",
      "directory": "./backend",
      "commands": [
        {
          "id": "backend-task",
          "title": "اجرای تسک بک‌اند",
          "type": "opencode-command",
          "command": "backend-task",
          "askForArguments": true
        }
      ]
    },
    {
      "id": "main",
      "title": "مدیریت پروژه",
      "directory": ".",
      "commands": [
        {
          "id": "review-status",
          "title": "بررسی وضعیت پروژه",
          "type": "prompt",
          "text": "Review the project instructions and current state. Summarize progress, blockers, and the next recommended task."
        }
      ]
    }
  ]
}
```

Rules:

- `version` is required and only `1` is supported.
- `name` is optional and defaults to the discovered project folder name.
- Section IDs must be unique per project; command IDs must be unique per section.
- Section `title` and `directory` are required. `commands` may be empty.
- Section directories are resolved relative to the config file's directory, must exist, must stay
  inside the project root, and symlink/junction segments are rejected.
- Supported command types are `opencode-command` and `prompt`; arbitrary shell execution is not
  supported.
- `opencode-command.command` is stored without a leading slash. The bridge executes it through
  `POST /session/{id}/command`, passing command name and arguments separately. It does not send
  slash-command text as a normal prompt. OpenCode resolves the command's configured agent (for
  example `agent: backend-mentor`) and that agent's permissions in the selected working directory.
- `askForArguments` defaults to false; when true, the next normal Telegram message supplies the
  arguments. Do not enter the command name again. Send `-` to run it without arguments, or `/menu`
  to cancel. For a no-argument task button, set `askForArguments` to false.
- The command event subscription starts before dispatch so permission requests can be answered
  while the command HTTP request is pending. Final text comes from the completed command response.
  Missing commands/API errors are reported without silently falling back to ordinary prompts.

### Command permissions and deployment

This routing fix applies to every configured project; it does not grant permissions globally.
Permissions remain in each project's OpenCode configuration and agent definitions. TenantForge's
`backend-task` command selects `backend-mentor`, whose tracked bash policy already allows routine
commands while denying specific destructive operations. Sending `/backend-task` as ordinary prompt
text could instead use the default `plan` agent and trigger repeated approval requests.

After updating and republishing the bridge, restart it when no task is active and retry the command
button. Ensure the working clone also contains the current `.opencode/commands` and `.opencode/agents`
files. The local OpenCode version must support the documented command endpoint, string model ID,
`server.connected` event, and existing `permission.asked` events. See the
[OpenCode server API](https://opencode.ai/docs/server/#messages).

For a live smoke test, use a harmless custom command assigned to an agent with a known bash policy.
Run it through its Telegram button and verify the agent, expected approval behavior, and final
response; repeat with a command that requires approval. A successful HTTP reply is not proof that
all application work succeeded. The runner also checks OpenCode's returned message error.

The bridge still creates a new OpenCode session per request. `Always` approvals are session-scoped,
not permanent configuration. Multi-turn session continuity is a separate limitation; this change
does not turn permission approvals or normal replies into a persistent task conversation.

When a configured project is selected, the bot asks you to choose a section. Ordinary prompts and
predefined actions then run in the selected section's resolved working directory. Switching projects
clears the selected section and any pending command-argument input; model selection is preserved.

Menu buttons use short-lived server-side snapshots so Telegram callback data stays under the
64-byte Bot API limit and never embeds full project paths or long model IDs. Old buttons can expire
(after about 15 minutes) or become invalid after an app restart/configuration change; if that
happens, open `/menu` or refresh the relevant list. Changing project/model while a prompt is
running affects the next prompt only; the running prompt keeps the project/model snapshot it started
with.

If a project's `opencode.json` gates a tool behind `"ask"` permission (e.g. `bash`, `edit`),
OpenCode will pause and wait for approval. You'll get a separate Telegram permission message
describing the action/resources with three inline buttons — **✅ Once**, **🔁 Always**, **❌ Reject**
— tap one and the decision is relayed back to OpenCode so it can continue (or stop) accordingly.
Permission messages are never replaced by menu navigation.

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
