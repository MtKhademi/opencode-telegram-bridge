using System.Text;
using OpenCodeTelegramBridge.Services;

namespace OpenCodeTelegramBridge.Tests;

public class TelegramMenuLogicTests
{
    [Fact]
    public void ProjectLabels_AddParentContextForDuplicateNames()
    {
        var projects = new List<(string Name, string Path)>
        {
            ("api", "/src/customer-a/api"),
            ("api", "/src/customer-b/api"),
            ("front", "/src/customer-a/front"),
        };

        var labels = TelegramMenuLogic.BuildProjectLabels(projects);

        Assert.Equal("api (customer-a)", labels[0]);
        Assert.Equal("api (customer-b)", labels[1]);
        Assert.Equal("front", labels[2]);
    }

    [Fact]
    public void Page_ReturnsEightItemsAndClampsPage()
    {
        var values = Enumerable.Range(1, 20).ToArray();

        Assert.Equal(8, TelegramMenuLogic.Page(values, 0).Count);
        Assert.Equal([9, 10, 11, 12, 13, 14, 15, 16], TelegramMenuLogic.Page(values, 1));
        Assert.Equal([17, 18, 19, 20], TelegramMenuLogic.Page(values, 99));
    }

    [Fact]
    public void CallbackDataLimit_UsesUtf8BytesIncludingPersianNames()
    {
        var shortTokenPayload = "project:select:abcdef123456:fedcba654321";
        var badPayload = "project:select:" + new string('پ', 40);

        Assert.True(TelegramMenuLogic.IsCallbackDataValid(shortTokenPayload));
        Assert.False(TelegramMenuLogic.IsCallbackDataValid(badPayload));
        Assert.True(Encoding.UTF8.GetByteCount(shortTokenPayload) <= TelegramMenuLogic.CallbackDataLimitBytes);
    }

    [Theory]
    [InlineData("perm:once:per_123", "perm", "once", "per_123")]
    [InlineData("perm:always:per_123", "perm", "always", "per_123")]
    [InlineData("perm:reject:per_123", "perm", "reject", "per_123")]
    [InlineData("menu:main", "menu", "main", null)]
    [InlineData("project:select:snap", "project", "select", "snap")]
    [InlineData("model:refresh", "model", "refresh", null)]
    [InlineData("section:select:snap", "section", "select", "snap")]
    [InlineData("command:run:snap", "command", "run", "snap")]
    public void ParseCallback_RoutesKnownCallbacks(string data, string prefix, string action, string? value)
    {
        var route = TelegramMenuLogic.ParseCallback(data);

        Assert.True(route.IsValid);
        Assert.Equal(prefix, route.Prefix);
        Assert.Equal(action, route.Action);
        Assert.Equal(value, route.Value);
    }

    [Theory]
    [InlineData("perm:bad:per_123")]
    [InlineData("prompt:this must not be forwarded")]
    [InlineData("")]
    public void ParseCallback_RejectsInvalidOrUnknownCallbacks(string data)
    {
        Assert.False(TelegramMenuLogic.ParseCallback(data).IsValid);
    }

    [Fact]
    public void GroupModelsByProvider_PreservesFullModelIdentifiers()
    {
        var grouped = TelegramMenuLogic.GroupModelsByProvider([
            "9router/company-combo-local",
            "9router/company-combo-gpt",
            "opencode/big-pickle",
            "invalid"
        ]);

        Assert.Equal(["9router/company-combo-gpt", "9router/company-combo-local"], grouped["9router"]);
        Assert.Equal(["opencode/big-pickle"], grouped["opencode"]);
        Assert.False(grouped.ContainsKey("invalid"));
    }

    [Fact]
    public void ParseModel_DefaultEquivalentIsNotAProviderModel()
    {
        Assert.Null(OpenCodeManager.ParseModel("default"));
        Assert.Null(OpenCodeManager.ParseModel("missing-slash"));
        Assert.Equal(("provider", "model"), OpenCodeManager.ParseModel("provider/model"));
    }
}
