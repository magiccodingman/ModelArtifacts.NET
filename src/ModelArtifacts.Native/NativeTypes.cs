using System.Runtime.InteropServices;

namespace ModelArtifacts.Native;

internal enum MaStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    InvalidHandle = 2,
    SourceError = 3,
    DownloadError = 4,
    IoError = 5,
    Cancelled = 6,
    OutOfMemory = 7,
    InternalError = 255
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MaManagerOptions
{
    public uint StructSize;
    public uint AbiVersion;
    public byte* ConfigJson;
    public nuint ConfigJsonLength;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MaBuffer
{
    public byte* Data;
    public nuint Length;
}
