using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;

namespace SikaTimeTracker.Core.Tests;

[TestClass]
public sealed class ClassificationEngineTests
{
    private readonly ClassificationEngine _engine = new();

    [TestMethod]
    public void Classify_UsesHighestPriorityEnabledRule()
    {
        var rules = new[]
        {
            Rule(1, 10, "code", priority: 10),
            Rule(2, 20, "Visual Studio Code", priority: 50),
            Rule(3, 30, "Visual Studio Code", priority: 100, isEnabled: false)
        };

        var result = _engine.Classify("Code", "Visual Studio Code", rules, defaultCategoryId: 99);

        Assert.AreEqual(20, result.CategoryId);
        Assert.AreEqual(2, result.RuleId);
        Assert.IsNull(result.RuleError);
    }

    [TestMethod]
    public void Classify_CanMatchProcessOrWindowTitleWithRegex()
    {
        var rule = new ClassificationRule(
            7,
            3,
            RuleTarget.ProcessNameOrWindowTitle,
            RuleMatchType.RegularExpression,
            "^(steam|game).*$",
            IgnoreCase: true,
            Priority: 10);

        var result = _engine.Classify("STEAM", "Library", [rule], defaultCategoryId: 1);

        Assert.AreEqual(3, result.CategoryId);
        Assert.AreEqual(7, result.RuleId);
    }

    [TestMethod]
    public void Classify_CanMatchBrowserWebsiteDomain()
    {
        var rule = new ClassificationRule(
            8,
            2,
            RuleTarget.ProcessNameOrWindowTitle,
            RuleMatchType.RegularExpression,
            "^github\\.com$",
            IgnoreCase: true,
            Priority: 10);

        var result = _engine.Classify(
            "msedge",
            "Repository - Microsoft Edge",
            [rule],
            defaultCategoryId: 1,
            websiteDomain: "github.com");

        Assert.AreEqual(2, result.CategoryId);
        Assert.AreEqual(8, result.RuleId);
    }

    [TestMethod]
    public void Classify_InvalidRegexFallsBackWithoutThrowing()
    {
        var rule = new ClassificationRule(
            1,
            2,
            RuleTarget.WindowTitle,
            RuleMatchType.RegularExpression,
            "[unclosed",
            IgnoreCase: true,
            Priority: 1);

        var result = _engine.Classify("code", "Editor", [rule], defaultCategoryId: 1);

        Assert.AreEqual(1, result.CategoryId);
        Assert.IsNull(result.RuleId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.RuleError));
    }

    [TestMethod]
    public void ValidatePattern_RejectsEmptyPattern()
    {
        var rule = Rule(1, 2, "   ", priority: 1);

        Assert.AreEqual("匹配内容不能为空", _engine.ValidatePattern(rule));
    }

    private static ClassificationRule Rule(
        long id,
        long categoryId,
        string pattern,
        int priority,
        bool isEnabled = true)
    {
        return new ClassificationRule(
            id,
            categoryId,
            RuleTarget.ProcessNameOrWindowTitle,
            RuleMatchType.Contains,
            pattern,
            IgnoreCase: true,
            priority,
            isEnabled);
    }
}
