namespace PassReset.Common;

/// <summary>
/// Levenshtein edit distance between two strings. Used by the change flow to enforce a
/// minimum distance between the old and new password. A deterministic string algorithm —
/// it never varies per directory adapter, so it is a free function, not a provider seam.
/// </summary>
public static class PasswordDistance
{
    /// <summary>Computes the Levenshtein distance between two strings.</summary>
    public static int Levenshtein(string currentPassword, string newPassword)
    {
        var n = currentPassword.Length;
        var m = newPassword.Length;
        var d = new int[n + 1, m + 1];

        if (n == 0) return m;
        if (m == 0) return n;

        for (int i = 0; i <= n; d[i, 0] = i++) { }
        for (int j = 0; j <= m; d[0, j] = j++) { }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (newPassword[j - 1] == currentPassword[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }
}
