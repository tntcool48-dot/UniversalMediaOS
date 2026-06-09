using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;

namespace UniversalMediaOS.Core.Social
{
    public class WatchPartySync
    {
        private HubConnection? _connection;
        public event EventHandler<long>? RemoteSeekRequested;
        public event EventHandler<bool>? RemotePlayPauseRequested;

        public async Task ConnectAsync(string serverUrl)
        {
            _connection = new HubConnectionBuilder()
                .WithUrl(serverUrl)
                .Build();

            _connection.On<long>("Seek", time => RemoteSeekRequested?.Invoke(this, time));
            _connection.On<bool>("PlayPause", isPlaying => RemotePlayPauseRequested?.Invoke(this, isPlaying));

            await _connection.StartAsync();
        }

        public async Task SendPlayPauseAsync(bool isPlaying)
        {
            if (_connection != null && _connection.State == HubConnectionState.Connected)
                await _connection.SendAsync("PlayPause", isPlaying);
        }

        public async Task SendSeekAsync(long timeTicks)
        {
            if (_connection != null && _connection.State == HubConnectionState.Connected)
                await _connection.SendAsync("Seek", timeTicks);
        }
    }
}
