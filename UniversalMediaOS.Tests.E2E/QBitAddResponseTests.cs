using System.Net;
using System.Net.Sockets;
using System.Text;
using UniversalMediaOS.Core.Routing;
using Xunit;

namespace UniversalMediaOS.Tests.E2E;

public sealed class QBitAddResponseTests
{
    [Theory]
    [InlineData("Ok.", 200, true)]
    [InlineData("{\"success_count\":1,\"failure_count\":0,\"pending_count\":0,\"added_torrent_ids\":[\"19a349ab37182209c745ed55798debc022db9149\"]}", 200, true)]
    [InlineData("{\"success_count\":0,\"failure_count\":0,\"pending_count\":1,\"added_torrent_ids\":[]}", 202, true)]
    [InlineData("{\"success_count\":0,\"failure_count\":1,\"pending_count\":0,\"added_torrent_ids\":[]}", 200, false)]
    [InlineData("{\"success_count\":1}", 200, false)]
    public async Task SingleMagnetAcceptsLegacyOrExplicitAcceptedResult(string body, int status, bool expected)
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
        var server = Task.Run(async () =>
        {
            for (int request = 0; request < 2; request++)
            {
                var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
                try
                {
                    Assert.Equal(request == 0 ? "/api/v2/auth/login" : "/api/v2/torrents/add", context.Request.Url!.AbsolutePath);
                    if (request == 0)
                    {
                        context.Response.Headers.Add("Set-Cookie", "SID=add-fixture; HttpOnly; Path=/");
                        context.Response.ContentLength64 = 0;
                    }
                    else
                    {
                        context.Response.StatusCode = status;
                        byte[] bytes = Encoding.UTF8.GetBytes(body);
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes, deadline.Token);
                    }
                }
                finally { context.Response.Close(); }
            }
        });
        var gate = new QBitLogicGate(origin);
        Assert.True(await gate.AuthenticateAsync(username: "fixture", password: "fixture", token: deadline.Token));
        bool accepted = await gate.AddMagnetAsync("magnet:?xt=urn:btih:19A349AB37182209C745ED55798DEBC022DB9149", "fixture", deadline.Token);
        await server;
        Assert.Equal(expected, accepted);
    }
}
