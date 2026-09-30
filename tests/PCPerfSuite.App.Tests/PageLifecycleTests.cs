using PCPerfSuite.App.ViewModels;

namespace PCPerfSuite.App.Tests;

/// <summary>Quelle page est sous les yeux de l'utilisateur : la page courante, et seulement fenêtre visible.</summary>
public class PageLifecycleTests
{
    /// <summary>Note chaque valeur reçue, dans l'ordre commun à toutes les pages du test.</summary>
    private sealed class RecordingPage : IPageLifecycle
    {
        private readonly List<string> _log;
        private readonly string _name;

        public RecordingPage(string name, List<string> log)
        {
            _name = name;
            _log = log;
        }

        public bool? Shown { get; private set; }

        public bool IsPageShown
        {
            set
            {
                Shown = value;
                _log.Add($"{_name}={value}");
            }
        }
    }

    private static NavEntry Entry(string key, object viewModel)
        => new(new NavPage(key, NavSection.Monitor, key, "x"), viewModel);

    [Fact]
    public void Update_WindowShown_ShowsOnlyTheCurrentPage()
    {
        var log = new List<string>();
        var processes = new RecordingPage("processes", log);
        var storage = new RecordingPage("storage", log);

        PageLifecycle.Update(new[] { Entry("processes", processes), Entry("storage", storage) }, "storage", isWindowShown: true);

        Assert.False(processes.Shown);
        Assert.True(storage.Shown);
    }

    [Fact]
    public void Update_WindowHidden_ShowsNothing()
    {
        var log = new List<string>();
        var processes = new RecordingPage("processes", log);

        PageLifecycle.Update(new[] { Entry("processes", processes) }, "processes", isWindowShown: false);

        Assert.False(processes.Shown);
    }

    [Fact]
    public void Update_HidesOtherPagesBeforeShowingTheCurrentOne()
    {
        // La page courante est avant l'autre dans la liste : elle doit quand même être affichée en dernier.
        var log = new List<string>();
        var cpu = new RecordingPage("cpu", log);
        var processes = new RecordingPage("processes", log);

        PageLifecycle.Update(new[] { Entry("cpu", cpu), Entry("processes", processes) }, "cpu", isWindowShown: true);

        Assert.Equal(new[] { "processes=False", "cpu=True" }, log);
    }

    [Fact]
    public void Update_IgnoresPagesWithoutLifecycle_AndHandlesSettings()
    {
        var log = new List<string>();
        var settings = new RecordingPage("settings", log);
        NavEntry settingsEntry = new(NavigationMenu.Settings, settings);

        PageLifecycle.Update(new[] { Entry("monitoring", new object()), settingsEntry }, PageKeys.Settings, isWindowShown: true);

        Assert.True(settings.Shown);
        Assert.Equal(new[] { "settings=True" }, log);
    }

    [Fact]
    public void Update_NoCurrentPage_HidesEverything()
    {
        var log = new List<string>();
        var processes = new RecordingPage("processes", log);

        PageLifecycle.Update(new[] { Entry("processes", processes) }, currentKey: null, isWindowShown: true);

        Assert.False(processes.Shown);
    }
}
