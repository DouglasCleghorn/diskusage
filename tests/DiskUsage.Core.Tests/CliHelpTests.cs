using DiskUsage.Cli;

namespace DiskUsage.Core.Tests;

[TestClass]
public sealed class CliHelpTests
{
    [TestMethod]
    [DataRow("browse")]
    [DataRow("scan")]
    [DataRow("export")]
    [DataRow("upload")]
    public void Both_help_forms_return_the_same_command_reference(string command)
    {
        string[] flag = [command, "--help"];
        string[] verb = ["help", command];
        var text = CliHelp.GetRequestedText(flag, CommandArguments.Parse(flag));
        Assert.AreEqual(text, CliHelp.GetRequestedText(verb, CommandArguments.Parse(verb)));
        StringAssert.Contains(text!, "Usage:");
        StringAssert.Contains(text!, $"diskusage {command}");
        StringAssert.Contains(text!, "--threads");
        StringAssert.Contains(text!, "Exit codes:");
        StringAssert.Contains(text!, "130");
    }

    [TestMethod]
    public void Global_help_and_case_insensitive_help_work()
    {
        string[] flag = ["--help"];
        string[] verb = ["help"];
        var text = CliHelp.GetRequestedText(flag, CommandArguments.Parse(flag));
        Assert.AreEqual(text, CliHelp.GetRequestedText(verb, CommandArguments.Parse(verb)));
        StringAssert.Contains(text!, "AUTOMATION.md");
        Assert.AreEqual(CliHelp.GetText("export"), CliHelp.GetText("EXPORT"));
        string[] uppercase = ["HELP", "EXPORT"];
        Assert.AreEqual(CliHelp.GetText("export"), CliHelp.GetRequestedText(uppercase, CommandArguments.Parse(uppercase)));
    }

    [TestMethod]
    public void Help_is_scoped_to_the_command()
    {
        StringAssert.Contains(CliHelp.GetText("scan"), "per directory");
        Assert.IsFalse(CliHelp.GetText("scan").Contains("--extensions", StringComparison.Ordinal));
        StringAssert.Contains(CliHelp.GetText("export"), "Globally largest");
        Assert.IsFalse(CliHelp.GetText("export").Contains("--bucket", StringComparison.Ordinal));
        StringAssert.Contains(CliHelp.GetText("upload"), "--bucket");
        Assert.IsFalse(CliHelp.GetText("upload").Contains("--output FILE", StringComparison.Ordinal));
        Assert.IsFalse(CliHelp.GetText("browse").Contains("--format", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Export_help_documents_machine_readable_contract()
    {
        var text = CliHelp.GetText("export");
        foreach (var expected in new[] { "full_path", "size_bytes", "created_utc", "modified_utc",
            "stderr", "UTF-8", "NOT inferred", "partial scan", "unspecified", "250,000" })
            StringAssert.Contains(text, expected);
    }

    [TestMethod]
    [DataRow("browse")]
    [DataRow("scan")]
    [DataRow("export")]
    [DataRow("upload")]
    public async Task Help_returns_before_scanning_or_uploading(string command)
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "diskusage-help-" + Guid.NewGuid().ToString("N"));
        // A missing source, invalid thread count, and absent bucket must not affect help.
        Assert.AreEqual(0, await CliApplication.RunAsync(
            [command, missingPath, "--threads", "0", "--help"], CancellationToken.None));
    }

    [TestMethod]
    public void Invalid_help_topics_are_rejected_and_normal_commands_are_untouched()
    {
        string[] unknown = ["help", "unknown"];
        Assert.Throws<ArgumentException>(() => CliHelp.GetRequestedText(unknown, CommandArguments.Parse(unknown)));
        string[] extra = ["help", "export", "upload"];
        Assert.Throws<ArgumentException>(() => CliHelp.GetRequestedText(extra, CommandArguments.Parse(extra)));
        string[] normal = ["export", ".", "--format", "csv"];
        Assert.IsNull(CliHelp.GetRequestedText(normal, CommandArguments.Parse(normal)));
    }
}
