using System.Net;
using System.Net.Sockets;
using UniversalMediaOS.Core.Routing;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class QBitSessionCookieTests
{
    [Theory]
    [InlineData("SID=fixture-session; HttpOnly; Path=/", 200, true)]
    [InlineData("QBT_SID_63483=fixture-session; HttpOnly; Path=/", 204, true)]
    [InlineData("tracking=fixture-session; Path=/", 204, false)]
    [InlineData("QBT_SID_invalid=fixture-session; Path=/", 204, false)]
    [InlineData("", 204, false)]
    [InlineData("SID=fixture-session; HttpOnly; Path=/", 401, false)]
    public async Task AuthenticationRecognizesOnlySuccessfulQbitSessionCookies(string cookie, int status, bool expected)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        string origin = $"http://127.0.0.1:{port}";
        using var listener = new HttpListener();
        listener.Prefixes.Add(origin + "/");
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
            try
            {
                Assert.Equal("/api/v2/auth/login", context.Request.Url!.AbsolutePath);
                context.Response.StatusCode = status;
                if (cookie.Length > 0) context.Response.Headers.Add("Set-Cookie", cookie);
                context.Response.ContentLength64 = 0;
            }
            finally { context.Response.Close(); }
        });
        var gate = new QBitLogicGate(origin);
        bool authenticated = await gate.AuthenticateAsync(username: "fixture", password: "fixture", token: deadline.Token);
        await response;
        Assert.Equal(expected, authenticated);
        Assert.Equal(expected ? cookie.Split(';')[0] : string.Empty, gate.Cookie);
    }
}
