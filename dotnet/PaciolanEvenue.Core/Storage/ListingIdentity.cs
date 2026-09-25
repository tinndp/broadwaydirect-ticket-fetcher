using System.Globalization;

namespace PaciolanEvenue.Core.Storage;

/// <summary>
/// Canonical listing Id, ported byte-for-byte from
/// ETECH.Application.Library.SK4DataSource.Core/Helpers/ListingIdentity.cs (the real Rowing bot's
/// hash) and cross-checked against the Python paciolanevenue package's own port
/// (mongo_inventory.py). Independent copy - this dotnet/ demo does not reference the main
/// ETECH.Application.MarkAutomation repo.
/// </summary>
public static class ListingIdentity
{
    private const string CompactAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const int CompactRadix = 36;
    private const int CompactLength = 13;

    public static string Build(string eventId, string fingerprint)
    {
        var preimage = (eventId ?? "") + "\n" + (fingerprint ?? "");
        return CompactEncode(GetDeterministicHashCode(preimage));
    }

    /// <summary>Level must be combined with Section here - using Section alone would collide two
    /// different sections that share a level, row and seat range onto the same Id. Renders a
    /// missing low/high seat as the literal string "None" (matches the Python port's f-string
    /// behavior) so both implementations hash identically for the same listing.</summary>
    /// <summary>seatTag (lettered seats "W", GA "PL6", no-digit codes "NC6") is appended only when set,
    /// so plain numbered seats keep the _id they had before 2026-09-25 (python mongo_inventory.py).</summary>
    public static string PaciolanEvenueFingerprint(string level, string section, string row, int? lowSeat, int? highSeat, string? seatTag = null)
    {
        var fullSection = string.IsNullOrEmpty(section) ? level : $"{level}:{section}";
        var lowStr = lowSeat?.ToString(CultureInfo.InvariantCulture) ?? "None";
        var highStr = highSeat?.ToString(CultureInfo.InvariantCulture) ?? "None";
        var fp = $"{fullSection}_{row}_{lowStr}_{highStr}";
        return string.IsNullOrEmpty(seatTag) ? fp : $"{fp}_{seatTag}";
    }

    public static string BuildPaciolanEvenue(string sourceEventId, string level, string section, string row, int? lowSeat, int? highSeat, string? seatTag = null) =>
        Build(sourceEventId, PaciolanEvenueFingerprint(level, section, row, lowSeat, highSeat, seatTag));

    /// <summary>Same djb2-style ulong hash as the .NET Rowing bot / Python port.</summary>
    public static ulong GetDeterministicHashCode(string? str)
    {
        str ??= "";
        unchecked
        {
            var hash1 = (5381UL << 16) + 5381UL;
            var hash2 = hash1;
            for (var i = 0; i < str.Length; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ str[i];
                if (i == str.Length - 1) break;
                hash2 = ((hash2 << 5) + hash2) ^ str[i + 1];
            }
            return hash1 + hash2 * 1566083941UL;
        }
    }

    public static string CompactEncode(ulong hash)
    {
        var chars = new char[CompactLength];
        for (var i = CompactLength - 1; i >= 0; i--)
        {
            chars[i] = CompactAlphabet[(int)(hash % CompactRadix)];
            hash /= CompactRadix;
        }
        return new string(chars);
    }
}
