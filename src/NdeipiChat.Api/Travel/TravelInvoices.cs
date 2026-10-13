using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;
using QRCoder;
using SkiaSharp;

namespace NdeipiChat.Api.Travel;

/// <summary>
/// FR-4.4: a tax invoice for each paid booking, signed with Lets Travel's ECDSA P-256 key so anyone
/// can check it against the published public key, and a PDF voucher to print or keep offline with the
/// booking, the invoice and a code the vendor can scan.
/// </summary>
public sealed class TravelInvoices(ChatDbContext db, TravelIssuer issuer, IDataProtectionProvider protection, TimeProvider clock)
{
    IDataProtector KeyProtector => protection.CreateProtector("travel-invoice-key");

    /// <summary>The current signing key, created the first time it's needed.</summary>
    async Task<(TravelSigningKey Record, ECDsa Key)> KeyAsync(CancellationToken ct)
    {
        var record = await db.TravelSigningKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).FirstOrDefaultAsync(ct);
        if (record is null)
        {
            using var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            record = new TravelSigningKey
            {
                Id = $"lt-{clock.GetUtcNow():yyyyMMdd}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}",
                PublicKey = Convert.ToBase64String(fresh.ExportSubjectPublicKeyInfo()),
                PrivateKeyProtected = KeyProtector.Protect(Convert.ToBase64String(fresh.ExportPkcs8PrivateKey())),
                CreatedAt = clock.GetUtcNow()
            };
            db.TravelSigningKeys.Add(record);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another request made one at the same moment; use the newest.
                db.ChangeTracker.Clear();
                record = await db.TravelSigningKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).FirstAsync(ct);
            }
        }
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(KeyProtector.Unprotect(record.PrivateKeyProtected)), out _);
        return (record, key);
    }

    public async Task<string?> PublicKeyAsync(string keyId, CancellationToken ct) =>
        await db.TravelSigningKeys.AsNoTracking().Where(k => k.Id == keyId).Select(k => k.PublicKey).FirstOrDefaultAsync(ct);

    /// <summary>The booking's invoice: its canonical text and the signature over it. Only once paid.</summary>
    public async Task<TravelInvoiceDto?> InvoiceAsync(User me, Guid bookingId, CancellationToken ct)
    {
        var b = await db.TravelBookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId && (b.TravelerId == me.Id || b.VendorId == me.Id || TravelCatalog.IsAdmin(me)), ct);
        if (b is null)
            return null;
        if (b.InvoiceNumber is null)
            throw new ChatRejectedException("The invoice is issued once the payment is confirmed.");
        var booking = (await issuer.DtosAsync(me, [b], ct))[0];
        var canonical = Canonical(b, booking);
        var (record, key) = await KeyAsync(ct);
        using (key)
        {
            var signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));
            return new TravelInvoiceDto(b.InvoiceNumber, b.Id, canonical, signature, record.Id, record.PublicKey);
        }
    }

    /// <summary>
    /// What is signed: the invoice's facts in a fixed order with no whitespace, so the same invoice
    /// always gives the same bytes. The traveller's ID number is left out.
    /// </summary>
    static string Canonical(TravelBooking b, TravelBookingDto d)
    {
        var item = d.Items[0];
        return JsonSerializer.Serialize(new
        {
            invoice = b.InvoiceNumber,
            booking = b.Reference,
            issued = b.ConfirmedAt?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            seller = d.VendorName,
            buyer = b.LeadGuestName,
            item = new
            {
                type = item.Type,
                title = item.Title,
                detail = item.Subtitle,
                check_in = item.CheckIn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                check_out = item.CheckOut?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                starts = item.StartsAt?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                guests = item.Guests,
                lines = item.Lines.Select(l => new { label = l.Label, amount = Amount(l.Amount) })
            },
            total = Amount(b.Amount),
            discount = Amount(b.Discount),
            paid = Amount(b.Amount - b.Discount),
            platform_fee = Amount(b.Fee),
            currency = b.Currency
        });
    }

    static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>The PDF voucher: the booking, the invoice and its signature, and a code the vendor can scan.</summary>
    public async Task<(byte[] Pdf, string Name)?> VoucherPdfAsync(User me, Guid bookingId, CancellationToken ct)
    {
        var invoice = await InvoiceAsync(me, bookingId, ct);
        if (invoice is null)
            return null;
        var b = await db.TravelBookings.AsNoTracking().FirstAsync(x => x.Id == bookingId, ct);
        var d = (await issuer.DtosAsync(me, [b], ct))[0];
        var item = d.Items[0];

        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = $"Lets Travel {b.Reference}", Creator = "Ndeipi Lets Travel" }))
        {
            var canvas = document.BeginPage(595, 842);
            using var title = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), 22);
            using var heading = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), 13);
            using var body = new SKFont(SKTypeface.Default, 11);
            using var small = new SKFont(SKTypeface.Default, 8);
            using var ink = new SKPaint { Color = new SKColor(0x1F, 0x2A, 0x44), IsAntialias = true };
            using var muted = new SKPaint { Color = new SKColor(0x6B, 0x72, 0x80), IsAntialias = true };
            using var band = new SKPaint { Color = new SKColor(0x1F, 0xA9, 0x71) };

            canvas.DrawRect(0, 0, 595, 70, band);
            using (var white = new SKPaint { Color = SKColors.White, IsAntialias = true })
            {
                canvas.DrawText("Lets Travel · Voucher", 40, 44, SKTextAlign.Left, title, white);
                canvas.DrawText(b.Reference, 555, 44, SKTextAlign.Right, title, white);
            }

            float y = 110;
            void Line(string text, SKFont font, SKPaint paint, float gap = 18)
            {
                foreach (var part in Wrap(text, font, 330))
                {
                    canvas.DrawText(part, 40, y, SKTextAlign.Left, font, paint);
                    y += gap;
                }
            }
            Line(item.Title, heading, ink, 20);
            Line(item.Subtitle, body, muted);
            Line(item.CheckIn is { } ci ? $"Check-in {ci:ddd d MMM yyyy} · Check-out {item.CheckOut:ddd d MMM yyyy}" : $"Departs {item.StartsAt:ddd d MMM yyyy, HH:mm} UTC", body, ink);
            Line($"{item.Guests} guest{(item.Guests == 1 ? "" : "s")} · Lead guest: {b.LeadGuestName}", body, ink);
            if (d.Address is { Length: > 0 } address)
                Line("Address: " + address, body, ink);
            if (d.MeetingPoint is { Length: > 0 } meeting)
                Line("Meeting point: " + meeting, body, ink);
            Line($"With {d.VendorName}" + (b.Phone is null ? "" : $" · Guest phone {b.Phone}"), body, ink);
            if (b.Notice is { } notice)
                Line("Note: " + notice, body, ink);
            y += 10;

            Line($"Tax invoice {invoice.Number}", heading, ink, 20);
            foreach (var l in item.Lines)
            {
                canvas.DrawText(l.Label, 40, y, SKTextAlign.Left, body, ink);
                canvas.DrawText(TravelContract.Money(l.Amount), 370, y, SKTextAlign.Right, body, ink);
                y += 16;
            }
            if (b.Discount > 0)
            {
                canvas.DrawText($"Believe Points ({b.PointsRedeemed})", 40, y, SKTextAlign.Left, body, ink);
                canvas.DrawText("−" + TravelContract.Money(b.Discount), 370, y, SKTextAlign.Right, body, ink);
                y += 16;
            }
            canvas.DrawText("Paid from the Ndeipi wallet", 40, y + 4, SKTextAlign.Left, heading, ink);
            canvas.DrawText(TravelContract.Money(b.Amount - b.Discount), 370, y + 4, SKTextAlign.Right, heading, ink);
            y += 30;

            using (var qr = QrBitmap(issuer.PrintedCode(b.Id)))
                { using var image = SKImage.FromBitmap(qr); canvas.DrawImage(image, new SKRect(400, 100, 555, 255), new SKSamplingOptions(SKFilterMode.Nearest)); }
            canvas.DrawText("Show at check-in", 477, 270, SKTextAlign.Center, body, muted);

            y = Math.Max(y, 300);
            Line("This invoice is signed by Lets Travel (ECDSA P-256, SHA-256). Verify the signature over the", small, muted, 11);
            Line($"invoice's canonical text with key {invoice.KeyId}, published at /{TravelContract.BasePath}/invoice-keys/{invoice.KeyId}.", small, muted, 11);
            foreach (var chunk in Chunk("Signature: " + invoice.Signature, 110))
                Line(chunk, small, muted, 11);
            document.EndPage();
        }
        return (stream.ToArray(), $"LetsTravel-{b.Reference}.pdf");
    }

    static SKBitmap QrBitmap(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return SKBitmap.Decode(new PngByteQRCode(data).GetGraphic(8));
    }

    static IEnumerable<string> Wrap(string text, SKFont font, float width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (font.MeasureText(candidate) > width && line.Length > 0)
            {
                yield return line.ToString();
                line.Clear().Append(word);
            }
            else
                line.Clear().Append(candidate);
        }
        if (line.Length > 0)
            yield return line.ToString();
    }

    static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }
}
