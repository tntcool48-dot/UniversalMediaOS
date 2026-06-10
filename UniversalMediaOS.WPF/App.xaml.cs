using System;
using System.IO;
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

        public App()
        {
            Services = ConfigureServices();
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
            services.AddTransient<SearchViewModel>();
            services.AddTransient<MangaViewModel>();
            services.AddTransient<DownloadsViewModel>();
            services.AddTransient<PlaybackViewModel>();

            return services.BuildServiceProvider();
        }
    }
}
