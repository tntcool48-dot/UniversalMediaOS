using System;

namespace UniversalMediaOS.Core.Services
{
    public sealed class PlaybackSyncController
    {
        private readonly WatchTogetherClientService _client;
        private readonly object _ownershipSync = new();
        private WeakReference<object>? _activePlaybackOwner;

        public PlaybackSyncController(WatchTogetherClientService client)
        {
            _client = client;
        }

        public bool CanSend => _client.State == WatchRoomConnectionState.Connected;
        public bool IsHost => _client.Role == WatchRoomRole.Host;
        public double OffsetSeconds => _client.OffsetSeconds;

        /// <summary>
        /// Makes the visible playback surface the only player allowed to participate in
        /// Watch Together. A weak reference avoids keeping a closed playback tab alive if
        /// a view is removed without running its normal unload path.
        /// </summary>
        public void ActivatePlayback(object owner)
        {
            ArgumentNullException.ThrowIfNull(owner);
            lock (_ownershipSync)
            {
                _activePlaybackOwner = new WeakReference<object>(owner);
            }
        }

        public void DeactivatePlayback(object owner)
        {
            ArgumentNullException.ThrowIfNull(owner);
            lock (_ownershipSync)
            {
                if (_activePlaybackOwner != null &&
                    _activePlaybackOwner.TryGetTarget(out object? activeOwner) &&
                    ReferenceEquals(activeOwner, owner))
                {
                    _activePlaybackOwner = null;
                }
            }
        }

        public bool IsActivePlayback(object owner)
        {
            ArgumentNullException.ThrowIfNull(owner);
            lock (_ownershipSync)
            {
                if (_activePlaybackOwner == null ||
                    !_activePlaybackOwner.TryGetTarget(out object? activeOwner))
                {
                    _activePlaybackOwner = null;
                    return false;
                }

                return ReferenceEquals(activeOwner, owner);
            }
        }

        public double ToBaseTime(double localSeconds)
        {
            return Math.Max(0, localSeconds - OffsetSeconds);
        }

        public double ToLocalTime(double baseSeconds)
        {
            return Math.Max(0, baseSeconds + OffsetSeconds);
        }

        public object CreateActionPayload(string action, double localSeconds, string mediaTitle)
        {
            return new
            {
                type = "action",
                action,
                timestamp = ToBaseTime(localSeconds),
                media_title = mediaTitle ?? string.Empty
            };
        }

        public object CreateSyncResponsePayload(string targetId, double localSeconds, bool isPlaying = false)
        {
            return new
            {
                type = "sync_response",
                target_id = targetId,
                timestamp = ToBaseTime(localSeconds),
                is_playing = isPlaying
            };
        }
    }
}
