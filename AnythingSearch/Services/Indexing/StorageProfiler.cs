using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AnythingSearch.Helper;
using Microsoft.Win32.SafeHandles;

namespace AnythingSearch.Services;

/// <summary>What kind of device a drive actually sits on.</summary>
public enum StorageMedia
{
    /// <summary>The device could not be identified - a storage space, a RAID controller, a VM
    /// disk, or a driver that does not answer the query. Callers must stay conservative.</summary>
    Unknown,

    /// <summary>Incurs a seek penalty: a mechanical hard disk. Parallel walks make it thrash.</summary>
    Rotational,

    /// <summary>Solid state behind SATA/SAS/RAID. Handles a few concurrent walks well.</summary>
    SolidState,

    /// <summary>Solid state on NVMe. Deep queues are what the device is built for.</summary>
    Nvme,

    /// <summary>USB, SD or MMC. Solid state or not, the bus is the limit.</summary>
    Removable
}

/// <summary>What was found out about one drive, and a line for the log.</summary>
public sealed record StorageProfile(StorageMedia Media, string Description);

/// <summary>
/// Identifies the storage behind a drive letter, so indexing can be paced for the device it is
/// actually reading rather than for an average machine that does not exist.
///
/// The app ships to a very wide range of hardware, and the right number of concurrent directory
/// walkers differs by roughly an order of magnitude across it: a mechanical disk wants one walker
/// because every extra one turns sequential reads into a seek storm, while an NVMe drive is built
/// for deep queues and is left mostly idle by one. A single compiled-in number cannot serve both,
/// and a setting only helps the small fraction of users who would ever find it.
///
/// Detection is one DeviceIoControl per drive, cached for the life of the process. The volume
/// handle is opened with NO access rights, which is what lets this work without administrator
/// rights - it is a property query about the device, not a read of its contents.
///
/// Anything unrecognised is reported as <see cref="StorageMedia.Unknown"/> and paced exactly the
/// way the app was paced before this existed, so an unusual configuration can never come out of
/// this worse off than it started.
/// </summary>
public static class StorageProfiler
{
    private static readonly ConcurrentDictionary<char, StorageProfile> Cache = new();

    /// <summary>
    /// The storage behind a path's drive. Results are cached per drive letter: the answer cannot
    /// change while the app is running, and the query is not free.
    /// </summary>
    public static StorageProfile Profile(string path)
    {
        var letter = DriveLetter(path);
        if (letter == '\0') return new StorageProfile(StorageMedia.Unknown, "unknown volume");

        return Cache.GetOrAdd(letter, static drive =>
        {
            var profile = Detect(drive);
            Logger.Log($"Storage profile {drive}: {profile.Description}");
            return profile;
        });
    }

    private static char DriveLetter(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') return '\0';
            return char.ToUpperInvariant(root[0]);
        }
        catch { return '\0'; }
    }

    private static StorageProfile Detect(char driveLetter)
    {
        try
        {
            // A removable volume is capped by its bus whatever the media is, and that is worth
            // knowing before any device query: a USB stick and an internal SSD can both report
            // "no seek penalty" while being nothing alike to walk.
            var driveType = new DriveInfo($"{driveLetter}:\\").DriveType;
            if (driveType == DriveType.Removable)
                return new StorageProfile(StorageMedia.Removable, "removable drive");

            using var handle = OpenVolume(driveLetter);
            if (handle == null || handle.IsInvalid)
                return new StorageProfile(StorageMedia.Unknown, "device query unavailable");

            var bus = QueryBusType(handle);

            // The bus answers first where it is decisive. NVMe is never mechanical, and a device
            // hanging off USB/SD/MMC is limited by the bus no matter what it is made of.
            switch (bus)
            {
                case BusTypeNvme:
                    return new StorageProfile(StorageMedia.Nvme, "NVMe solid state");
                case BusTypeUsb:
                case BusTypeSd:
                case BusTypeMmc:
                    return new StorageProfile(StorageMedia.Removable, $"external drive (bus {bus})");
            }

            var seekPenalty = QuerySeekPenalty(handle);
            if (seekPenalty == true)
                return new StorageProfile(StorageMedia.Rotational, "mechanical hard disk");
            if (seekPenalty == false)
                return new StorageProfile(StorageMedia.SolidState, $"solid state (bus {bus})");

            return new StorageProfile(StorageMedia.Unknown, $"unrecognised device (bus {bus})");
        }
        catch (Exception ex)
        {
            // Detection is an optimisation. Never let it stop the drive being indexed.
            return new StorageProfile(StorageMedia.Unknown, $"detection failed ({ex.Message})");
        }
    }

    private static SafeFileHandle? OpenVolume(char driveLetter)
    {
        // Zero desired access: enough to ask the device about itself, and the reason this needs
        // no elevation. Asking for GENERIC_READ here would fail for a standard user.
        var handle = CreateFile(
            $@"\\.\{driveLetter}:",
            0,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        return handle.IsInvalid ? null : handle;
    }

    /// <summary>True = mechanical, false = solid state, null = the device did not answer.</summary>
    private static bool? QuerySeekPenalty(SafeFileHandle handle)
    {
        var query = new StoragePropertyQuery
        {
            PropertyId = StorageDeviceSeekPenaltyProperty,
            QueryType = PropertyStandardQuery
        };

        var size = Marshal.SizeOf<DeviceSeekPenaltyDescriptor>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, ref query,
                    Marshal.SizeOf<StoragePropertyQuery>(), buffer, size, out _, IntPtr.Zero))
                return null;

            return Marshal.PtrToStructure<DeviceSeekPenaltyDescriptor>(buffer).IncursSeekPenalty;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>The STORAGE_BUS_TYPE the device reports, or 0 (unknown) if it does not answer.</summary>
    private static int QueryBusType(SafeFileHandle handle)
    {
        var query = new StoragePropertyQuery
        {
            PropertyId = StorageDeviceProperty,
            QueryType = PropertyStandardQuery
        };

        // STORAGE_DEVICE_DESCRIPTOR is variable length - it carries vendor and product strings
        // after the fixed part - so the buffer is sized generously and only the fixed part read.
        const int BufferSize = 1024;
        var buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, ref query,
                    Marshal.SizeOf<StoragePropertyQuery>(), buffer, BufferSize, out var returned, IntPtr.Zero))
                return BusTypeUnknown;

            // BusType sits at a fixed offset in the descriptor; anything shorter is not one.
            if (returned < BusTypeOffset + sizeof(int)) return BusTypeUnknown;

            return Marshal.ReadInt32(buffer, BusTypeOffset);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    #region Win32

    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;

    /// <summary>
    /// Byte offset of BusType inside STORAGE_DEVICE_DESCRIPTOR: two DWORDs, four bytes of
    /// device type and flags, then four DWORD string offsets.
    /// </summary>
    private const int BusTypeOffset = 28;

    private const int BusTypeUnknown = 0x00;
    private const int BusTypeUsb = 0x07;
    private const int BusTypeSd = 0x0C;
    private const int BusTypeMmc = 0x0D;
    private const int BusTypeNvme = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        ref StoragePropertyQuery lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    #endregion
}
