using System;

namespace DragonSwordTreasureRadar
{
    internal static class DpiAwareness
    {
        private static readonly IntPtr PerMonitorAwareV2 =
            new IntPtr(-4);

        public static void Enable()
        {
            try
            {
                if (NativeMethods.SetProcessDpiAwarenessContext(
                    PerMonitorAwareV2))
                {
                    return;
                }
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }

            try
            {
                const int ProcessPerMonitorDpiAware = 2;
                if (NativeMethods.SetProcessDpiAwareness(
                    ProcessPerMonitorDpiAware) == 0)
                {
                    return;
                }
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }

            try
            {
                NativeMethods.SetProcessDPIAware();
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }
        }
    }
}
