using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.Helpers;

namespace UniversalMediaOS.WPF.ViewModels
{
    public partial class WatchTogetherViewModel : ObservableObject, IDisposable
    {
        private readonly DomainHotSwapper _config;
        private readonly WatchRoomRelayService _relayService;
        private readonly WatchTogetherClientService _clientService;
        private readonly IExternalLauncher _externalLauncher;

        [ObservableProperty] private string _serverUrl = "localhost";
        [ObservableProperty] private string _roomId = string.Empty;
        [ObservableProperty] private double _offsetSeconds;
        [ObservableProperty] private int _relayPort = 8000;
        [ObservableProperty] private string _relayStatus = "Relay stopped";
        [ObservableProperty] private string _connectionStatus = "Disconnected";
        [ObservableProperty] private string _roleText = "No role";
        [ObservableProperty] private string _hostUrl = string.Empty;
        [ObservableProperty] private string _hostUrlToShare = string.Empty;

        public bool IsRelayRunning => _relayService.IsRunning;
        public bool IsConnected => _clientService.State == WatchRoomConnectionState.Connected;

        public WatchTogetherViewModel(
            DomainHotSwapper config,
            WatchRoomRelayService relayService,
            WatchTogetherClientService clientService,
            IExternalLauncher externalLauncher)
        {
            _config = config;
            _relayService = relayService;
            _clientService = clientService;
            _externalLauncher = externalLauncher;
            ServerUrl = _clientService.ServerUrl;
            RoomId = _clientService.RoomId;
            OffsetSeconds = _clientService.OffsetSeconds;
            RelayPort = int.TryParse(config.GetSetting("WatchTogetherPort"), out int port) ? Math.Clamp(port, 1, 65535) : 8000;
            _clientService.StateChanged += ClientService_StateChanged;
            RefreshState();
        }

        [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanStartRelay))]
        private async Task StartRelayAsync()
        {
            RelayPort = Math.Clamp(RelayPort, 1, 65535);
            try
            {
                await _relayService.StartAsync(RelayPort);
                _config.SetSetting("WatchTogetherPort", RelayPort.ToString(CultureInfo.InvariantCulture));
                RelayStatus = $"Relay listening on port {RelayPort}. Share this machine's LAN or VPN address.";
            }
            catch (Exception ex)
            {
                RelayStatus = $"Relay failed to start: {ex.Message}";
                UniversalMediaOS.Core.Helpers.AppLogger.Log(RelayStatus, "WARNING");
            }
            finally
            {
                OnPropertyChanged(nameof(IsRelayRunning));
                NotifyRelayCommandState();
            }
        }

        private bool CanStartRelay() => !IsRelayRunning;

        [RelayCommand(CanExecute = nameof(CanStopRelay))]
        private void StopRelay()
        {
            _relayService.Stop();
            RelayStatus = "Relay stopped";
            OnPropertyChanged(nameof(IsRelayRunning));
            NotifyRelayCommandState();
        }

        private bool CanStopRelay() => IsRelayRunning;

        [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanConnect))]
        private async Task ConnectAsync()
        {
            await _clientService.ConnectAsync(ServerUrl, RoomId, OffsetSeconds);
            RefreshState();
        }

        private bool CanConnect() =>
            _clientService.State is WatchRoomConnectionState.Disconnected or WatchRoomConnectionState.Failed;

        [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanDisconnect))]
        private async Task DisconnectAsync()
        {
            await _clientService.DisconnectAsync();
            RefreshState();
        }

        private bool CanDisconnect() =>
            _clientService.State is WatchRoomConnectionState.Connecting or
                WatchRoomConnectionState.Connected or
                WatchRoomConnectionState.Reconnecting;

        [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanShareHostUrl))]
        private async Task ShareHostUrlAsync()
        {
            try
            {
                await _clientService.SendAsync(new { action = "set_host", url = HostUrlToShare.Trim() });
            }
            catch (Exception ex)
            {
                ConnectionStatus = $"Share failed: {ex.Message}";
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Watch Together URL share failed: {ex.Message}", "WARNING");
            }
        }

        private bool CanShareHostUrl() =>
            IsConnected &&
            _clientService.Role == WatchRoomRole.Host &&
            !string.IsNullOrWhiteSpace(HostUrlToShare);

        partial void OnHostUrlToShareChanged(string value) => ShareHostUrlCommand.NotifyCanExecuteChanged();

        [RelayCommand(CanExecute = nameof(CanOpenHostUrl))]
        private void OpenHostUrl()
        {
            if (!string.IsNullOrWhiteSpace(HostUrl))
            {
                _externalLauncher.OpenUrl(HostUrl);
            }
        }

        private bool CanOpenHostUrl() => !string.IsNullOrWhiteSpace(HostUrl);

        partial void OnHostUrlChanged(string value) => OpenHostUrlCommand.NotifyCanExecuteChanged();

        private void ClientService_StateChanged(object? sender, EventArgs e)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.InvokeAsync(RefreshState);
            }
            else
            {
                RefreshState();
            }
        }

        private void RefreshState()
        {
            ConnectionStatus = _clientService.State.ToString();
            RoleText = _clientService.Role switch
            {
                WatchRoomRole.Host => "Host",
                WatchRoomRole.Peer => "Peer",
                _ => "No role"
            };
            HostUrl = _clientService.HostUrl;
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsRelayRunning));
            NotifyRelayCommandState();
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
            ShareHostUrlCommand.NotifyCanExecuteChanged();
            OpenHostUrlCommand.NotifyCanExecuteChanged();
        }

        private void NotifyRelayCommandState()
        {
            StartRelayCommand.NotifyCanExecuteChanged();
            StopRelayCommand.NotifyCanExecuteChanged();
        }

        public void Dispose()
        {
            _clientService.StateChanged -= ClientService_StateChanged;
        }
    }
}
