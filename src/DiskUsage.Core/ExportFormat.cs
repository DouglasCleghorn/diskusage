namespace DiskUsage.Core;

public enum ExportDataFormat
{
    Parquet,
    Csv,
    Tsv
}

public enum ExportCompression
{
    None,
    Brotli,
    Gzip,
    Zip
}

public readonly record struct ExportFormat(ExportDataFormat DataFormat, ExportCompression Compression = ExportCompression.None)
{
    public static ExportFormat Parquet => new(ExportDataFormat.Parquet);
    public static ExportFormat Csv => new(ExportDataFormat.Csv);
    public static ExportFormat Tsv => new(ExportDataFormat.Tsv);
    public static ExportFormat CsvBrotli => new(ExportDataFormat.Csv, ExportCompression.Brotli);
    public static ExportFormat TsvBrotli => new(ExportDataFormat.Tsv, ExportCompression.Brotli);
    public static ExportFormat CsvGzip => new(ExportDataFormat.Csv, ExportCompression.Gzip);
    public static ExportFormat TsvGzip => new(ExportDataFormat.Tsv, ExportCompression.Gzip);
    public static ExportFormat CsvZip => new(ExportDataFormat.Csv, ExportCompression.Zip);
    public static ExportFormat TsvZip => new(ExportDataFormat.Tsv, ExportCompression.Zip);
}

public static class ExportFormatParser
{
    private static readonly ExportFormat[] SupportedFormats =
    [
        ExportFormat.Parquet,
        ExportFormat.Csv,
        ExportFormat.CsvBrotli,
        ExportFormat.CsvGzip,
        ExportFormat.CsvZip,
        ExportFormat.Tsv,
        ExportFormat.TsvBrotli,
        ExportFormat.TsvGzip,
        ExportFormat.TsvZip
    ];

    public static ExportFormat Parse(string? value, string? compression = null)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "parquet"
            : value.Trim().TrimStart('.').ToLowerInvariant();

        normalized = normalized switch
        {
            "csvbr" => "csv.br",
            "tsvbr" => "tsv.br",
            "csvgz" => "csv.gz",
            "tsvgz" => "tsv.gz",
            "csvzip" => "csv.zip",
            "tsvzip" => "tsv.zip",
            _ => normalized
        };

        var (dataFormat, embeddedCompression) = normalized switch
        {
            "parquet" => (ExportDataFormat.Parquet, ExportCompression.None),
            "csv" => (ExportDataFormat.Csv, ExportCompression.None),
            "tsv" => (ExportDataFormat.Tsv, ExportCompression.None),
            "csv.br" or "csv.brotli" => (ExportDataFormat.Csv, ExportCompression.Brotli),
            "tsv.br" or "tsv.brotli" => (ExportDataFormat.Tsv, ExportCompression.Brotli),
            "csv.gz" or "csv.gzip" => (ExportDataFormat.Csv, ExportCompression.Gzip),
            "tsv.gz" or "tsv.gzip" => (ExportDataFormat.Tsv, ExportCompression.Gzip),
            "csv.zip" => (ExportDataFormat.Csv, ExportCompression.Zip),
            "tsv.zip" => (ExportDataFormat.Tsv, ExportCompression.Zip),
            _ => throw new ArgumentException("--format must be parquet, csv, tsv, or a compressed form such as csv.br, tsv.gz, or csv.zip.")
        };

        var hasExplicitCompression = !string.IsNullOrWhiteSpace(compression);
        var explicitCompression = hasExplicitCompression
            ? ParseCompression(compression!)
            : ExportCompression.None;

        if (hasExplicitCompression && embeddedCompression != ExportCompression.None && explicitCompression != embeddedCompression)
        {
            throw new ArgumentException("Compression specified by --format conflicts with --compression.");
        }

        var selectedCompression = hasExplicitCompression ? explicitCompression : embeddedCompression;
        if (dataFormat == ExportDataFormat.Parquet && selectedCompression != ExportCompression.None)
        {
            throw new ArgumentException("--compression applies to csv and tsv exports; Parquet manages its own encoding.");
        }

        return new ExportFormat(dataFormat, selectedCompression);
    }

    public static bool TryParseFileName(string fileName, out ExportFormat format)
    {
        foreach (var candidate in SupportedFormats.OrderByDescending(item => item.Extension().Length))
        {
            if (fileName.EndsWith($".{candidate.Extension()}", StringComparison.OrdinalIgnoreCase))
            {
                format = candidate;
                return true;
            }
        }

        format = default;
        return false;
    }

    public static string EnsureFileExtension(string fileName, ExportFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var extension = $".{format.Extension()}";
        var duplicateSuffix = extension + extension;
        while (fileName.EndsWith(duplicateSuffix, StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^extension.Length];
        }

        return fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? fileName
            : fileName + extension;
    }

    public static void ValidateCompressionLevel(this ExportFormat format, int? compressionLevel)
    {
        if (compressionLevel is null)
        {
            return;
        }

        var valid = format.DataFormat == ExportDataFormat.Parquet
            ? compressionLevel is >= 0 and <= 9
            : format.Compression switch
        {
            ExportCompression.Brotli => compressionLevel is >= 0 and <= 11,
            ExportCompression.Gzip or ExportCompression.Zip => compressionLevel is >= 0 and <= 9,
            _ => false
        };

        if (!valid)
        {
            var range = format.DataFormat != ExportDataFormat.Parquet && format.Compression == ExportCompression.Brotli
                ? "0 through 11"
                : "0 through 9";
            if (format.DataFormat != ExportDataFormat.Parquet && format.Compression == ExportCompression.None)
            {
                throw new ArgumentException("--compression-level requires Parquet or br, gz, or zip compression.");
            }

            var formatName = format.DataFormat == ExportDataFormat.Parquet ? "Parquet Zstandard" : format.CompressionName();
            throw new ArgumentOutOfRangeException(nameof(compressionLevel), $"Compression level for {formatName} must be {range}.");
        }
    }

    public static string Extension(this ExportFormat format)
    {
        var baseExtension = format.DataFormat switch
        {
            ExportDataFormat.Parquet => "parquet",
            ExportDataFormat.Csv => "csv",
            ExportDataFormat.Tsv => "tsv",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

        var compressionExtension = format.Compression switch
        {
            ExportCompression.None => string.Empty,
            ExportCompression.Brotli => ".br",
            ExportCompression.Gzip => ".gz",
            ExportCompression.Zip => ".zip",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

        return baseExtension + compressionExtension;
    }

    public static string ContentType(this ExportFormat format) => format.Compression == ExportCompression.Zip
        ? "application/zip"
        : format.DataFormat switch
        {
            ExportDataFormat.Parquet => "application/vnd.apache.parquet",
            ExportDataFormat.Csv => "text/csv",
            ExportDataFormat.Tsv => "text/tab-separated-values",
            _ => "application/octet-stream"
        };

    public static string? ContentEncoding(this ExportFormat format) => format.Compression switch
    {
        ExportCompression.Brotli => "br",
        ExportCompression.Gzip => "gzip",
        _ => null
    };

    public static string CompressionName(this ExportFormat format) => format.Compression switch
    {
        ExportCompression.None => "plain text",
        ExportCompression.Brotli => "Brotli",
        ExportCompression.Gzip => "gzip",
        ExportCompression.Zip => "ZIP",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public static char Delimiter(this ExportFormat format) => format.DataFormat switch
    {
        ExportDataFormat.Csv => ',',
        ExportDataFormat.Tsv => '\t',
        _ => throw new InvalidOperationException("Only CSV and TSV formats have delimiters.")
    };

    private static ExportCompression ParseCompression(string value) => value.Trim().TrimStart('.').ToLowerInvariant() switch
    {
        "none" or "plain" or "text" => ExportCompression.None,
        "br" or "brotli" => ExportCompression.Brotli,
        "gz" or "gzip" => ExportCompression.Gzip,
        "zip" => ExportCompression.Zip,
        _ => throw new ArgumentException("--compression must be none, br, gz, or zip.")
    };
}
