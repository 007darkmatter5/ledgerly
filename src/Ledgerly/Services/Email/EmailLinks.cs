using Microsoft.AspNetCore.Components;

namespace Ledgerly.Services.Email;

/// <summary>Builds absolute links for emails, preferring the configured public address over the request's host.</summary>
public class EmailLinks(EmailSettingsStore settings, NavigationManager navigation)
{
    public async Task<string> BuildAsync(string relativePath, IReadOnlyDictionary<string, object?> query)
    {
        var configured = (await settings.GetEffectiveAsync()).Options.PublicBaseUrl;
        var baseUri = string.IsNullOrWhiteSpace(configured)
            ? new Uri(navigation.BaseUri)
            : new Uri(configured.TrimEnd('/') + "/");

        return navigation.GetUriWithQueryParameters(new Uri(baseUri, relativePath).AbsoluteUri, query);
    }
}
