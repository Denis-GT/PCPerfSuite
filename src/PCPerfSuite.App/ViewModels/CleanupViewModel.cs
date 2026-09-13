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

    public string Name => Category.Name;
    public string Description => Category.Description;
    public bool RequiresAdmin => Category.RequiresAdmin;
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
            item.LastResultMessage = result.FilesSkipped > 0
                ? $"{ByteFormatter.Format(result.BytesFreed)} libérés, {result.FilesSkipped} fichier(s) verrouillé(s) ignoré(s)."
                : $"{ByteFormatter.Format(result.BytesFreed)} libérés.";

            CacheCategoryResult rescanned = await _service.ScanAsync(item.Category);
            item.ApplyScan(rescanned);
        }
        catch (Exception ex)
        {
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
    }
}
