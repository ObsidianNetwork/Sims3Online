using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SimsOnline.DedicatedServer.Observability;

/// <summary>
/// Computes the Sentry Release tag from the git repository state.
/// Runs synchronously at startup because Sentry initialization is synchronous.
/// </summary>
internal static class SentryReleaseResolver
{
    public static SentryReleaseInfo Resolve()
    {
        string? sha = TryRunGit("rev-parse", "HEAD");
        string? status = TryRunGit("status", "--porcelain");
        return Parse(sha, status ?? string.Empty);
    }

    internal static SentryReleaseInfo Parse(string? sha, string status)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return new SentryReleaseInfo("simsonline-dedicatedserver@unknown", "simsonline", "unknown", false);
        }

        string trimmed = sha.Trim();
        string shortSha = trimmed.Length >= 7 ? trimmed[..7] : trimmed;
        bool dirty = !string.IsNullOrWhiteSpace(status);
        return new SentryReleaseInfo($"simsonline-dedicatedserver@{shortSha}", "simsonline", shortSha, dirty);
    }

    public static string ShortHash(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        StringBuilder builder = new StringBuilder(12);
        for (int i = 0; i < 6; i++)
        {
            builder.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string? TryRunGit(params string[] args)
    {
        try
        {
            using Process process = new Process();
            process.StartInfo.FileName = "git";
            process.StartInfo.WorkingDirectory = AppContext.BaseDirectory;
            process.StartInfo.Arguments = string.Join(" ", args);
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return null;
            }

            return output.Trim();
        }
        catch
        {
            return null;
        }
    }
}
