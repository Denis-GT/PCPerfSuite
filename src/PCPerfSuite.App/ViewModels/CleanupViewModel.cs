using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Cache;

namespace PCPerfSuite.App.ViewModels;

public sealed partial class CacheItemViewModel : ObservableObject
{
    public CacheCategory Category { get; }

    [ObservableProperty] private long sizeBytes;
    [ObservableProperty] private bool exists;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? lastResultMessage;
    [ObservableProperty] private bool lastResultFailed;

    /// <summary>Part de ce cache dans l'espace récupérable total, de 0 à 100.</summary>
    [ObservableProperty] private double sharePercent;

    public string Name => Category.Name;
    public string Description => Category.Description;
    public bool RequiresAdmin => Category.RequiresAdmin;

    /// <summary>Glyphe Segoe Fluent Icons illustrant la catégorie.</summary>
    public string Icon => char.ConvertFromUtf32(Category.Id switch
    {
        "temp-user" or "temp-system" => 0xE8B7,
        "nvidia-shader-cache" or "amd-shader-cache" or "intel-shader-cache" or "directx-shader-cache" => 0xE950,
        "steam-shader-cache" => 0xE7FC,
        "windows-update-cache" => 0xE895,
        "prefetch" => 0xE945,
        "wer" => 0xE7BA,
        "thumbnail-cache" => 0xE8B9,
        "delivery-optimization" => 0xE896,
        _ => 0xE74D,
    });
    public string SizeDisplay => IsBusy && SizeBytes == 0 ? "Analyse…" : ByteFormatter.Format(SizeBytes);

    private IReadOnlyList<string> _existingPaths = Array.Empty<string>();
    public string? PrimaryPath => _existingPaths.FirstOrDefault();

    public CacheItemViewModel(CacheCategory category) => Category = category;

    public void ApplyScan(CacheCategoryResult result)
    {
        SizeBytes = result.SizeBytes;
        Exists = result.Exists;
        _existingPaths = result.ExistingPaths;
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(PrimaryPath));
    }

    partial void OnSizeBytesChanged(long value) => OnPropertyChanged(nameof(SizeDisplay));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(SizeDisplay));
}

public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly CacheCleanerService _service = new();

    public ObservableCollectionEx<CacheItemViewModel> Items { get; } = new();

    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private string totalReclaimableDisplay = "0 o";

    public CleanupViewModel()
    {
        foreach (CacheCategory category in KnownCaches.All)
        {
            Items.Add(new CacheItemViewModel(category));
        }
        _ = ScanAllAsync();
    }

    [RelayCommand]
    private async Task ScanAllAsync()
    {
        IsScanning = true;
        try
        {
            foreach (CacheItemViewModel item in Items)
            {
                item.IsBusy = true;
                CacheCategoryResult result = await _service.ScanAsync(item.Category);
                item.ApplyScan(result);
                item.IsBusy = false;
                UpdateTotal();
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task CleanAsync(CacheItemViewModel item)
    {
        item.IsBusy = true;
        try
        {
            CacheCleanResult result = await _service.CleanAsync(item.Category);
            item.LastResultFailed = false;
            item.LastResultMessage = result.FilesSkipped > 0
                ? $"{ByteFormatter.Format(result.BytesFreed)} libérés, {result.FilesSkipped} fichier(s) verrouillé(s) ignoré(s)."
                : $"{ByteFormatter.Format(result.BytesFreed)} libérés.";

            CacheCategoryResult rescanned = await _service.ScanAsync(item.Category);
            item.ApplyScan(rescanned);
        }
        catch (Exception ex)
        {
            item.LastResultFailed = true;
            item.LastResultMessage = $"Échec : {ex.Message}";
        }
        finally
        {
            item.IsBusy = false;
            UpdateTotal();
        }
    }

    [RelayCommand]
    private async Task CleanAllAsync()
    {
        foreach (CacheItemViewModel item in Items.Where(i => i.Exists).ToList())
        {
            await CleanAsync(item);
        }
    }

    [RelayCommand]
    private void OpenFolder(CacheItemViewModel item)
    {
        if (item.PrimaryPath is { } path)
        {
            CacheCleanerService.OpenFolder(path);
        }
    }

    [RelayCommand]
    private void EmptyRecycleBin()
    {
        CacheCleanerService.EmptyRecycleBin();
    }

    private void UpdateTotal()
    {
        long total = Items.Sum(i => i.SizeBytes);
        TotalReclaimableDisplay = ByteFormatter.Format(total);
        foreach (CacheItemViewModel item in Items)
        {
            item.SharePercent = total > 0 ? 100.0 * item.SizeBytes / total : 0;
        }
    }
}
