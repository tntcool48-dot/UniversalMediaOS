using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Buffers.Binary;
using System.Xml.Linq;
using UniversalMediaOS.Core.OtherMedia;

namespace UniversalMediaOS.Core.Streaming;

public sealed partial class HlsLoopbackProxy
{
    internal async Task<string> RewriteDashManifestForSessionAsync(byte[] bytes, Uri effective,
        string sessionId, CancellationToken token = default)
    {
        var result = await PlaylistMediaDownload.RewriteDashAsync(bytes, effective,
            uri => Task.FromResult(CreateLocalUrl("dashseg", sessionId, uri.AbsoluteUri)), token,
            allowByteRanges: false).ConfigureAwait(false);
        var root = XElement.Parse(result.Manifest);
        foreach (var initialization in root.Descendants().Where(element => element.Name.LocalName == "Initialization"))
        {
            var attribute = initialization.Attribute("sourceURL")!;
            var uri = new UriBuilder(attribute.Value) { Path = "/dashinit" };
            attribute.Value = uri.Uri.AbsoluteUri;
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    // LibVLC 3 rejects the ISO BMFF version-1 unknown-duration sentinel even
    // for finite DASH. Only normalize that field in bounded initialization
    // bytes; the validated MPD and fragment timestamps still define duration.
    internal static void NormalizeFiniteDashInitialization(byte[] bytes)
    {
        bool moov = false, movieHeader = false;
        void Boxes(int start, int end, bool inMovie)
        {
            int offset = start;
            while (offset < end)
            {
                if (end - offset < 8) throw new InvalidDataException("The DASH initialization has a truncated MP4 box.");
                ulong size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
                int header = 8;
                if (size == 1)
                {
                    if (end - offset < 16) throw new InvalidDataException("The DASH initialization has a truncated large box.");
                    size = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset + 8, 8)); header = 16;
                }
                if (size == 0) size = (ulong)(end - offset);
                if (size < (ulong)header || size > (ulong)(end - offset))
                    throw new InvalidDataException("The DASH initialization has an invalid MP4 box size.");
                string type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
                int payload = offset + header, next = offset + (int)size;
                if (!inMovie && type == "moov") { moov = true; Boxes(payload, next, true); }
                else if (inMovie && type == "mvhd")
                {
                    movieHeader = true;
                    if (next - payload < 4) throw new InvalidDataException("The DASH movie header is truncated.");
                    byte version = bytes[payload];
                    int scaleOffset = payload + (version == 1 ? 20 : 12);
                    int durationOffset = scaleOffset + 4;
                    if (version > 1 || next - durationOffset < (version == 1 ? 8 : 4) ||
                        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(scaleOffset, 4)) == 0)
                        throw new InvalidDataException("The DASH movie header is invalid.");
                    if (version == 1 && BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(durationOffset, 8)) == ulong.MaxValue)
                        bytes.AsSpan(durationOffset, 8).Clear();
                }
                offset = next;
            }
        }
        if (bytes.Length > 2 * 1024 * 1024 || !PlaylistMediaDownload.IsDashMp4Prefix(bytes))
            throw new InvalidDataException("The DASH initialization is empty, excessive or not MP4.");
        Boxes(0, bytes.Length, false);
        if (!moov || !movieHeader) throw new InvalidDataException("The DASH initialization has no movie header.");
    }

    private async Task ServeDashInitializationAsync(HttpListenerResponse response, ProxySession session, string remoteUrl,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_limits.ManifestDeadline);
        using var remote = await SendSafeAsync(remoteUrl, session, HttpCompletionOption.ResponseHeadersRead,
            token: deadline.Token).ConfigureAwait(false);
        if (!remote.IsSuccessStatusCode)
        {
            await WriteError(response, (int)remote.StatusCode, "The DASH initialization is unavailable."); return;
        }
        byte[] bytes = await ReadWithLimitAsync(remote.Content, 2 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
        NormalizeFiniteDashInitialization(bytes);
        response.StatusCode = 200; response.ContentType = "video/mp4"; response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
        response.Close();
    }

    private async Task ServeDashManifestAsync(HttpListenerResponse response, ProxySession session,
        string sessionId, string remoteUrl, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_limits.ManifestDeadline);
        try
        {
            using var remote = await SendSafeAsync(remoteUrl, session, HttpCompletionOption.ResponseHeadersRead,
                token: deadline.Token).ConfigureAwait(false);
            if (!remote.IsSuccessStatusCode)
            {
                await WriteError(response, (int)remote.StatusCode, "The DASH manifest is unavailable.");
                return;
            }
            byte[] bytes = await ReadWithLimitAsync(remote.Content, 2 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
            Uri effective = remote.RequestMessage?.RequestUri ?? new Uri(remoteUrl);
            string manifest = await RewriteDashManifestForSessionAsync(bytes, effective, sessionId, deadline.Token).ConfigureAwait(false);
            byte[] rewritten = Encoding.UTF8.GetBytes(manifest);
            response.StatusCode = 200;
            response.ContentType = "application/dash+xml";
            response.ContentLength64 = rewritten.Length;
            await response.OutputStream.WriteAsync(rewritten, deadline.Token).ConfigureAwait(false);
            response.Close();
        }
        catch (Exception ex) when (!token.IsCancellationRequested &&
            ex is InvalidDataException or OperationCanceledException or HttpRequestException)
        {
            await WriteError(response, 502, "This finite DASH source could not be validated for native playback.");
        }
    }

    private async Task ServeDashSegmentAsync(HttpListenerResponse response, ProxySession session,
        string remoteUrl, HttpListenerRequest request, CancellationToken token)
    {
        long limit = _limits.MaximumSegmentBytes;
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(token);
        overall.CancelAfter(_limits.MediaDeadline);
        using var headersDeadline = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
        headersDeadline.CancelAfter(_limits.HeaderDeadline);
        string? range = request.Headers["Range"];
        if (range != null && (!RangeHeaderValue.TryParse(range, out var parsed) || parsed.Unit != "bytes" ||
            parsed.Ranges.Count != 1 || parsed.Ranges.First().From == null ||
            parsed.Ranges.First().From < 0 || parsed.Ranges.First().To < parsed.Ranges.First().From))
        {
            await WriteError(response, 400, "This DASH byte range is unsupported.");
            return;
        }
        using var remote = await SendSafeAsync(remoteUrl, session, HttpCompletionOption.ResponseHeadersRead,
            range, headersDeadline.Token).ConfigureAwait(false);
        headersDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
        if (!remote.IsSuccessStatusCode)
        {
            await WriteError(response, (int)remote.StatusCode, "The DASH media resource is unavailable.");
            return;
        }
        if (remote.Content.Headers.ContentLength > limit)
        {
            await WriteError(response, 502, "The DASH resource exceeded its size limit.");
            return;
        }
        // A native seek may start inside an already validated MP4 box. Verify the
        // resource's leading bytes separately before relaying that internal range.
        if (remote.Content.Headers.ContentRange?.From is > 0)
        {
            headersDeadline.CancelAfter(_limits.HeaderDeadline);
            using var leading = await SendSafeAsync(remoteUrl, session, HttpCompletionOption.ResponseHeadersRead,
                "bytes=0-1023", headersDeadline.Token).ConfigureAwait(false);
            headersDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
            if (!leading.IsSuccessStatusCode || leading.Content.Headers.ContentRange?.From is > 0)
                throw new InvalidDataException("The DASH initialization prefix is unavailable.");
            await using var leadingStream = await leading.Content.ReadAsStreamAsync(overall.Token).ConfigureAwait(false);
            byte[] leadingBytes = new byte[1024];
            using var prefixDeadline = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            prefixDeadline.CancelAfter(_limits.MediaIdleDeadline);
            int count = await leadingStream.ReadAtLeastAsync(leadingBytes, 8, false, prefixDeadline.Token).ConfigureAwait(false);
            if (!PlaylistMediaDownload.IsDashMp4Prefix(leadingBytes.AsSpan(0, count)))
                throw new InvalidDataException("The DASH resource has no readable MP4 prefix.");
        }
        await using var input = await remote.Content.ReadAsStreamAsync(overall.Token).ConfigureAwait(false);
        byte[] buffer = new byte[16 * 1024];
        using var bodyDeadline = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
        bodyDeadline.CancelAfter(_limits.MediaIdleDeadline);
        int first = await input.ReadAtLeastAsync(buffer, 8, false, bodyDeadline.Token).ConfigureAwait(false);
        if (first == 0 || (remote.Content.Headers.ContentRange?.From is not > 0 &&
            !PlaylistMediaDownload.IsDashMp4Prefix(buffer.AsSpan(0, first))))
        {
            await WriteError(response, 502, "The CDN returned empty or non-MP4 DASH media.");
            return;
        }
        response.StatusCode = (int)remote.StatusCode;
        response.ContentType = "video/mp4";
        if (remote.Content.Headers.ContentLength is { } length) response.ContentLength64 = length;
        if (remote.Content.Headers.ContentRange is { } contentRange) response.Headers["Content-Range"] = contentRange.ToString();
        if (remote.Headers.AcceptRanges.Count > 0) response.Headers["Accept-Ranges"] = string.Join(",", remote.Headers.AcceptRanges);
        await CopyMediaBodyAsync(input, response.OutputStream, buffer.AsMemory(0, first),
            limit, remote.Content.Headers.ContentLength, _limits.MediaIdleDeadline, overall.Token).ConfigureAwait(false);
        response.Close();
    }
}
