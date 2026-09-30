namespace PCPerfSuite.Core.Safety;

/// <summary>Ce que fait un onglet de réglage au réveil de veille, après avoir relu le matériel.</summary>
public enum ResumeAction
{
    /// <summary>Relire seulement : « Appliquer au démarrage » décoché, autre matériel, ou accord manquant.</summary>
    ReadOnly,

    /// <summary>Relire seulement, parce que la sécurité thermique a retiré ces réglages pendant la session.</summary>
    SkipAfterEmergency,

    /// <summary>Réappliquer les réglages enregistrés.</summary>
    Reapply,
}

/// <summary>La décision au réveil, commune au CPU et au GPU, isolée ici pour être testée sans matériel.</summary>
public static class TuningResume
{
    /// <param name="applyAtStartup">Case « Appliquer au démarrage ».</param>
    /// <param name="sameHardware">Réglages faits sur ce matériel (GPU : <c>GpuIdentity.Matches</c> ; CPU : toujours vrai).</param>
    /// <param name="allowed">Écriture possible et accords donnés (avertissement CPU, renonciation Intel Arc).</param>
    /// <param name="emergencyThisSession">La sécurité thermique a déclenché pendant la session.</param>
    public static ResumeAction Decide(bool applyAtStartup, bool sameHardware, bool allowed, bool emergencyThisSession)
    {
        if (!applyAtStartup || !sameHardware || !allowed) return ResumeAction.ReadOnly;
        return emergencyThisSession ? ResumeAction.SkipAfterEmergency : ResumeAction.Reapply;
    }
}
