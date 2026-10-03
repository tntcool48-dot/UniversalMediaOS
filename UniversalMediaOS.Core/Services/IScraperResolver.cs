using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UniversalMediaOS.Core.Services;

public interface IScraperResolver
{
    bool IsAvailable { get; }
    Task EnsureReadyAsync(CancellationToken token = default);
    Task<ScraperStreamResult?> ResolveAsync(string query, string episodeId, int maxSiteAttempts,
        CancellationToken token = default, Action<string>? progressLog = null, string audioPreference = "sub",
        bool preferNative = true, IReadOnlyList<string>? titleAliases = null,
        int aniListId = 0, int malId = 0, IReadOnlyList<string>? titleSynonyms = null);
}
