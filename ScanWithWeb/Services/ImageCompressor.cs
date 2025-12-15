using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;

namespace ScanWithWeb.Services;

/// <summary>
/// Image compression utility for reducing scan file sizes
/// Automatically compresses large BMP images to JPEG
/// </summary>
public static class ImageCompressor
{
    // Images larger than this threshold (in bytes) will be compressed
    // 5MB threshold - A4 at 150 DPI is about 4MB, so this targets A3 or high DPI scans
    private const int CompressionThreshold = 5 * 1024 * 1024;

    // JPEG quality (0-100) - 85 is a good balance of quality and size
    private const long JpegQuality = 85;

    /// <summary>
    /// Compresses image data if it exceeds the threshold
    /// Returns compressed JPEG data for large images, or original data for small images
    /// </summary>
    /// <param name="imageData">Original image data (BMP format)</param>
    /// <param name="format">Output format name (will be updated if compressed)</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    /// <returns>Compressed or original image data</returns>
    public static byte[] CompressIfNeeded(byte[] imageData, ref string format, ILogger? logger = null)
    {
        // Only compress if larger than threshold
        if (imageData.Length < CompressionThreshold)
        {
            logger?.LogDebug("[ImageCompressor] Image size {Size:N0} bytes is below threshold, skipping compression",
                imageData.Length);
            return imageData;
        }

        return CompressToJpeg(imageData, ref format, logger);
    }

    /// <summary>
    /// Always compresses image to JPEG format
    /// </summary>
    public static byte[] CompressToJpeg(byte[] imageData, ref string format, ILogger? logger = null)
    {
        try
        {
            using var inputStream = new MemoryStream(imageData);
            using var image = Image.FromStream(inputStream);
            using var outputStream = new MemoryStream();

            // Get JPEG encoder
            var jpegEncoder = GetEncoder(ImageFormat.Jpeg);
            if (jpegEncoder == null)
            {
                logger?.LogWarning("[ImageCompressor] JPEG encoder not found, returning original data");
                return imageData;
            }

            // Set quality parameter
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);

            image.Save(outputStream, jpegEncoder, encoderParams);

            var compressedData = outputStream.ToArray();
            var compressionRatio = (double)imageData.Length / compressedData.Length;

            logger?.LogInformation(
                "[ImageCompressor] Compressed {OriginalSize:N0} bytes -> {CompressedSize:N0} bytes ({Ratio:F1}x reduction)",
                imageData.Length, compressedData.Length, compressionRatio);

            format = "jpg";
            return compressedData;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[ImageCompressor] Compression failed, returning original data");
            return imageData;
        }
    }

    /// <summary>
    /// Compresses image to PNG format (lossless compression)
    /// </summary>
    public static byte[] CompressToPng(byte[] imageData, ref string format, ILogger? logger = null)
    {
        try
        {
            using var inputStream = new MemoryStream(imageData);
            using var image = Image.FromStream(inputStream);
            using var outputStream = new MemoryStream();

            image.Save(outputStream, ImageFormat.Png);

            var compressedData = outputStream.ToArray();
            var compressionRatio = (double)imageData.Length / compressedData.Length;

            logger?.LogInformation(
                "[ImageCompressor] PNG compressed {OriginalSize:N0} bytes -> {CompressedSize:N0} bytes ({Ratio:F1}x reduction)",
                imageData.Length, compressedData.Length, compressionRatio);

            format = "png";
            return compressedData;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[ImageCompressor] PNG compression failed, returning original data");
            return imageData;
        }
    }

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        var codecs = ImageCodecInfo.GetImageEncoders();
        foreach (var codec in codecs)
        {
            if (codec.FormatID == format.Guid)
            {
                return codec;
            }
        }
        return null;
    }
}
