using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UniversalMediaOS.Core.Streaming;

namespace UniversalMediaOS.Core.OtherMedia;

internal sealed partial class PlaylistMediaDownload
{
    private static readonly XNamespace DashNamespace = "urn:mpeg:dash:schema:mpd:2011";
    private static readonly Regex DashToken = new(@"\$(RepresentationID|Bandwidth|Number|Time)(?:%0([1-9])d)?\$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private async Task<(string Path, double Duration)> LocalizeDashAsync(AudiovisualSource source,
        string directory, PlaylistJob job, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var context = new ProxySession(source.Location.AbsoluteUri, source.UserAgent, source.Cookie,
            null, source.Referer, DateTime.UtcNow, source.RequestHeaders);
        using var response = await SendAsync(source.Location, context, deadline.Token).ConfigureAwait(false);
        byte[] bytes = await ReadSmallAsync(response, 2 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
        Uri effective = response.RequestMessage?.RequestUri ?? source.Location;
        var (manifest, duration) = await RewriteDashAsync(bytes, effective, async uri =>
            Path.GetFileName(await job.AssetAsync(uri, false, requireMp4: true).ConfigureAwait(false)), token).ConfigureAwait(false);
        string path = Path.Combine(directory, "playlist.mpd");
        await File.WriteAllTextAsync(path, manifest, new UTF8Encoding(false), token).ConfigureAwait(false);
        job.CreatedFiles.Add(path);
        return (path, duration);
    }

    // Both native streaming and saved downloads use this finite-format contract.
    // Rebuild only selected tracks and explicit resources; never relay provider XML.
    internal static async Task<(string Manifest, double Duration)> RewriteDashAsync(byte[] bytes, Uri effective,
        Func<Uri, Task<string>> mapResource, CancellationToken token = default, bool allowByteRanges = true)
    {
        using var input = new MemoryStream(bytes);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
        XElement root;
        try { root = XElement.Load(reader); }
        catch (XmlException ex) { throw new InvalidDataException("The source did not return a valid DASH manifest.", ex); }
        if (root.Name != DashNamespace + "MPD" || ((string?)root.Attribute("type") ?? "static") != "static")
            throw new InvalidDataException("Only completed static DASH videos can be downloaded.");
        if (root.Descendants().Any(element => element.Name.LocalName is "ContentProtection" or "SegmentBase") ||
            root.DescendantsAndSelf().Attributes().Any(attribute => attribute.Name.NamespaceName == "http://www.w3.org/1999/xlink"))
            throw new InvalidDataException("This protected, indexed or externally linked DASH format is not supported for downloads.");
        XElement[] periods = root.Elements(DashNamespace + "Period").ToArray();
        if (periods.Length != 1 || ParseDashDuration((string?)periods[0].Attribute("start"), 0) != 0)
            throw new InvalidDataException("Downloads require one complete DASH period beginning at the start of the video.");
        XElement period = periods[0];
        double duration = ParseDashDuration((string?)period.Attribute("duration"),
            ParseDashDuration((string?)root.Attribute("mediaPresentationDuration"), double.NaN));
        double declared = ParseDashDuration((string?)root.Attribute("mediaPresentationDuration"), duration);
        if (!double.IsFinite(duration) || duration <= 0 || Math.Abs(duration - declared) > Math.Max(1, duration * .001))
            throw new InvalidDataException("The DASH manifest has no consistent finite video duration.");

        var selected = new List<(XElement Set, XElement Representation, string Type, string Language)>();
        foreach (XElement set in period.Elements(DashNamespace + "AdaptationSet"))
        {
            XElement[] representations = set.Elements(DashNamespace + "Representation").ToArray();
            if (representations.Length == 0 || representations.Length > 128)
                throw new InvalidDataException("The DASH track list is empty or excessive.");
            string type = (string?)set.Attribute("contentType") ??
                ((string?)set.Attribute("mimeType") ?? (string?)representations[0].Attribute("mimeType") ?? "").Split('/')[0];
            if (type is not ("video" or "audio"))
                throw new InvalidDataException("This DASH video has an unsupported embedded track. Choose another native source.");
            if (type == "video" && selected.Any(track => track.Type == "video"))
                throw new InvalidDataException("This DASH video has multiple video groups. Its exact rendition is not qualified for downloads.");
            foreach (var languageGroup in representations.GroupBy(rep =>
                         (string?)rep.Attribute("lang") ?? (string?)set.Attribute("lang") ?? ""))
            {
                XElement representation = languageGroup.OrderByDescending(rep => DashInteger(rep, "bandwidth", 0)).First();
                string mime = (string?)representation.Attribute("mimeType") ?? (string?)set.Attribute("mimeType") ?? "";
                if (mime != type + "/mp4")
                    throw new InvalidDataException("Only MP4-based DASH tracks are supported for downloads.");
                selected.Add((set, representation, type, languageGroup.Key));
            }
        }
        if (selected.Count(track => track.Type == "video") != 1 || !selected.Any(track => track.Type == "audio") || selected.Count > 16)
            throw new InvalidDataException("The DASH video must have one video and a bounded readable audio track list.");

        // Build a fresh manifest from owned names only. Provider XML never reaches
        // FFmpeg with remote BaseURLs, templates, external links or hidden resources.
        string durationText = XmlConvert.ToString(TimeSpan.FromSeconds(duration));
        var localPeriod = new XElement(DashNamespace + "Period", new XAttribute("start", "PT0S"),
            new XAttribute("duration", durationText));
        var localRoot = new XElement(DashNamespace + "MPD", new XAttribute("type", "static"),
            new XAttribute("profiles", "urn:mpeg:dash:profile:isoff-main:2011"),
            new XAttribute("minBufferTime", "PT1S"), new XAttribute("mediaPresentationDuration", durationText), localPeriod);
        int index = 0;
        int resourceCount = 0;
        async Task<string> MapAsync(Uri uri)
        {
            token.ThrowIfCancellationRequested();
            if (++resourceCount > 20000 || !HlsLoopbackProxy.IsAllowedRemoteUri(uri.AbsoluteUri))
                throw new InvalidDataException("The DASH resource list is excessive or has an unsafe location.");
            return await mapResource(uri).ConfigureAwait(false);
        }
        foreach (var track in selected.OrderBy(track => track.Type == "video" ? 0 : 1))
        {
            token.ThrowIfCancellationRequested();
            XElement[] ancestry = [root, period, track.Set, track.Representation];
            Uri baseUri = effective;
            foreach (XElement ancestor in ancestry)
            {
                XElement[] bases = ancestor.Elements(DashNamespace + "BaseURL").ToArray();
                if (bases.Length > 1) throw new InvalidDataException("Multiple DASH base locations are not qualified for downloads.");
                if (bases.Length == 1) baseUri = new Uri(baseUri, bases[0].Value.Trim());
            }
            XElement[] descriptors = ancestry.SelectMany(ancestor => ancestor.Elements()
                .Where(element => element.Name == DashNamespace + "SegmentTemplate" || element.Name == DashNamespace + "SegmentList")).ToArray();
            if (descriptors.Length == 0) throw new InvalidDataException("The DASH video has no supported segment description.");
            XName kind = descriptors[^1].Name;
            descriptors = descriptors.Where(element => element.Name == kind).ToArray();
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XElement descriptor in descriptors)
                foreach (XAttribute attribute in descriptor.Attributes()) attributes[attribute.Name.LocalName] = attribute.Value;
            long scale = DashInteger(attributes, "timescale", 1);
            if (scale < 1 || DashInteger(attributes, "presentationTimeOffset", 0) != 0)
                throw new InvalidDataException("This DASH timescale or presentation offset is not qualified for downloads.");
            XElement? timeline = descriptors.Select(element => element.Element(DashNamespace + "SegmentTimeline"))
                .LastOrDefault(element => element != null);
            var schedule = DashSchedule(timeline, attributes, scale, duration);
            var localSegments = new XElement(DashNamespace + "SegmentList", new XAttribute("timescale", scale),
                new XElement(DashNamespace + "SegmentTimeline", schedule.Select(segment =>
                    new XElement(DashNamespace + "S", new XAttribute("t", segment.Start), new XAttribute("d", segment.Length)))));
            if (schedule.All(segment => segment.Length == schedule[0].Length))
                localSegments.Add(new XAttribute("duration", schedule[0].Length));
            string initialization;
            string? initRange = null;
            XElement[]? urls = null;
            if (kind.LocalName == "SegmentTemplate")
            {
                initialization = attributes.GetValueOrDefault("initialization", "");
                if (initialization.Length == 0 || !attributes.ContainsKey("media"))
                    throw new InvalidDataException("The DASH template has no initialization or media reference.");
                initialization = ExpandDashTemplate(initialization, track.Representation, 0, 0);
            }
            else
            {
                XElement? init = descriptors.Select(element => element.Element(DashNamespace + "Initialization"))
                    .LastOrDefault(element => element != null);
                if (init == null)
                    throw new InvalidDataException("The DASH segment list has no initialization reference.");
                initialization = (string?)init?.Attribute("sourceURL") ?? "";
                initRange = (string?)init?.Attribute("range");
                urls = descriptors.Select(element => element.Elements(DashNamespace + "SegmentURL").ToArray())
                    .LastOrDefault(elements => elements.Length > 0);
                if (urls == null || urls.Length != schedule.Count)
                    throw new InvalidDataException("The DASH segment list does not cover the declared video duration.");
            }
            if (!allowByteRanges && (initRange != null || urls?.Any(url => url.Attribute("mediaRange") != null) == true))
                throw new InvalidDataException("Indexed DASH ranges require Watch via download. This native format is not qualified.");
            string localInit = await MapAsync(new Uri(baseUri, initialization)).ConfigureAwait(false);
            var localInitialization = new XElement(DashNamespace + "Initialization", new XAttribute("sourceURL", localInit));
            CopyDashRange(initRange, localInitialization, "range");
            localSegments.AddFirst(localInitialization);
            long number = DashInteger(attributes, "startNumber", 1);
            for (int segmentIndex = 0; segmentIndex < schedule.Count; segmentIndex++)
            {
                var segment = schedule[segmentIndex];
                string remote = urls == null ? ExpandDashTemplate(attributes["media"], track.Representation,
                    checked(number + segmentIndex), segment.Start) : (string?)urls[segmentIndex].Attribute("media") ?? "";
                if (remote.Length == 0) throw new InvalidDataException("A DASH segment has no media reference.");
                string local = await MapAsync(new Uri(baseUri, remote)).ConfigureAwait(false);
                var localSegment = new XElement(DashNamespace + "SegmentURL", new XAttribute("media", local));
                CopyDashRange(urls == null ? null : (string?)urls[segmentIndex].Attribute("mediaRange"), localSegment, "mediaRange");
                localSegments.Add(localSegment);
            }
            var localSet = new XElement(DashNamespace + "AdaptationSet", new XAttribute("contentType", track.Type),
                new XAttribute("mimeType", track.Type + "/mp4"));
            if (track.Language.Length > 0) localSet.Add(new XAttribute("lang", track.Language));
            var localRepresentation = new XElement(DashNamespace + "Representation", new XAttribute("id", $"track-{index++}"),
                new XAttribute("bandwidth", DashInteger(track.Representation, "bandwidth", 1)), localSegments);
            foreach (string attribute in new[] { "codecs", "width", "height", "frameRate", "audioSamplingRate" })
                if ((string?)track.Representation.Attribute(attribute) is { Length: > 0 } value)
                    localRepresentation.Add(new XAttribute(attribute, value));
            localSet.Add(localRepresentation); localPeriod.Add(localSet);
        }
        string rewritten = localRoot.ToString(SaveOptions.DisableFormatting);
        if (Encoding.UTF8.GetByteCount(rewritten) > 16 * 1024 * 1024)
            throw new InvalidDataException("The rewritten DASH manifest is excessive.");
        return (rewritten, duration);
    }

    private static double ParseDashDuration(string? value, double fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try { return XmlConvert.ToTimeSpan(value).TotalSeconds; }
        catch (FormatException ex) { throw new InvalidDataException("The DASH duration is invalid.", ex); }
    }

    private static long DashInteger(XElement element, string name, long fallback) =>
        DashInteger(element.Attributes().ToDictionary(attribute => attribute.Name.LocalName, attribute => attribute.Value), name, fallback);

    private static long DashInteger(IReadOnlyDictionary<string, string> attributes, string name, long fallback)
    {
        if (!attributes.TryGetValue(name, out string? value)) return fallback;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) || Math.Abs((double)number) > 9e15)
            throw new InvalidDataException("The DASH segment numbering is invalid.");
        return number;
    }

    private static List<(long Start, long Length)> DashSchedule(XElement? timeline,
        IReadOnlyDictionary<string, string> attributes, long scale, double duration)
    {
        double end = duration * scale;
        if (!double.IsFinite(end) || end > 9e15) throw new InvalidDataException("The DASH timeline is excessive.");
        var schedule = new List<(long Start, long Length)>();
        long cursor = 0;
        if (timeline == null)
        {
            long length = DashInteger(attributes, "duration", 0);
            if (length <= 0 || Math.Ceiling(end / length) > 20000)
                throw new InvalidDataException("The DASH segment duration or count is invalid.");
            while (cursor < end) { schedule.Add((cursor, length)); cursor = checked(cursor + length); }
        }
        else
        {
            XElement[] entries = timeline.Elements(DashNamespace + "S").ToArray();
            if (entries.Length == 0 || entries.Length > 20000) throw new InvalidDataException("The DASH timeline is empty or excessive.");
            for (int i = 0; i < entries.Length; i++)
            {
                XElement entry = entries[i];
                long start = DashInteger(entry, "t", cursor), length = DashInteger(entry, "d", 0), repeat = DashInteger(entry, "r", 0);
                if (start != cursor || length <= 0 || repeat < -1)
                    throw new InvalidDataException("The DASH timeline has a gap, overlap or invalid segment.");
                if (repeat == -1)
                {
                    double stop = i + 1 < entries.Length ? DashInteger(entries[i + 1], "t", -1) : end;
                    repeat = (long)Math.Ceiling((stop - start) / length) - 1;
                }
                if (repeat < 0 || repeat > 20000 - schedule.Count - 1)
                    throw new InvalidDataException("The DASH timeline has excessive or invalid repeats.");
                for (long r = 0; r <= repeat; r++)
                { schedule.Add((cursor, length)); cursor = checked(cursor + length); }
            }
            if (cursor < end - scale * .1 || schedule.Count == 0 || schedule[^1].Start >= end)
                throw new InvalidDataException("The DASH timeline does not cover the declared video duration.");
        }
        return schedule;
    }

    private static string ExpandDashTemplate(string template, XElement representation, long number, long time)
    {
        string escaped = template.Replace("$$", "\u0001", StringComparison.Ordinal);
        string expanded = DashToken.Replace(escaped, match =>
        {
            string name = match.Groups[1].Value;
            string value = name switch
            {
                "RepresentationID" => (string?)representation.Attribute("id") ?? throw new InvalidDataException("The DASH representation has no ID."),
                "Bandwidth" => DashInteger(representation, "bandwidth", 0).ToString(CultureInfo.InvariantCulture),
                "Number" => number.ToString(CultureInfo.InvariantCulture),
                _ => time.ToString(CultureInfo.InvariantCulture)
            };
            return match.Groups[2].Success && name != "RepresentationID"
                ? value.PadLeft(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), '0') : value;
        });
        if (expanded.Contains('$')) throw new InvalidDataException("The DASH URL template contains an unsupported token.");
        return expanded.Replace('\u0001', '$');
    }

    private static void CopyDashRange(string? range, XElement target, string attribute)
    {
        if (range == null) return;
        string[] endpoints = range.Split('-');
        if (endpoints.Length != 2 || !long.TryParse(endpoints[0], out long start) || !long.TryParse(endpoints[1], out long end) || start < 0 || end < start)
            throw new InvalidDataException("The DASH byte range is invalid.");
        target.Add(new XAttribute(attribute, range));
    }

    internal static bool IsDashMp4Prefix(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < 8) return false;
        uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(prefix);
        return length >= 8 && (prefix[4..8].SequenceEqual("ftyp"u8) || prefix[4..8].SequenceEqual("styp"u8) ||
            prefix[4..8].SequenceEqual("moof"u8) || prefix[4..8].SequenceEqual("moov"u8));
    }
}
