using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Core;

/// <summary>
/// TLS to Vast.ai workers. Vast issues every instance a certificate signed by its own root CA
/// ("Vast.ai Jupyter CA", https://console.vast.ai/static/jvastai_root.cer), which no system trust store
/// carries. Vast's own SDK client trusts exactly that root and checks the hostname; this does the same.
/// The root is embedded and pinned by fingerprint rather than downloaded, so a network attacker cannot
/// substitute it.
/// </summary>
public static class VastTls
{
    /// <summary>Vast.ai's root CA, as published at https://console.vast.ai/static/jvastai_root.cer.</summary>
    const string RootPem = """
        -----BEGIN CERTIFICATE-----
        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
        OvUOZ5Ickj8xqLOiNyT8MSLDfzE=
        -----END CERTIFICATE-----
        """;

    /// <summary>SHA-256 fingerprint of <see cref="RootPem"/>, checked at load.</summary>
    const string RootSha256 = "A1A78F9E1A806C74DAD08B8F01F5F08C739FB70A5BFBB52D28923751C223451D";

    /// <summary>The pinned root, loaded once.</summary>
    public static readonly X509Certificate2 Root = LoadRoot();

    static X509Certificate2 LoadRoot()
    {
        X509Certificate2 cert = X509Certificate2.CreateFromPem(RootPem.Replace("\n        ", "\n"));
        string fingerprint = Convert.ToHexString(SHA256.HashData(cert.RawData));
        if (fingerprint != RootSha256)
        {
            throw new InvalidOperationException("Embedded Vast.ai root certificate does not match its pinned fingerprint.");
        }
        return cert;
    }

    /// <summary>Accepts a certificate only if it chains to Vast's root and matches the host it was requested for.</summary>
    public static bool Validate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
    {
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            return false;
        }
        using X509Chain vastChain = new();
        vastChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        vastChain.ChainPolicy.CustomTrustStore.Add(Root);
        vastChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (chain is not null)
        {
            foreach (X509ChainElement element in chain.ChainElements)
            {
                vastChain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }
        using X509Certificate2 leaf = new(certificate);
        bool ok = vastChain.Build(leaf) && vastChain.ChainElements[^1].Certificate.Thumbprint == Root.Thumbprint;
        if (!ok)
        {
            Logs.Warning($"[VastAI] Rejected a worker certificate that does not chain to Vast's root: {string.Join(", ", vastChain.ChainStatus.Select(s => s.StatusInformation.Trim()))}");
        }
        return ok;
    }

    /// <summary>An HTTP client for calls to Vast workers (their PyWorker), trusting only Vast's root.</summary>
    public static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Validate },
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromMinutes(10)
    };
}

/// <summary>
/// A loopback listener that forwards every connection to one Vast.ai worker over TLS, verified by
/// <see cref="VastTls.Validate"/>. Core's swarm backend only trusts the system certificate store, so it
/// connects here in plain HTTP instead, and never leaves the machine unencrypted. It carries HTTP and
/// WebSocket traffic alike, since it relays bytes after the TLS handshake.
/// </summary>
public sealed class VastTlsRelay : IAsyncDisposable
{
    /// <summary>The worker's host (its public IP), also the name its certificate must match.</summary>
    readonly string Host;

    /// <summary>The worker's public gateway port.</summary>
    readonly int Port;

    /// <summary>The loopback listener the child backend connects to.</summary>
    readonly TcpListener Listener;

    /// <summary>Stops the accept loop and every open pipe.</summary>
    readonly CancellationTokenSource Cancel = new();

    /// <summary>The loopback URL to use in place of the worker's own https URL.</summary>
    public string LocalUrl => $"http://127.0.0.1:{((IPEndPoint)Listener.LocalEndpoint).Port}";

    /// <summary>Starts relaying to the https worker at <paramref name="workerUrl"/>.</summary>
    public VastTlsRelay(string workerUrl)
    {
        Uri uri = new(workerUrl);
        if (uri.Scheme != "https")
        {
            throw new SwarmReadableErrorException($"Vast.ai worker address '{workerUrl}' is not HTTPS; refusing to send its access token in the clear. Use a worker image that serves TLS.");
        }
        Host = uri.Host;
        Port = uri.Port;
        Listener = new TcpListener(IPAddress.Loopback, 0);
        Listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>Accepts loopback connections until disposed.</summary>
    async Task AcceptLoop()
    {
        while (!Cancel.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await Listener.AcceptTcpClientAsync(Cancel.Token);
            }
            catch (Exception) when (Cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Logs.Debug($"[VastAI] Relay accept failed: {ex.Message}");
                continue;
            }
            _ = Task.Run(() => Pipe(client));
        }
    }

    /// <summary>Relays one loopback connection to the worker over verified TLS.</summary>
    async Task Pipe(TcpClient client)
    {
        using (client)
        using (TcpClient upstream = new())
        {
            try
            {
                await upstream.ConnectAsync(Host, Port, Cancel.Token);
                await using SslStream tls = new(upstream.GetStream(), false, VastTls.Validate);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = Host }, Cancel.Token);
                NetworkStream local = client.GetStream();
                Task up = local.CopyToAsync(tls, Cancel.Token);
                Task down = tls.CopyToAsync(local, Cancel.Token);
                await Task.WhenAny(up, down);
            }
            catch (OperationCanceledException)
            {
                // Relay shut down.
            }
            catch (Exception ex)
            {
                Logs.Debug($"[VastAI] Relay connection to {Host}:{Port} ended: {ex.Message}");
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Cancel.Cancel();
        Listener.Stop();
        Cancel.Dispose();
        return ValueTask.CompletedTask;
    }
}
