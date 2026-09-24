namespace Ledgerly.Services;

/// <summary>
/// Tells open browser sessions when sharing changes for their user (an invitation arrives, is answered, or
/// access is removed), so the change shows without a reload. In-memory: Ledgerly runs as a single instance,
/// and anyone not connected picks the change up the next time they load a page.
/// </summary>
public class SharingNotifier
{
    /// <summary>Raised with the id of a user whose sharing changed.</summary>
    public event Action<string>? Changed;

    public void Notify(params string[] userIds)
    {
        foreach (var userId in userIds.Distinct())
            Changed?.Invoke(userId);
    }
}
