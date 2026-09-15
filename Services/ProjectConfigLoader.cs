using System.Text.Json;
using OpenCodeTelegramBridge.Models;

namespace OpenCodeTelegramBridge.Services;

public sealed class ProjectConfigLoader
{
    public const string FileName = "telegram-bridge.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public ProjectConfigLoadResult Load(string projectRoot, string discoveredProjectName)
    {
        var root = GetCanonicalDirectory(projectRoot);
        var configPath = Path.Combine(root, FileName);
        if (!File.Exists(configPath)) return new ProjectConfigLoadResult(false, null, null);

        TelegramBridgeProjectConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<TelegramBridgeProjectConfig>(File.ReadAllText(configPath), JsonOptions);
        }
        catch (Exception ex)
        {
            return new ProjectConfigLoadResult(true, null, $"فایل {FileName} JSON معتبر نیست: {ex.Message}");
        }

        if (config == null) return new ProjectConfigLoadResult(true, null, $"فایل {FileName} خالی یا نامعتبر است.");
        var errors = ValidateAndResolve(config, root, discoveredProjectName);
        return errors.Count == 0
            ? new ProjectConfigLoadResult(true, config, null)
            : new ProjectConfigLoadResult(true, null, string.Join("\n", errors));
    }

    private static List<string> ValidateAndResolve(TelegramBridgeProjectConfig config, string root, string discoveredProjectName)
    {
        var errors = new List<string>();
        if (config.Version != 1) errors.Add("فقط version برابر 1 پشتیبانی می‌شود.");
        if (string.IsNullOrWhiteSpace(config.Name)) config.Name = discoveredProjectName;
        if (config.Sections == null) errors.Add("sections باید وجود داشته باشد.");
        if (config.Sections == null) return errors;

        var sectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in config.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Id)) errors.Add("id بخش نباید خالی باشد.");
            else if (!sectionIds.Add(section.Id)) errors.Add($"id بخش تکراری است: {section.Id}");

            if (string.IsNullOrWhiteSpace(section.Title)) errors.Add($"title بخش '{section.Id}' نباید خالی باشد.");
            if (string.IsNullOrWhiteSpace(section.Directory)) errors.Add($"directory بخش '{section.Id}' نباید خالی باشد.");
            else
            {
                var resolved = TryResolveSectionDirectory(root, section.Directory, out var error);
                if (resolved == null) errors.Add($"directory بخش '{section.Id}' نامعتبر است: {error}");
                else section.ResolvedDirectory = resolved;
            }

            section.Commands ??= [];
            var commandIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var command in section.Commands)
            {
                if (string.IsNullOrWhiteSpace(command.Id)) errors.Add($"id دستور در بخش '{section.Id}' نباید خالی باشد.");
                else if (!commandIds.Add(command.Id)) errors.Add($"id دستور تکراری در بخش '{section.Id}': {command.Id}");

                if (string.IsNullOrWhiteSpace(command.Title)) errors.Add($"title دستور '{command.Id}' نباید خالی باشد.");
                if (command.Type is not ("opencode-command" or "prompt")) errors.Add($"type دستور '{command.Id}' پشتیبانی نمی‌شود.");
                if (command.Type == "opencode-command")
                {
                    if (string.IsNullOrWhiteSpace(command.Command)) errors.Add($"command برای دستور '{command.Id}' نباید خالی باشد.");
                    else command.Command = command.Command.Trim().TrimStart('/');
                }
                if (command.Type == "prompt" && string.IsNullOrWhiteSpace(command.Text))
                    errors.Add($"text برای دستور prompt '{command.Id}' نباید خالی باشد.");
            }
        }
        return errors;
    }

    public static string? TryResolveSectionDirectory(string projectRoot, string sectionDirectory, out string? error)
    {
        error = null;
        if (Path.IsPathRooted(sectionDirectory))
        {
            error = "مسیر مطلق مجاز نیست.";
            return null;
        }

        var root = GetCanonicalDirectory(projectRoot);
        var combined = Path.GetFullPath(Path.Combine(root, sectionDirectory));
        if (!Directory.Exists(combined))
        {
            error = "پوشه وجود ندارد.";
            return null;
        }

        var resolved = GetCanonicalDirectory(combined);
        if (!IsInsideOrEqual(root, resolved))
        {
            error = "مسیر از ریشه پروژه خارج می‌شود.";
            return null;
        }
        if (ContainsSymlinkOrJunction(root, resolved))
        {
            error = "مسیر بخش شامل symlink یا junction است و برای جلوگیری از خروج از ریشه پروژه رد شد.";
            return null;
        }

        return resolved;
    }

    private static string GetCanonicalDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full)) full = new DirectoryInfo(full).FullName;
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsInsideOrEqual(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(root, candidate, comparison)) return true;
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison) && !Path.IsPathRooted(relative);
    }

    private static bool ContainsSymlinkOrJunction(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (string.IsNullOrWhiteSpace(relative) || relative == ".") return false;

        var current = root;
        foreach (var part in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            if (!string.IsNullOrEmpty(info.LinkTarget)) return true;
        }
        return false;
    }
}
