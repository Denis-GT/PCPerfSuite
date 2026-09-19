using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.Hardware.Gpu;

/// <summary>
/// Accès minimal à ADLX (AMD Device Library eXtra), l'API qu'AMD livre avec le pilote Adrenalin
/// (amdadlx64.dll dans System32) et qui remplace ADL pour le réglage des Radeon.
///
/// ADLX n'expose que deux fonctions C ; tout le reste passe par des interfaces façon COM (un pointeur
/// vers une table de fonctions). On appelle directement les emplacements de ces tables : leur ordre
/// est recopié des headers C du SDK officiel (GPUOpen-LibrariesAndSDKs/ADLX, SDK/Include, structures
/// "…Vtbl"), ce qui évite d'embarquer une DLL native de liaison. Les tables ne font que s'allonger
/// d'une version à l'autre (les nouveautés arrivent dans des interfaces "…1", "…2_1"), donc ces
/// emplacements restent valables sur les pilotes plus récents.
/// </summary>
internal static unsafe class AdlxNative
{
    public const int Ok = 0;
    private const int AlreadyEnabled = 1;
    private const int AlreadyInitialized = 2;

    public const int GpuTypeDiscrete = 2;

    public static bool Succeeded(int result) => result is Ok or AlreadyEnabled or AlreadyInitialized;

    [StructLayout(LayoutKind.Sequential)]
    public struct IntRange
    {
        public int Min;
        public int Max;
        public int Step;
    }

    // --- Emplacements des tables de fonctions (index dans la vtable) ---------------------------

    /// <summary>IADLXInterface : base de toutes les interfaces sauf IADLXSystem.</summary>
    public static class Iface
    {
        public const int Release = 1;
        public const int QueryInterface = 2;
    }

    /// <summary>IADLXSystem (pas de Acquire/Release : l'objet appartient à ADLX).</summary>
    public static class SystemIface
    {
        public const int GetGPUs = 1;
        public const int GetGPUTuningServices = 8;
    }

    public static class GpuList
    {
        public const int Size = 3;
        public const int AtGpu = 11;
    }

    public static class Gpu
    {
        public const int Type = 5;
        public const int Name = 7;
    }

    public static class TuningServices
    {
        public const int ResetToFactory = 5;
        public const int IsSupportedManualGfxTuning = 8;
        public const int IsSupportedManualVramTuning = 9;
        public const int IsSupportedManualFanTuning = 10;
        public const int IsSupportedManualPowerTuning = 11;
        public const int GetManualGfxTuning = 14;
        public const int GetManualVramTuning = 15;
        public const int GetManualFanTuning = 16;
        public const int GetManualPowerTuning = 17;
    }

    /// <summary>IADLXManualGraphicsTuning2 (RDNA) et son extension 2_1 (valeurs par défaut).</summary>
    public static class GfxTuning2
    {
        public const int GetMaxFrequencyRange = 6;
        public const int GetMaxFrequency = 7;
        public const int SetMaxFrequency = 8;
        public const int GetVoltageRange = 9;
        public const int GetVoltage = 10;
        public const int SetVoltage = 11;
        public const int GetMaxFrequencyDefault = 13;
        public const int GetVoltageDefault = 14;
    }

    /// <summary>IADLXManualVRAMTuning2 et son extension 2_1.</summary>
    public static class VramTuning2
    {
        public const int GetMaxFrequencyRange = 7;
        public const int GetMaxFrequency = 8;
        public const int SetMaxFrequency = 9;
        public const int GetMaxFrequencyDefault = 10;
    }

    /// <summary>IADLXManualPowerTuning et son extension 1.</summary>
    public static class PowerTuning
    {
        public const int GetPowerLimitRange = 3;
        public const int GetPowerLimit = 4;
        public const int SetPowerLimit = 5;
        public const int GetPowerLimitDefault = 10;
    }

    public static class FanTuning
    {
        public const int GetFanTuningRanges = 3;
        public const int GetFanTuningStates = 4;
        public const int IsValidFanTuningStates = 6;
        public const int SetFanTuningStates = 7;
    }

    public static class FanStateList
    {
        public const int Size = 3;
        public const int AtState = 11;
    }

    public static class FanState
    {
        public const int GetFanSpeed = 3;
        public const int SetFanSpeed = 4;
        public const int GetTemperature = 5;
    }

    // --- Chargement ------------------------------------------------------------------------------

    private static IntPtr _library;
    private static delegate* unmanaged[Cdecl]<int> _terminate;

    /// <summary>Charge amdadlx64.dll depuis System32 (jamais depuis le dossier de l'app, pour ne pas
    /// charger une DLL déposée à côté) et initialise ADLX. Retourne le pointeur IADLXSystem, ou zéro
    /// si le pilote AMD n'est pas installé ou refuse l'initialisation.</summary>
    public static IntPtr TryInitialize()
    {
        string dll = Environment.Is64BitProcess ? "amdadlx64.dll" : "amdadlx32.dll";
        if (!NativeLibrary.TryLoad(dll, typeof(AdlxNative).Assembly, DllImportSearchPath.System32, out _library))
        {
            return IntPtr.Zero;
        }

        if (!NativeLibrary.TryGetExport(_library, "ADLXQueryFullVersion", out IntPtr queryVersion)
            || !NativeLibrary.TryGetExport(_library, "ADLXInitialize", out IntPtr initialize)
            || !NativeLibrary.TryGetExport(_library, "ADLXTerminate", out IntPtr terminate))
        {
            Unload();
            return IntPtr.Zero;
        }

        ulong version = 0;
        if (!Succeeded(((delegate* unmanaged[Cdecl]<ulong*, int>)queryVersion)(&version)))
        {
            Unload();
            return IntPtr.Zero;
        }

        // On demande la version que le pilote annonce lui-même : c'est celle qu'il est certain
        // d'accepter, et les emplacements utilisés ici existent depuis la toute première.
        IntPtr system = IntPtr.Zero;
        int result = ((delegate* unmanaged[Cdecl]<ulong, IntPtr*, int>)initialize)(version, &system);
        if (!Succeeded(result) || system == IntPtr.Zero)
        {
            Unload();
            return IntPtr.Zero;
        }

        _terminate = (delegate* unmanaged[Cdecl]<int>)terminate;
        return system;
    }

    public static void Terminate()
    {
        if (_terminate is not null)
        {
            _terminate();
            _terminate = null;
        }

        Unload();
    }

    private static void Unload()
    {
        if (_library == IntPtr.Zero) return;
        NativeLibrary.Free(_library);
        _library = IntPtr.Zero;
    }

    // --- Appels par emplacement ------------------------------------------------------------------

    private static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    public static void Release(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(obj, Iface.Release))(obj);
    }

    /// <summary>Interface étendue (ex. "IADLXManualGraphicsTuning2_1"), ou zéro si le pilote ne la
    /// fournit pas. L'objet renvoyé porte sa propre référence, à libérer.</summary>
    public static IntPtr QueryInterface(IntPtr obj, string interfaceId)
    {
        if (obj == IntPtr.Zero) return IntPtr.Zero;

        IntPtr result = IntPtr.Zero;
        fixed (char* iid = interfaceId)
        {
            int status = ((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)Slot(obj, Iface.QueryInterface))(
                obj, iid, &result);
            return Succeeded(status) ? result : IntPtr.Zero;
        }
    }

    /// <summary>f(this, T** out) — getters d'objets (GetGPUs, GetGPUTuningServices, GetFanTuningStates…).</summary>
    public static IntPtr GetObject(IntPtr obj, int slot)
    {
        IntPtr result = IntPtr.Zero;
        int status = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(obj, slot))(obj, &result);
        return Succeeded(status) ? result : IntPtr.Zero;
    }

    /// <summary>f(this, gpu, T** out) — services de réglage d'un GPU donné.</summary>
    public static IntPtr GetObjectForGpu(IntPtr obj, int slot, IntPtr gpu)
    {
        IntPtr result = IntPtr.Zero;
        int status = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(obj, slot))(obj, gpu, &result);
        return Succeeded(status) ? result : IntPtr.Zero;
    }

    /// <summary>f(this, gpu, adlx_bool* out) — tests "IsSupported…".</summary>
    public static bool IsSupportedForGpu(IntPtr obj, int slot, IntPtr gpu)
    {
        byte supported = 0;
        int status = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, int>)Slot(obj, slot))(obj, gpu, &supported);
        return Succeeded(status) && supported != 0;
    }

    /// <summary>f(this, gpu) — ResetToFactory.</summary>
    public static bool CallForGpu(IntPtr obj, int slot, IntPtr gpu)
        => Succeeded(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(obj, slot))(obj, gpu));

    public static bool TryGetInt(IntPtr obj, int slot, out int value)
    {
        int v = 0;
        int status = ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(obj, slot))(obj, &v);
        value = v;
        return Succeeded(status);
    }

    public static bool TrySetInt(IntPtr obj, int slot, int value)
        => Succeeded(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(obj, slot))(obj, value));

    public static bool TryGetRange(IntPtr obj, int slot, out IntRange range)
    {
        IntRange r = default;
        int status = ((delegate* unmanaged[Stdcall]<IntPtr, IntRange*, int>)Slot(obj, slot))(obj, &r);
        range = r;
        return Succeeded(status) && r.Max > r.Min;
    }

    /// <summary>GetFanTuningRanges(this, speedRange*, temperatureRange*).</summary>
    public static bool TryGetTwoRanges(IntPtr obj, int slot, out IntRange first, out IntRange second)
    {
        IntRange a = default, b = default;
        int status = ((delegate* unmanaged[Stdcall]<IntPtr, IntRange*, IntRange*, int>)Slot(obj, slot))(obj, &a, &b);
        first = a;
        second = b;
        return Succeeded(status);
    }

    /// <summary>Size() des listes ADLX : renvoie directement un adlx_uint, pas un ADLX_RESULT.</summary>
    public static uint GetSize(IntPtr list, int slot)
        => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(list, slot))(list);

    /// <summary>At_…(this, index, T** out) — élément d'une liste, avec sa propre référence.</summary>
    public static IntPtr GetAt(IntPtr list, int slot, uint index)
    {
        IntPtr result = IntPtr.Zero;
        int status = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(list, slot))(list, index, &result);
        return Succeeded(status) ? result : IntPtr.Zero;
    }

    /// <summary>IsValidFanTuningStates(this, list, adlx_int* errorIndex).</summary>
    public static bool IsValidWithList(IntPtr obj, int slot, IntPtr list)
    {
        int errorIndex = 0;
        return Succeeded(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, int>)Slot(obj, slot))(obj, list, &errorIndex));
    }

    /// <summary>SetFanTuningStates(this, list).</summary>
    public static bool CallWithObject(IntPtr obj, int slot, IntPtr arg)
        => Succeeded(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(obj, slot))(obj, arg));

    /// <summary>Name(this, const char** out) : chaîne possédée par ADLX, copiée aussitôt.</summary>
    public static string? GetString(IntPtr obj, int slot)
    {
        sbyte* value = null;
        int status = ((delegate* unmanaged[Stdcall]<IntPtr, sbyte**, int>)Slot(obj, slot))(obj, &value);
        return Succeeded(status) && value is not null ? Marshal.PtrToStringUTF8((IntPtr)value) : null;
    }
}
