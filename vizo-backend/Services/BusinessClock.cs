namespace vizo_backend.Services;

/// <summary>
/// What time it is, for this business.
///
/// PAKISTAN TIME, NOT UTC. Pakistan is UTC+5, so between midnight and 5am
/// local the UTC date is still yesterday. An order taken at 1am on the 3rd was
/// being written down as the 2nd -- which on a sales ledger moves the sale into
/// the wrong day, the wrong week, and at month end the wrong month.
///
/// ApiControllerBase has the same two methods for controllers, which cannot see
/// this class without a dependency they do not otherwise need. Both delegate to
/// the same reasoning; this one exists for services and document builders.
///
/// Kind is always Unspecified: every timestamp column in this schema is
/// "timestamp without time zone" and Npgsql refuses to write anything else.
/// </summary>
public static class BusinessClock
{
    private static readonly TimeZoneInfo Zone = Resolve();

    private static TimeZoneInfo Resolve()
    {
        /* Windows and Linux name the same zone differently. Try both by id so
           the machine's own locale cannot change the answer. */
        foreach (var id in new[] { "Pakistan Standard Time", "Asia/Karachi" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        /* Pakistan has not observed daylight saving since 2009, so a fixed
           offset is correct rather than an approximation -- and far better than
           silently falling back to UTC, which is the bug this fixes. */
        return TimeZoneInfo.CreateCustomTimeZone("PKT", TimeSpan.FromHours(5), "Pakistan Time", "PKT");
    }

    /// <summary>Now, in Pakistan, with Kind=Unspecified.</summary>
    public static DateTime Now() => DateTime.SpecifyKind(
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone), DateTimeKind.Unspecified);

    /// <summary>Today's business date. What a ledger means by "today".</summary>
    public static DateOnly Today() => DateOnly.FromDateTime(Now());
}
