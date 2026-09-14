namespace Ledgerly.Services;

/// <summary>A save was rejected for a reason the user can fix; the message is safe to show them.</summary>
public class LedgerValidationException(string message) : InvalidOperationException(message);
