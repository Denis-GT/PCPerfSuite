using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Cache;
using PCPerfSuite.Core.PowerSettings;
using PCPerfSuite.Core.SystemInfo;

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

/// <summary>
/// La corbeille de Windows, présentée comme une ligne du même gabarit que les caches mais à part : elle ne
/// compte pas dans les parts des barres (ce n'est pas un cache qui se régénère), et sa taille n'entre dans le
/// total « récupérable » que si l'utilisateur a demandé que « Tout nettoyer » la vide.
/// </summary>
public sealed partial class RecycleBinItemViewModel : ObservableObject
{
    [ObservableProperty] private long sizeBytes;
    [ObservableProperty] private long itemCount;

    /// <summary>Faux tant que Windows n'a pas répondu, ou s'il ne répond pas : la ligne affiche alors « N/D »
    /// plutôt qu'un zéro qui ferait croire la corbeille vide.</summary>
    [ObservableProperty] private bool isKnown;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? lastResultMessage;
    [ObservableProperty] private bool lastResultFailed;

    public string Name => "Corbeille";

    public string Description => !IsKnown
        ? "Windows n'a pas communiqué le contenu de la corbeille."
        : ItemCount switch
        {
            0 => "La corbeille est vide.",
            1 => "1 élément, tous lecteurs confondus. Vidée définitivement : rien ne permet de le récupérer ensuite.",
            _ => $"{ItemCount} éléments, tous lecteurs confondus. Vidée définitivement : rien ne permet de les récupérer ensuite.",
        };

    public string SizeDisplay => IsBusy && !IsKnown ? "Analyse…" : IsKnown ? ByteFormatter.Format(SizeBytes) : "N/D";

    /// <summary>Le bouton n'a rien à faire d'une corbeille déjà vide, ni pendant une opération.</summary>
    public bool CanEmpty => IsKnown && ItemCount > 0 && !IsBusy;

    public void Apply(RecycleBinInfo? info)
    {
        IsKnown = info is not null;
        SizeBytes = info?.SizeBytes ?? 0;
        ItemCount = info?.ItemCount ?? 0;
        Refresh();
    }

    partial void OnIsBusyChanged(bool value) => Refresh();

    private void Refresh()
    {
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(CanEmpty));
    }
}

public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly CacheCleanerService _service = new();

    /// <summary>Faux pendant la restauration des réglages : la case cochée au démarrage ne doit pas provoquer
    /// une réécriture du fichier.</summary>
    private bool _initialized;

    public ObservableCollectionEx<CacheItemViewModel> Items { get; } = new();

    public RecycleBinItemViewModel RecycleBin { get; } = new();

    /// <summary>« Tout nettoyer » vide aussi la corbeille.</summary>
    [ObservableProperty] private bool includeRecycleBinInCleanAll;

    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private string totalReclaimableDisplay = "0 o";

    /// <summary>Vrai quand l'app tourne sous un autre compte que la personne devant l'écran : les caches
    /// affichés ici sont alors ceux de l'autre profil, ce que <see cref="OtherProfileMessage"/> explique.</summary>
    public bool IsOtherProfile => SessionUser.IsOtherProfile;

    public string OtherProfileMessage => SessionUser.OtherProfileMessage ?? "";

    public CleanupViewModel()
    {
        foreach (CacheCategory category in KnownCaches.All)
        {
            Items.Add(new CacheItemViewModel(category));
        }

        IncludeRecycleBinInCleanAll = AppSettingsStore.Load().Cleanup.IncludeRecycleBinInCleanAll;
        _initialized = true;

        _ = ScanAllAsync();
    }

    partial void OnIncludeRecycleBinInCleanAllChanged(bool value)
    {
        UpdateTotal();
        if (!_initialized) return;

        AppSettings settings = AppSettingsStore.Load();
        settings.Cleanup.IncludeRecycleBinInCleanAll = value;
        AppSettingsStore.Save(settings);
    }

    [RelayCommand]
    private async Task ScanAllAsync()
    {
        IsScanning = true;
        try
        {
            await ScanRecycleBinAsync();

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

    private async Task ScanRecycleBinAsync()
    {
        RecycleBin.IsBusy = true;
        try
        {
            RecycleBin.Apply(await Task.Run(CacheCleanerService.QueryRecycleBin));
        }
        finally
        {
            RecycleBin.IsBusy = false;
            UpdateTotal();
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

        // Une corbeille vide n'a rien à libérer : inutile d'écrire « 0 o libérés » sur sa ligne.
        if (IncludeRecycleBinInCleanAll && RecycleBin.CanEmpty)
        {
            await EmptyRecycleBinAsync();
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

    /// <summary>Pas de CanExecute : la vue lie IsEnabled à <see cref="RecycleBinItemViewModel.CanEmpty"/>, qui
    /// se met à jour tout seul, alors qu'un CanExecute demanderait de rappeler NotifyCanExecuteChanged à
    /// chaque changement de la corbeille.</summary>
    [RelayCommand]
    private async Task EmptyRecycleBinAsync()
    {
        long before = RecycleBin.SizeBytes;
        RecycleBin.IsBusy = true;
        try
        {
            bool done = await Task.Run(CacheCleanerService.EmptyRecycleBin);
            RecycleBin.LastResultFailed = !done;
            RecycleBin.LastResultMessage = done
                ? $"{ByteFormatter.Format(before)} libérés."
                : "Windows a refusé de vider la corbeille.";

            RecycleBin.Apply(await Task.Run(CacheCleanerService.QueryRecycleBin));
        }
        finally
        {
            RecycleBin.IsBusy = false;
            UpdateTotal();
        }
    }

    /// <summary>La somme des caches, plus la corbeille quand « Tout nettoyer » la vide : le chiffre annonce
    /// ce que ce bouton va réellement libérer. Les parts des barres, elles, ne portent que sur les caches.</summary>
    private void UpdateTotal()
    {
        long caches = Items.Sum(i => i.SizeBytes);
        long total = caches + (IncludeRecycleBinInCleanAll ? RecycleBin.SizeBytes : 0);
        TotalReclaimableDisplay = ByteFormatter.Format(total);
        foreach (CacheItemViewModel item in Items)
        {
            item.SharePercent = caches > 0 ? 100.0 * item.SizeBytes / caches : 0;
        }
    }
}
