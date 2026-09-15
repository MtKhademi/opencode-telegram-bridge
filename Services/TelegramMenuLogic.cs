using System.Text;

namespace OpenCodeTelegramBridge.Services;

public static class TelegramMenuLogic
{
    public const int CallbackDataLimitBytes = 64;
    public const int PageSize = 8;

    public static List<string> BuildProjectLabels(IReadOnlyList<(string Name, string Path)> projects)
    {
        var duplicateNames = projects
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return projects.Select(p => duplicateNames.Contains(p.Name)
            ? $"{p.Name} ({Path.GetFileName(Path.GetDirectoryName(p.Path.TrimEnd('/')) ?? "")})"
            : p.Name).ToList();
    }

    public static IReadOnlyList<T> Page<T>(IReadOnlyList<T> items, int page, int pageSize = PageSize)
    {
        if (items.Count == 0) return Array.Empty<T>();
        page = Math.Clamp(page, 0, MaxPage(items.Count, pageSize));
        return items.Skip(page * pageSize).Take(pageSize).ToList();
    }

    public static int MaxPage(int count, int pageSize = PageSize) => Math.Max(0, (count - 1) / pageSize);

    public static bool IsCallbackDataValid(string data) => Encoding.UTF8.GetByteCount(data) <= CallbackDataLimitBytes;

    public static CallbackRoute ParseCallback(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return CallbackRoute.Invalid;
        var parts = data.Split(':');
        return parts[0] switch
        {
            "perm" when parts.Length == 3 && parts[1] is "once" or "always" or "reject" => new CallbackRoute("perm", parts[1], parts[2]),
            "menu" when parts.Length >= 2 => new CallbackRoute("menu", parts[1], parts.Length > 2 ? parts[2] : null),
            "project" when parts.Length >= 2 => new CallbackRoute("project", parts[1], parts.Length > 2 ? parts[2] : null),
            "model" when parts.Length >= 2 => new CallbackRoute("model", parts[1], parts.Length > 2 ? parts[2] : null),
            "section" when parts.Length >= 2 => new CallbackRoute("section", parts[1], parts.Length > 2 ? parts[2] : null),
            "command" when parts.Length >= 2 => new CallbackRoute("command", parts[1], parts.Length > 2 ? parts[2] : null),
            _ => CallbackRoute.Invalid,
        };
    }

    public static Dictionary<string, List<string>> GroupModelsByProvider(IEnumerable<string> models)
    {
        return models
            .Select(m => (Model: m, Parsed: OpenCodeManager.ParseModel(m)))
            .Where(x => x.Parsed != null)
            .GroupBy(x => x.Parsed!.Value.providerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Model).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record CallbackRoute(string Prefix, string Action, string? Value)
{
    public static CallbackRoute Invalid { get; } = new("", "", null);
    public bool IsValid => !string.IsNullOrEmpty(Prefix);
}
