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
public sealed class SesEmailService : IEmailService
{
    private readonly AmazonSimpleEmailServiceV2Client _client;
    private readonly string _fromAddress;
    private readonly ILogger<SesEmailService> _logger;

    public SesEmailService(IConfiguration config, ILogger<SesEmailService> logger)
    {
        _logger = logger;
        _fromAddress = $"{config["Ses:FromName"] ?? "Esportra"} <{config["Ses:FromEmail"] ?? "operations@esportra.com"}>";
        _client = CreateClient(config);
    }

    private static AmazonSimpleEmailServiceV2Client CreateClient(IConfiguration config)
    {
        var accessKeyId = config["Ses:AccessKeyId"];
        var secretKey = config["Ses:SecretAccessKey"];
        var region = config["Ses:Region"] ?? "us-east-1";

        if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretKey)
            || accessKeyId.StartsWith("REPLACE") || secretKey.StartsWith("REPLACE"))
        {
            throw new InvalidOperationException("SES email service is not configured.");
        }

        return new AmazonSimpleEmailServiceV2Client(
            new BasicAWSCredentials(accessKeyId, secretKey),
            RegionEndpoint.GetBySystemName(region));
    }

    public async Task SendAsync(string toEmail, EmailType type, object data, CancellationToken ct = default)
    {
        var (subject, html) = EmailTemplates.BuildTemplate(type, data);

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

        _logger.LogInformation("[Email] Sending {Type} via SES", type);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await _client.SendEmailAsync(request, cts.Token);
            _logger.LogInformation("[Email] Sent {Type} email successfully", type);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[Email] SES timed out for {Type}", type);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Email] Failed to send {Type}", type);
            throw;
        }
    }
}
