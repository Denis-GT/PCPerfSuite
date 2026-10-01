using CommunityToolkit.Mvvm.ComponentModel;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Hardware.Cpu;
using PCPerfSuite.Core.Hardware.Cpu.CoreParking;

namespace PCPerfSuite.App.ViewModels;

/// <summary>
/// Un réglage du parking des cœurs, tel qu'affiché : un pourcentage ou une liste, et deux valeurs quand la machine a une
/// batterie. Chaque modification passe par <see cref="CoreParkingService"/> (origine notée avant la première écriture),
/// une fois le curseur posé, puis est relue : si Windows retient autre chose, l'utilisateur le voit tout de suite.
/// </summary>
public sealed partial class CoreParkingSettingViewModel : ObservableObject
{
    private readonly CoreParkingService _service;
    private readonly CoreParkingSetting _setting;
    private readonly Debouncer _debounce;
    private readonly Action<string> _report;
    private bool _suppressWrite;

    public CoreParkingSettingViewModel(
        CoreParkingService service, CoreParkingSetting setting, CoreParkingValue value, Debouncer debounce, Action<string> report)
    {
        _service = service;
        _setting = setting;
        _debounce = debounce;
        _report = report;
        ShowBattery = service.HasBattery;
        SetSilently(value);
        UpdateOrigin(service.OriginValues());
    }

    public CoreParkingSetting Setting => _setting;
    public string Label => _setting.LabelFor(_service.IsHybrid);
    public string Alias => _setting.Alias;
    public string Description => _setting.Description;
    public IReadOnlyList<CpuPowerChoice>? Choices => _setting.Choices;
    public bool IsChoice => _setting.Choices is not null;
    public bool IsNumeric => _setting.Choices is null;
    public bool ShowBattery { get; }
    public double Min => _setting.Min;
    public double Max => _setting.Max;

    [ObservableProperty] private double acValue;
    [ObservableProperty] private double batteryValue;
    [ObservableProperty] private CpuPowerChoice? acChoice;
    [ObservableProperty] private CpuPowerChoice? batteryChoice;

    /// <summary>« Origine : 4 % » une fois que l'app a touché à ce réglage, null sinon.</summary>
    [ObservableProperty] private string? originText;

    /// <summary>Repose les valeurs lues dans le plan, sans écrire : relecture à l'ouverture, ou après un préréglage.</summary>
    public void SetSilently(CoreParkingValue value)
    {
        _suppressWrite = true;
        AcValue = value.Ac;
        BatteryValue = value.Dc;
        AcChoice = _setting.Choices?.FirstOrDefault(c => c.Value == value.Ac);
        BatteryChoice = _setting.Choices?.FirstOrDefault(c => c.Value == value.Dc);
        _suppressWrite = false;
    }

    /// <param name="origins">Origines lues une fois pour tous les réglages (<see cref="CoreParkingService.OriginValues"/>).</param>
    public void UpdateOrigin(IReadOnlyDictionary<string, uint> origins)
        => OriginText = CoreParkingService.Origin(origins, _setting) is { } origin
            ? ShowBattery
                ? $"Origine : {_setting.Format(origin.Ac)} sur secteur, {_setting.Format(origin.Dc)} sur batterie."
                : $"Origine : {_setting.Format(origin.Ac)}."
            : null;

    partial void OnAcValueChanged(double value) => Schedule();

    partial void OnBatteryValueChanged(double value) => Schedule();

    partial void OnAcChoiceChanged(CpuPowerChoice? value)
    {
        if (value is not null && !_suppressWrite) AcValue = value.Value;
    }

    partial void OnBatteryChoiceChanged(CpuPowerChoice? value)
    {
        if (value is not null && !_suppressWrite) BatteryValue = value.Value;
    }

    private void Schedule()
    {
        if (_suppressWrite) return;

        // Écrire réactive le plan d'alimentation entier : on attend que le curseur se pose.
        _debounce.Schedule(_setting.Id, WriteNow);
    }

    private void WriteNow()
    {
        uint ac = (uint)Math.Round(AcValue);
        uint battery = ShowBattery ? (uint)Math.Round(BatteryValue) : ac;

        CoreParkingWriteResult result = _service.Write([new CoreParkingTarget(_setting, new CoreParkingValue(ac, battery))]);
        CoreParkingApplied? applied = result.Applied.FirstOrDefault();
        if (applied?.Applied is { } now) SetSilently(now);
        UpdateOrigin(_service.OriginValues());

        string message = result.Error is { } error ? error
            : applied is { Matches: false, Applied: { } kept }
                ? $"« {Label} » : Windows a retenu {Describe(kept)} au lieu de {Describe(applied.Requested)} (réglage piloté par le fabricant du PC ?)."
                : $"« {Label} » : {Describe(new CoreParkingValue(ac, battery))}.";
        _report(result.Note is { } note ? $"{note} {message}" : message);
    }

    private string Describe(CoreParkingValue value)
        => ShowBattery
            ? $"{_setting.Format(value.Ac)} sur secteur, {_setting.Format(value.Dc)} sur batterie"
            : _setting.Format(value.Ac);
}
