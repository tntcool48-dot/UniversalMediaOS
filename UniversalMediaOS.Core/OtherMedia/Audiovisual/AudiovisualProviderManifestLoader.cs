using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalMediaOS.Core.Configuration;

namespace UniversalMediaOS.Core.OtherMedia;

public sealed class AudiovisualProviderManifestLoader
{
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxIncludeDepth = 4;
    private const int MaxManifestCount = 32;
    private static readonly TimeSpan RemoteCacheDuration = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _hostResolver;
    private readonly ConcurrentDictionary<string, RemoteManifestCacheEntry> _remoteCache =
        new(StringComparer.OrdinalIgnoreCase);

    public AudiovisualProviderManifestLoader(HttpClient? httpClient = null)
        : this(
            httpClient ?? AudiovisualHttp.SharedClient,
            TimeProvider.System,
            static (host, token) => Dns.GetHostAddressesAsync(host, token))
    {
    }

    internal AudiovisualProviderManifestLoader(
        HttpClient httpClient,
        TimeProvider timeProvider,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostResolver = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _hostResolver = hostResolver ??
            ((host, token) => Dns.GetHostAddressesAsync(host, token));
    }

    public Task<AudiovisualProviderManifest> LoadConfiguredAsync(
        DomainHotSwapper config,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        AudiovisualOptions options = AudiovisualOptions.FromConfiguration(config);
        return LoadManyAsync(options.ProviderIndexLocations, token);
    }

    public Task<AudiovisualProviderManifest> LoadAsync(
        string locationOrJson,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locationOrJson);
        return LoadManyAsync([locationOrJson], token);
    }

    public async Task<AudiovisualProviderManifest> LoadManyAsync(
        IEnumerable<string> locationsOrJson,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(locationsOrJson);

        var state = new LoadState();
        foreach (string location in locationsOrJson.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            token.ThrowIfCancellationRequested();
            await LoadRecursiveAsync(
                ManifestReference.Create(location.Trim(), parent: null),
                depth: 0,
                state,
                token).ConfigureAwait(false);
        }

        return new AudiovisualProviderManifest
        {
            Version = 1,
            Providers = state.Providers.Values
                .OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private async Task LoadRecursiveAsync(
        ManifestReference reference,
        int depth,
        LoadState state,
        CancellationToken token)
    {
        if (depth > MaxIncludeDepth)
        {
            throw new InvalidDataException($"Provider manifest include depth exceeds {MaxIncludeDepth}.");
        }

        if (state.Visited.Count >= MaxManifestCount)
        {
            throw new InvalidDataException($"Provider manifest count exceeds {MaxManifestCount}.");
        }

        string visitKey = reference.VisitKey;
        if (!state.Visited.Add(visitKey))
        {
            return;
        }

        string json = await ReadReferenceAsync(reference, token).ConfigureAwait(false);
        AudiovisualProviderManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AudiovisualProviderManifest>(json, JsonOptions)
                ?? throw new InvalidDataException("Provider manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Provider manifest '{reference.DisplayName}' is not valid JSON.", ex);
        }

        if (manifest.Version != 1)
        {
            throw new InvalidDataException(
                $"Provider manifest '{reference.DisplayName}' uses unsupported version {manifest.Version}.");
        }

        foreach (AudiovisualProviderDefinition rawProvider in manifest.Providers)
        {
            AudiovisualProviderDefinition provider = NormalizeProvider(rawProvider, reference);
            if (reference.RemoteUri is not null && provider.AllowPrivateNetwork)
            {
                throw new InvalidDataException(
                    $"Remote manifest '{reference.DisplayName}' cannot grant provider '{provider.Id}' private-network access.");
            }

            IReadOnlyList<string> errors = provider.GetValidationErrors();
            if (errors.Count > 0)
            {
                throw new InvalidDataException(
                    $"Provider '{provider.Id}' in '{reference.DisplayName}' is invalid: {string.Join(" ", errors)}");
            }

            if (!state.Providers.TryAdd(provider.Id, provider))
            {
                throw new InvalidDataException($"Duplicate audiovisual provider id '{provider.Id}'.");
            }
        }

        foreach (string include in manifest.Includes.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            ManifestReference child = ManifestReference.Create(include.Trim(), reference);
            await LoadRecursiveAsync(child, depth + 1, state, token).ConfigureAwait(false);
        }
    }

    private async Task<string> ReadReferenceAsync(
        ManifestReference reference,
        CancellationToken token)
    {
        if (reference.InlineJson is not null)
        {
            return reference.InlineJson.Length <= MaxManifestBytes
                ? reference.InlineJson
                : throw new InvalidDataException("Inline provider manifest exceeds the size limit.");
        }

        if (reference.LocalPath is not null)
        {
            var file = new FileInfo(reference.LocalPath);
            if (!file.Exists)
            {
                throw new FileNotFoundException("Provider manifest was not found.", file.FullName);
            }

            if (file.Length > MaxManifestBytes)
            {
                throw new InvalidDataException($"Provider manifest '{file.FullName}' exceeds the size limit.");
            }

            return await File.ReadAllTextAsync(file.FullName, Encoding.UTF8, token).ConfigureAwait(false);
        }

        Uri remoteUri = reference.RemoteUri
            ?? throw new InvalidDataException("Provider manifest reference has no readable location.");
        await AudiovisualNetworkBoundary.EnsurePublicHostAsync(
            remoteUri,
            _hostResolver,
            token).ConfigureAwait(false);
        string cacheKey = remoteUri.AbsoluteUri;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (_remoteCache.TryGetValue(cacheKey, out RemoteManifestCacheEntry? cached) &&
            cached.ExpiresAtUtc > now)
        {
            return cached.Json;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, remoteUri);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string json = await ReadBoundedStringAsync(response.Content, MaxManifestBytes, timeoutCts.Token)
            .ConfigureAwait(false);
        _remoteCache[cacheKey] = new RemoteManifestCacheEntry(
            json,
            now.Add(RemoteCacheDuration));
        return json;
    }

    private static AudiovisualProviderDefinition NormalizeProvider(
        AudiovisualProviderDefinition provider,
        ManifestReference origin)
    {
        string baseUrl = provider.BaseUrl.Trim();
        string searchTemplate = provider.SearchUrlTemplate.Trim();

        if (baseUrl.Length == 0 && origin.RemoteUri is not null)
        {
            baseUrl = new Uri(origin.RemoteUri, ".").AbsoluteUri;
        }

        if (baseUrl.Length == 0 && origin.LocalPath is not null)
        {
            // Local manifests may still declare absolute provider URLs, but a
            // filesystem location must never become an HTTP request base.
            baseUrl = string.Empty;
        }

        return provider with
        {
            Id = provider.Id.Trim(),
            Name = provider.Name.Trim(),
            BaseUrl = baseUrl,
            SearchUrlTemplate = searchTemplate,
            Adapter = string.IsNullOrWhiteSpace(provider.Adapter)
                ? "json-index-v1"
                : provider.Adapter.Trim(),
            MediaKinds = provider.MediaKinds.Distinct().ToArray(),
            Languages = provider.Languages
                .Where(language => !string.IsNullOrWhiteSpace(language))
                .Select(language => language.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            RightsStatement = provider.RightsStatement.Trim(),
            Attribution = provider.Attribution.Trim(),
            AllowedHosts = provider.AllowedHosts
                .Where(host => !string.IsNullOrWhiteSpace(host))
                .Select(host => host.Trim().TrimEnd('.'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static async Task<string> ReadBoundedStringAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken token)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
        {
            throw new InvalidDataException("Provider manifest response exceeds the size limit.");
        }

        await using Stream stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];

        while (true)
        {
            int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException("Provider manifest response exceeds the size limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), token).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed class LoadState
    {
        public HashSet<string> Visited { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, AudiovisualProviderDefinition> Providers { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record RemoteManifestCacheEntry(string Json, DateTimeOffset ExpiresAtUtc);

    private sealed record ManifestReference
    {
        public string? InlineJson { get; init; }

        public string? LocalPath { get; init; }

        public Uri? RemoteUri { get; init; }

        public string VisitKey => InlineJson is not null
            ? $"inline:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(InlineJson)))}"
            : LocalPath is not null
                ? $"file:{LocalPath}"
                : $"uri:{RemoteUri!.AbsoluteUri}";

        public string DisplayName => LocalPath ?? RemoteUri?.AbsoluteUri ?? "inline manifest";

        public static ManifestReference Create(string value, ManifestReference? parent)
        {
            if (value.TrimStart().StartsWith('{'))
            {
                return new ManifestReference { InlineJson = value.Trim() };
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? absoluteUri))
            {
                if (absoluteUri.Scheme == Uri.UriSchemeFile)
                {
                    return new ManifestReference
                    {
                        LocalPath = Path.GetFullPath(absoluteUri.LocalPath)
                    };
                }

                if (absoluteUri.Scheme is not ("http" or "https") ||
                    !string.IsNullOrWhiteSpace(absoluteUri.UserInfo))
                {
                    throw new InvalidDataException("Remote provider manifests must use HTTP(S) without embedded credentials.");
                }

                return new ManifestReference { RemoteUri = absoluteUri };
            }

            if (parent?.RemoteUri is not null &&
                Uri.TryCreate(parent.RemoteUri, value, out Uri? relativeRemote) &&
                relativeRemote.Scheme is "http" or "https")
            {
                return new ManifestReference { RemoteUri = relativeRemote };
            }

            string localPath = parent?.LocalPath is not null
                ? Path.Combine(Path.GetDirectoryName(parent.LocalPath) ?? string.Empty, value)
                : value;
            return new ManifestReference { LocalPath = Path.GetFullPath(localPath) };
        }
    }
}
