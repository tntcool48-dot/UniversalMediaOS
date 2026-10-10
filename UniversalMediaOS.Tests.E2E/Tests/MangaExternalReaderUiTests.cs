using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using UniversalMediaOS.Tests.E2E.Infrastructure;
using Xunit;

namespace UniversalMediaOS.Tests.E2E.Tests;

public sealed class MangaExternalReaderUiTests
{
    [Fact]
    public void ExternalChapterIsExplicitlyAWebsiteAndDoesNotLoadOnBrowse()
    {
        using var scenario = new Scenario();
        scenario.OpenChapters();
        Assert.True(scenario.Has("Open chapter 1 website"), "An external-only chapter must identify its website action before selection.");
        Assert.True(scenario.Has("Website only"));
        Assert.True(scenario.Has("Open chapter 2"));
        Assert.Equal(0, scenario.Website.Requests);
        Assert.Equal(0, scenario.PageRequests);
    }

    [Fact]
    public void FailedWebsiteCanRetryRetainItsTabAndReturnToNativeChapterChoices()
    {
        using var scenario = new Scenario();
        scenario.Website.Status = 503;
        scenario.OpenChapters();
        Assert.True(scenario.Has("Website only"));
        Assert.Equal(0, scenario.Website.Requests);
        Assert.Equal(0, scenario.PageRequests);
        scenario.VisualHold("explicit-website-choice");
        scenario.Button("Open chapter 1 website").Invoke();
        scenario.WaitForWebsiteRequest();
        scenario.WaitFor("Website could not load. Retry website or go back.");
        Assert.True(scenario.Has("Mock Manga › Ch. 1"));
        Assert.Equal(0, scenario.PageRequests);
        scenario.VisualHold("failed-website");
        scenario.Website.Status = 200;
        scenario.Button("Retry website").Invoke();
        scenario.WaitFor("Website reader");
        scenario.WaitFor("EXTERNAL CHAPTER ONE");
        Assert.False(scenario.Has("Website could not load. Retry website or go back."));
        Assert.False(scenario.Has("Retry website"));
        int requests = scenario.Website.Requests;
        Assert.True(requests >= 2);
        scenario.VisualHold("recovered-explicit-website");
        scenario.Window().FindAllDescendants(cf => cf.ByAutomationId("SelectTab")).First().AsButton().Invoke();
        scenario.Window().FindAllDescendants(cf => cf.ByAutomationId("SelectTab"))
            .Single(element => element.Name == "Manga").AsButton().Invoke();
        scenario.WaitFor("EXTERNAL CHAPTER ONE");
        Assert.Equal(requests, scenario.Website.Requests);
        scenario.Button("Back").Invoke();
        scenario.WaitFor("Open chapter 1 website");
        Assert.True(scenario.Has("Open chapter 2"));
        Assert.True(scenario.Has("Mock Manga"));
        Assert.False(scenario.Has("Website reader"));
        scenario.Button("Open chapter 2").Invoke();
        scenario.WaitFor("Mock Manga › Ch. 2");
        Assert.True(SpinWait.SpinUntil(() => scenario.PageRequests == 2, TimeSpan.FromSeconds(5)));
        Assert.Equal(requests, scenario.Website.Requests);
        Assert.False(scenario.Has("EXTERNAL CHAPTER ONE"));
        Assert.False(scenario.Fixture.App.HasExited);
    }

    private sealed class Scenario : IDisposable
    {
        public ReaderServer Website { get; } = new();
        public AppFixture Fixture { get; }
        private int _pageRequests;
        public int PageRequests => Volatile.Read(ref _pageRequests);
        public Scenario()
        {
            Fixture = new AppFixture(mangaPagePng: null, mangaChapterFeed: _ => (200, JsonSerializer.Serialize(new
            {
                total = 2,
                data = new[]
                {
                    new { id = "external-chapter-one", attributes = new { chapter = "1", title = "Website chapter", pages = 0, externalUrl = Website.Url } },
                    new { id = "native-chapter-two", attributes = new { chapter = "2", title = "Native chapter", pages = 2, externalUrl = "" } }
                }
            })), mangaPageStatus: _ => { Interlocked.Increment(ref _pageRequests); return 200; });
        }
        public Window Window() => Fixture.App.GetAllTopLevelWindows(Fixture.Automation)
            .Single(window => window.Name == AppFixture.ExpectedMainWindowTitle);
        public Button Button(string name) => Window().FindFirstDescendant(cf => cf.ByName(name))!.AsButton();
        public bool Has(string name) => Window().FindAllDescendants(cf => cf.ByName(name)).Any(element => !element.IsOffscreen);
        public void WaitFor(string name)
        {
            if (SpinWait.SpinUntil(() => Has(name), TimeSpan.FromSeconds(8))) return;
            Assert.Fail(Diagnostics(name));
        }
        public void WaitForWebsiteRequest()
        {
            // Cold WebView2 profile creation is separate from navigation. The
            // hosted failure spent the whole request deadline before Navigate.
            string navigationMarker = $"[MangaView] Navigating WebView to external chapter: {Website.Url}";
            var startup = System.Diagnostics.Stopwatch.StartNew();
            while (Website.Requests == 0 && !ReadOwnedLog().Contains(navigationMarker, StringComparison.Ordinal))
            {
                if (Fixture.App.HasExited || startup.Elapsed >= TimeSpan.FromSeconds(30))
                {
                    Assert.Fail(Diagnostics("The explicitly selected website reader must initialize before navigation."));
                    return;
                }
                Thread.Sleep(50);
            }
            if (!SpinWait.SpinUntil(() => Website.Requests > 0, TimeSpan.FromSeconds(12)))
                Assert.Fail(Diagnostics("The initialized external reader must reach its owned server within the navigation deadline."));
        }
        private string ReadOwnedLog()
        {
            string logText;
            try
            {
                using var log = new FileStream(Path.Combine(Fixture.SandboxPath, "Roaming", "UniversalMediaOS", "app.log"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (log.Length > 16_384) log.Seek(-16_384, SeekOrigin.End);
                using var reader = new StreamReader(log);
                logText = reader.ReadToEnd();
                if (logText.Length > 16_384) logText = logText[^16_384..];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logText = $"Owned application log unavailable: {ex.Message}";
            }
            return logText;
        }
        public string Diagnostics(string action)
        {
            return $"{action}; owned PID={Fixture.App.ProcessId}; exited={Fixture.App.HasExited}; " +
                $"website requests={Website.Requests}; page requests={PageRequests}\n{ReadOwnedLog()}";
        }
        public void OpenChapters()
        {
            Button("Library").Invoke();
            WaitFor("Open Mock Manga chapters");
            Button("Open Mock Manga chapters").Invoke();
            WaitFor("Open chapter 2");
            Assert.True(Has("Ch. 1"), "The external website choice must visibly retain its chapter number.");
            Assert.True(Has("Ch. 2"), "The native choice must visibly retain its chapter number.");
            Assert.True(Has("2 pages"), "The native chapter must show its bound page count.");
        }
        public void VisualHold(string phase)
        {
            string? directory = Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_MANGA_EXTERNAL_UI_QA_DIR");
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "external-reader-ui-ready.json"), JsonSerializer.Serialize(new
            {
                Phase = phase, Pid = Fixture.App.ProcessId, Window = Window().Properties.NativeWindowHandle.Value.ToInt64(),
                Profile = Fixture.SandboxPath, WebsiteRequests = Website.Requests, PageRequests, Utc = DateTime.UtcNow
            }));
            if (int.TryParse(Environment.GetEnvironmentVariable("UNIVERSAL_MEDIA_OS_MANGA_EXTERNAL_UI_HOLD_MS"), out int hold))
                Thread.Sleep(Math.Clamp(hold, 0, 60_000));
        }
        public void Dispose()
        {
            try { Fixture.Dispose(); }
            finally { Website.Dispose(); }
        }
    }

    private sealed class ReaderServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _requests;
        public string Url { get; }
        public int Status = 200;
        public int Requests => Volatile.Read(ref _requests);
        public ReaderServer()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            string origin = $"http://127.0.0.1:{port}/";
            Url = origin + "chapter-one";
            _listener.Prefixes.Add(origin);
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch (Exception) when (_stop.IsCancellationRequested) { break; }
                    if (context.Request.Url!.AbsolutePath == "/chapter-one") Interlocked.Increment(ref _requests);
                    context.Response.StatusCode = Volatile.Read(ref Status);
                    context.Response.ContentType = "text/html; charset=utf-8";
                    byte[] body = Encoding.UTF8.GetBytes("<!doctype html><html><head><title>Owned Manga reader</title></head>" +
                        "<body style='background:#eee;color:#222;font:28px sans-serif;padding:30px'><h1>EXTERNAL CHAPTER ONE</h1>" +
                        "<p>Explicit website fallback test page</p></body></html>");
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            });
        }
        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            _loop.GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
