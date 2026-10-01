using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PCPerfSuite.Core.PowerSettings.Animations;

/// <summary>Lecture et écriture d'un réglage d'animation, isolées pour être simulées dans les tests.</summary>
public interface IAnimationSettingsAccess
{
    /// <summary>Valeur actuelle, ou null si Windows ne l'a pas rendue. Ne lève pas.</summary>
    bool? Read(AnimationSetting setting);

    /// <summary>Écrit la valeur et prévient les fenêtres. Lève <see cref="InvalidOperationException"/>, avec un
    /// message affichable, si Windows refuse : c'est une action demandée, un échec muet laisserait croire qu'elle a
    /// pris.</summary>
    void Write(AnimationSetting setting, bool enabled);
}

/// <summary>
/// Accès réel : SystemParametersInfoW avec SPIF_UPDATEINIFILE|SPIF_SENDCHANGE (Windows écrit le profil et diffuse
/// WM_SETTINGCHANGE lui-même), jamais d'écriture de UserPreferencesMask, dont la disposition n'est pas documentée. Les
/// deux réglages sans appel SPI passent par un DWORD de HKCU suivi d'un WM_SETTINGCHANGE.
///
/// À appeler hors du thread d'interface : la diffusion attend chaque fenêtre de premier niveau (500 ms au plus pour
/// une fenêtre bloquée), y compris celles de PCPerfSuite.
/// </summary>
public sealed class Win32AnimationSettingsAccess : IAnimationSettingsAccess
{
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    private static readonly IntPtr HwndBroadcast = new(0xFFFF);
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint BroadcastTimeoutMs = 500;

    public bool? Read(AnimationSetting setting)
    {
        try
        {
            switch (setting.Kind)
            {
                case AnimationSettingKind.SpiBool:
                case AnimationSettingKind.SpiDragFullWindows:
                    int value = 0;
                    return SystemParametersInfo(setting.GetAction, 0, ref value, 0) ? value != 0 : null;

                case AnimationSettingKind.SpiAnimationInfo:
                    var info = new AnimationInfo { CbSize = (uint)Marshal.SizeOf<AnimationInfo>() };
                    return SystemParametersInfo(setting.GetAction, info.CbSize, ref info, 0) ? info.MinAnimate != 0 : null;

                case AnimationSettingKind.UserRegistry:
                    // Valeur absente : Windows applique l'effet tant que personne n'y a touché.
                    int? dword = RegistryHelper.ReadDword(RegistryHive.CurrentUser, setting.RegistrySubKey!, setting.RegistryValue!, defaultValue: 1);
                    return dword is { } d ? d != 0 : null;

                default:
                    return null;
            }
        }
        catch
        {
            // Best-effort : une lecture refusée (stratégie de groupe) se dit « illisible », pas « désactivé ».
            return null;
        }
    }

    public void Write(AnimationSetting setting, bool enabled)
    {
        const uint flags = SpifUpdateIniFile | SpifSendChange;
        bool ok;

        switch (setting.Kind)
        {
            case AnimationSettingKind.SpiBool:
                ok = SystemParametersInfo(setting.SetAction, 0, new IntPtr(enabled ? 1 : 0), flags);
                break;

            case AnimationSettingKind.SpiDragFullWindows:
                ok = SystemParametersInfo(setting.SetAction, enabled ? 1u : 0u, IntPtr.Zero, flags);
                break;

            case AnimationSettingKind.SpiAnimationInfo:
                var info = new AnimationInfo { CbSize = (uint)Marshal.SizeOf<AnimationInfo>(), MinAnimate = enabled ? 1 : 0 };
                ok = SystemParametersInfo(setting.SetAction, info.CbSize, ref info, flags);
                break;

            case AnimationSettingKind.UserRegistry:
                RegistryHelper.WriteDword(RegistryHive.CurrentUser, setting.RegistrySubKey!, setting.RegistryValue!, enabled ? 1 : 0);
                Broadcast(setting.ChangeArea);
                return;

            default:
                throw new InvalidOperationException($"Type de réglage inconnu : {setting.Kind}.");
        }

        if (!ok)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Windows a refusé de modifier « {setting.Name} » (erreur {error}). Une stratégie de groupe verrouille "
                + "peut-être ce réglage.");
        }
    }

    /// <summary>Best-effort : la valeur est déjà écrite, une diffusion qui échoue la laisse prendre effet plus tard.</summary>
    private static void Broadcast(string? area)
    {
        try
        {
            SendMessageTimeout(HwndBroadcast, WmSettingChange, UIntPtr.Zero, area, SmtoAbortIfHung, BroadcastTimeoutMs, out _);
        }
        catch { /* voir ci-dessus */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo
    {
        public uint CbSize;
        public int MinAnimate;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint uiParam, ref int pvParam, uint winIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint uiParam, ref AnimationInfo pvParam, uint winIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint uiParam, IntPtr pvParam, uint winIni);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string? lParam, uint flags,
        uint timeoutMs, out UIntPtr result);
}
