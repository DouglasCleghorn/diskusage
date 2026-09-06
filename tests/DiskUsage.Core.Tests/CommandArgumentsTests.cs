using DiskUsage.Cli;
using DiskUsage.Core;

namespace DiskUsage.Core.Tests;

[TestClass]
public sealed class CommandArgumentsTests
{
    [TestMethod]
    public void Defaults_and_implicit_browse_are_preserved()
    {
        var empty = CommandArguments.Parse([]);
        Assert.AreEqual("browse", empty.Command);
        Assert.AreEqual(0, empty.Positionals.Count);
        Assert.AreEqual(ScanOptions.DefaultMaxDegreeOfParallelism, empty.GetInt("threads", -1));
        var path = CommandArguments.Parse(["Some Folder", "--threads", "2"]);
        Assert.AreEqual("browse", path.Command);
        Assert.AreEqual("Some Folder", path.Positionals.Single());
        Assert.AreEqual(2, path.GetInt("threads", -1));
        var export = CommandArguments.Parse(["export"]);
        Assert.AreEqual("parquet", export.Get("format"));
        Assert.IsNull(export.Get("compression"));
        Assert.IsNull(export.GetOptionalInt("top"));
        Assert.IsFalse(export.Has("top"));
    }

    [TestMethod]
    public void Repeated_filters_case_and_option_delimiters_are_preserved()
    {
        var parsed = CommandArguments.Parse(["EXPORT", "--EXTENSIONS=TXT", "--extensions", "*.CSV",
            "--SIZE", ">=10", "--size=<=20", "--OUTPUT=MixedCase.csv", "--format:csv", "Some Folder"]);
        CollectionAssert.AreEqual(new[] { "TXT", "*.CSV" }, parsed.GetValues("extensions").ToArray());
        CollectionAssert.AreEqual(new[] { ">=10", "<=20" }, parsed.GetValues("size").ToArray());
        Assert.AreEqual("MixedCase.csv", parsed.Get("output"));
        Assert.AreEqual("csv", parsed.Get("format"));
        Assert.AreEqual("Some Folder", parsed.Positionals.Single());
    }

    [TestMethod]
    public void End_of_options_and_at_prefixed_paths_remain_literal()
    {
        Assert.AreEqual("--HELP", CommandArguments.Parse(["export", "--format", "csv", "--", "--HELP"]).Positionals.Single());
        Assert.AreEqual("export", CommandArguments.Parse(["--", "export"]).Positionals.Single());
        Assert.AreEqual("@inventory", CommandArguments.Parse(["@inventory"]).Positionals.Single());
        Assert.AreEqual("@inventory", CommandArguments.Parse(["export", "@inventory"]).Positionals.Single());
        Assert.IsFalse(CommandArguments.Parse(["export", "--", "--help"]).Has("help"));
    }

    [TestMethod]
    [DataRow("--stdout", "--stdout")]
    [DataRow("--format=csv", "--format=tsv")]
    [DataRow("--top=1", "--top=2")]
    [DataRow("--threads=1", "--THREADS=2")]
    [DataRow("--output=one", "--output=two")]
    public void Non_repeatable_options_reject_duplicates(string first, string second) =>
        Assert.Throws<ArgumentException>(() => CommandArguments.Parse(["export", first, second]));

    [TestMethod]
    [DataRow("--top", "abc")]
    [DataRow("--top", "0")]
    [DataRow("--top", "-1")]
    [DataRow("--threads", "0")]
    [DataRow("--threads", "2147483648")]
    [DataRow("--format", " ")]
    [DataRow("--extensions", "")]
    [DataRow("--size", "")]
    public void Invalid_option_values_are_rejected(string name, string value) =>
        Assert.Throws<ArgumentException>(() => CommandArguments.Parse(["export", name, value]));

    [TestMethod]
    public void Missing_repeated_values_and_extra_paths_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => CommandArguments.Parse(["export", "--extensions", "txt", "--extensions"]));
        Assert.Throws<ArgumentException>(() => CommandArguments.Parse(["export", "one", "two"]));
        Assert.Throws<ArgumentException>(() => CommandArguments.Parse(["one", "export", "two"]));
    }

    [TestMethod]
    [DataRow("scan", "--format=csv")]
    [DataRow("scan", "--extensions=txt")]
    [DataRow("browse", "--top=5")]
    [DataRow("export", "--depth=2")]
    [DataRow("export", "--bucket=inventory")]
    [DataRow("upload", "--stdout")]
    public void Options_are_scoped_to_their_command(string command, string option) =>
        Assert.Throws<ArgumentException>(() => CommandArguments.Parse([command, option]));

    [TestMethod]
    public void Required_upload_arguments_validate_before_execution()
    {
        Assert.Throws<ArgumentException>(() => CommandArguments.Parse(["upload"]));
        Assert.AreEqual("inventory", CommandArguments.Parse(["upload", "--bucket", "inventory"]).Require("bucket"));
        Assert.AreEqual("us-east-1", CommandArguments.Parse(["upload", "--bucket", "inventory"]).Get("region"));
    }

    [TestMethod]
    public void Help_and_version_bypass_operation_validation()
    {
        Assert.IsTrue(CommandArguments.Parse(["upload", "-h"]).Has("help"));
        Assert.IsTrue(CommandArguments.Parse(["--version"]).Has("version"));
        Assert.IsTrue(CommandArguments.Parse(["upload", "--version"]).Has("version"));
        Assert.IsTrue(CommandArguments.Parse(["HELP", "EXPORT"]).Has("help"));
        Assert.AreEqual(CliHelp.GetText("export"), CliHelp.GetRequestedText([], CommandArguments.Parse(["export", "-h"])));
    }

    [TestMethod]
    public void Scan_top_zero_remains_valid_and_defaults_are_per_command()
    {
        Assert.AreEqual(0, CommandArguments.Parse(["scan", "--top", "0"]).GetInt("top", -1));
        Assert.AreEqual(50, CommandArguments.Parse(["scan"]).GetInt("top", -1));
        Assert.AreEqual(1, CommandArguments.Parse(["scan"]).GetInt("depth", -1));
    }

    [TestMethod]
    [DataRow("--stdout=true")]
    [DataRow("--stdout=false")]
    [DataRow("--include-hidden=true")]
    public void Boolean_flags_do_not_accept_values(string option) =>
        Assert.Throws<ArgumentException>(() => CommandArguments.Parse(["export", option]));

    [TestMethod]
    public void Option_like_values_and_paths_after_separator_keep_their_case()
    {
        var parsed = CommandArguments.Parse(["export", "--output=--RESULT", "--", "--RESULT"]);
        Assert.AreEqual("--RESULT", parsed.Get("output"));
        Assert.AreEqual("--RESULT", parsed.Positionals.Single());
    }

    [TestMethod]
    public async Task Cancellation_still_reaches_the_scanner()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => CliApplication.RunAsync(
            ["scan", Path.GetTempPath()], cancellation.Token));
    }
}
