using Ledgerly.Data;

namespace Ledgerly.Services;

/// <summary>A ledger someone else shared with the signed-in user, with the layer settings they chose for it.</summary>
/// <param name="Nickname">What the user renamed it to, if they did.</param>
/// <param name="DefaultName">"Owner's ledger", from the owner's display name or the start of their email.</param>
public sealed record SharedLedger(int MemberId, int LedgerId, string? Nickname, string DefaultName, string OwnerName, string Color, LedgerRole Role, bool IsVisible)
{
    public string Name => Nickname ?? DefaultName;

    public bool IsReadOnly => Role == LedgerRole.Viewer;

    /// <summary>"view only", "can add transactions" or "can edit", for labels and tooltips.</summary>
    public string AccessText => LedgerScope.AccessText(Role);
}

/// <summary>
/// The ledgers whose data the signed-in user sees: their home ledger (their own, or the sample one) plus the
/// shared ledgers they've accepted and switched on. Shared ledgers are hidden while exploring sample data.
/// </summary>
public sealed class LedgerScope
{
    public static readonly LedgerScope None = new() { HomeLedgerId = 0, HomeIsSample = false, Shared = [] };

    public required int HomeLedgerId { get; init; }
    public required bool HomeIsSample { get; init; }

    /// <summary>Every accepted shared ledger, including ones switched off.</summary>
    public required IReadOnlyList<SharedLedger> Shared { get; init; }

    public IEnumerable<SharedLedger> Visible => HomeIsSample ? [] : Shared.Where(s => s.IsVisible);

    public bool HasVisibleShared => Visible.Any();

    public IReadOnlyList<int> VisibleLedgerIds => [HomeLedgerId, .. Visible.Select(s => s.LedgerId)];

    public bool IsHome(int ledgerId) => ledgerId == HomeLedgerId;

    /// <summary>The shared ledger a row belongs to, or null for the user's own.</summary>
    public SharedLedger? SharedFor(int ledgerId) => IsHome(ledgerId) ? null : Shared.FirstOrDefault(s => s.LedgerId == ledgerId);

    /// <summary>
    /// What the user may do in a ledger: everything in their home ledger, their role in a shared one that's switched on,
    /// and nothing anywhere else (including shared ledgers that are switched off).
    /// </summary>
    public LedgerRole? RoleIn(int ledgerId) =>
        IsHome(ledgerId) ? LedgerRole.Editor : Visible.FirstOrDefault(s => s.LedgerId == ledgerId)?.Role;

    public bool Can(int ledgerId, LedgerRole role) => RoleIn(ledgerId) >= role;

    /// <summary>Add, change and delete anything (accounts, bills, income, transfers, loans, categories, payees).</summary>
    public bool CanEdit(int ledgerId) => Can(ledgerId, LedgerRole.Editor);

    /// <summary>Record payments and transfers, and add or edit transactions.</summary>
    public bool CanContribute(int ledgerId) => Can(ledgerId, LedgerRole.Contributor);

    /// <summary>Shared ledgers (switched on) the user can add things to with at least <paramref name="role"/>.</summary>
    public IEnumerable<SharedLedger> WritableShared(LedgerRole role) => Visible.Where(s => s.Role >= role);

    /// <summary>A colored bar down the left edge of a table row from a shared ledger.</summary>
    public string RowStyle(int ledgerId) => SharedFor(ledgerId) is { } shared ? $"box-shadow: inset 4px 0 0 {shared.Color}" : "";

    public static string AccessText(LedgerRole role) => role switch
    {
        LedgerRole.Editor => "can edit",
        LedgerRole.Contributor => "can add transactions",
        _ => "view only"
    };
}

/// <summary>A pending invitation for the signed-in user.</summary>
public sealed record LedgerInvitation(int MemberId, string OwnerName, string OwnerEmail, LedgerRole Role, DateTime InvitedAt);

/// <summary>Someone the signed-in user shares their ledger with (or has invited).</summary>
public sealed record LedgerShare(int MemberId, string Name, string Email, LedgerRole Role, bool IsAccepted, DateTime InvitedAt);
