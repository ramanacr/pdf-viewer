using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp.RuntimeBinder;

namespace PdfViewer.Services.Scanning;

/// <summary>
/// Scanning through Windows Image Acquisition 2.0, the scanner support built into Windows, by its
/// scripting interface (WIA.DeviceManager, late bound: no interop assembly, nothing installed).
/// The scanner's own driver does the work; this never installs or updates one. With no scanner,
/// or with the Windows Image Acquisition service stopped, it says so instead of failing.
/// Every call runs on a single-threaded apartment thread of its own, as WIA requires.
/// </summary>
public sealed class WiaScannerService : IScannerService
{
    // Device properties (wiadef.h).
    private const int DocumentHandlingCapabilities = 3086, DocumentHandlingStatus = 3087, DocumentHandlingSelect = 3088, Pages = 3096;
    private const int Feeder = 0x001, Flatbed = 0x002, Duplex = 0x004;
    // Item properties.
    private const int CurrentIntent = 6146, HorizontalResolution = 6147, VerticalResolution = 6148, DataType = 4103, BitDepth = 4104;
    private const string FormatPng = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";
    private const int ScannerDeviceType = 1;

    // WIA error codes (wiadef.h).
    private const int PaperEmpty = unchecked((int)0x80210003), PaperJam = unchecked((int)0x80210002), Busy = unchecked((int)0x80210006),
        Offline = unchecked((int)0x80210005), CoverOpen = unchecked((int)0x80210016), LampOff = unchecked((int)0x80210017),
        DeviceNotFound = unchecked((int)0x80210015), UserCancelled = unchecked((int)0x80210064), ServiceStopped = unchecked((int)0x80070422);

    /// <summary>The WIA scripting library is registered (it is part of Windows; Server editions may lack it).</summary>
    public static bool IsAvailable => OperatingSystem.IsWindows() && Type.GetTypeFromProgID("WIA.DeviceManager") != null;

    public Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) => StaWorker.Run<IReadOnlyList<ScannerDevice>>(() =>
    {
        dynamic manager = Manager();
        var devices = new List<ScannerDevice>();
        try
        {
            dynamic infos = manager.DeviceInfos;
            int count = infos.Count;
            for (int i = 1; i <= count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic info = infos[i];
                if ((int)info.Type != ScannerDeviceType) continue;
                string id = info.DeviceID;
                string name = Property(info.Properties, "Name") as string ?? "Scanner";
                // What it can do is read by connecting (GetCapabilitiesAsync), which can take minutes for a network scanner that is off.
                devices.Add(new ScannerDevice(id, name, true, true, true, CapabilitiesKnown: false));
                Release(info);
            }
            Release(infos);
        }
        catch (COMException ex)
        {
            throw Unavailable(ex);
        }
        finally
        {
            Release(manager);
        }
        return devices;
    });

    public Task<ScannerDevice> GetCapabilitiesAsync(ScannerDevice device, CancellationToken cancellationToken = default) => StaWorker.Run<ScannerDevice>(() =>
    {
        dynamic manager = Manager();
        dynamic? connected = null;
        try
        {
            dynamic infos = manager.DeviceInfos;
            for (int i = 1; i <= (int)infos.Count && connected == null; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic info = infos[i];
                if ((string)info.DeviceID == device.Id) connected = info.Connect();
            }
            if (connected == null) throw new ScannerUnavailableException("The scanner is no longer connected. Check that it is switched on and connected, then try again.");
            int caps = Convert.ToInt32(PropertyById(connected.Properties, DocumentHandlingCapabilities) ?? Flatbed);
            return device with
            {
                HasFlatbed = (caps & Flatbed) != 0 || (caps & Feeder) == 0,
                HasFeeder = (caps & Feeder) != 0,
                HasDuplex = (caps & Duplex) != 0,
                CapabilitiesKnown = true,
            };
        }
        catch (COMException ex)
        {
            throw Unavailable(ex);
        }
        catch (Exception ex) when (ex is RuntimeBinderException or InvalidCastException or FormatException)
        {
            return device with { CapabilitiesKnown = true, HasFeeder = false, HasDuplex = false };
        }
        finally
        {
            if (connected != null) Release(connected);
            Release(manager);
        }
    });

    public Task<int> ScanAsync(ScanSettings settings, Action<ScannedPage> onPage, CancellationToken cancellationToken = default) => StaWorker.Run(() =>
    {
        dynamic manager = Manager();
        dynamic? device = null;
        int scanned = 0;
        try
        {
            dynamic infos = manager.DeviceInfos;
            for (int i = 1; i <= (int)infos.Count && device == null; i++)
            {
                dynamic info = infos[i];
                if ((string)info.DeviceID == settings.DeviceId) device = info.Connect();
            }
            if (device == null) throw new ScannerUnavailableException("The scanner is no longer connected. Check that it is switched on and connected, then try again.");

            bool feeder = settings.Source == ScanSource.Feeder;
            int select = feeder ? Feeder | (settings.Duplex ? Duplex : 0) : Flatbed;
            TrySet(device.Properties, DocumentHandlingSelect, select);
            if (feeder) TrySet(device.Properties, Pages, 1); // a page each transfer: the loop asks until the feeder is empty

            dynamic item = PickItem(device, feeder);
            int intent = settings.Color switch { ScanColorMode.Color => 1, ScanColorMode.Grayscale => 2, _ => 4 };
            TrySet(item.Properties, CurrentIntent, intent);
            TrySet(item.Properties, DataType, settings.Color switch { ScanColorMode.Color => 3, ScanColorMode.Grayscale => 2, _ => 0 });
            TrySet(item.Properties, BitDepth, settings.Color switch { ScanColorMode.Color => 24, ScanColorMode.Grayscale => 8, _ => 1 });
            TrySet(item.Properties, HorizontalResolution, settings.Dpi);
            TrySet(item.Properties, VerticalResolution, settings.Dpi);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[]? page;
                try
                {
                    page = Transfer(item);
                }
                catch (COMException ex) when (ex.HResult == PaperEmpty)
                {
                    if (scanned == 0) throw new ScannerUnavailableException("The document feeder is empty. Put the pages in the feeder and try again.", ex);
                    break;
                }
                if (page == null || page.Length == 0) break;
                onPage(new ScannedPage(page));
                scanned++;
                if (!feeder) break;
                // Some drivers say when the feeder has run out rather than failing the next transfer.
                if (PropertyById(device.Properties, DocumentHandlingStatus) is { } status && (Convert.ToInt32(status) & Feeder) == 0) break;
            }
            return scanned;
        }
        catch (COMException ex)
        {
            throw Unavailable(ex);
        }
        catch (RuntimeBinderException ex)
        {
            throw new ScannerUnavailableException("The scanner's driver does not support scanning this way.", ex);
        }
        finally
        {
            if (device != null) Release(device);
            Release(manager);
        }
    });

    private static dynamic Manager()
    {
        var type = Type.GetTypeFromProgID("WIA.DeviceManager")
            ?? throw new ScannerUnavailableException("Windows Image Acquisition is not available on this computer, so scanners cannot be used.");
        try
        {
            return Activator.CreateInstance(type)!;
        }
        catch (COMException ex)
        {
            throw Unavailable(ex);
        }
    }

    /// <summary>The flatbed or feeder item (WIA 2 drivers can list each as a child item of its own).</summary>
    private static dynamic PickItem(dynamic device, bool feeder)
    {
        dynamic items = device.Items;
        int count = items.Count;
        if (count == 0) throw new ScannerUnavailableException("The scanner reports nothing to scan from.");
        if (count > 1)
        {
            for (int i = 1; i <= count; i++)
            {
                dynamic candidate = items[i];
                string name = (Property(candidate.Properties, "Item Name") as string ?? Property(candidate.Properties, "Name") as string ?? string.Empty).ToLowerInvariant();
                bool isFeeder = name.Contains("feeder") || name.Contains("adf");
                if (isFeeder == feeder) return candidate;
            }
        }
        return items[1];
    }

    /// <summary>One page, as PNG when the driver can deliver it, else in its own format.</summary>
    private static byte[]? Transfer(dynamic item)
    {
        dynamic image;
        try
        {
            image = item.Transfer(FormatPng);
        }
        catch (COMException ex) when (ex.HResult is not (PaperEmpty or PaperJam or Busy or Offline or CoverOpen or LampOff or UserCancelled))
        {
            image = item.Transfer();
        }
        try
        {
            return image.FileData.BinaryData as byte[];
        }
        finally
        {
            Release(image);
        }
    }

    private static object? Property(dynamic properties, string name)
    {
        int count = properties.Count;
        for (int i = 1; i <= count; i++)
        {
            dynamic p = properties[i];
            if ((string)p.Name == name) return p.Value;
        }
        return null;
    }

    private static object? PropertyById(dynamic properties, int id)
    {
        try
        {
            int count = properties.Count;
            for (int i = 1; i <= count; i++)
            {
                dynamic p = properties[i];
                if ((int)p.PropertyID == id) return p.Value;
            }
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException) { }
        return null;
    }

    /// <summary>Sets a property when the driver has it and accepts the value; drivers differ, and the defaults scan anyway.</summary>
    private static void TrySet(dynamic properties, int id, int value)
    {
        try
        {
            int count = properties.Count;
            for (int i = 1; i <= count; i++)
            {
                dynamic p = properties[i];
                if ((int)p.PropertyID != id) continue;
                p.Value = value;
                return;
            }
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or ArgumentException) { }
    }

    private static ScannerUnavailableException Unavailable(COMException ex) => new(ex.HResult switch
    {
        PaperJam => "The paper is jammed in the scanner. Clear it and try again.",
        Busy => "The scanner is busy. Wait for it to finish, then try again.",
        Offline or DeviceNotFound => "The scanner is offline. Check that it is switched on and connected, then try again.",
        CoverOpen => "The scanner's cover is open. Close it and try again.",
        LampOff => "The scanner's lamp is off. Wait for it to warm up, then try again.",
        UserCancelled => "Scanning was cancelled at the scanner.",
        ServiceStopped => "The Windows Image Acquisition service is not running. Start it in Services (\"Windows Image Acquisition (WIA)\"), then try again.",
        _ => "The scanner could not be used. Check that it is switched on and connected, and that its driver is installed.",
    }, ex);

    private static void Release(object? com)
    {
        try
        {
            if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }
        catch (ArgumentException) { }
    }
}
