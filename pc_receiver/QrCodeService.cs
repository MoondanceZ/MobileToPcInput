using System.IO;
using Avalonia.Media.Imaging;
using QRCoder;

namespace pc_receiver;

public static class QrCodeService
{
    public static string BuildConnectUri(string host, int port)
    {
        return $"mobiletopcinput://connect?host={host}&port={port}";
    }

    public static Bitmap CreateBitmap(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        var bytes = png.GetGraphic(8, [0x1C, 0x27, 0x39], [0xFF, 0xFF, 0xFF]);
        return new Bitmap(new MemoryStream(bytes));
    }
}
