using System.Diagnostics;
using CommSpot.Client.Core;

namespace CommSpot.Client;

/// <summary>Checks GitHub releases for a signed update and swaps the app executable in place.</summary>
internal static class SelfUpdate
{
    private static readonly Uri ManifestUrl = new("https://github.com/tabuu77/commspot/releases/latest/download/manifest.json");

    /// <summary>Pinned Ed25519 public keys (current, next). Only manifests signed by one of these are accepted.</summary>
    private static readonly byte[][] TrustedKeys =
    [
        Convert.FromBase64String(UpdateKeys.Current),
        Convert.FromBase64String(UpdateKeys.Next),
    ];

    private static string? Rid => OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : null;

    private static string ExeName => OperatingSystem.IsWindows() ? "CommSpot.exe" : "CommSpot";

    /// <summary>The installed executable, or null when running from a dev build (dotnet CommSpot.dll).</summary>
    private static string? ExePath =>
        Environment.ProcessPath is { } p && Path.GetFileName(p).Equals(ExeName, StringComparison.OrdinalIgnoreCase) ? p : null;

    public static Version Current => typeof(SelfUpdate).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static bool Supported => Rid is not null && ExePath is not null;

    /// <summary>Removes the previous version left behind by the last update.</summary>
    public static void CleanupOld()
    {
        if (ExePath is { } exe)
        {
            try
            {
                File.Delete(exe + ".old");
                string work = Path.Combine(Path.GetDirectoryName(exe)!, ".commspot-update");
                if (Directory.Exists(work))
                {
                    Directory.Delete(work, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static async Task<UpdateOffer?> CheckAsync(CancellationToken ct = default)
    {
        if (!Supported)
        {
            return null;
        }
        using var u = new Updater(TrustedKeys, ManifestUrl, Rid!);
        return await u.CheckAsync(Current, ct).ConfigureAwait(false);
    }

    /// <summary>Downloads and verifies the update, puts it in place of the running exe and starts it. The caller then exits.</summary>
    public static async Task ApplyAsync(UpdateOffer offer, IProgress<double> progress, CancellationToken ct = default)
    {
        string exe = ExePath ?? throw new InvalidOperationException("Not an installed build.");
        string work = Path.Combine(Path.GetDirectoryName(exe)!, ".commspot-update");
        using var u = new Updater(TrustedKeys, ManifestUrl, Rid!);
        string fresh = await u.DownloadAsync(offer, work, ExeName, progress, ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fresh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        string old = exe + ".old";
        File.Delete(old);
        File.Move(exe, old); // a running exe can be renamed, not overwritten
        try
        {
            File.Move(fresh, exe);
        }
        catch
        {
            File.Move(old, exe); // put the working version back
            throw;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }
}
