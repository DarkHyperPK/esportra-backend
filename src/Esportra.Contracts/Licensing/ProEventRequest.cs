namespace Esportra.Contracts.Licensing;

public sealed record ProEventRequest(
    Guid RequestId,
    string EventName,
    string GameTitle,
    string Email,
    DateOnly StartsOn,
    DateOnly EndsOn,
    int TeamCount,
    string Website = "",
    string AccessToken = "");

public sealed record ProEventPaymentDetails(string SellerName, string BankName, string AccountTitle,
    string AccountNumber, string Instructions, string? QrImageUrl);
public sealed record ProEventInvoice(int Amount, string Currency, DateTime ExpiresAt, ProEventPaymentDetails PaymentDetails);
public sealed record ProEventRequestReceipt(string Reference, int? EstimatedAmount, string Currency, ProEventInvoice? Invoice = null);
public sealed record UpdateProEventRequestStatus(string Status);
