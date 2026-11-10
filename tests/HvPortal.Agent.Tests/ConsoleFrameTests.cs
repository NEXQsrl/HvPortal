using HvPortal.Agent.HyperV;

namespace HvPortal.Agent.Tests;

/// <summary>
/// Faster console (0.9.2): only the band of rows that changed is sent. What GetVirtualSystemThumbnailImage returns on a
/// real host, and the frame rate reached, are in the real-host test plan (V85).
/// </summary>
public sealed class ConsoleFrameTests
{
    private const int Stride = 8; // 4 pixels RGB565 per row

    private static byte[] Frame(int rows, params int[] changedRows)
    {
        var f = new byte[rows * Stride];
        foreach (var r in changedRows)
        {
            f[r * Stride + 3] = 0xAB;
        }
        return f;
    }

    [Fact]
    public void Equal_frames_have_no_changed_rows()
    {
        Assert.Equal((0, 0), ConsoleService.ChangedRows(Frame(6), Frame(6), Stride));
    }

    [Fact]
    public void The_band_goes_from_the_first_to_the_last_changed_row()
    {
        Assert.Equal((2, 1), ConsoleService.ChangedRows(Frame(6), Frame(6, 2), Stride));
        Assert.Equal((1, 4), ConsoleService.ChangedRows(Frame(6), Frame(6, 1, 4), Stride));
        Assert.Equal((0, 6), ConsoleService.ChangedRows(Frame(6), Frame(6, 0, 5), Stride));
        Assert.Equal((5, 1), ConsoleService.ChangedRows(Frame(6), Frame(6, 5), Stride));
    }

    [Fact]
    public void Frames_that_cannot_be_compared_row_by_row_are_sent_whole()
    {
        Assert.Equal((0, 6), ConsoleService.ChangedRows(Frame(5), Frame(6), Stride));
        Assert.Equal((0, 0), ConsoleService.ChangedRows(Frame(6), Frame(6), 0));
    }
}
