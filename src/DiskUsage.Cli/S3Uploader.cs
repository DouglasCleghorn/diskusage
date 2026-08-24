using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DiskUsage.Core;

namespace DiskUsage.Cli;

internal sealed record S3UploadOptions(
    string? Endpoint,
    string Bucket,
    string Key,
    string Region,
    string? AccessKey,
    string? SecretKey,
    string? SessionToken,
    bool ForcePathStyle);

internal static class S3Uploader
{
    public static async Task UploadAsync(
        string filePath,
        ExportFormat format,
        S3UploadOptions options,
        CancellationToken cancellationToken)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle
        };

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }
        else
        {
            config.ServiceURL = options.Endpoint;
            config.AuthenticationRegion = options.Region;
        }

        AWSCredentials? credentials = null;
        if (!string.IsNullOrWhiteSpace(options.AccessKey) || !string.IsNullOrWhiteSpace(options.SecretKey))
        {
            if (string.IsNullOrWhiteSpace(options.AccessKey) || string.IsNullOrWhiteSpace(options.SecretKey))
            {
                throw new ArgumentException("--access-key and --secret-key must be supplied together.");
            }

            credentials = string.IsNullOrWhiteSpace(options.SessionToken)
                ? new BasicAWSCredentials(options.AccessKey, options.SecretKey)
                : new SessionAWSCredentials(options.AccessKey, options.SecretKey, options.SessionToken);
        }

        using var client = credentials is null
            ? new AmazonS3Client(config)
            : new AmazonS3Client(credentials, config);

        var request = new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = options.Key,
            FilePath = filePath,
            ContentType = format.ContentType()
        };

        if (format.ContentEncoding() is { } contentEncoding)
        {
            request.Headers.ContentEncoding = contentEncoding;
        }

        await client.PutObjectAsync(request, cancellationToken);
    }
}
