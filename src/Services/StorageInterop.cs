using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace xDriverUpdater.Services;

/// <summary>
/// Native Win32 and COM storage interop for querying physical drive telemetry,
/// NVMe health information, ATA SMART attributes, and device volume mappings.
/// Requires no external NuGet packages.
/// </summary>
public static class StorageInterop
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;

    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint SMART_GET_VERSION = 0x00074080;
    public const uint SMART_RCV_DRIVE_DATA = 0x0007C088;
    public const uint IOCTL_SCSI_PASS_THROUGH = 0x0004D004;
    public const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x0004D014;
    public const uint IOCTL_ATA_PASS_THROUGH = 0x0004D02C;
    public const uint IOCTL_ATA_PASS_THROUGH_DIRECT = 0x0004D030;

    public const byte SCSI_IOCTL_DATA_IN = 1;
    public const byte SCSI_IOCTL_DATA_OUT = 0;
    public const byte SCSI_IOCTL_DATA_UNSPECIFIED = 2;

    public const ushort ATA_FLAGS_DATA_IN = 0x02;
    public const ushort ATA_FLAGS_DATA_OUT = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct SCSI_PASS_THROUGH
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBufferOffset;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Cdb;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SCSI_PASS_THROUGH_WITH_BUFFERS
    {
        public SCSI_PASS_THROUGH Spt;
        public uint Filler;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] SenseBuf;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4096)]
        public byte[] DataBuf;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SCSI_PASS_THROUGH_DIRECT
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Cdb;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER
    {
        public SCSI_PASS_THROUGH_DIRECT Sptd;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] SenseInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ATA_PASS_THROUGH_EX
    {
        public ushort Length;
        public ushort AtaFlags;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte ReservedAsUchar;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public uint ReservedAsUlong;
        public IntPtr DataBufferOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] PreviousTaskFile;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] CurrentTaskFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ATA_PASS_THROUGH_EX_WITH_BUFFERS
    {
        public ATA_PASS_THROUGH_EX Apt;
        public uint Filler;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4096)]
        public byte[] Buf;
    }

    // STORAGE_PROPERTY_ID
    public const uint StorageDeviceProperty = 0;
    public const uint StorageAdapterProperty = 1;
    public const uint StorageDeviceSeekPenaltyProperty = 7;
    public const uint StorageDeviceTemperatureProperty = 30;
    public const uint StorageAdapterProtocolSpecificProperty = 49;
    public const uint StorageDeviceProtocolSpecificProperty = 50;

    // STORAGE_PROTOCOL_TYPE
    public const uint ProtocolTypeUnknown = 0;
    public const uint ProtocolTypeScsi = 1;
    public const uint ProtocolTypeAta = 2;
    public const uint ProtocolTypeNvme = 3;

    // NVMe Data Types & Log Pages
    public const uint NVMeDataTypeLogPage = 2;
    public const uint NVME_LOG_PAGE_HEALTH_INFO = 2;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// Open a physical drive handle. First attempts GENERIC_READ | GENERIC_WRITE (elevated),
    /// falling back to FILE_ANY_ACCESS (0) which allows querying descriptors and geometry even without full write access.
    /// </summary>
    public static SafeFileHandle OpenDrive(int diskIndex)
    {
        var path = @"\\.\PhysicalDrive" + diskIndex;
        var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        }
        return handle;
    }

    /// <summary>
    /// Open a volume handle (e.g. \\.\C:) with FILE_ANY_ACCESS for mapping to physical disk.
    /// </summary>
    public static SafeFileHandle OpenVolume(string driveLetter)
    {
        var path = @"\\.\" + driveLetter.TrimEnd('\\', '/');
        return CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
    }

    /// <summary>
    /// Queries the physical disk number and partition for a logical drive letter.
    /// </summary>
    public static (int DiskNumber, int PartitionNumber)? GetVolumeDiskNumber(string driveLetter)
    {
        try
        {
            using var handle = OpenVolume(driveLetter);
            if (handle.IsInvalid) return null;

            var buffer = Marshal.AllocHGlobal(12);
            try
            {
                if (DeviceIoControl(handle, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, buffer, 12, out _, IntPtr.Zero))
                {
                    var devNum = Marshal.ReadInt32(buffer, 4);
                    var partNum = Marshal.ReadInt32(buffer, 8);
                    return (devNum, partNum);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            // Ignore volume query errors
        }
        return null;
    }

    /// <summary>
    /// Queries physical disk size via IOCTL_DISK_GET_DRIVE_GEOMETRY_EX.
    /// </summary>
    public static long GetDiskSizeBytes(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (DeviceIoControl(handle, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, IntPtr.Zero, 0, buffer, 256, out _, IntPtr.Zero))
            {
                // DISK_GEOMETRY Geometry (24 bytes) followed by DiskSize (LARGE_INTEGER at offset 24)
                return Marshal.ReadInt64(buffer, 24);
            }
        }
        catch
        {
            // Ignore
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return 0;
    }

    /// <summary>
    /// Queries basic device descriptors (Vendor, Product/Model, SerialNumber, BusType).
    /// </summary>
    public static (string Vendor, string Product, string Serial, int BusType) GetDeviceDescriptor(SafeFileHandle handle)
    {
        var query = Marshal.AllocHGlobal(12);
        var desc = Marshal.AllocHGlobal(1024);
        try
        {
            Marshal.WriteInt32(query, 0, (int)StorageDeviceProperty);
            Marshal.WriteInt32(query, 4, 0); // PropertyStandardQuery

            if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, query, 12, desc, 1024, out _, IntPtr.Zero))
            {
                var busType = Marshal.ReadInt32(desc, 28);
                var vendorOffset = Marshal.ReadInt32(desc, 12);
                var productOffset = Marshal.ReadInt32(desc, 16);
                var serialOffset = Marshal.ReadInt32(desc, 24);

                var vendor = vendorOffset > 0 ? Marshal.PtrToStringAnsi(new IntPtr(desc.ToInt64() + vendorOffset))?.Trim() ?? "" : "";
                var product = productOffset > 0 ? Marshal.PtrToStringAnsi(new IntPtr(desc.ToInt64() + productOffset))?.Trim() ?? "" : "";
                var serial = serialOffset > 0 ? Marshal.PtrToStringAnsi(new IntPtr(desc.ToInt64() + serialOffset))?.Trim() ?? "" : "";

                return (vendor, product, serial, busType);
            }
        }
        catch
        {
            // Ignore
        }
        finally
        {
            Marshal.FreeHGlobal(query);
            Marshal.FreeHGlobal(desc);
        }
        return ("", "", "", 0);
    }

    /// <summary>
    /// Checks whether the drive incurs seek penalty (false = SSD, true = HDD).
    /// </summary>
    public static bool? GetSeekPenalty(SafeFileHandle handle)
    {
        var query = Marshal.AllocHGlobal(12);
        var desc = Marshal.AllocHGlobal(1024);
        try
        {
            Marshal.WriteInt32(query, 0, (int)StorageDeviceSeekPenaltyProperty);
            Marshal.WriteInt32(query, 4, 0); // PropertyStandardQuery

            if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, query, 12, desc, 1024, out _, IntPtr.Zero))
            {
                // IncursSeekPenalty is a byte at offset 8 (DEVICE_SEEK_PENALTY_DESCRIPTOR)
                var incursSeek = Marshal.ReadByte(desc, 8) != 0;
                return incursSeek;
            }
        }
        catch
        {
            // Ignore
        }
        finally
        {
            Marshal.FreeHGlobal(query);
            Marshal.FreeHGlobal(desc);
        }
        return null;
    }

    /// <summary>
    /// Reads direct NVMe SMART / Health Information Log (512 bytes) via IOCTL_STORAGE_QUERY_PROPERTY.
    /// Works natively on Windows 10/11 for all NVMe SSDs without any 3rd party drivers or tools.
    /// </summary>
    public static byte[]? QueryNvmeHealthLog(SafeFileHandle handle)
    {
        const int querySize = 12 + 40; // STORAGE_PROPERTY_QUERY (12) + STORAGE_PROTOCOL_SPECIFIC_DATA (40)
        const int bufferSize = querySize + 512;
        var buffer = Marshal.AllocHGlobal(bufferSize);

        try
        {
            // Try StorageAdapterProtocolSpecificProperty (49), then StorageDeviceProtocolSpecificProperty (50)
            uint[] propertyIds = [StorageAdapterProtocolSpecificProperty, StorageDeviceProtocolSpecificProperty];
            foreach (var propId in propertyIds)
            {
                for (var i = 0; i < bufferSize; i++) Marshal.WriteByte(buffer, i, 0);

                Marshal.WriteInt32(buffer, 0, (int)propId);
                Marshal.WriteInt32(buffer, 4, 0); // PropertyStandardQuery

                var protoOffset = 8;
                Marshal.WriteInt32(buffer, protoOffset + 0, (int)ProtocolTypeNvme); // ProtocolType = 3
                Marshal.WriteInt32(buffer, protoOffset + 4, (int)NVMeDataTypeLogPage); // DataType = 2
                Marshal.WriteInt32(buffer, protoOffset + 8, (int)NVME_LOG_PAGE_HEALTH_INFO); // ProtocolDataRequestValue = 2
                Marshal.WriteInt32(buffer, protoOffset + 12, 0);
                Marshal.WriteInt32(buffer, protoOffset + 16, 40); // ProtocolDataOffset from STORAGE_PROTOCOL_SPECIFIC_DATA
                Marshal.WriteInt32(buffer, protoOffset + 20, 512); // ProtocolDataLength

                if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, buffer, (uint)bufferSize, buffer, (uint)bufferSize, out var returned, IntPtr.Zero) && returned >= 48 + 512)
                {
                    // STORAGE_PROTOCOL_DATA_DESCRIPTOR:
                    // Version (0), Size (4), ProtocolSpecificData starts at offset 8.
                    var dataRelOffset = Marshal.ReadInt32(buffer, 8 + 16);
                    var dataOffset = 8 + dataRelOffset;
                    if (dataOffset + 512 <= bufferSize)
                    {
                        var result = new byte[512];
                        Marshal.Copy(new IntPtr(buffer.ToInt64() + dataOffset), result, 0, 512);
                        return result;
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return null;
    }

    /// <summary>
    /// Reads direct temperature via StorageDeviceTemperatureProperty (30).
    /// </summary>
    public static int? QueryDeviceTemperature(SafeFileHandle handle)
    {
        var query = Marshal.AllocHGlobal(12);
        var desc = Marshal.AllocHGlobal(1024);
        try
        {
            Marshal.WriteInt32(query, 0, (int)StorageDeviceTemperatureProperty);
            Marshal.WriteInt32(query, 4, 0); // PropertyStandardQuery

            if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, query, 12, desc, 1024, out _, IntPtr.Zero))
            {
                // STORAGE_TEMPERATURE_DATA_DESCRIPTOR
                // InfoCount is at offset 12. STORAGE_TEMPERATURE_INFO entries start at 16.
                var infoCount = Marshal.ReadInt16(desc, 12);
                if (infoCount > 0)
                {
                    var temp = Marshal.ReadInt16(desc, 16 + 2); // Temperature in Celsius
                    if (temp != -32768 && temp > -50 && temp < 150)
                    {
                        return temp;
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }
        finally
        {
            Marshal.FreeHGlobal(query);
            Marshal.FreeHGlobal(desc);
        }
        return null;
    }

    /// <summary>
    /// Executes a SCSI command via IOCTL_SCSI_PASS_THROUGH (buffered internal data buffers).
    /// Used by CrystalDiskInfo for broad USB mass-storage bridge compatibility.
    /// </summary>
    public static bool ExecuteScsiPassThroughBuffered(
        SafeFileHandle handle,
        byte[] cdb,
        byte cdbLength,
        byte dataIn,
        byte[]? dataBuffer,
        uint timeoutSeconds = 5)
    {
        if (handle.IsInvalid || handle.IsClosed) return false;

        var dataLen = (uint)(dataBuffer?.Length ?? 0);
        if (dataLen > 4096) dataLen = 4096;

        try
        {
            var sptwb = new SCSI_PASS_THROUGH_WITH_BUFFERS();
            sptwb.Spt.Length = (ushort)Marshal.SizeOf<SCSI_PASS_THROUGH>();
            sptwb.Spt.CdbLength = cdbLength;
            sptwb.Spt.SenseInfoLength = 24;
            sptwb.Spt.DataIn = dataIn;
            sptwb.Spt.DataTransferLength = dataLen;
            sptwb.Spt.TimeOutValue = timeoutSeconds;
            sptwb.Spt.DataBufferOffset = Marshal.OffsetOf<SCSI_PASS_THROUGH_WITH_BUFFERS>("DataBuf");
            sptwb.Spt.SenseInfoOffset = (uint)Marshal.OffsetOf<SCSI_PASS_THROUGH_WITH_BUFFERS>("SenseBuf").ToInt32();
            sptwb.Spt.Cdb = new byte[16];
            Array.Copy(cdb, sptwb.Spt.Cdb, Math.Min(cdb.Length, 16));
            sptwb.SenseBuf = new byte[32];
            sptwb.DataBuf = new byte[4096];

            if (dataIn == SCSI_IOCTL_DATA_OUT && dataBuffer != null)
            {
                Array.Copy(dataBuffer, sptwb.DataBuf, (int)dataLen);
            }

            var totalLength = (uint)(sptwb.Spt.DataBufferOffset.ToInt64() + dataLen);
            var structSize = Marshal.SizeOf(sptwb);
            IntPtr pStruct = Marshal.AllocHGlobal(structSize);
            try
            {
                Marshal.StructureToPtr(sptwb, pStruct, false);

                bool success = DeviceIoControl(handle, IOCTL_SCSI_PASS_THROUGH, pStruct, (uint)Marshal.SizeOf<SCSI_PASS_THROUGH>(), pStruct, totalLength, out _, IntPtr.Zero);
                if (!success)
                {
                    success = DeviceIoControl(handle, IOCTL_SCSI_PASS_THROUGH, pStruct, totalLength, pStruct, totalLength, out _, IntPtr.Zero);
                }

                if (!success) return false;

                var result = Marshal.PtrToStructure<SCSI_PASS_THROUGH_WITH_BUFFERS>(pStruct);
                if (result.Spt.ScsiStatus != 0 && result.Spt.ScsiStatus != 2)
                {
                    return false;
                }

                if (dataIn == SCSI_IOCTL_DATA_IN && dataBuffer != null && dataLen > 0)
                {
                    Array.Copy(result.DataBuf, dataBuffer, (int)dataLen);
                }

                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(pStruct);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Executes a SCSI command via IOCTL_SCSI_PASS_THROUGH_DIRECT.
    /// </summary>
    public static bool ExecuteScsiPassThroughDirect(
        SafeFileHandle handle,
        byte[] cdb,
        byte cdbLength,
        byte dataIn,
        byte[]? dataBuffer,
        uint timeoutSeconds = 5)
    {
        if (handle.IsInvalid || handle.IsClosed) return false;

        var dataLen = (uint)(dataBuffer?.Length ?? 0);
        IntPtr pData = IntPtr.Zero;
        if (dataLen > 0)
        {
            pData = Marshal.AllocHGlobal((int)dataLen);
            if (dataIn == SCSI_IOCTL_DATA_OUT && dataBuffer != null)
            {
                Marshal.Copy(dataBuffer, 0, pData, (int)dataLen);
            }
            else
            {
                for (int i = 0; i < (int)dataLen; i++) Marshal.WriteByte(pData, i, 0);
            }
        }

        try
        {
            var sptdwb = new SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER();
            sptdwb.Sptd.Length = (ushort)Marshal.SizeOf<SCSI_PASS_THROUGH_DIRECT>();
            sptdwb.Sptd.CdbLength = cdbLength;
            sptdwb.Sptd.SenseInfoLength = 64;
            sptdwb.Sptd.DataIn = dataIn;
            sptdwb.Sptd.DataTransferLength = dataLen;
            sptdwb.Sptd.TimeOutValue = timeoutSeconds;
            sptdwb.Sptd.DataBuffer = pData;
            sptdwb.Sptd.SenseInfoOffset = (uint)Marshal.OffsetOf<SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER>("SenseInfo");
            sptdwb.Sptd.Cdb = new byte[16];
            Array.Copy(cdb, sptdwb.Sptd.Cdb, Math.Min(cdb.Length, 16));
            sptdwb.SenseInfo = new byte[64];

            var structSize = Marshal.SizeOf(sptdwb);
            IntPtr pStruct = Marshal.AllocHGlobal(structSize);
            try
            {
                Marshal.StructureToPtr(sptdwb, pStruct, false);

                if (!DeviceIoControl(handle, IOCTL_SCSI_PASS_THROUGH_DIRECT, pStruct, (uint)structSize, pStruct, (uint)structSize, out _, IntPtr.Zero))
                {
                    return false;
                }

                var result = Marshal.PtrToStructure<SCSI_PASS_THROUGH_DIRECT_WITH_BUFFER>(pStruct);
                if (result.Sptd.ScsiStatus != 0 && result.Sptd.ScsiStatus != 2)
                {
                    return false;
                }

                if (dataIn == SCSI_IOCTL_DATA_IN && dataBuffer != null && dataLen > 0)
                {
                    Marshal.Copy(pData, dataBuffer, 0, (int)dataLen);
                }

                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(pStruct);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pData);
            }
        }
    }

    /// <summary>
    /// Executes a SCSI command trying buffered pass-through first, then direct pass-through.
    /// </summary>
    public static bool ExecuteScsiCommand(
        SafeFileHandle handle,
        byte[] cdb,
        byte cdbLength,
        byte dataIn,
        byte[]? dataBuffer,
        uint timeoutSeconds = 5)
    {
        if (ExecuteScsiPassThroughBuffered(handle, cdb, cdbLength, dataIn, dataBuffer, timeoutSeconds))
        {
            return true;
        }

        return ExecuteScsiPassThroughDirect(handle, cdb, cdbLength, dataIn, dataBuffer, timeoutSeconds);
    }

    /// <summary>
    /// Executes an ATA command via IOCTL_ATA_PASS_THROUGH.
    /// </summary>
    public static bool ExecuteAtaPassThrough(
        SafeFileHandle handle,
        byte command,
        byte features,
        byte sectorCount,
        byte lbaLow,
        byte lbaMid,
        byte lbaHigh,
        byte deviceHead,
        byte[] dataBuffer,
        uint timeoutSeconds = 3)
    {
        if (handle.IsInvalid || handle.IsClosed || dataBuffer == null || dataBuffer.Length < 512) return false;

        try
        {
            var aptwb = new ATA_PASS_THROUGH_EX_WITH_BUFFERS();
            aptwb.Apt.Length = (ushort)Marshal.SizeOf<ATA_PASS_THROUGH_EX>();
            aptwb.Apt.AtaFlags = ATA_FLAGS_DATA_IN;
            aptwb.Apt.DataTransferLength = 512;
            aptwb.Apt.TimeOutValue = timeoutSeconds;
            aptwb.Apt.DataBufferOffset = Marshal.OffsetOf<ATA_PASS_THROUGH_EX_WITH_BUFFERS>("Buf");
            aptwb.Apt.PreviousTaskFile = new byte[8];
            aptwb.Apt.CurrentTaskFile = new byte[8];
            aptwb.Apt.CurrentTaskFile[0] = features;
            aptwb.Apt.CurrentTaskFile[1] = sectorCount;
            aptwb.Apt.CurrentTaskFile[2] = lbaLow;
            aptwb.Apt.CurrentTaskFile[3] = lbaMid;
            aptwb.Apt.CurrentTaskFile[4] = lbaHigh;
            aptwb.Apt.CurrentTaskFile[5] = deviceHead;
            aptwb.Apt.CurrentTaskFile[6] = command;
            aptwb.Buf = new byte[4096];

            var totalSize = (uint)(aptwb.Apt.DataBufferOffset.ToInt64() + 512);
            var structSize = Marshal.SizeOf(aptwb);
            IntPtr pStruct = Marshal.AllocHGlobal(structSize);
            try
            {
                Marshal.StructureToPtr(aptwb, pStruct, false);
                if (!DeviceIoControl(handle, IOCTL_ATA_PASS_THROUGH, pStruct, totalSize, pStruct, totalSize, out _, IntPtr.Zero))
                {
                    return false;
                }

                var result = Marshal.PtrToStructure<ATA_PASS_THROUGH_EX_WITH_BUFFERS>(pStruct);
                Array.Copy(result.Buf, dataBuffer, 512);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(pStruct);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts SCSI-to-ATA Translation (SAT-12 and SAT-16) and ATA pass-through to query
    /// 512-byte ATA SMART attributes and thresholds on external USB / SCSI bridges.
    /// Supports bridge chips including Toshiba USB bridges, Realtek RTL9210, ASMedia ASM1153E/ASM235, JMicron, etc.
    /// </summary>
    public static bool QuerySatSmart(SafeFileHandle handle, out byte[]? smartData, out byte[]? smartThresholds)
    {
        smartData = null;
        smartThresholds = null;

        byte[] targets = [0xA0, 0x00, 0xB0];
        var dataBuffer = new byte[512];

        // 1. Try SAT-12 ATA PASS-THROUGH (12) - Opcode 0xA1 (Primary method in CrystalDiskInfo)
        foreach (var target in targets)
        {
            var cdb12 = new byte[12];
            cdb12[0] = 0xA1; // ATA PASS THROUGH (12)
            cdb12[1] = 0x08; // Protocol: 4 (PIO Data-In) << 1
            cdb12[2] = 0x0E; // CK_COND=0, T_DIR=1, BYTE_BLOCK=1, T_LENGTH=2
            cdb12[3] = 0xD0; // Feature: SMART READ DATA (0xD0)
            cdb12[4] = 1;    // Sector count = 1
            cdb12[5] = 1;    // LBA Low = 1
            cdb12[6] = 0x4F; // LBA Mid (SMART Cyl Low)
            cdb12[7] = 0xC2; // LBA High (SMART Cyl High)
            cdb12[8] = target;
            cdb12[9] = 0xB0; // Command: SMART (0xB0)

            Array.Clear(dataBuffer, 0, dataBuffer.Length);
            if (ExecuteScsiCommand(handle, cdb12, 12, SCSI_IOCTL_DATA_IN, dataBuffer) && IsValidSmartData(dataBuffer))
            {
                smartData = (byte[])dataBuffer.Clone();

                // Read thresholds using same target
                var cdb12Thresh = (byte[])cdb12.Clone();
                cdb12Thresh[3] = 0xD1; // Feature: SMART READ THRESHOLDS
                var threshBuffer = new byte[512];
                if (ExecuteScsiCommand(handle, cdb12Thresh, 12, SCSI_IOCTL_DATA_IN, threshBuffer))
                {
                    smartThresholds = threshBuffer;
                }
                return true;
            }
        }

        // 2. Try SAT-16 ATA PASS-THROUGH (16) - Opcode 0x85
        foreach (var target in targets)
        {
            var cdb16 = new byte[16];
            cdb16[0] = 0x85; // ATA PASS THROUGH (16)
            cdb16[1] = 0x08; // Protocol: PIO Data-In
            cdb16[2] = 0x0E; // CK_COND=0, T_DIR=1, BYTE_BLOCK=1, T_LENGTH=2
            cdb16[3] = 0x00;
            cdb16[4] = 0xD0; // Feature: SMART READ DATA
            cdb16[5] = 0x00;
            cdb16[6] = 1;    // Sector count = 1
            cdb16[7] = 0x00;
            cdb16[8] = 1;    // LBA Low = 1
            cdb16[9] = 0x00;
            cdb16[10] = 0x4F;// LBA Mid (0x4F)
            cdb16[11] = 0x00;
            cdb16[12] = 0xC2;// LBA High (0xC2)
            cdb16[13] = target;
            cdb16[14] = 0xB0;// Command: SMART
            cdb16[15] = 0x00;

            Array.Clear(dataBuffer, 0, dataBuffer.Length);
            if (ExecuteScsiCommand(handle, cdb16, 16, SCSI_IOCTL_DATA_IN, dataBuffer) && IsValidSmartData(dataBuffer))
            {
                smartData = (byte[])dataBuffer.Clone();

                var cdb16Thresh = (byte[])cdb16.Clone();
                cdb16Thresh[4] = 0xD1;
                var threshBuffer = new byte[512];
                if (ExecuteScsiCommand(handle, cdb16Thresh, 16, SCSI_IOCTL_DATA_IN, threshBuffer))
                {
                    smartThresholds = threshBuffer;
                }
                return true;
            }
        }

        // 3. Try Direct ATA PASS THROUGH (IOCTL_ATA_PASS_THROUGH)
        foreach (var target in targets)
        {
            Array.Clear(dataBuffer, 0, dataBuffer.Length);
            if (ExecuteAtaPassThrough(handle, 0xB0, 0xD0, 1, 1, 0x4F, 0xC2, target, dataBuffer) && IsValidSmartData(dataBuffer))
            {
                smartData = (byte[])dataBuffer.Clone();
                var threshBuffer = new byte[512];
                if (ExecuteAtaPassThrough(handle, 0xB0, 0xD1, 1, 1, 0x4F, 0xC2, target, threshBuffer))
                {
                    smartThresholds = threshBuffer;
                }
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Queries ATA IDENTIFY DEVICE (512 bytes) via SAT-12, SAT-16, and ATA pass-through.
    /// Recovers true vendor model, serial number, and RPM from behind external USB bridges.
    /// </summary>
    public static byte[]? QuerySatIdentify(SafeFileHandle handle)
    {
        byte[] targets = [0xA0, 0x00, 0xB0];
        var buffer = new byte[512];

        // 1. Try SAT-12 (Opcode 0xA1)
        foreach (var target in targets)
        {
            var cdb12 = new byte[12];
            cdb12[0] = 0xA1; // ATA PASS THROUGH (12)
            cdb12[1] = 0x08; // Protocol: PIO Data-In (4 << 1)
            cdb12[2] = 0x0E; // CK_COND=0, T_DIR=1, BYTE_BLOCK=1, T_LENGTH=2
            cdb12[3] = 0x00;
            cdb12[4] = 1;    // Sector count = 1
            cdb12[5] = 0x00;
            cdb12[6] = 0x00;
            cdb12[7] = 0x00;
            cdb12[8] = target;
            cdb12[9] = 0xEC; // Command: IDENTIFY DEVICE

            Array.Clear(buffer, 0, buffer.Length);
            if (ExecuteScsiCommand(handle, cdb12, 12, SCSI_IOCTL_DATA_IN, buffer) && IsValidIdentifyData(buffer))
            {
                return (byte[])buffer.Clone();
            }
        }

        // 2. Try SAT-16 (Opcode 0x85)
        foreach (var target in targets)
        {
            var cdb16 = new byte[16];
            cdb16[0] = 0x85;
            cdb16[1] = 0x08;
            cdb16[2] = 0x0E;
            cdb16[6] = 1;
            cdb16[13] = target;
            cdb16[14] = 0xEC;

            Array.Clear(buffer, 0, buffer.Length);
            if (ExecuteScsiCommand(handle, cdb16, 16, SCSI_IOCTL_DATA_IN, buffer) && IsValidIdentifyData(buffer))
            {
                return (byte[])buffer.Clone();
            }
        }

        // 3. Try Direct ATA PASS THROUGH
        foreach (var target in targets)
        {
            Array.Clear(buffer, 0, buffer.Length);
            if (ExecuteAtaPassThrough(handle, 0xEC, 0x00, 1, 0, 0, 0, target, buffer) && IsValidIdentifyData(buffer))
            {
                return (byte[])buffer.Clone();
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts SCSI LOG SENSE (Page 0x0D - Temperature) for external USB / SCSI enclosures.
    /// </summary>
    public static int? QueryScsiTemperature(SafeFileHandle handle)
    {
        var cdb = new byte[10];
        cdb[0] = 0x4D; // LOG SENSE (10)
        cdb[2] = 0x4D; // Page 0x0D | PC 0x40
        cdb[7] = 0;
        cdb[8] = 64;   // Allocation length 64 bytes

        var buffer = new byte[64];
        if (ExecuteScsiCommand(handle, cdb, 10, SCSI_IOCTL_DATA_IN, buffer))
        {
            if (buffer.Length >= 10 && buffer[0] == 0x0D)
            {
                var paramLen = buffer[7];
                if (paramLen >= 2 && buffer.Length >= 10)
                {
                    var temp = (int)buffer[9];
                    if (temp is > 0 and < 120) return temp;
                }
                else if (paramLen >= 1 && buffer.Length >= 9)
                {
                    var temp = (int)buffer[8];
                    if (temp is > 0 and < 120) return temp;
                }
            }
        }
        return null;
    }

    public static bool IsValidSmartData(byte[] buffer)
    {
        if (buffer == null || buffer.Length < 512) return false;
        // Verify attribute section (bytes 2..361) contains at least one recognized valid SMART attribute ID
        for (int i = 2; i + 11 < buffer.Length && i < 362; i += 12)
        {
            var id = buffer[i];
            if (id > 0 && id < 255)
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsValidIdentifyData(byte[] buffer)
    {
        if (buffer == null || buffer.Length < 512) return false;
        // Words 27-46 (bytes 54-93) contain drive model string
        int printable = 0;
        for (int i = 54; i < 94 && i < buffer.Length; i++)
        {
            var b = buffer[i];
            if (b is >= 32 and <= 126) printable++;
        }
        return printable >= 4;
    }

    /// <summary>
    /// Executes WMI queries in-process via COM reflection (SWbemLocator) with ZERO NuGet packages.
    /// Provides access to MSStorageDriver_FailurePredictData, MSStorageDriver_FailurePredictThresholds,
    /// MSStorageDriver_FailurePredictStatus, and Win32_DiskDrive.
    /// </summary>
    public static class ComWmi
    {
        public static List<Dictionary<string, object?>> Query(string namespaceName, string wql)
        {
            var results = new List<Dictionary<string, object?>>();
            try
            {
                var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
                if (locatorType == null) return results;

                var locator = Activator.CreateInstance(locatorType);
                if (locator == null) return results;

                var services = locatorType.InvokeMember("ConnectServer", BindingFlags.InvokeMethod, null, locator, new object[] { ".", namespaceName });
                if (services == null) return results;

                var servicesType = services.GetType();
                var col = servicesType.InvokeMember("ExecQuery", BindingFlags.InvokeMethod, null, services, new object[] { wql });
                if (col is not IEnumerable enumerable) return results;

                foreach (var item in enumerable)
                {
                    if (item == null) continue;
                    var itemType = item.GetType();
                    var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                    var propertiesObj = itemType.InvokeMember("Properties_", BindingFlags.GetProperty, null, item, null);
                    if (propertiesObj is IEnumerable propEnum)
                    {
                        foreach (var prop in propEnum)
                        {
                            if (prop == null) continue;
                            var pType = prop.GetType();
                            var name = (string?)pType.InvokeMember("Name", BindingFlags.GetProperty, null, prop, null);
                            var val = pType.InvokeMember("Value", BindingFlags.GetProperty, null, prop, null);

                            if (name != null)
                            {
                                if (val is Array arr)
                                {
                                    var bytes = new byte[arr.Length];
                                    for (var i = 0; i < arr.Length; i++)
                                    {
                                        bytes[i] = Convert.ToByte(arr.GetValue(i));
                                    }
                                    dict[name] = bytes;
                                }
                                else
                                {
                                    dict[name] = val;
                                }
                            }
                        }
                    }
                    results.Add(dict);
                }
            }
            catch
            {
                // WMI query error
            }
            return results;
        }
    }
}


