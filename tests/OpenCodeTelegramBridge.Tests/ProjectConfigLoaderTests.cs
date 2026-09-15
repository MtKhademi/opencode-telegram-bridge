using OpenCodeTelegramBridge.Services;

namespace OpenCodeTelegramBridge.Tests;

public sealed class ProjectConfigLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "otb-tests-" + Guid.NewGuid().ToString("N"));

    public ProjectConfigLoaderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void MissingConfig_IsLegacyBehaviorNotError()
    {
        var result = new ProjectConfigLoader().Load(_root, "legacy");

        Assert.False(result.Exists);
        Assert.False(result.IsValid);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ValidConfig_ResolvesSectionDirectoryRelativeToConfigFile()
    {
        Directory.CreateDirectory(Path.Combine(_root, "backend"));
        File.WriteAllText(Path.Combine(_root, ProjectConfigLoader.FileName), """
        {
          "version": 1,
          "name": "TenantForge",
          "sections": [
            {
              "id": "backend",
              "title": "بک‌اند",
              "directory": "./backend",
              "commands": [
                { "id": "task", "title": "اجرای تسک", "type": "opencode-command", "command": "/backend-task", "askForArguments": true },
                { "id": "status", "title": "وضعیت", "type": "prompt", "text": "Review status" }
              ]
            }
          ]
        }
        """);

        var result = new ProjectConfigLoader().Load(_root, "fallback");

        Assert.True(result.IsValid);
        Assert.Equal("TenantForge", result.Config!.Name);
        Assert.Equal(Path.Combine(_root, "backend"), result.Config.Sections[0].ResolvedDirectory);
        Assert.Equal("backend-task", result.Config.Sections[0].Commands[0].Command);
        Assert.True(result.Config.Sections[0].Commands[0].AskForArguments);
    }

    [Fact]
    public void InvalidVersion_IsRejected()
    {
        File.WriteAllText(Path.Combine(_root, ProjectConfigLoader.FileName), "{ \"version\": 2, \"sections\": [] }");

        var result = new ProjectConfigLoader().Load(_root, "project");

        Assert.True(result.Exists);
        Assert.False(result.IsValid);
        Assert.Contains("version", result.Error);
    }

    [Fact]
    public void DuplicateSectionIds_AreRejected()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        File.WriteAllText(Path.Combine(_root, ProjectConfigLoader.FileName), """
        { "version": 1, "sections": [
          { "id": "main", "title": "یک", "directory": ".", "commands": [] },
          { "id": "MAIN", "title": "دو", "directory": "./a", "commands": [] }
        ] }
        """);

        var result = new ProjectConfigLoader().Load(_root, "project");

        Assert.False(result.IsValid);
        Assert.Contains("تکراری", result.Error);
    }

    [Fact]
    public void AbsoluteAndEscapingSectionDirectories_AreRejected()
    {
        File.WriteAllText(Path.Combine(_root, ProjectConfigLoader.FileName), """
        { "version": 1, "sections": [
          { "id": "abs", "title": "abs", "directory": "/tmp", "commands": [] },
          { "id": "escape", "title": "escape", "directory": "..", "commands": [] }
        ] }
        """);

        var result = new ProjectConfigLoader().Load(_root, "project");

        Assert.False(result.IsValid);
        Assert.Contains("مطلق", result.Error);
        Assert.Contains("خارج", result.Error);
    }

    [Fact]
    public void SectionDirectorySymlinkEscape_IsRejected()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(Path.GetTempPath(), "otb-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var link = Path.Combine(_root, "linked");
            Directory.CreateSymbolicLink(link, outside);
            File.WriteAllText(Path.Combine(_root, ProjectConfigLoader.FileName), """
            { "version": 1, "sections": [
              { "id": "linked", "title": "linked", "directory": "./linked", "commands": [] }
            ] }
            """);

            var result = new ProjectConfigLoader().Load(_root, "project");

            Assert.False(result.IsValid);
            Assert.Contains("symlink", result.Error);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
