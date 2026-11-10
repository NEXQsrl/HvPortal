using System.Buffers.Binary;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.VmConsole;

namespace HvPortal.Gateway.Tests.Unit;

/// <summary>Binary console messages for the browser: full frames (type 1) and partial frames (type 2, 0.9.2).</summary>
public sealed class ConsoleFrameMessageTests
{
    [Fact]
    public void A_full_frame_keeps_the_original_layout()
    {
        var m = ConsoleRelay.FrameMessage(new ConsoleFrame { Width = 640, Height = 480, GuestWidth = 1280, GuestHeight = 960, MouseAvailable = true }, [9, 9]);
        Assert.Equal(12, m.Length);
        Assert.Equal(1, m[0]);
        Assert.Equal(1, m[1]);
        Assert.Equal(640, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(2)));
        Assert.Equal(480, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(4)));
        Assert.Equal(1280, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(6)));
        Assert.Equal(960, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(8)));
        Assert.Equal([9, 9], m[10..]);
    }

    [Fact]
    public void A_partial_frame_carries_its_first_row_and_row_count()
    {
        var m = ConsoleRelay.FrameMessage(new ConsoleFrame { Width = 640, Height = 480, Top = 100, Rows = 16 }, [7]);
        Assert.Equal(2, m[0]);
        Assert.Equal(100, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(10)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(12)));
        Assert.Equal(7, m[14]);
    }

    [Fact]
    public void A_band_outside_the_frame_is_never_sent_as_partial()
    {
        Assert.Equal(1, ConsoleRelay.FrameMessage(new ConsoleFrame { Width = 640, Height = 480, Top = 470, Rows = 16 }, [7])[0]);
        Assert.Equal(1, ConsoleRelay.FrameMessage(new ConsoleFrame { Width = 640, Height = 480, Top = -1, Rows = 4 }, [7])[0]);
    }
}
