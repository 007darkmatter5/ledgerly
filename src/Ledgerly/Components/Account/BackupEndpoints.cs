using Ledgerly.Data;
using Ledgerly.Services;
using Ledgerly.Services.Backup;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Ledgerly.Components.Account;

/// <summary>Download and restore whole-install backups (admins only). The page is Pages/Manage/Admin/Backup.razor.</summary>
internal static class BackupEndpoints
{
    public const string PagePath = "/Account/Manage/Admin/Backup";

    public static IEndpointConventionBuilder MapBackupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(PagePath).RequireAuthorization(policy => policy.RequireRole(AdminService.AdminRole));

        group.MapGet("/Download", async (BackupService backups, HttpContext context) =>
        {
            // Build the zip in a temporary file (deleted when the response finishes) so the download has a size.
            var file = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            try
            {
                await backups.WriteBackupAsync(file, context.RequestAborted);
                file.Position = 0;
            }
            catch
            {
                await file.DisposeAsync();
                throw;
            }

            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.File(file, "application/zip", backups.FileName());
        });

        group.MapPost("/Restore", async (
            IFormFile? file,
            [FromForm] string? confirm,
            BackupService backups,
            AdminService admins,
            SignInManager<ApplicationUser> signInManager,
            HttpContext context) =>
        {
            if (file is null || file.Length == 0)
                return RedirectWithStatus(context, PagePath, "Error: Choose a backup file to restore.");
            if (confirm != "on")
                return RedirectWithStatus(context, PagePath, "Error: Tick the box to confirm that restoring replaces everything in this install.");
            if (file.Length > BackupService.MaxBackupBytes)
                return RedirectWithStatus(context, PagePath, "Error: That file is too large to be a Ledgerly backup.");

            BackupManifest manifest;
            try
            {
                await using var stream = file.OpenReadStream();
                // Not cancelled if the browser disconnects: stopping halfway through would leave a half-finished restore.
                manifest = await backups.RestoreAsync(stream, CancellationToken.None);
            }
            catch (BackupException ex)
            {
                return RedirectWithStatus(context, PagePath, $"Error: {ex.Message} Nothing was changed.");
            }

            await admins.EnsureAdminExistsAsync();
            await signInManager.SignOutAsync();
            return RedirectWithStatus(context, "/Account/Login",
                $"Restored the backup from {manifest.CreatedAtUtc.ToLocalTime():MMM d, yyyy h:mm tt} ({Plural(manifest.Users, "account")}). Sign in with an account from that backup.");
        })
        .WithMetadata(new RequestSizeLimitAttribute(BackupService.MaxBackupBytes))
        .WithFormOptions(multipartBodyLengthLimit: BackupService.MaxBackupBytes);

        return group;
    }

    private static IResult RedirectWithStatus(HttpContext context, string path, string message)
    {
        context.Response.Cookies.Append(IdentityRedirectManager.StatusCookieName, message, new CookieOptions
        {
            SameSite = SameSiteMode.Strict,
            HttpOnly = true,
            IsEssential = true,
            MaxAge = TimeSpan.FromSeconds(5)
        });
        return TypedResults.LocalRedirect($"~{path}");
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
