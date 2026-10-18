using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PdfViewer.Services.Scanning;

/// <summary>
/// A scanner Windows knows about. Until <see cref="CapabilitiesKnown"/>, the flatbed, feeder and
/// duplex flags are only what might be there: reading them means connecting to the scanner,
/// which for a network scanner that is switched off can take minutes.
/// </summary>
public sealed record ScannerDevice(string Id, string Name, bool HasFlatbed, bool HasFeeder, bool HasDuplex, bool CapabilitiesKnown = true);

public enum ScanColorMode
{
    Color,
    Grayscale,
    BlackAndWhite,
}

public enum ScanSource
{
    Flatbed,
    Feeder,
}

/// <summary>What to scan: the device, colour, resolution, where the paper is, and both sides or one.</summary>
public sealed record ScanSettings(string DeviceId, ScanColorMode Color, int Dpi, ScanSource Source, bool Duplex);

/// <summary>A scanned page: an encoded image file (BMP, PNG, TIFF or JPEG, whatever the driver delivers).</summary>
public sealed record ScannedPage(byte[] ImageFile);

/// <summary>Scanning cannot happen: no scanner, the scanning service is missing or stopped, or the device is busy or offline.</summary>
public sealed class ScannerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The scanners on this machine and scanning from them. The application's implementation is
/// Windows Image Acquisition (<see cref="WiaScannerService"/>); tests use a fake.
/// </summary>
public interface IScannerService
{
    /// <summary>The scanners installed; empty when there are none.</summary>
    Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>The device with what it can do (flatbed, feeder, duplex), read from the scanner itself.</summary>
    Task<ScannerDevice> GetCapabilitiesAsync(ScannerDevice device, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans with <paramref name="settings"/>, handing over each page as it arrives (from a
    /// feeder, every page it holds). Returns the pages scanned.
    /// </summary>
    Task<int> ScanAsync(ScanSettings settings, Action<ScannedPage> onPage, CancellationToken cancellationToken = default);
}
