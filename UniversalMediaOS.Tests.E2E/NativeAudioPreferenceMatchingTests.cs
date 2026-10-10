using UniversalMediaOS.WPF.ViewModels;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class NativeAudioPreferenceMatchingTests
{
    [Theory]
    [InlineData("renamed", 7)]
    [InlineData("two-letter-tag", 7)]
    [InlineData("exact-before-language", 7)]
    [InlineData("ambiguous-language", -1)]
    [InlineData("duplicate-exact", -1)]
    [InlineData("label-only", -1)]
    [InlineData("reused-id-wrong-language", -1)]
    [InlineData("unrecognized-tag", -1)]
    public void AudioRestorationNeedsUniqueActualMetadata(string scenario, int expected)
    {
        var previous = PlaybackTrackOption.Native(4, "Japanese", scenario == "unrecognized-tag" ? "not-a-language" : "jpn", "Old dialogue", "Audio");
        var english = PlaybackTrackOption.Native(1, "Japanese", "eng", "New first", "Audio");
        PlaybackTrackOption[] choices = scenario switch
        {
            "renamed" => [english, PlaybackTrackOption.Native(7, "Track 2", "jpn", "New dialogue", "Audio")],
            "two-letter-tag" => [english, PlaybackTrackOption.Native(7, "Track 2", "ja", "New dialogue", "Audio")],
            "exact-before-language" => [english,
                PlaybackTrackOption.Native(7, "Track 2", "jpn", "Old dialogue", "Audio"),
                PlaybackTrackOption.Native(8, "Track 3", "jpn", "Commentary", "Audio")],
            "ambiguous-language" => [english,
                PlaybackTrackOption.Native(7, "Track 2", "jpn", "New dialogue", "Audio"),
                PlaybackTrackOption.Native(8, "Track 3", "jpn", "Commentary", "Audio")],
            "duplicate-exact" => [PlaybackTrackOption.Native(7, "Track 2", "jpn", "Old dialogue", "Audio"),
                PlaybackTrackOption.Native(8, "Track 3", "jpn", "Old dialogue", "Audio")],
            "label-only" => [PlaybackTrackOption.Native(7, "Japanese", "und", "New dialogue", "Audio")],
            "reused-id-wrong-language" => [PlaybackTrackOption.Native(4, "Japanese", "eng", "Old dialogue", "Audio")],
            "unrecognized-tag" => [PlaybackTrackOption.Native(7, "Japanese", "not-a-language", "New dialogue", "Audio")],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var selected = PlaybackTrackOption.RememberedNativeAudio(choices, previous.Key, previous.NativeLanguage);
        Assert.Equal(expected, selected?.NativeId ?? -1);
    }
}
