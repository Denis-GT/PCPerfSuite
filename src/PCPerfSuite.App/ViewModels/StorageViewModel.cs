using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Storage;

namespace PCPerfSuite.App.ViewModels;

public sealed partial class DriveOption : ObservableObject
{
    public required string RootPath { get; init; }
    public required string Label { get; init; }

    [ObservableProperty] private bool isSelected;
}

public sealed partial class StorageViewModel : ObservableObject
{
    private readonly DiskSpaceScanner _scanner = new();
    private CancellationTokenSource? _scanCts;

    public ObservableCollectionEx<DriveOption> Drives { get; } = new();
    public ObservableCollectionEx<FolderNode> Breadcrumb { get; } = new();

    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private string? statusText;
    [ObservableProperty] private FolderNode? rootNode;
    [ObservableProperty] private FolderNode? currentNode;
    [ObservableProperty] private FolderNode? hoveredNode;
    [ObservableProperty] private FolderNode? selectedNode;

    public StorageViewModel()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            long used = drive.TotalSize - drive.AvailableFreeSpace;
            Drives.Add(new DriveOption
            {
                RootPath = drive.RootDirectory.FullName,
                Label = $"{drive.Name.TrimEnd('\\')} — {ByteFormatter.Format(used)} / {ByteFormatter.Format(drive.TotalSize)}",
            });
        }

        if (Drives.Count > 0) Drives[0].IsSelected = true;
    }

    partial void OnSelectedNodeChanged(FolderNode? value)
    {
        if (value is not { CanDrillInto: true }) return;
        CurrentNode = value;
        Breadcrumb.Add(value);
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        List<DriveOption> selected = Drives.Where(d => d.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText = "Sélectionne au moins un disque.";
            return;
        }

        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        IsScanning = true;
        RootNode = null;
        CurrentNode = null;
        Breadcrumb.Clear();

        var progress = new Progress<ScanProgress>(p =>
            StatusText = $"Analyse en cours… {ByteFormatter.Format(p.BytesScanned)} — {Truncate(p.CurrentPath, 70)}");

        try
        {
            FolderNode root;
            if (selected.Count == 1)
            {
                root = await _scanner.ScanAsync(selected[0].RootPath, selected[0].RootPath.TrimEnd('\\'), progress, cts.Token);
            }
            else
            {
                var children = new List<FolderNode>();
                foreach (DriveOption drive in selected)
                {
                    children.Add(await _scanner.ScanAsync(drive.RootPath, drive.RootPath.TrimEnd('\\'), progress, cts.Token));
                }
                root = new FolderNode { Name = "Disques sélectionnés", SizeBytes = children.Sum(c => c.SizeBytes), Children = children };
            }

            RootNode = root;
            CurrentNode = root;
            Breadcrumb.Add(root);
            StatusText = $"Terminé — {ByteFormatter.Format(root.SizeBytes)} au total.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Analyse annulée.";
        }
        catch (Exception ex)
        {
            StatusText = $"Échec de l'analyse : {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    private void NavigateToBreadcrumb(FolderNode node)
    {
        int index = Breadcrumb.IndexOf(node);
        if (index < 0) return;

        while (Breadcrumb.Count > index + 1)
        {
            Breadcrumb.RemoveAt(Breadcrumb.Count - 1);
        }
        CurrentNode = node;
    }

    [RelayCommand]
    private void OpenCurrentInExplorer()
    {
        string? path = CurrentNode?.FullPath;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true,
        });
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : "…" + value[^maxLength..];
}
