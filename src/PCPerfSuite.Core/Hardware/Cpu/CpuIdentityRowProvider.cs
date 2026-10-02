using PCPerfSuite.Core.Compatibility;

namespace PCPerfSuite.Core.Hardware.Cpu;

/// <summary>
/// Diagnostic « Processeur identifié pour les groupes » : l'identité (<see cref="CpuIdentity"/>) que les groupes de
/// profils comparent avant de poser des watts. Sans nom de processeur dans le registre (machine virtuelle, image du
/// fabricant), aucune limite en watts d'un groupe n'est posée sur ce PC : la ligne le dit, c'est ce qui explique les
/// refus. La ligne « CPU » montre, elle, le nom lu par le relevé, qui peut exister quand celui du registre manque.
/// </summary>
public sealed class CpuIdentityRowProvider : ICompatibilityRowProvider
{
    public const string RowTitle = "Processeur identifié pour les groupes";

    private readonly CpuIdentity _identity;

    public CpuIdentityRowProvider(CpuIdentity identity) => _identity = identity;

    public string Title => RowTitle;

    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public IReadOnlyList<CompatibilityRow> GetRows() => [BuildRow(_identity)];

    public static CompatibilityRow BuildRow(CpuIdentity identity)
        => identity.HasName
            ? new CompatibilityRow(RowTitle, identity.Describe(),
                "Les limites en watts d'un groupe ne se posent que sur le processeur où elles ont été relevées : celui-ci.", true)
            : new CompatibilityRow(RowTitle, "Nom absent du registre",
                $"Le registre de Windows ne donne pas le nom du processeur ({identity.Describe()}) : sans lui, deux modèles d'une "
                + "même génération ne se distinguent pas, et aucune limite en watts d'un groupe n'est posée sur ce PC. Les autres "
                + "réglages des groupes s'appliquent.", false);
}
