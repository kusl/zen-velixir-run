namespace Zen.Blazor.Beacons;

public sealed record BeaconExample(string Title, string Code)
{
    public static IReadOnlyList<BeaconExample> For(string origin) =>
    [
        new("Send and read back",
            $$"""
            curl -i -X POST {{origin}}/api/beacons/my-app \
              -H 'Content-Type: application/json' \
              -d '{"event":"signup","plan":"free"}'

            curl {{origin}}/api/entries/<id from Location>
            """),
        new("Page analytics",
            $$"""
            navigator.sendBeacon("{{origin}}/api/beacons/my-site-views",
              JSON.stringify({ path: location.pathname, ref: document.referrer }));

            curl "{{origin}}/api/beacons/my-site-views?referer=https://my.site/"
            """),
        new("CSP report-only",
            $$"""
            Content-Security-Policy-Report-Only: default-src 'self'; report-uri {{origin}}/api/beacons/my-site-csp

            curl "{{origin}}/api/beacons/my-site-csp/entries?limit=20"
            """),
        new("Visit and reaction counts",
            $$"""
            fetch("{{origin}}/api/beacons/my-post-like", { method: "POST" });

            const { total } = await (await fetch("{{origin}}/api/beacons/my-post-like")).json();
            """),
    ];
}
