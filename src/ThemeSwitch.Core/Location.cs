using System.Text.Json;

namespace ThemeSwitch.Core;

/// <param name="TimeZoneId">IANA id reported by the lookup service, used to sanity-check the result.</param>
public sealed record GeoPoint(double Latitude, double Longitude, string? Name = null, string? TimeZoneId = null);

public static class Geo
{
    public static double DistanceKm(GeoPoint a, GeoPoint b)
    {
        const double R = 6371, Deg = Math.PI / 180;
        double dLat = (b.Latitude - a.Latitude) * Deg, dLon = (b.Longitude - a.Longitude) * Deg;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                   + Math.Cos(a.Latitude * Deg) * Math.Cos(b.Latitude * Deg) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}

/// <summary>Parsers for the two IP geolocation services. Each returns null on any failure response.</summary>
public static class IpGeo
{
    public const string IpWhoIsUrl = "https://ipwho.is/";
    public const string IpApiUrl = "https://ipapi.co/json/";

    public static GeoPoint? ParseIpWhoIs(string json) => TryParse(json, root =>
    {
        if (!root.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True) return null;
        string? zone = root.TryGetProperty("timezone", out var tz) && tz.ValueKind == JsonValueKind.Object
                       && tz.TryGetProperty("id", out var id) ? id.GetString() : null;
        return Point(root, "city", "country", zone);
    });

    public static GeoPoint? ParseIpApi(string json) => TryParse(json, root =>
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True) return null;
        string? zone = root.TryGetProperty("timezone", out var tz) && tz.ValueKind == JsonValueKind.String ? tz.GetString() : null;
        return Point(root, "city", "country_name", zone);
    });

    static GeoPoint? Point(JsonElement root, string cityField, string countryField, string? zone)
    {
        if (!root.TryGetProperty("latitude", out var lat) || lat.ValueKind != JsonValueKind.Number) return null;
        if (!root.TryGetProperty("longitude", out var lon) || lon.ValueKind != JsonValueKind.Number) return null;
        double latitude = lat.GetDouble(), longitude = lon.GetDouble();
        // GetDouble turns 1e400 into Infinity rather than failing, so check the range explicitly.
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180)
            return null;
        var parts = new[] { cityField, countryField }
            .Select(f => root.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
            .Where(s => !string.IsNullOrWhiteSpace(s));
        string name = string.Join(", ", parts);
        return new GeoPoint(latitude, longitude, name.Length > 0 ? name : null, zone);
    }

    static GeoPoint? TryParse(string json, Func<JsonElement, GeoPoint?> read)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? read(doc.RootElement) : null;
        }
        // Valid JSON of an unexpected shape (wrong field types, absurd numbers) is a failure too.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

/// <param name="Location">The location to use from now on (may be the previous one).</param>
/// <param name="Accepted">True when the fresh lookup became the new trusted location.</param>
public sealed record LocationChoice(GeoPoint? Location, bool Accepted, string Note);

/// <summary>
/// Rejects IP lookups that disagree with the PC's clock — the usual sign of a VPN exit elsewhere —
/// so the theme keeps following the real sun.
/// </summary>
public static class LocationGuard
{
    public static bool MatchesTimeZone(GeoPoint point, TimeZoneInfo pcZone, DateTime nowUtc)
    {
        var pcOffset = pcZone.GetUtcOffset(nowUtc);
        if (point.TimeZoneId is { } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            return zone.GetUtcOffset(nowUtc) == pcOffset;
        // No usable zone id: solar time drifts from civil time, but rarely by more than ~3 hours.
        // Wrap to ±12 h so places just across the date line from their offset (Tonga, Samoa) work.
        double diff = point.Longitude / 15 - pcOffset.TotalHours;
        diff -= 24 * Math.Round(diff / 24);
        return Math.Abs(diff) <= 3;
    }

    public static LocationChoice Choose(GeoPoint? candidate, GeoPoint? lastGood, TimeZoneInfo pcZone, DateTime nowUtc)
    {
        if (candidate is null)
            return new(lastGood, false, lastGood is null ? "lookup failed, no location yet" : "lookup failed, keeping last good location");
        if (MatchesTimeZone(candidate, pcZone, nowUtc))
            return new(candidate, true, "ok");
        if (lastGood is not null)
            return new(lastGood, false, $"lookup says {candidate.Name ?? "elsewhere"} ({candidate.TimeZoneId}), which doesn't match this PC's time zone (VPN?); keeping last good location");
        return new(candidate, true, "time zone mismatch, but no earlier location to fall back to");
    }
}
