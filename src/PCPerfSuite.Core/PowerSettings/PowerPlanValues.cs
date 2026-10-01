using System.Runtime.InteropServices;

namespace PCPerfSuite.Core.PowerSettings;

/// <summary>Une valeur à écrire dans un plan d'alimentation : un réglage, sur secteur et sur batterie.</summary>
public readonly record struct PowerValueWrite(Guid Setting, uint Ac, uint Dc);

/// <summary>Lecture et écriture des réglages d'un plan d'alimentation, abstraites pour les tests.</summary>
public interface IPowerPlanValues
{
    /// <summary>Plan actif, null si Windows ne le dit pas.</summary>
    Guid? ActiveScheme();

    /// <summary>Nom du plan dans la langue de Windows (« Équilibré »), null s'il est illisible ou s'il n'existe plus.</summary>
    string? FriendlyName(Guid scheme);

    /// <summary>Valeurs secteur et batterie d'un réglage. False si ce Windows ne connaît pas ce réglage, ou si le plan
    /// n'existe plus.</summary>
    bool TryRead(Guid scheme, Guid subGroup, Guid setting, out uint ac, out uint dc);

    /// <summary>Écrit toutes les valeurs, puis réactive le plan une seule fois s'il est actif. False dès qu'une
    /// écriture est refusée (app sans administrateur, réglage inconnu) : les précédentes restent écrites.</summary>
    bool TryWrite(Guid scheme, Guid subGroup, IReadOnlyList<PowerValueWrite> writes);
}

/// <summary>
/// Réglages des plans d'alimentation par l'API powrprof, plutôt que par powercfg.exe : elle voit aussi les réglages
/// masqués par défaut (le parking des cœurs en fait partie), et ne dépend pas de la langue de Windows. Best-effort
/// (règle 2) : rien ne lève, un refus rend false.
///
/// Réactiver le plan (PowerSetActiveScheme) est ce qui fait appliquer la valeur tout de suite, et c'est l'écriture la
/// plus lourde de l'app : une rafale de valeurs ne le fait qu'une fois, à la fin.
/// </summary>
public sealed class PowerPlanValues : IPowerPlanValues
{
    public static PowerPlanValues Instance { get; } = new();

    public Guid? ActiveScheme()
    {
        IntPtr scheme = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out scheme) != 0 || scheme == IntPtr.Zero) return null;
            return Marshal.PtrToStructure<Guid>(scheme);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (scheme != IntPtr.Zero) LocalFree(scheme);
        }
    }

    public string? FriendlyName(Guid scheme)
    {
        try
        {
            uint size = 0;
            if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) != 0 || size == 0)
            {
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return null;
                string? name = Marshal.PtrToStringUni(buffer)?.Trim();
                return string.IsNullOrEmpty(name) ? null : name;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
    }

    public bool TryRead(Guid scheme, Guid subGroup, Guid setting, out uint ac, out uint dc)
    {
        ac = 0;
        dc = 0;
        try
        {
            return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out ac) == 0
                   && PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, out dc) == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool TryWrite(Guid scheme, Guid subGroup, IReadOnlyList<PowerValueWrite> writes)
    {
        if (writes.Count == 0) return true;

        try
        {
            foreach (PowerValueWrite write in writes)
            {
                Guid setting = write.Setting;
                if (PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, write.Ac) != 0) return false;
                if (PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subGroup, ref setting, write.Dc) != 0) return false;
            }

            // Sans cette réactivation, Windows enregistre la valeur mais ne l'applique qu'au prochain changement de
            // plan. Un plan inactif n'a rien à réappliquer : sa valeur servira quand on le choisira.
            return ActiveScheme() != scheme || PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid, IntPtr settingGuid, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
