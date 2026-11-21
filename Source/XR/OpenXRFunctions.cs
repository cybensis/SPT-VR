using System;
using System.Runtime.InteropServices;

namespace TarkovVR.Source.XR
{
    // Looks up OpenXR functions through the features' xrGetInstanceProcAddr. All our features must share this one delegate type
    // for it: OpenXRHapticFrequency puts a managed delegate at the top of that chain, and Mono turns a pointer to a managed
    // delegate back into that same delegate whatever type is asked for. Any other type fails the cast and stops OpenXR from starting.
    internal static class OpenXRFunctions
    {
        private const int XR_SUCCESS = 0;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int GetInstanceProcAddrDelegate(ulong instance, IntPtr name, IntPtr function);

        // Returns IntPtr.Zero when the runtime doesn't have the function
        internal static IntPtr Find(IntPtr getInstanceProcAddr, ulong instance, string functionName, out int result)
        {
            GetInstanceProcAddrDelegate lookUp = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrDelegate>(getInstanceProcAddr);
            IntPtr namePointer = Marshal.StringToHGlobalAnsi(functionName);
            IntPtr resultPointer = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(resultPointer, IntPtr.Zero);
                result = lookUp(instance, namePointer, resultPointer);
                return result == XR_SUCCESS ? Marshal.ReadIntPtr(resultPointer) : IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(namePointer);
                Marshal.FreeHGlobal(resultPointer);
            }
        }
    }
}
