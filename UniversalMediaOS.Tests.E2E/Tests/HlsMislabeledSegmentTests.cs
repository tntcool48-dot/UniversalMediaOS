using UniversalMediaOS.Core.Streaming;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class HlsMislabeledSegmentTests
{
    private static byte[] TransportStream(int leadingBytes = 0)
    {
        byte[] data = new byte[leadingBytes + 188 * 4];
        for (int packet = 0; packet < 4; packet++)
            new byte[] { 0x47, 0x01, 0x00, 0x10 }.CopyTo(data, leadingBytes + 188 * packet);
        return data;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(187)]
    public void ActualTransportStreamIsRecognizedIncludingUnalignedRanges(int leadingBytes) =>
        Assert.True(HlsLoopbackProxy.IsMpegTsPayload(TransportStream(leadingBytes)));

    [Theory]
    [InlineData("FFD8FF")]
    [InlineData("89504E47")]
    [InlineData("47494638")]
    public void RealImageSignaturesStayRejected(string magic)
    {
        byte[] image = Convert.FromHexString(magic).Concat(TransportStream()).ToArray();
        Assert.False(HlsLoopbackProxy.IsMpegTsPayload(image));
    }

    [Fact]
    public void AnIsolatedSyncByteOrCorruptedPacketDoesNotEstablishVideo()
    {
        byte[] data = new byte[2048];
        data[0] = 0x47;
        Assert.False(HlsLoopbackProxy.IsMpegTsPayload(data));
        data = TransportStream();
        data[188 * 3] = 0;
        Assert.False(HlsLoopbackProxy.IsMpegTsPayload(data));
        Assert.False(HlsLoopbackProxy.IsMpegTsPayload(TransportStream().AsSpan(0, 188 * 3)));
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("text/html")]
    [InlineData("application/javascript")]
    [InlineData("application/json")]
    public void MislabelledSegmentsKeepPlayingButActualDecoysStayRejected(string contentType)
    {
        Assert.True(HlsLoopbackProxy.IsSupportedSegmentPayload(contentType, TransportStream()));
        Assert.False(HlsLoopbackProxy.IsSupportedSegmentPayload(contentType, "<html>Blocked</html>"u8));
        Assert.False(HlsLoopbackProxy.IsSupportedSegmentPayload(contentType, "{\"error\":\"blocked\"}"u8));
    }
}
