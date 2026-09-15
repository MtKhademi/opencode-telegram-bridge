# Vendored dependency: WTelegramClient

`WTelegramClient-src/` is a source copy of [wiz0u/WTelegramClient](https://github.com/wiz0u/WTelegramClient)
(MIT licensed), built here as a plain `ProjectReference` instead of the NuGet package.

Why: WTelegramClient's own upstream `.csproj` conditionally references a Roslyn source
generator project to (re)generate the `TL.*.cs` files from Telegram's schema. Those generated
files are already checked into the upstream repo, so this trimmed `.csproj` just compiles them
directly — no generator, no NuGet packages, fully offline build.

## The one patch applied

`Client.Helpers.cs`, inside `ChannelMessagesFromUrl(...)`, originally had:

```csharp
chats?[chatId] = chat;
```

Null-conditional assignment (`x?[i] = y` / `x?.y = z`) is a C# 14 feature. At the time this patch
was applied the project targeted `net8.0` with an older compiler, so it was rewritten as:

```csharp
if (chats != null)
    chats[chatId] = chat;
```

Same behavior, compiles on any C# version.

## Updating

To pick up a newer WTelegramClient release, delete `WTelegramClient-src/` and re-copy `src/` from
a fresh clone of the upstream repo, then re-apply the one patch above and drop in the trimmed
`WTelegramClient.csproj` from this folder (or just diff it against upstream's `src/WTelegramClient.csproj`).
