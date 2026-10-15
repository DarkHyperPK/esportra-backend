using System.Collections.Concurrent;
using Esportra.Infrastructure.Email;

namespace Esportra.Api.Tests.Infrastructure;

public sealed class TestEmailSink : IEmailService
{
    public ConcurrentQueue<(string Recipient, EmailType Type, object Data)> Messages { get; } = new();
    public bool Fail { get; set; }
    public Task SendAsync(string toEmail, EmailType type, object data, CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("Isolated email provider failure.");
        Messages.Enqueue((toEmail, type, data));
        return Task.CompletedTask;
    }
}
