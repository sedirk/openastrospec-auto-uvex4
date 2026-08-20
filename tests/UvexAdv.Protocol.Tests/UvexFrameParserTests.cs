using UvexAdv.Protocol;

namespace UvexAdv.Protocol.Tests;

public sealed class UvexFrameParserTests
{
    [Fact]
    public void ParsesFragmentedAndCoalescedFrames()
    {
        var parser = new UvexFrameParser();

        Assert.Empty(parser.Append("noise:GP"));
        var frames = parser.Append("OS;123;5500;3500;7500;#:ITEM;21.5;#");

        Assert.Equal(2, frames.Count);
        Assert.Equal("GPOS", frames[0].Code);
        Assert.Equal("123", frames[0].Arguments[0]);
        Assert.Equal("ITEM", frames[1].Code);
        Assert.True(frames[1].TryGetDouble(0, out var temperature));
        Assert.Equal(21.5, temperature);
    }

    [Theory]
    [InlineData(":GPOS;#", "GPOS")]
    [InlineData(" :IST0;167;# ", "IST0")]
    [InlineData(":ISLV;#", "ISLV")]
    public void ParsesDocumentedWireForms(string raw, string code)
    {
        Assert.True(UvexFrameParser.TryParse(raw, out var frame));
        Assert.Equal(code, frame.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GPOS;1;#")]
    [InlineData(":TOO-LONG;#")]
    [InlineData(":GPOS;1;")]
    public void RejectsMalformedFrames(string raw)
    {
        Assert.False(UvexFrameParser.TryParse(raw, out _));
    }

    [Fact]
    public void SerializesCommandsWithProtocolDelimiters()
    {
        var slitMove = UvexCommands.SlitMove(3, true);
        Assert.Equal(":SMOV;3;1;#", slitMove.ToWireString());
        Assert.True(slitMove.CausesMotion);
        Assert.False(slitMove.ExpectsResponse);
        Assert.Equal(":IBSY;#", UvexCommands.Busy().ToWireString());
        Assert.Equal(":SGOF;3;#", UvexCommands.SlitOffset(3).ToWireString());
        Assert.Equal(":SSOF;3;-12;#", UvexCommands.SlitSetOffset(3, -12).ToWireString());
        Assert.Equal(":SPS0;3;#", UvexCommands.SlitCalibratePosition(3).ToWireString());
        Assert.Equal(":SPAC;#", UvexCommands.SlitAutoCalibratePhotodiode().ToWireString());
        Assert.Equal(":SLON;#", UvexCommands.SlitIlluminationOn().ToWireString());
        Assert.Equal(":SLOF;#", UvexCommands.SlitIlluminationOff().ToWireString());
        Assert.Equal(":FSTP;#", UvexCommands.FocusStop().ToWireString());
    }
}
