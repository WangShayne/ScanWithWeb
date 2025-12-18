namespace ScanWithWeb.Models;

/// <summary>
/// Configurable image rotation rules used by rotation="auto".
/// </summary>
public sealed class ImageRotationOptions
{
    /// <summary>
    /// Rotation when using flatbed/platen (UseAdf=false).
    /// </summary>
    public int AutoPlatenDegrees { get; set; } = 0;

    /// <summary>
    /// Rotation when using ADF simplex (UseAdf=true, Duplex=false).
    /// </summary>
    public int AutoAdfSimplexDegrees { get; set; } = 270;

    /// <summary>
    /// Rotation for odd pages (front side) when using ADF duplex (UseAdf=true, Duplex=true).
    /// </summary>
    public int AutoAdfDuplexOddDegrees { get; set; } = 270;

    /// <summary>
    /// Rotation for even pages (back side) when using ADF duplex (UseAdf=true, Duplex=true).
    /// </summary>
    public int AutoAdfDuplexEvenDegrees { get; set; } = 90;
}


