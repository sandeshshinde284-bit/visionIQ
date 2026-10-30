using VisionIQ.Ingestor.Checkpoints;

namespace VisionIQ.Ingestor.Tests;

public sealed class TimeCursorTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 10, 15, 0, 123, TimeSpan.Zero);

    [Fact]
    public void Format_and_parse_round_trip()
    {
        var parsed = TimeCursor.Parse(new TimeCursor(At, new HashSet<string> { "b", "a" }).Format())!;

        Assert.Equal(At, parsed.At);
        Assert.True(parsed.Boundary.SetEquals(new[] { "a", "b" }));
    }

    [Fact]
    public void Items_at_the_same_time_are_skipped_only_when_already_processed()
    {
        var cursor = new TimeCursor(At, new HashSet<string> { "1240" });

        Assert.False(cursor.IsNew(At, "1240"));
        Assert.True(cursor.IsNew(At, "1241"));
        Assert.False(cursor.IsNew(At.AddSeconds(-1), "9"));
        Assert.True(cursor.IsNew(At.AddMilliseconds(1), "1240"));
    }

    [Fact]
    public void Advance_keeps_every_id_at_the_newest_time()
    {
        var first = TimeCursor.Advance(null, [(At, "1"), (At, "2"), (At.AddSeconds(-5), "0")])!;
        var second = TimeCursor.Advance(first, [(At, "3")])!;

        Assert.Equal(At, first.At);
        Assert.True(first.Boundary.SetEquals(new[] { "1", "2" }));
        Assert.True(second.Boundary.SetEquals(new[] { "1", "2", "3" }));
    }

    [Fact]
    public void Advance_without_new_items_keeps_the_cursor()
    {
        var cursor = new TimeCursor(At, new HashSet<string> { "1" });

        Assert.Equal(cursor, TimeCursor.Advance(cursor, []));
    }

    [Fact]
    public void Empty_text_means_no_checkpoint()
    {
        Assert.Null(TimeCursor.Parse(null));
        Assert.Null(TimeCursor.Parse("  "));
    }

    [Fact]
    public void An_unreadable_checkpoint_is_an_error_not_a_silent_restart()
    {
        Assert.Throws<FormatException>(() => TimeCursor.Parse("not-a-date|1"));
    }
}
