using Amazon;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Esportra.Infrastructure.Email;

/// <summary>
/// Sends emails via Amazon SES v2 API.
/// Config required: Ses:AccessKeyId, Ses:SecretAccessKey, Ses:Region,
///                  Ses:FromEmail, Ses:FromName
/// </summary>
public sealed class SesEmailService(
    IConfiguration config,
    ILogger<SesEmailService> logger) : IEmailService
{
    private readonly string _fromAddress =
        $"{config["Ses:FromName"] ?? "Esportra"} <{config["Ses:FromEmail"] ?? "operations@esportra.com"}>";

    public async Task SendAsync(string toEmail, EmailType type, object data, CancellationToken ct = default)
    {
        var accessKeyId = config["Ses:AccessKeyId"];
        var secretKey = config["Ses:SecretAccessKey"];
        var region = config["Ses:Region"] ?? "us-east-1";

        if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretKey)
            || accessKeyId.StartsWith("REPLACE") || secretKey.StartsWith("REPLACE"))
        {
            throw new InvalidOperationException("SES email service is not configured.");
        }

        var (subject, html) = ResendEmailService.BuildTemplate(type, data);

        var credentials = new BasicAWSCredentials(accessKeyId, secretKey);
        using var client = new AmazonSimpleEmailServiceV2Client(credentials, RegionEndpoint.GetBySystemName(region));

        var request = new SendEmailRequest
        {
            FromEmailAddress = _fromAddress,
            Destination = new Destination { ToAddresses = [toEmail] },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = subject, Charset = "UTF-8" },
                    Body = new Body
                    {
                        Html = new Content { Data = html, Charset = "UTF-8" },
                    },
                },
            },
        };

        logger.LogInformation("[Email] Sending {Type} via SES", type);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await client.SendEmailAsync(request, cts.Token);
            logger.LogInformation("[Email] Sent {Type} email successfully", type);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("[Email] SES timed out for {Type}", type);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Email] Failed to send {Type}", type);
            throw;
        }
    }
}
