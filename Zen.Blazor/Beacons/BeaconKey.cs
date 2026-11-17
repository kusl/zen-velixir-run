using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Zen.Blazor.Beacons;

public static partial class BeaconKey
{
    public const int MaxLength = 64;

    public static bool IsValid([NotNullWhen(true)] string? key) => key is not null && Pattern().IsMatch(key);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._~:-]{0,63}\z")]
    private static partial Regex Pattern();
}
