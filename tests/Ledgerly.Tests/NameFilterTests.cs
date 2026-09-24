using Ledgerly.Components.Shared;
using Ledgerly.Data;

namespace Ledgerly.Tests;

public class NameFilterTests
{
    private static readonly Category MyDining = new() { Id = 1, LedgerId = 10, Name = "Dining" };
    private static readonly Category MyRent = new() { Id = 2, LedgerId = 10, Name = "Rent" };
    private static readonly Category SharedDining = new() { Id = 7, LedgerId = 20, Name = "dining" };
    private static readonly Category SharedPets = new() { Id = 8, LedgerId = 20, Name = "Pets" };

    [Fact]
    public void Choices_have_one_entry_per_name_preferring_the_users_own()
    {
        var choices = NameFilter.Choices([SharedDining, SharedPets, MyRent, MyDining], homeLedgerId: 10);

        Assert.Equal([MyDining, SharedPets, MyRent], choices);
    }

    [Fact]
    public void A_filter_matches_the_same_name_in_any_ledger()
    {
        var choices = NameFilter.Choices([MyDining, MyRent, SharedDining, SharedPets], homeLedgerId: 10);

        Assert.True(NameFilter.Matches(SharedDining, MyDining.Id, choices));
        Assert.True(NameFilter.Matches(MyDining, MyDining.Id, choices));
        Assert.False(NameFilter.Matches(MyRent, MyDining.Id, choices));
        Assert.True(NameFilter.Matches(SharedPets, SharedPets.Id, choices));

        // No filter matches everything; 0 means "none set".
        Assert.True(NameFilter.Matches<Category>(null, null, choices));
        Assert.True(NameFilter.Matches<Category>(null, 0, choices));
        Assert.False(NameFilter.Matches(MyRent, 0, choices));
    }
}
