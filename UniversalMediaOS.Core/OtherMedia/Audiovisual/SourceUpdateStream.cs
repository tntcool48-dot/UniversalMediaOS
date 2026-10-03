using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace UniversalMediaOS.Core.OtherMedia;

internal sealed record SourceProviderBatch(string Id, Func<CancellationToken, Task<IReadOnlyList<AudiovisualSource>>> Search)
{
    internal Func<CancellationToken, IAsyncEnumerable<AudiovisualSource>>? Stream { get; init; }
}

internal static class SourceUpdateStream
{
    private sealed record ProviderRead(string Id, IAsyncEnumerator<AudiovisualSource> Iterator, Stopwatch Clock);
    private sealed record ReadResult(bool HasSource, ProviderOutcome? Failure);

    internal static async IAsyncEnumerable<AudiovisualSourceUpdate> ReadAsync(SourceSearchRequest request,
        IEnumerable<SourceProviderBatch> providers,
        Func<AudiovisualSource, CancellationToken, Task<AudiovisualSource?>>? verifyArabic = null,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var readers = providers.Select(provider => new ProviderRead(provider.Id,
            ReadProviderAsync(provider, lifetime.Token).GetAsyncEnumerator(lifetime.Token), Stopwatch.StartNew())).ToList();
        var pending = readers.ToDictionary(reader => MoveNextSafelyAsync(reader), reader => reader);
        int candidates = 0, ready = 0, unverified = 0, rejected = 0, failures = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending.Keys).WaitAsync(token).ConfigureAwait(false);
                var reader = pending[completed];
                pending.Remove(completed);
                ReadResult batch = await completed.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (batch.Failure != null)
                {
                    failures++;
                    yield return new(request.OperationId, "", AudiovisualSourceUpdateKind.ProviderFailed, Outcome: batch.Failure);
                    continue;
                }
                if (batch.HasSource)
                {
                    var source = reader.Iterator.Current;
                    // Continue this provider while the caller consumes the current candidate.
                    pending.Add(MoveNextSafelyAsync(reader), reader);
                    token.ThrowIfCancellationRequested();
                    string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ProviderId + "\n" + source.Location.OriginalString)));
                    if (!seen.Add(key)) continue;
                    candidates++;
                    yield return new(request.OperationId, key, AudiovisualSourceUpdateKind.CandidateDiscovered, source);
                    var verification = ExactAudiovisualMatcher.VerifyEvidence(request, source.Evidence);
                    AudiovisualSource candidate = source;
                    if (verification.Status == SourceVerificationStatus.Verified)
                    {
                        if (!source.Location.IsAbsoluteUri || source.Location.Scheme is not ("http" or "https") ||
                            source.Location.UserInfo.Length != 0 || source.AccessMode is not (AudiovisualSourceAccessMode.DirectMedia or AudiovisualSourceAccessMode.WebPage))
                            verification = new(SourceVerificationStatus.Rejected, "unsupported_transport");
                        else
                        {
                            // Presentation metadata is applied only after independent evidence passes.
                            candidate = source with { Identity = source.Evidence!.Identity!, Unit = source.Evidence.Unit!,
                                Languages = source.Evidence.Audio?.Languages ?? Array.Empty<string>() };
                            if (request.RequireArabicCartoonVerification)
                            {
                                var checkedSource = verifyArabic == null ? null : await verifyArabic(candidate, lifetime.Token).ConfigureAwait(false);
                                if (checkedSource == null) verification = new(SourceVerificationStatus.Unverified, "arabic_transport_unverified");
                                else candidate = checkedSource;
                            }
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    yield return new(request.OperationId, key, AudiovisualSourceUpdateKind.VerificationChanged, candidate, verification);
                    if (verification.Status == SourceVerificationStatus.Verified)
                    {
                        ready++;
                        yield return new(request.OperationId, key, AudiovisualSourceUpdateKind.SourceReady, candidate, verification);
                    }
                    else if (verification.Status == SourceVerificationStatus.Rejected) rejected++;
                    else unverified++;
                }
            }
            token.ThrowIfCancellationRequested();
            yield return new(request.OperationId, "", AudiovisualSourceUpdateKind.Completed,
                Completion: new(candidates, ready, unverified, rejected, failures));
        }
        finally
        {
            lifetime.Cancel();
            await Task.WhenAll(pending.Keys).ConfigureAwait(false);
            foreach (var reader in readers)
                await reader.Iterator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<AudiovisualSource> ReadProviderAsync(SourceProviderBatch provider,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (provider.Stream != null)
        {
            await foreach (var source in provider.Stream(token).WithCancellation(token).ConfigureAwait(false))
                yield return source;
        }
        else
        {
            foreach (var source in await provider.Search(token).ConfigureAwait(false))
                yield return source;
        }
    }

    private static async Task<ReadResult> MoveNextSafelyAsync(ProviderRead reader)
    {
        try { return new(await reader.Iterator.MoveNextAsync().ConfigureAwait(false), null); }
        catch (Exception ex)
        {
            var status = ex is OperationCanceledException ? ProviderOutcomeStatus.Timeout : ProviderOutcomeStatus.Unavailable;
            return new(false, new(reader.Id, status, reader.Clock.Elapsed,
                DiagnosticCode: ex is OperationCanceledException ? "provider_cancelled_or_timed_out" : "provider_search_failed"));
        }
    }
}
