using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCPerfSuite.App.Utils;
using PCPerfSuite.Core.Storage;

namespace PCPerfSuite.App.ViewModels;

/// <summary>Disque proposé à l'analyse, affiché en tuile : icône selon le type, nom, jauge d'occupation.</summary>
public sealed partial class DriveOption : ObservableObject
{
    public required string RootPath { get; init; }
    public required string Title { get; init; }
    public required string Kind { get; init; }

    /// <summary>Glyphe Segoe Fluent Icons du type de disque.</summary>
    public required string Icon { get; init; }

    public bool IsSystem { get; init; }
    public long UsedBytes { get; init; }
    public long TotalBytes { get; init; }

    public double UsedPercent => TotalBytes > 0 ? 100.0 * UsedBytes / TotalBytes : 0;
    public string UsageText => $"{ByteFormatter.Format(UsedBytes)} utilisés sur {ByteFormatter.Format(TotalBytes)}";

    [ObservableProperty] private bool isSelected;

    /// <summary>Décrit un volume, ou renvoie null s'il devient illisible entre l'énumération et la lecture
    /// (clé USB retirée, lecteur réseau déconnecté) : mieux vaut l'omettre de la liste que de faire tomber
    /// tout le ViewModel — et avec lui la fenêtre principale, construite juste après.</summary>
    public static DriveOption? TryFromDrive(DriveInfo drive, string? systemRoot)
    {
        (string kind, int glyph) = drive.DriveType switch
        {
            DriveType.Removable => ("Disque amovible", 0xE88E),
            DriveType.Network => ("Lecteur réseau", 0xE8CE),
            DriveType.CDRom => ("Lecteur optique", 0xE958),
            DriveType.Ram => ("Disque RAM", 0xE964),
            _ => ("Disque local", 0xEDA2),
        };

        try
        {
            if (!drive.IsReady) return null;

            // TotalSize et AvailableFreeSpace lèvent si le volume disparaît entre l'énumération et ici
            // (clé retirée, partage réseau coupé) : sans ce try, l'exception remonterait jusqu'au
            // constructeur de la fenêtre principale, qui n'existe pas encore pour l'afficher.
            long total = drive.TotalSize;
            long free = drive.AvailableFreeSpace;

            // Nom de volume illisible (droits) : on nomme le disque par son type, comme l'Explorateur
            // le fait pour un volume sans nom.
            string volumeLabel;
            try
            {
                volumeLabel = drive.VolumeLabel;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                volumeLabel = "";
            }

            string letter = drive.Name.TrimEnd('\\');
            string root = drive.RootDirectory.FullName;
            return new DriveOption
            {
                RootPath = root,
                Title = $"{(string.IsNullOrWhiteSpace(volumeLabel) ? kind : volumeLabel)} ({letter})",
                Kind = kind,
                Icon = char.ConvertFromUtf32(glyph),
                IsSystem = string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase),
                UsedBytes = total - free,
                TotalBytes = total,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public sealed partial class StorageViewModel : ObservableObject
{
    private const string DialogTitle = "PCPerfSuite";
    private const string StaleNodeMessage = "Cet élément ne fait plus partie de la carte affichée. Relance l'analyse pour la remettre à jour.";

    private readonly DiskSpaceScanner _scanner = new();
    private CancellationTokenSource? _scanCts;

    public ObservableCollectionEx<DriveOption> Drives { get; } = new();

    /// <summary>Analyse complète ou rescan d'un dossier en cours, annulable via CancelScan.</summary>
    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private bool isDeleting;
    [ObservableProperty] private string? statusText;
    [ObservableProperty] private FolderNode? rootNode;
    [ObservableProperty] private FolderNode? hoveredNode;
    [ObservableProperty] private FolderNode? selectedNode;

    /// <summary>Incrémenté après chaque modification en place de l'arbre (rescan, suppression) : la treemap se
    /// redessine en gardant ses blocs dépliés, ce qu'une réaffectation de RootNode ne permettrait pas.</summary>
    [ObservableProperty] private int treeVersion;

    public StorageViewModel()
    {
        // Le peuplement part hors du thread UI : DriveInfo.IsReady attend le délai d'expiration SMB sur un
        // lecteur réseau dont le serveur est hors ligne (plusieurs secondes), et ce constructeur est appelé
        // depuis celui de MainViewModel, donc avant que la fenêtre principale ne s'affiche.
        _ = LoadDrivesAsync();
    }

    private async Task LoadDrivesAsync()
    {
        string? systemRoot = Path.GetPathRoot(Environment.SystemDirectory);

        List<DriveOption> options = await Task.Run(() =>
        {
            var list = new List<DriveOption>();
            foreach (DriveInfo drive in SafeGetDrives())
            {
                if (DriveOption.TryFromDrive(drive, systemRoot) is { } option) list.Add(option);
            }
            return list;
        });

        Drives.ReplaceAll(options);
        if (Drives.Count > 0) Drives[0].IsSelected = true;
    }

    /// <summary>L'énumération elle-même peut lever sur une configuration inhabituelle (volume monté sans
    /// lettre, pilote de stockage en erreur) : on rend alors une liste vide plutôt que de tout arrêter.</summary>
    private static DriveInfo[] SafeGetDrives()
    {
        try { return DriveInfo.GetDrives(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<DriveInfo>(); }
    }

    partial void OnIsScanningChanged(bool value) => NotifyTreeCommandsChanged();

    partial void OnIsDeletingChanged(bool value) => NotifyTreeCommandsChanged();

    private void NotifyTreeCommandsChanged()
    {
        ScanCommand.NotifyCanExecuteChanged();
        RescanCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    // Une seule opération à la fois sur l'arbre : un rescan ou une suppression qui se croiseraient
    // appliqueraient leurs écarts de taille à une arborescence déjà modifiée par l'autre.
    private bool CanModifyTree() => !IsScanning && !IsDeleting;

    [RelayCommand(CanExecute = nameof(CanModifyTree))]
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
        SelectedNode = null;

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
            ReleaseScanCts(cts);
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    /// <summary>Libère la source d'annulation d'une analyse terminée. Chaque analyse libère la sienne, une
    /// fois son await revenu : plus personne ne s'en sert à ce moment-là, alors qu'une libération au moment
    /// de l'annulation la retirerait sous les pieds de l'analyse encore en cours.</summary>
    private void ReleaseScanCts(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_scanCts, cts)) _scanCts = null;
        cts.Dispose();
    }

    // ----- Menu contextuel de la treemap (le bloc visé arrive en CommandParameter) -----

    [RelayCommand(CanExecute = nameof(HasRealPath))]
    private void Open(FolderNode? node)
    {
        if (!TryGetExistingPath(node, out string path)) return;

        // Toujours passer par l'Explorateur, y compris pour un fichier. PCPerfSuite tourne en administrateur :
        // démarrer directement l'application associée lui ferait hériter de ce jeton élevé, sans la moindre
        // invite UAC. explorer.exe, lui, tourne déjà sous le compte normal et ouvre l'élément avec ses droits.
        StartShell(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true },
            $"Impossible d'ouvrir « {path} »");
    }

    [RelayCommand(CanExecute = nameof(HasRealPath))]
    private void OpenInExplorer(FolderNode? node)
    {
        if (!TryGetExistingPath(node, out string path)) return;

        StartShell(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true },
            $"Impossible d'ouvrir l'Explorateur sur « {path} »");
    }

    [RelayCommand(CanExecute = nameof(HasRealPath))]
    private void CopyPath(FolderNode? node)
    {
        if (!HasRealPath(node)) return;

        // Le presse-papiers est partagé par toutes les apps : s'il est tenu ouvert à cet instant (historique du
        // presse-papiers, bureau à distance…), l'écriture échoue. Clipboard.SetText réessaie déjà en interne, et
        // chaque tentative bloque le thread UI : empiler notre propre boucle ne ferait que figer la fenêtre plus
        // longtemps pour le même résultat.
        try
        {
            Clipboard.SetText(node.FullPath);
            StatusText = $"Chemin copié : {node.FullPath}";
        }
        catch (ExternalException ex)
        {
            // COMException en dérive ; le presse-papiers remonte aussi des ExternalException brutes.
            ShowMessage($"Le presse-papiers est occupé par une autre application, réessaie dans un instant.\n\n{ex.Message}",
                MessageBoxImage.Warning);
        }
    }

    [RelayCommand(CanExecute = nameof(HasRealPath))]
    private void ShowProperties(FolderNode? node)
    {
        if (!TryGetExistingPath(node, out string path)) return;

        try
        {
            ShellFileOperations.ShowProperties(path, OwnerHandle());
        }
        catch (Win32Exception ex)
        {
            ShowMessage($"Impossible d'afficher les propriétés de « {path} » :\n{ex.Message}", MessageBoxImage.Warning);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private async Task RescanAsync(FolderNode? node)
    {
        if (!CanRescan(node) || RootNode is not { } root) return;

        List<FolderNode>? ancestry = FolderTree.FindAncestry(root, node);
        if (ancestry is null)
        {
            ShowMessage(StaleNodeMessage, MessageBoxImage.Information);
            return;
        }

        if (!TryGetExistingPath(node, out string path)) return;

        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        string name = node.Name;
        IsScanning = true;
        StatusText = $"Nouvelle analyse de « {name} »…";

        var progress = new Progress<ScanProgress>(p =>
            StatusText = $"Nouvelle analyse de « {name} »… {ByteFormatter.Format(p.BytesScanned)} — {Truncate(p.CurrentPath, 60)}");

        try
        {
            FolderNode fresh = await _scanner.ScanAsync(path, name, progress, cts.Token);

            List<FolderNode> previousChildren = node.Children.ToList();
            long delta = FolderTree.ApplyRescan(ancestry, fresh);
            ForgetDetachedNodes(previousChildren);
            TreeVersion++;

            StatusText = $"« {name} » rescanné : {ByteFormatter.Format(node.SizeBytes)} ({FormatDelta(delta)}).";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Nouvelle analyse annulée, la carte n'a pas été modifiée.";
        }
        catch (Exception ex)
        {
            StatusText = $"Échec de la nouvelle analyse : {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            ReleaseScanCts(cts);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync(FolderNode? node)
    {
        if (!CanDelete(node) || RootNode is not { } root) return;

        string? refusal = DeletionGuard.GetRefusalReason(node);
        if (refusal is not null)
        {
            ShowMessage(refusal, MessageBoxImage.Information);
            return;
        }

        List<FolderNode>? ancestry = FolderTree.FindAncestry(root, node);
        if (ancestry is null)
        {
            ShowMessage(StaleNodeMessage, MessageBoxImage.Information);
            return;
        }

        if (!TryGetExistingPath(node, out string path)) return;

        string kind = node.IsFile ? "fichier" : "dossier";
        MessageBoxResult answer = ShowMessage(
            $"Envoyer ce {kind} dans la Corbeille ?\n\n" +
            $"Nom : {node.Name}\n" +
            $"Chemin : {path}\n" +
            $"Type : {kind}\n" +
            $"Taille : {ByteFormatter.Format(node.SizeBytes)}\n\n" +
            "Tu pourras le restaurer depuis la Corbeille — l'espace n'est donc pas encore rendu au disque, il faudra " +
            "vider celle-ci. Si Windows ne peut pas l'y placer (élément trop volumineux, disque sans Corbeille), il " +
            "te demandera confirmation avant toute suppression définitive.",
            MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        IsDeleting = true;
        StatusText = $"Envoi de « {node.Name} » dans la Corbeille…";

        try
        {
            RecycleResult result = await ShellFileOperations.SendToRecycleBinAsync(path, OwnerHandle());

            // Seule preuve fiable : l'élément a disparu. Un dossier peut n'avoir été déplacé qu'en partie
            // (fichier verrouillé, annulation en cours de route) : dans ce cas on ne touche pas à la carte.
            if (node.IsFile ? File.Exists(path) : Directory.Exists(path))
            {
                string partial = node.IsFile
                    ? ""
                    : "\n\nUne partie de son contenu a peut-être déjà été déplacée : « Rescanner ce dossier » remettra la carte à jour.";

                if (result.Aborted)
                {
                    StatusText = "Suppression annulée.";
                    ShowMessage($"Suppression annulée : « {path} » n'a pas été envoyé dans la Corbeille.{partial}", MessageBoxImage.Information);
                }
                else
                {
                    StatusText = "Échec de la suppression.";
                    string code = result.ErrorCode != 0 ? $" (code 0x{result.ErrorCode:X})" : "";
                    ShowMessage($"Impossible d'envoyer « {path} » dans la Corbeille{code}.\n" +
                                $"Fichier en cours d'utilisation, accès refusé ou chemin trop long ?{partial}", MessageBoxImage.Warning);
                }
                return;
            }

            FolderTree.Remove(ancestry);
            ForgetDetachedNodes(new[] { node });
            TreeVersion++;

            // La Corbeille est sur le même volume : l'élément a changé de dossier, pas disparu du disque. La carte
            // ne montre donc pas de l'espace libéré, et le dire évite de laisser croire le contraire.
            StatusText = $"« {node.Name} » ({ByteFormatter.Format(node.SizeBytes)}) est dans la Corbeille — " +
                         "l'espace ne sera rendu au disque qu'en la vidant.";
        }
        catch (Exception ex)
        {
            StatusText = "Échec de la suppression.";
            ShowMessage($"Impossible d'envoyer « {path} » dans la Corbeille :\n{ex.Message}", MessageBoxImage.Warning);
        }
        finally
        {
            IsDeleting = false;
        }
    }

    private bool HasRealPath([NotNullWhen(true)] FolderNode? node) => node is { IsAggregate: false, FullPath.Length: > 0 };

    private bool CanRescan([NotNullWhen(true)] FolderNode? node) => CanModifyTree() && HasRealPath(node) && !node.IsFile;

    private bool CanDelete([NotNullWhen(true)] FolderNode? node) => CanModifyTree() && HasRealPath(node);

    // La carte est une photo prise au moment du scan : l'élément a pu être déplacé ou supprimé depuis.
    private bool TryGetExistingPath([NotNullWhen(true)] FolderNode? node, out string path)
    {
        path = node?.FullPath ?? "";
        if (!HasRealPath(node)) return false;
        if (node.IsFile ? File.Exists(path) : Directory.Exists(path)) return true;

        ShowMessage($"« {path} » n'existe plus. « Rescanner ce dossier » sur son dossier parent, ou une nouvelle analyse, remettra la carte à jour.",
            MessageBoxImage.Information);
        return false;
    }

    // La barre d'info sous la carte et la sélection ne doivent pas continuer à désigner un élément retiré de l'arbre.
    private void ForgetDetachedNodes(IEnumerable<FolderNode> detachedRoots)
    {
        List<FolderNode> roots = detachedRoots.ToList();
        if (HoveredNode is { } hovered && roots.Any(r => FolderTree.Contains(r, hovered))) HoveredNode = null;
        if (SelectedNode is { } selected && roots.Any(r => FolderTree.Contains(r, selected))) SelectedNode = null;
    }

    private static void StartShell(ProcessStartInfo start, string failureMessage)
    {
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Win32Exception ex)
        {
            // Ex: fichier sans application associée.
            ShowMessage($"{failureMessage} :\n{ex.Message}", MessageBoxImage.Warning);
        }
    }

    private static MessageBoxResult ShowMessage(string text, MessageBoxImage image,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxResult defaultResult = MessageBoxResult.OK)
    {
        Window? owner = Application.Current?.MainWindow;
        return owner is null
            ? MessageBox.Show(text, DialogTitle, buttons, image, defaultResult)
            : MessageBox.Show(owner, text, DialogTitle, buttons, image, defaultResult);
    }

    private static IntPtr OwnerHandle()
        => Application.Current?.MainWindow is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;

    private static string FormatDelta(long delta)
        => delta == 0 ? "inchangé" : $"{(delta > 0 ? "+" : "−")}{ByteFormatter.Format(Math.Abs(delta))}";

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : "…" + value[^maxLength..];
}
