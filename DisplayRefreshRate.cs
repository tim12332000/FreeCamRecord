using System.Runtime.InteropServices;

namespace FreeCamRecord;

internal static class DisplayRefreshRate
{
    // Read the active mode of the monitor containing most of the recording rectangle.
    // This does not change Windows display settings or select a different monitor's maximum.
    internal static int ForArea(Rectangle area)
    {
        var screen = Screen.FromRectangle(area);
        var mode = new DeviceMode { Size = (ushort)Marshal.SizeOf<DeviceMode>() };
        if (!EnumDisplaySettingsEx(screen.DeviceName, -1, ref mode, 0) || mode.Frequency is < 10 or > 1000)
            return 60;
        return (int)mode.Frequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string device, int modeNumber, ref DeviceMode mode, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPixel, Width, Height, DisplayFlags, Frequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
}
