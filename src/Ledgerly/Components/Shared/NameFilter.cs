using Ledgerly.Data;

namespace Ledgerly.Components.Shared;

/// <summary>
/// Category and payee filters that span shared ledgers. Each ledger has its own categories and payees, so a filter
/// matches by name, ignoring case: picking "Dining" also finds a shared ledger's "dining".
/// </summary>
public static class NameFilter
{
    /// <summary>One choice per name, preferring the user's own (<paramref name="homeLedgerId"/>), sorted by name.</summary>
    public static List<T> Choices<T>(IEnumerable<T> items, int homeLedgerId) where T : INamedLedgerEntity =>
        items.OrderBy(i => i.LedgerId != homeLedgerId)
            .DistinctBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Whether <paramref name="item"/> passes a filter set to <paramref name="filter"/>: null matches everything,
    /// 0 matches only items without one, and otherwise the chosen entry's name.
    /// </summary>
    public static bool Matches<T>(T? item, int? filter, IEnumerable<T> choices) where T : class, INamedLedgerEntity =>
        filter switch
        {
            null => true,
            0 => item is null,
            _ => item is not null
                 && (item.Id == filter || choices.FirstOrDefault(c => c.Id == filter) is { } chosen && string.Equals(item.Name, chosen.Name, StringComparison.OrdinalIgnoreCase))
        };
}
