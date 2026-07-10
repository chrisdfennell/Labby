namespace Labby;

/// <summary>
/// Sunrise/sunset via the NOAA solar position algorithm — good to about a
/// minute, which is plenty for a dashboard.
/// </summary>
public static class SolarMath
{
    public sealed record SunTimes(DateTimeOffset? Sunrise, DateTimeOffset? Sunset)
    {
        public TimeSpan? DayLength => Sunrise is { } rise && Sunset is { } set ? set - rise : null;
    }

    public static SunTimes For(DateOnly date, double latitude, double longitude, TimeZoneInfo timeZone)
    {
        var rise = Compute(date, latitude, longitude, rising: true);
        var set = Compute(date, latitude, longitude, rising: false);
        var sunrise = rise is { } r ? ToLocal(date, r, timeZone) : (DateTimeOffset?)null;
        var sunset = set is { } s ? ToLocal(date, s, timeZone) : (DateTimeOffset?)null;
        // The event's UTC hour can wrap past midnight (e.g. a US-East sunset lands
        // after 00:00 UTC), putting it a calendar day early — sunset before sunrise.
        if (sunrise is { } sr && sunset is { } ss && ss < sr)
            sunset = ss.AddDays(1);
        return new SunTimes(sunrise, sunset);
    }

    private static DateTimeOffset ToLocal(DateOnly date, double utcHours, TimeZoneInfo tz)
    {
        var utc = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc).AddHours(utcHours);
        return new DateTimeOffset(utc).ToOffset(tz.GetUtcOffset(utc));
    }

    /// <summary>UTC hour of the event, or null in polar day/night.</summary>
    private static double? Compute(DateOnly date, double latitude, double longitude, bool rising)
    {
        const double zenith = 90.833; // official sunrise/sunset (refraction + solar radius)
        var dayOfYear = date.DayOfYear;

        var lngHour = longitude / 15.0;
        var t = dayOfYear + ((rising ? 6.0 : 18.0) - lngHour) / 24.0;

        var meanAnomaly = 0.9856 * t - 3.289;
        var trueLongitude = meanAnomaly
            + 1.916 * Math.Sin(Rad(meanAnomaly))
            + 0.020 * Math.Sin(Rad(2 * meanAnomaly))
            + 282.634;
        trueLongitude = Wrap(trueLongitude, 360);

        var rightAscension = Deg(Math.Atan(0.91764 * Math.Tan(Rad(trueLongitude))));
        rightAscension = Wrap(rightAscension, 360);
        // Put RA in the same quadrant as the true longitude, then convert to hours.
        rightAscension += Math.Floor(trueLongitude / 90) * 90 - Math.Floor(rightAscension / 90) * 90;
        rightAscension /= 15;

        var sinDec = 0.39782 * Math.Sin(Rad(trueLongitude));
        var cosDec = Math.Cos(Math.Asin(sinDec));

        var cosHourAngle = (Math.Cos(Rad(zenith)) - sinDec * Math.Sin(Rad(latitude)))
                           / (cosDec * Math.Cos(Rad(latitude)));
        if (cosHourAngle is > 1 or < -1)
            return null; // sun never rises/sets on this date at this latitude

        var hourAngle = rising ? 360 - Deg(Math.Acos(cosHourAngle)) : Deg(Math.Acos(cosHourAngle));
        hourAngle /= 15;

        var localMeanTime = hourAngle + rightAscension - 0.06571 * t - 6.622;
        return Wrap(localMeanTime - lngHour, 24);
    }

    private static double Rad(double degrees) => degrees * Math.PI / 180;
    private static double Deg(double radians) => radians * 180 / Math.PI;
    private static double Wrap(double value, double range) => ((value % range) + range) % range;
}
