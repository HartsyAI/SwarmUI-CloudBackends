using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Hartsy.Extensions.CloudBackends.Core;
using Hartsy.Extensions.CloudBackends.Providers.RunPod;
using Hartsy.Extensions.CloudBackends.Providers.VastAI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SwarmUI.Utils;

namespace Hartsy.Extensions.CloudBackends.Tests;

/// <summary>When the serverless backend takes a request and leases another worker, and when it lets the request wait.</summary>
[TestFixture]
public class RoutingTests
{
    [Test]
    public void TakesRequestsBeforeAnyWorkerIsUp()
    {
        // No worker running: the request must queue on the cloud backend rather than fail for lack of a candidate.
        Assert.That(CloudBackendBase.ShouldTakeRequest(slots: 0, pendingAcquires: 0, maxWorkers: 1, anyWorkerRunning: false), Is.True);
        Assert.That(CloudBackendBase.ShouldTakeRequest(slots: 0, pendingAcquires: 1, maxWorkers: 1, anyWorkerRunning: false), Is.True);
    }

    [Test]
    public void ScalesOutWhileBelowMaxWorkers()
    {
        Assert.That(CloudBackendBase.ShouldTakeRequest(slots: 1, pendingAcquires: 0, maxWorkers: 2, anyWorkerRunning: true), Is.True);
    }

    [Test]
    public void WaitsOnRunningWorkersAtCapacity()
    {
        Assert.That(CloudBackendBase.ShouldTakeRequest(slots: 1, pendingAcquires: 0, maxWorkers: 1, anyWorkerRunning: true), Is.False);
        Assert.That(CloudBackendBase.ShouldTakeRequest(slots: 1, pendingAcquires: 1, maxWorkers: 2, anyWorkerRunning: true), Is.False);
    }
}

/// <summary>The remembered model list: small, previews stripped, rebuilt with what the UI needs.</summary>
[TestFixture]
public class ModelCacheTests
{
    [Test]
    public void RoundTripKeepsNamesAndSmallFieldsOnly()
    {
        Dictionary<string, Dictionary<string, JObject>> models = new()
        {
            ["Stable-Diffusion"] = new()
            {
                ["sdxl/juggernaut.safetensors"] = new JObject
                {
                    ["name"] = "sdxl/juggernaut.safetensors",
                    ["title"] = "Juggernaut",
                    ["architecture"] = "stable-diffusion-xl-v1-base",
                    ["preview_image"] = "data:image/jpg;base64," + new string('A', 5000),
                    ["description"] = "long text"
                }
            }
        };
        string raw = CloudBackendBase.SerializeModelCache(models);
        Assert.That(raw, Does.Not.Contain("base64"));
        Assert.That(raw, Does.Not.Contain("long text"));
        ConcurrentDictionary<string, Dictionary<string, JObject>> loaded = CloudBackendBase.ParseModelCache(raw);
        JObject model = loaded["Stable-Diffusion"]["sdxl/juggernaut.safetensors"];
        Assert.That(model["title"]?.ToString(), Is.EqualTo("Juggernaut"));
        Assert.That(model["architecture"]?.ToString(), Is.EqualTo("stable-diffusion-xl-v1-base"));
        Assert.That(model["local"]?.Value<bool>(), Is.False);
        Assert.That(model["preview_image"]?.ToString(), Is.EqualTo("imgs/model_placeholder.jpg"));
    }

    [Test]
    public void MissingTitleIsRebuiltFromTheName()
    {
        ConcurrentDictionary<string, Dictionary<string, JObject>> loaded = CloudBackendBase.ParseModelCache("{\"Lora\":{\"style/ink.safetensors\":{}}}");
        Assert.That(loaded["Lora"]["style/ink.safetensors"]["title"]?.ToString(), Is.EqualTo("ink.safetensors"));
    }

    [Test]
    public void DamagedCacheThrowsSoTheCallerCanIgnoreIt()
    {
        Assert.That(() => CloudBackendBase.ParseModelCache("{not json"), Throws.Exception);
    }
}

/// <summary>RunPod lease job output parsing.</summary>
[TestFixture]
public class RunPodLeaseTests
{
    static JObject Lease(int protocol = 2) => new()
    {
        ["success"] = true,
        ["public_url"] = "https://abc-7801.proxy.runpod.net/",
        ["token"] = "t0ken",
        ["worker_id"] = "abc",
        ["protocol"] = protocol
    };

    [Test]
    public void ReadsTheFirstStreamedOutput()
    {
        JObject stream = new() { ["status"] = "IN_PROGRESS", ["stream"] = new JArray(new JObject { ["output"] = Lease() }) };
        Assert.That(RunPodServerlessProvider.FirstOutput(stream)?["token"]?.ToString(), Is.EqualTo("t0ken"));
        Assert.That(RunPodServerlessProvider.FirstOutput(new JObject { ["status"] = "IN_QUEUE", ["stream"] = new JArray() }), Is.Null);
        Assert.That(RunPodServerlessProvider.FirstOutput(new JObject { ["status"] = "IN_QUEUE" }), Is.Null);
    }

    [Test]
    public void ParsesAValidLease()
    {
        CloudWorkerInfo worker = RunPodServerlessProvider.ToWorker("job1", Lease());
        Assert.That(worker.PublicUrl, Is.EqualTo("https://abc-7801.proxy.runpod.net"));
        Assert.That(worker.Token, Is.EqualTo("t0ken"));
        Assert.That(worker.LeaseId, Is.EqualTo("job1"));
    }

    /// <summary>The version 1 worker's exact answer to the lease action (rp_handler.py on RunPod-Worker-SwarmUI 1.x).</summary>
    static JObject Version1Refusal() => new()
    {
        ["success"] = false,
        ["error"] = "Unknown action: lease",
        ["available_actions"] = new JArray("wakeup", "ready", "health", "keepalive", "shutdown")
    };

    [Test]
    public void RecognizesTheVersion1WorkerAnsweringALease()
    {
        Assert.That(RunPodServerlessProvider.IsVersion1Refusal(Version1Refusal()), Is.True);
        // Aggregated plain-job output is one object; aggregated streaming output an array of chunks.
        Assert.That(RunPodServerlessProvider.IsVersion1Refusal(RunPodServerlessProvider.EndedOutput(new JObject { ["status"] = "COMPLETED", ["output"] = Version1Refusal() })), Is.True);
        Assert.That(RunPodServerlessProvider.IsVersion1Refusal(RunPodServerlessProvider.EndedOutput(new JObject { ["status"] = "COMPLETED", ["output"] = new JArray(Version1Refusal()) })), Is.True);
        // A version 2 refusal, or an unknown action from some other image, is not the version 1 worker.
        Assert.That(RunPodServerlessProvider.IsVersion1Refusal(new JObject { ["success"] = false, ["error"] = "busy", ["error_id"] = "lease_busy" }), Is.False);
        Assert.That(RunPodServerlessProvider.IsVersion1Refusal(new JObject { ["success"] = false, ["error"] = "Unknown action: lease", ["available_actions"] = new JArray("run") }), Is.False);
        Assert.That(RunPodServerlessProvider.IsVersion1Refusal(Lease()), Is.False);
        Assert.That(RunPodServerlessProvider.IsVersion1Refusal(null), Is.False);
    }

    [Test]
    public void ReadsTemplatePortsInEitherShape()
    {
        Assert.That(RunPodServerlessProvider.TemplatePorts(new JArray("7801/http", "22/tcp")), Is.EqualTo(new[] { "7801/http", "22/tcp" }));
        Assert.That(RunPodServerlessProvider.TemplatePorts(new JValue("7801/http, 22/tcp")), Is.EqualTo(new[] { "7801/http", "22/tcp" }));
        Assert.That(RunPodServerlessProvider.TemplatePorts(new JValue("")), Is.Empty);
        Assert.That(RunPodServerlessProvider.TemplatePorts(null), Is.Empty);
    }

    [Test]
    public void FlagsCudaHostsTooOldForHartsyInference()
    {
        Assert.That(RunPodServerlessProvider.AllowsCudaBelow(new JArray("12.8", "12.9", "13.0"), 13.0), Is.True);
        Assert.That(RunPodServerlessProvider.AllowsCudaBelow(new JArray("13.0", "13.1"), 13.0), Is.False);
        // No list means any version, including old ones.
        Assert.That(RunPodServerlessProvider.AllowsCudaBelow(new JArray(), 13.0), Is.True);
        Assert.That(RunPodServerlessProvider.AllowsCudaBelow(null, 13.0), Is.True);
    }

    [Test]
    public void KeepsAVersion1WorkerAliveLikeCloudBackends1()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // 120s windows: top up only once less than 60s is left.
        Assert.That(RunPodServerlessProvider.KeepaliveDue(now.AddSeconds(61), 120, now), Is.False);
        Assert.That(RunPodServerlessProvider.KeepaliveDue(now.AddSeconds(59), 120, now), Is.True);
        Assert.That(RunPodServerlessProvider.KeepaliveDue(now.AddSeconds(-5), 120, now), Is.True);
        // Queued keepalives run back to back, so a top-up adds a window after the queued ones; a lapsed one starts now.
        Assert.That(RunPodServerlessProvider.ExtendKeepalive(now.AddSeconds(50), 120, now), Is.EqualTo(now.AddSeconds(170)));
        Assert.That(RunPodServerlessProvider.ExtendKeepalive(now.AddSeconds(-5), 120, now), Is.EqualTo(now.AddSeconds(120)));
    }

    [Test]
    public void RefusesOldWorkerImagesAndRefusals()
    {
        Assert.That(() => RunPodServerlessProvider.ToWorker("job1", Lease(protocol: 1)), Throws.TypeOf<SwarmReadableErrorException>().With.Message.Contains("too old"));
        JObject refused = new() { ["success"] = false, ["error"] = "busy", ["error_id"] = "lease_busy" };
        Assert.That(() => RunPodServerlessProvider.ToWorker("job1", refused), Throws.TypeOf<SwarmReadableErrorException>().With.Message.Contains("busy"));
    }
}

/// <summary>RunPod pod GPU selection.</summary>
[TestFixture]
public class RunPodPodTests
{
    static JObject Gpu(string id, double price, string availability, params string[] dataCenters) => new()
    {
        ["id"] = id,
        ["availability"] = availability,
        ["price"] = new JObject { ["secure"] = price },
        ["dataCenters"] = new JArray(dataCenters.Select(d => new JObject { ["id"] = d }))
    };

    [Test]
    public void OnlyTriesGpusAvailableWhereTheVolumeIs()
    {
        // The real catalog at the time this was found: the five cheapest GPUs were all outside EU-RO-1.
        JArray catalog = [Gpu("NVIDIA RTX A4000", 0.25, "LOW", "EUR-IS-1"), Gpu("NVIDIA RTX A5000", 0.27, "LOW", "CA-MTL-1"), Gpu("NVIDIA RTX A6000", 0.53, "LOW"),
            Gpu("NVIDIA RTX PRO 4000 Blackwell", 0.57, "LOW", "EU-RO-1"), Gpu("NVIDIA GeForce RTX 4090", 0.74, "MEDIUM", "EU-RO-1", "EUR-IS-1"), Gpu("NVIDIA H100", 2.89, "NONE", "EU-RO-1")];
        Assert.That(RunPodPodsProvider.OrderGpuCandidates(catalog, "SECURE", "EU-RO-1"), Is.EqualTo(new[] { "NVIDIA RTX PRO 4000 Blackwell", "NVIDIA GeForce RTX 4090" }));
        Assert.That(RunPodPodsProvider.OrderGpuCandidates(catalog, "SECURE", ""), Is.EqualTo(new[] { "NVIDIA RTX A4000", "NVIDIA RTX A5000", "NVIDIA RTX A6000", "NVIDIA RTX PRO 4000 Blackwell", "NVIDIA GeForce RTX 4090" }));
        Assert.That(RunPodPodsProvider.OrderGpuCandidates(catalog, "SECURE", "US-KS-2"), Is.Empty);
    }
}

/// <summary>Vast.ai session renewal and lease parsing.</summary>
[TestFixture]
public class VastSessionTests
{
    [Test]
    public void RenewsOnlyOnceLessThanHalfALifetimeRemains()
    {
        // Lifetime 120s: renew only with 75s (60 + 15 margin) or less left.
        Assert.That(VastAIProvider.NeedsRenewal(expiration: 1000 + 120, lifetime: 120, now: 1000), Is.False);
        Assert.That(VastAIProvider.NeedsRenewal(expiration: 1000 + 76, lifetime: 120, now: 1000), Is.False);
        Assert.That(VastAIProvider.NeedsRenewal(expiration: 1000 + 75, lifetime: 120, now: 1000), Is.True);
        Assert.That(VastAIProvider.NeedsRenewal(expiration: 1000 - 5, lifetime: 120, now: 1000), Is.True);
    }

    [Test]
    public void OnlyDefinitiveSessionAnswersEndALease()
    {
        foreach (int status in new[] { 400, 401, 403, 404, 410 })
        {
            Assert.That(new VastApiException(status, "x").EndsSession, Is.True, $"status {status}");
        }
        foreach (int status in new[] { 429, 500, 502, 503 })
        {
            Assert.That(new VastApiException(status, "x").EndsSession, Is.False, $"status {status}");
        }
    }

    [Test]
    public void OneUnreachableCheckDoesNotEndALease()
    {
        Assert.That(VastAIProvider.IsUnreachableForGood(1), Is.False);
        Assert.That(VastAIProvider.IsUnreachableForGood(VastAIProvider.UnreachableChecksBeforeEnd - 1), Is.False);
        Assert.That(VastAIProvider.IsUnreachableForGood(VastAIProvider.UnreachableChecksBeforeEnd), Is.True);
    }

    [Test]
    public void ParsesALeaseAndRefusesOldImages()
    {
        CloudWorkerInfo worker = new();
        VastAIProvider.ApplyLease(worker, new JObject { ["success"] = true, ["public_url"] = "https://1.2.3.4:40001", ["token"] = "tok", ["worker_id"] = "42", ["protocol"] = 2 });
        Assert.That(worker.PublicUrl, Is.EqualTo("https://1.2.3.4:40001"));
        Assert.That(worker.WorkerId, Is.EqualTo("42"));
        Assert.That(() => VastAIProvider.ApplyLease(new CloudWorkerInfo(), new JObject { ["success"] = true, ["public_url"] = "x", ["token"] = "y" }),
            Throws.TypeOf<SwarmReadableErrorException>().With.Message.Contains("too old"));
    }
}

/// <summary>Vast.ai certificate pinning.</summary>
[TestFixture]
public class VastTlsTests
{
    [Test]
    public void EmbeddedRootMatchesItsPinnedFingerprint()
    {
        Assert.That(VastTls.Root.Subject, Does.Contain("Vast.ai Jupyter CA"));
    }

    [Test]
    public void RejectsCertificatesNotIssuedByVast()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=1.2.3.4", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.That(VastTls.Validate(null, selfSigned, null, SslPolicyErrors.RemoteCertificateChainErrors), Is.False);
    }

    [Test]
    public void RejectsHostnameMismatchEvenIfOtherwiseTrusted()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=1.2.3.4", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.That(VastTls.Validate(null, cert, null, SslPolicyErrors.RemoteCertificateNameMismatch), Is.False);
    }

    [Test]
    public void RelayRefusesPlainHttpWorkers()
    {
        Assert.That(() => new VastTlsRelay("http://1.2.3.4:40001"), Throws.TypeOf<SwarmReadableErrorException>());
    }
}

/// <summary>Provider API backoff.</summary>
[TestFixture]
public class HttpRetryTests
{
    [Test]
    public void RetriesOnlyTransientStatuses()
    {
        Assert.That(HttpRetry.IsTransient(HttpStatusCode.TooManyRequests), Is.True);
        Assert.That(HttpRetry.IsTransient(HttpStatusCode.ServiceUnavailable), Is.True);
        Assert.That(HttpRetry.IsTransient(HttpStatusCode.InternalServerError), Is.True);
        Assert.That(HttpRetry.IsTransient(HttpStatusCode.Unauthorized), Is.False);
        Assert.That(HttpRetry.IsTransient(HttpStatusCode.NotFound), Is.False);
    }

    [Test]
    public void HonorsRetryAfterUpToTheCap()
    {
        Assert.That(HttpRetry.DelayFor(1, TimeSpan.FromSeconds(7)), Is.EqualTo(TimeSpan.FromSeconds(7)));
        Assert.That(HttpRetry.DelayFor(1, TimeSpan.FromMinutes(10)), Is.EqualTo(HttpRetry.MaxDelay));
    }

    [Test]
    public void BacksOffExponentiallyWithBoundedJitter()
    {
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            double expected = Math.Pow(2, attempt - 1);
            double actual = HttpRetry.DelayFor(attempt, null).TotalSeconds;
            Assert.That(actual, Is.GreaterThanOrEqualTo(expected).And.LessThanOrEqualTo(expected * 1.5));
        }
    }

    [Test]
    public void ReadsRetryAfterInBothForms()
    {
        using HttpResponseMessage delta = new(HttpStatusCode.TooManyRequests);
        delta.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
        Assert.That(HttpRetry.RetryAfter(delta), Is.EqualTo(TimeSpan.FromSeconds(12)));
        using HttpResponseMessage none = new(HttpStatusCode.TooManyRequests);
        Assert.That(HttpRetry.RetryAfter(none), Is.Null);
    }
}

/// <summary>Which RunPod failures end a lease check, and which keep the worker.</summary>
[TestFixture]
public class RunPodErrorTests
{
    [Test]
    public void OnlyCredentialAndMissingResourceErrorsArePermanent()
    {
        Assert.That(new RunPodApiException(401, "x").IsPermanent, Is.True);
        Assert.That(new RunPodApiException(403, "x").IsPermanent, Is.True);
        Assert.That(new RunPodApiException(404, "x").IsPermanent, Is.True);
        Assert.That(new RunPodApiException(429, "x").IsPermanent, Is.False);
        Assert.That(new RunPodApiException(500, "x").IsPermanent, Is.False);
        Assert.That(new RunPodApiException(503, "x").IsPermanent, Is.False);
    }
}
