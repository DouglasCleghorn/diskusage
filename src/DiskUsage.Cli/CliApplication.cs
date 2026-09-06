using DiskUsage.Core;

namespace DiskUsage.Cli;

internal static class CliApplication
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = CommandArguments.Parse(args);
        var help = CliHelp.GetRequestedText(args, parsed);
        if (help is not null)
        {
            Console.WriteLine(help);
            return 0;
        }
        if (parsed.Has("version"))
        {
            Console.WriteLine(typeof(CliApplication).Assembly.GetCustomAttributes(false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0]);
            return 0;
        }

        var path = Path.GetFullPath(parsed.Positionals.FirstOrDefault() ?? Environment.CurrentDirectory);
        var scanOptions = new ScanOptions
        {
            IncludeHidden = parsed.Has("include-hidden"),
            FollowLinks = parsed.Has("follow-links"),
            CollectFiles = true,
            MaxDegreeOfParallelism = parsed.GetInt("threads", ScanOptions.DefaultMaxDegreeOfParallelism)
        };

        return parsed.Command switch
        {
            "browse" => await RunBrowseAsync(path, scanOptions, cancellationToken),
            "scan" => await RunScanAsync(path, scanOptions, parsed, cancellationToken),
            "export" => await RunExportAsync(path, scanOptions, parsed, cancellationToken),
            "upload" => await RunUploadAsync(path, scanOptions, parsed, cancellationToken),
            _ => throw new ArgumentException($"Unknown command '{parsed.Command}'. Run 'diskusage help'.")
        };
    }

    private static async Task<int> RunBrowseAsync(
        string path,
        ScanOptions options,
        CancellationToken cancellationToken)
    {
        var progress = CreateProgress("Scanning");
        var result = await new FileSystemScanner().ScanAsync(path, options, progress, cancellationToken);
        ClearProgress();

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            TreePrinter.Print(result.Root, maxDepth: 1, maxItems: 100, includeFiles: true);
            PrintSummary(result);
            return 0;
        }

        return TerminalBrowser.Run(result);
    }

    private static async Task<int> RunScanAsync(
        string path,
        ScanOptions options,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        options = options with { CollectFiles = arguments.Has("include-files") };
        var progress = CreateProgress("Scanning");
        var result = await new FileSystemScanner().ScanAsync(path, options, progress, cancellationToken);
        ClearProgress();

        TreePrinter.Print(
            result.Root,
            arguments.GetInt("depth", 1),
            arguments.GetInt("top", 50),
            arguments.Has("include-files"));
        PrintSummary(result);
        return 0;
    }

    private static async Task<int> RunExportAsync(
        string path,
        ScanOptions options,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        options = options with { FileFilter = ExportFilterArguments.Parse(arguments) };
        var top = ExportFilterArguments.ParseTop(arguments);
        var format = ExportFormatParser.Parse(arguments.Get("format"), arguments.Get("compression"));
        var compressionLevel = arguments.GetOptionalInt("compression-level");
        format.ValidateCompressionLevel(compressionLevel);
        var outputArgument = arguments.Get("output");
        var writeToStdout = arguments.Has("stdout") || outputArgument == "-" || (outputArgument is null && Console.IsOutputRedirected);
        if (arguments.Has("stdout") && outputArgument is not null && outputArgument != "-")
        {
            throw new ArgumentException("--stdout cannot be combined with a file path in --output.");
        }

        var progress = CreateProgress("Exporting");
        ExportResult result;
        if (writeToStdout)
        {
            var output = Console.OpenStandardOutput();
            if (format.DataFormat == ExportDataFormat.Parquet)
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"diskusage-{Guid.NewGuid():N}.parquet");
                try
                {
                    result = await new FileListExporter().ExportAsync(
                        path,
                        tempPath,
                        format,
                        options,
                        progress,
                        cancellationToken,
                        compressionLevel,
                        top);
                    await using var input = new FileStream(
                        tempPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(output, cancellationToken);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch (IOException)
                    {
                        // The OS can clean up an orphaned temporary export later.
                    }
                }
            }
            else
            {
                result = await new FileListExporter().ExportAsync(
                    path,
                    output,
                    $"inventory.{format.Extension()}",
                    format,
                    options,
                    progress,
                    cancellationToken,
                    compressionLevel,
                    top);
            }

            await output.FlushAsync(cancellationToken);
            ClearProgress();
            Console.Error.WriteLine($"Wrote {result.Files:N0} files ({SizeFormatter.Format(result.Bytes)}) to stdout");
        }
        else
        {
            var output = outputArgument ?? $"diskusage-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{format.Extension()}";
            result = await new FileListExporter().ExportAsync(path, output, format, options, progress, cancellationToken, compressionLevel, top);
            ClearProgress();
            Console.WriteLine($"Wrote {result.Files:N0} files ({SizeFormatter.Format(result.Bytes)}) to {result.OutputPath}");
        }

        return 0;
    }

    private static async Task<int> RunUploadAsync(
        string path,
        ScanOptions options,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        options = options with { FileFilter = ExportFilterArguments.Parse(arguments) };
        var top = ExportFilterArguments.ParseTop(arguments);
        var format = ExportFormatParser.Parse(arguments.Get("format"), arguments.Get("compression"));
        var compressionLevel = arguments.GetOptionalInt("compression-level");
        format.ValidateCompressionLevel(compressionLevel);
        var endpoint = arguments.Get("endpoint");
        var bucket = arguments.Require("bucket");
        var key = arguments.Get("key") ?? $"diskusage/{Environment.MachineName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{format.Extension()}";
        var tempPath = Path.Combine(Path.GetTempPath(), $"diskusage-{Guid.NewGuid():N}.{format.Extension()}");

        try
        {
            var progress = CreateProgress("Preparing upload");
            var exported = await new FileListExporter().ExportAsync(path, tempPath, format, options, progress, cancellationToken, compressionLevel, top);
            ClearProgress();
            Console.Error.WriteLine($"Uploading {exported.Files:N0} files ({SizeFormatter.Format(new FileInfo(tempPath).Length)})...");

            await S3Uploader.UploadAsync(
                tempPath,
                format,
                new S3UploadOptions(
                    endpoint,
                    bucket,
                    key,
                    arguments.Get("region") ?? "us-east-1",
                    arguments.Get("access-key"),
                    arguments.Get("secret-key"),
                    arguments.Get("session-token"),
                    !arguments.Has("virtual-hosted-style")),
                cancellationToken);

            Console.WriteLine($"Uploaded to s3://{bucket}/{key}");
            return 0;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
                // A temporary export that could not be removed is harmless and remains discoverable by the OS.
            }
        }
    }

    private static IProgress<ScanProgress>? CreateProgress(string operation)
    {
        if (Console.IsErrorRedirected)
        {
            return null;
        }

        return new InlineProgress<ScanProgress>(value =>
        {
            var text = $"{operation}: {value.Files:N0} files  {SizeFormatter.Format(value.Bytes),10}  skipped {value.Skipped:N0}";
            var width = SafeWindowWidth();
            var available = Math.Max(1, width - 1);
            var visible = text.Length > available ? text[..available] : text.PadRight(available);
            Console.Error.Write('\r');
            Console.Error.Write(visible);
        });
    }

    private static void ClearProgress()
    {
        if (!Console.IsErrorRedirected)
        {
            Console.Error.Write('\r');
            Console.Error.Write(new string(' ', Math.Max(1, SafeWindowWidth() - 1)));
            Console.Error.Write('\r');
        }
    }

    private static int SafeWindowWidth()
    {
        try
        {
            return Math.Max(40, Console.WindowWidth);
        }
        catch (IOException)
        {
            return 100;
        }
    }

    private static void PrintSummary(ScanResult result) =>
        Console.WriteLine($"{result.Files:N0} files in {result.Directories:N0} directories, {SizeFormatter.Format(result.Bytes)}, {result.Skipped:N0} skipped, {result.Elapsed.TotalSeconds:N1}s");

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
