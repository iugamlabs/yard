using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using Xunit;
using Yard.AspNetCore;

namespace Yard.Tests;

public sealed class PinnedSmtpFactory(X509Certificate2 cert) : ISmtpClientFactory {
    public SmtpClient Create() =>
        new() {
            ServerCertificateValidationCallback = (_, certificate, _, _) =>
                certificate?.GetCertHashString() == cert.GetCertHashString(),
        };
}

public sealed class SmtpFixture : IAsyncDisposable {
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private Task? task;
    public int Port { get; private set; }
    public string Message { get; private set; } = "";
    public bool Authenticated { get; private set; }
    public X509Certificate2 Certificate { get; } = CreateCertificate();

    public SmtpFixture(bool tls, bool reject = false) {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        task = Serve(tls, reject);
    }

    private static X509Certificate2 CreateCertificate() {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1)
        );
    }

    private async Task Serve(bool tls, bool reject) {
        try {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            Stream stream = client.GetStream();
            if (tls) {
                var ssl = new SslStream(stream);
                await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions {
                        ServerCertificate = Certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    },
                    stop.Token
                );
                stream = ssl;
            }
            await using (stream) {
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                await using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, true) {
                    NewLine = "\r\n",
                    AutoFlush = true,
                };
                await writer.WriteLineAsync("220 localhost test fixture");
                bool data = false;
                var body = new StringBuilder();
                while (!stop.IsCancellationRequested) {
                    var line = await reader.ReadLineAsync(stop.Token);
                    if (line == null)
                        break;
                    if (data) {
                        if (line == ".") {
                            Message = body.ToString();
                            data = false;
                            await writer.WriteLineAsync("250 accepted");
                        } else
                            body.AppendLine(line);
                        continue;
                    }
                    if (line.StartsWith("EHLO")) {
                        await writer.WriteLineAsync("250-localhost");
                        await writer.WriteLineAsync("250-AUTH PLAIN");
                        await writer.WriteLineAsync("250 SIZE 1048576");
                    } else if (line.StartsWith("AUTH PLAIN")) {
                        Authenticated = true;
                        await writer.WriteLineAsync("235 authenticated");
                    } else if (line.StartsWith("MAIL FROM"))
                        await writer.WriteLineAsync(
                            reject ? "550 controlled rejection" : "250 sender ok"
                        );
                    else if (line.StartsWith("RCPT TO"))
                        await writer.WriteLineAsync("250 recipient ok");
                    else if (line == "DATA") {
                        data = true;
                        await writer.WriteLineAsync("354 end with dot");
                    } else if (line == "QUIT") {
                        await writer.WriteLineAsync("221 bye");
                        break;
                    } else if (line == "RSET")
                        await writer.WriteLineAsync("250 reset");
                    else
                        await writer.WriteLineAsync("250 ok");
                }
            }
        }
        catch (Exception e)
            when (e
                    is OperationCanceledException
                        or IOException
                        or AuthenticationException
                        or ObjectDisposedException
            ) { }
    }

    public async ValueTask DisposeAsync() {
        stop.Cancel();
        listener.Stop();
        if (task != null)
            await task;
        Certificate.Dispose();
        stop.Dispose();
    }
}

public sealed class SmtpTests {
    [Fact]
    public async Task SendsRealMimeMessageAndAuthenticates() {
        await using var server = new SmtpFixture(false);
        var options = Options.Create(
            new SmtpOptions {
                Host = "127.0.0.1",
                Port = server.Port,
                Username = "test",
                Password = "private-test-value",
                FromAddress = "sender@example.test",
                FromName = "Yard",
                Tls = "None",
            }
        );
        await new SmtpEmailSender(options, new SmtpClientFactory()).SendAsync(
            "recipient@example.test",
            "Verification",
            "Your code: 123456"
        );
        Assert.True(server.Authenticated);
        Assert.Contains("recipient@example.test", server.Message);
        Assert.Contains("123456", server.Message);
    }

    [Fact]
    public async Task UsesTlsForImplicitSsl() {
        await using var server = new SmtpFixture(true);
        var options = Options.Create(
            new SmtpOptions {
                Host = "127.0.0.1",
                Port = server.Port,
                FromAddress = "sender@example.test",
                Tls = "SslOnConnect",
            }
        );
        await new SmtpEmailSender(options, new PinnedSmtpFactory(server.Certificate)).SendAsync(
            "recipient@example.test",
            "TLS verification",
            "654321"
        );
        Assert.Contains("654321", server.Message);
    }

    [Fact]
    public async Task DoesNotBypassCertificateValidation() {
        await using var server = new SmtpFixture(true);
        var options = Options.Create(
            new SmtpOptions {
                Host = "127.0.0.1",
                Port = server.Port,
                FromAddress = "sender@example.test",
                Tls = "SslOnConnect",
            }
        );
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new SmtpEmailSender(options, new SmtpClientFactory()).SendAsync(
                "recipient@example.test",
                "TLS verification",
                "654321"
            )
        );
    }

    [Fact]
    public async Task SurfacesServerRejection() {
        await using var server = new SmtpFixture(false, true);
        var options = Options.Create(
            new SmtpOptions {
                Host = "127.0.0.1",
                Port = server.Port,
                FromAddress = "sender@example.test",
                Tls = "None",
            }
        );
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new SmtpEmailSender(options, new SmtpClientFactory()).SendAsync(
                "recipient@example.test",
                "verification",
                "654321"
            )
        );
    }
}
