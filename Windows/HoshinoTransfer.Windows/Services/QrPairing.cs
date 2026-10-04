using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using QRCoder;
using ZXing;
using ZXing.Common;

namespace HoshinoTransfer.Windows.Services;

/// <summary>
/// Pairing QR payloads. The QR carries the API link of the service that issued the code so the
/// scanning device learns both which server to talk to and which code to redeem.
/// </summary>
public static class PairingPayload
{
    public const string PairRoute = "api/v1/devices/pair";

    /// <summary>Builds the link encoded into the QR image, e.g. https://host/api/v1/devices/pair?code=12345678</summary>
    public static string BuildLink(string baseAddress, string code)
        => $"{baseAddress.TrimEnd('/')}/{PairRoute}?code={Uri.EscapeDataString(code)}";

    /// <summary>
    /// Accepts any of the shapes this app has ever emitted or might receive:
    /// a bare eight-digit code, hoshinotransfer://pair?code=..., or the full API link.
    /// </summary>
    public static string? ExtractCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();

        var query = text.IndexOf('?');
        if (query >= 0)
        {
            var parameters = text[(query + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var parameter in parameters)
            {
                var separator = parameter.IndexOf('=');
                if (separator <= 0) continue;
                if (!parameter[..separator].Trim().Equals("code", StringComparison.OrdinalIgnoreCase)) continue;
                var value = Uri.UnescapeDataString(parameter[(separator + 1)..]).Trim();
                if (IsCode(value)) return value;
            }
        }

        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (IsCode(digits)) return digits;

        var tail = new string(text.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return IsCode(tail) ? tail : null;
    }

    public static bool IsCode(string? value)
        => value is { Length: 8 } && value.All(char.IsDigit);
}

/// <summary>Renders pairing QR images and detects QR codes inside existing image files.</summary>
public static class QrPairing
{
    /// <summary>Renders the QR as a PNG stream for WPF's BitmapImage source.</summary>
    public static BitmapSource? RenderPng(string text, int pixelsPerModule = 10)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
            using var pngRenderer = new PngByteQRCode(data);
            var bytes = pngRenderer.GetGraphic(pixelsPerModule);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    /// <summary>
    /// Decodes the first QR code found in an image file. Uses WPF's bitmap pipeline and feeds
    /// raw BGRA pixels into ZXing, so no System.Drawing or native dependency is required.
    /// </summary>
    public static string? DecodeImageFile(string path)
    {
        try
        {
            var uri = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            var decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var converted = frame.Format != PixelFormats.Bgra32 ? new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0) : (BitmapSource)frame;
            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            if (width <= 0 || height <= 0) return null;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            var luminance = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
            var reader = new BarcodeReaderGeneric
            {
                Options = new DecodingOptions
                {
                    PossibleFormats = new[] { BarcodeFormat.QR_CODE },
                    TryHarder = true,
                    TryInverted = true,
                },
            };
            var result = reader.Decode(luminance);
            return result?.Text;
        }
        catch { return null; }
    }
}