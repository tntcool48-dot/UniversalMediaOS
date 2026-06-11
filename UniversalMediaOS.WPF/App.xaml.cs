using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Routing;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.WPF.ViewModels;

namespace UniversalMediaOS.WPF
{
    public partial class App : Application
    {
        public IServiceProvider Services { get; }

        public new static App Current => (App)Application.Current;

        private static ConsumetBootstrapper? _consumetServer;
        private static PythonBootstrapper? _pythonServer;
        private static System.Diagnostics.Process? _qbitProcess;

        public App()
        {
            Services = ConfigureServices();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string configPath = Path.Combine(appData, "UniversalMediaOS", "config.json");
            Directory.CreateDirectory(Path.Combine(appData, "UniversalMediaOS"));
            var config = new DomainHotSwapper(configPath);

            // Auto-manage local services if the user has enabled it in Settings
            if (config.GetSetting("AutoManageServices") == "true")
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                _consumetServer = new ConsumetBootstrapper(baseDir);
                _pythonServer = new PythonBootstrapper(baseDir);

                _ = Task.Run(async () => await _consumetServer.EnsureLatestConsumetAsync());
                _ = Task.Run(async () => await _pythonServer.BootPythonServiceAsync());

                _ = Task.Run(async () =>
                {
                    var dep = new DependencyBootstrapper(baseDir);
                    await dep.EnsureDependenciesAsync();
                    if (!string.IsNullOrEmpty(DependencyBootstrapper.DetectedQBitPath))
                    {
                        var startInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = DependencyBootstrapper.DetectedQBitPath,
                            Arguments = "--webui-port=8080",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        _qbitProcess = System.Diagnostics.Process.Start(startInfo);
                    }
                });
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _consumetServer?.StopServer();
            _pythonServer?.StopServer();

            try
            {
                if (_qbitProcess != null && !_qbitProcess.HasExited)
                    _qbitProcess.Kill(entireProcessTree: true);
            }
            catch { }

            base.OnExit(e);
        }

        private static IServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string baseDir = Path.Combine(appData, "UniversalMediaOS");
            string configPath = Path.Combine(baseDir, "config.json");

            // Core Services
            services.AddSingleton<DomainHotSwapper>(provider => new DomainHotSwapper(configPath));
            services.AddTransient<FuzzyShieldSearch>();
            services.AddTransient<MangaService>();
            services.AddTransient<TripleNetHandoff>();
            services.AddTransient<SeasonDownloader>();
            services.AddTransient<ServiceManager>();
            services.AddTransient<EpubReaderService>();

            // ViewModels
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddTransient<SearchViewModel>();
            services.AddTransient<MangaViewModel>();
            services.AddTransient<DownloadsViewModel>();
            services.AddTransient<PlaybackViewModel>();

            return services.BuildServiceProvider();
        }
    }
}
