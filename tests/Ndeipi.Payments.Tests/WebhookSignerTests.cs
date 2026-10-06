using System.Text;
using Ndeipi.Payments.Webhooks;

namespace Ndeipi.Payments.Tests;

/// <summary>
/// Webhook signatures (FR-WH-02, FR-WH-03, FR-WH-05): the valid, tampered-body, stale-timestamp,
/// malformed-header and wrong-key cases the SDK verifiers must also pass (SRS §9.1).
/// </summary>
public sealed class WebhookSignerTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(10);
    static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"id":"evt_01JA8Z","type":"transfer.state_changed"}""");

    [Fact]
    public void A_signed_delivery_verifies_with_the_endpoints_public_key()
    {
        var keys = WebhookSigner.GenerateKeyPair();
        var header = WebhookSigner.Sign(Body, Now, keys.PrivateKey);

        Assert.Matches(@"^t=\d+,v1=[A-Za-z0-9+/]+={0,2}$", header);
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", keys.PublicKeyPem);
        Assert.True(WebhookSigner.Verify(Body, header, keys.PublicKeyPem, Now.AddMinutes(1), Tolerance));
    }

    [Fact]
    public void A_tampered_body_fails()
    {
        var keys = WebhookSigner.GenerateKeyPair();
        var header = WebhookSigner.Sign(Body, Now, keys.PrivateKey);
        var tampered = Encoding.UTF8.GetBytes("""{"id":"evt_01JA8Z","type":"transfer.state_changed" }""");

        Assert.False(WebhookSigner.Verify(tampered, header, keys.PublicKeyPem, Now, Tolerance));
    }

    [Fact]
    public void A_delivery_older_than_the_tolerance_fails_but_a_retry_signed_at_its_own_attempt_time_passes()
    {
        var keys = WebhookSigner.GenerateKeyPair();
        var eventCreated = Now.AddDays(-1);
        var retryAttempt = Now.AddSeconds(-5);

        Assert.False(WebhookSigner.Verify(Body, WebhookSigner.Sign(Body, eventCreated, keys.PrivateKey), keys.PublicKeyPem, Now, Tolerance));
        Assert.True(WebhookSigner.Verify(Body, WebhookSigner.Sign(Body, retryAttempt, keys.PrivateKey), keys.PublicKeyPem, Now, Tolerance));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1=abc")]
    [InlineData("t=1791288000")]
    [InlineData("t=notanumber,v1=AAAA")]
    [InlineData("t=1791288000,v1=***")]
    [InlineData("garbage")]
    public void A_malformed_header_fails(string? header)
    {
        var keys = WebhookSigner.GenerateKeyPair();
        Assert.False(WebhookSigner.Verify(Body, header, keys.PublicKeyPem, Now, Tolerance));
    }

    [Fact]
    public void Another_endpoints_key_fails()
    {
        var mine = WebhookSigner.GenerateKeyPair();
        var theirs = WebhookSigner.GenerateKeyPair();

        Assert.False(WebhookSigner.Verify(Body, WebhookSigner.Sign(Body, Now, theirs.PrivateKey), mine.PublicKeyPem, Now, Tolerance));
    }

    [Fact]
    public void During_rotation_either_key_verifies()
    {
        var old = WebhookSigner.GenerateKeyPair();
        var next = WebhookSigner.GenerateKeyPair();
        var header = WebhookSigner.Sign(Body, Now, old.PrivateKey, next.PrivateKey);

        Assert.Equal(2, header.Split(",v1=").Length - 1);
        Assert.True(WebhookSigner.Verify(Body, header, old.PublicKeyPem, Now, Tolerance));
        Assert.True(WebhookSigner.Verify(Body, header, next.PublicKeyPem, Now, Tolerance));
    }
}
