using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Rewind
{
    /// <summary>One monitor as the graphics card sees it. Immutable.</summary>
    internal sealed class DisplayOutput
    {
        public readonly int Index;
        public readonly string DeviceName;
        public readonly bool Attached;
        public readonly bool Primary;
        public readonly int Left, Top, Width, Height;

        public DisplayOutput(int index, string deviceName, bool attached, bool primary, int left, int top, int width, int height)
        {
            Index = index;
            DeviceName = deviceName;
            Attached = attached;
            Primary = primary;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }

        public override string ToString()
        {
            return string.Format("{0}: {1} {2}x{3} at {4},{5}{6}{7}", Index, DeviceName, Width, Height, Left, Top,
                Primary ? " (primary)" : "", Attached ? "" : " (not attached)");
        }
    }

    /// <summary>
    /// Asks DXGI which monitors hang off the first graphics adapter, in the same order ffmpeg's
    /// ddagrab counts them (output_idx). "primary" is matched by device name against Windows'
    /// primary screen, so it survives monitors being re-plugged and renumbered.
    /// </summary>
    internal static class Dxgi
    {
        private const int ErrorNotFound = unchecked((int)0x887A0002);

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

        public static IList<DisplayOutput> ListOutputs()
        {
            object factoryObject;
            var iid = typeof(IDXGIFactory1).GUID;
            var hr = CreateDXGIFactory1(ref iid, out factoryObject);
            if (hr < 0) throw new COMException("CreateDXGIFactory1 failed", hr);
            var factory = (IDXGIFactory1)factoryObject;

            IDXGIAdapter1 adapter = null;
            try
            {
                hr = factory.EnumAdapters1(0, out adapter);
                if (hr < 0) throw new COMException("No graphics adapter found", hr);

                var primaryName = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.DeviceName : "";
                var outputs = new List<DisplayOutput>();
                for (uint i = 0; ; i++)
                {
                    IDXGIOutput output;
                    hr = adapter.EnumOutputs(i, out output);
                    if (hr == ErrorNotFound) break;
                    if (hr < 0) throw new COMException("EnumOutputs failed", hr);
                    try
                    {
                        DxgiOutputDesc desc;
                        hr = output.GetDesc(out desc);
                        if (hr < 0) throw new COMException("IDXGIOutput.GetDesc failed", hr);
                        outputs.Add(new DisplayOutput((int)i, desc.DeviceName, desc.AttachedToDesktop != 0,
                            string.Equals(desc.DeviceName, primaryName, StringComparison.OrdinalIgnoreCase),
                            desc.Left, desc.Top, desc.Right - desc.Left, desc.Bottom - desc.Top));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(output);
                    }
                }
                return outputs;
            }
            finally
            {
                if (adapter != null) Marshal.ReleaseComObject(adapter);
                Marshal.ReleaseComObject(factory);
            }
        }

        /// <summary>Turns the config's monitor setting into a ddagrab output_idx, or explains why it can't.</summary>
        public static int ResolveOutputIndex(string monitorSetting)
        {
            var outputs = ListOutputs();
            if (outputs.Count == 0) throw new InvalidOperationException("The graphics card reports no monitors.");

            if (monitorSetting == "primary")
            {
                foreach (var output in outputs) if (output.Primary && output.Attached) return output.Index;
                throw new InvalidOperationException("Couldn't find the primary monitor on the graphics card. Outputs:\n" + Describe(outputs));
            }

            int wanted;
            if (!int.TryParse(monitorSetting, out wanted)) throw new InvalidOperationException("monitor setting isn't a number: " + monitorSetting);
            foreach (var output in outputs)
            {
                if (output.Index != wanted) continue;
                if (!output.Attached) throw new InvalidOperationException("monitor=" + wanted + " isn't showing a desktop right now.\n" + Describe(outputs));
                return wanted;
            }
            throw new InvalidOperationException("monitor=" + wanted + " doesn't exist. Outputs:\n" + Describe(outputs));
        }

        public static string Describe(IList<DisplayOutput> outputs)
        {
            var sb = new StringBuilder();
            foreach (var output in outputs) sb.AppendLine(output.ToString());
            return sb.ToString().TrimEnd();
        }
    }

    // ---- the slice of DXGI needed to list outputs; vtable order matters, names don't ----

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumAdapters(uint index, out IntPtr adapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr hwnd, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr hwnd);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
        [PreserveSig] int GetDesc(IntPtr desc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid name, out long version);
        [PreserveSig] int GetDesc1(IntPtr desc);
    }

    [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIOutput
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int GetDesc(out DxgiOutputDesc desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DxgiOutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }
}
