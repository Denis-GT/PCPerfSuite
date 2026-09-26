using PCPerfSuite.App.Overlay;

namespace PCPerfSuite.App.Tests;

public class OverlayLineOrderTests
{
    private static readonly string[] Catalog = { "cpu", "gpu", "ram", "net" };

    [Fact]
    public void Merge_WithoutSavedOrder_IsTheCatalog()
        => Assert.Equal(Catalog, OverlayLineOrder.Merge(null, Catalog));

    [Fact]
    public void Merge_KeepsSavedOrder()
        => Assert.Equal(new[] { "net", "gpu", "cpu", "ram" }, OverlayLineOrder.Merge(new[] { "net", "gpu", "cpu", "ram" }, Catalog));

    [Fact]
    public void Merge_PlacesNewCatalogKeyRightAfterItsCatalogNeighbour()
    {
        // « ram » est apparue depuis l'enregistrement : elle vient après « gpu », sa voisine du catalogue.
        List<string> order = OverlayLineOrder.Merge(new[] { "net", "gpu", "cpu" }, Catalog);

        Assert.Equal(new[] { "net", "gpu", "ram", "cpu" }, order);
    }

    [Fact]
    public void Merge_PlacesNewFirstCatalogKeyAtTheStart()
        => Assert.Equal(new[] { "cpu", "net", "gpu", "ram" }, OverlayLineOrder.Merge(new[] { "net", "gpu", "ram" }, Catalog));

    [Fact]
    public void Merge_KeepsSavedKeysTheCatalogDoesNotKnowYet()
    {
        // Une métrique de batterie enregistrée, pas encore découverte sur ce relevé : elle garde sa place, et les
        // clés nouvelles se rangent chacune après sa voisine du catalogue (« ram » après « gpu », « net » après « ram »).
        List<string> order = OverlayLineOrder.Merge(new[] { "gpu", "battery.rate", "cpu" }, Catalog);

        Assert.Equal(new[] { "gpu", "ram", "net", "battery.rate", "cpu" }, order);
    }

    [Fact]
    public void Merge_DropsDuplicates()
        => Assert.Equal(Catalog, OverlayLineOrder.Merge(new[] { "cpu", "gpu", "cpu", "ram", "net", "gpu" }, Catalog));

    [Fact]
    public void Move_SwapsWithTheNeighbour()
    {
        Assert.Equal(new[] { "gpu", "cpu", "ram", "net" }, OverlayLineOrder.Move(Catalog, Catalog, "gpu", -1));
        Assert.Equal(new[] { "cpu", "ram", "gpu", "net" }, OverlayLineOrder.Move(Catalog, Catalog, "gpu", +1));
    }

    [Fact]
    public void Move_PassesOverHiddenKeysWithoutMovingThem()
    {
        string[] order = { "cpu", "gpu", "ram", "net" };
        string[] shown = { "cpu", "ram", "net" }; // « gpu » est décochée

        // « ram » monte au-dessus de « cpu », la voisine affichée : « gpu » n'a pas bougé.
        Assert.Equal(new[] { "ram", "gpu", "cpu", "net" }, OverlayLineOrder.Move(order, shown, "ram", -1));
    }

    [Fact]
    public void Move_AtTheEnds_ChangesNothing()
    {
        Assert.Equal(Catalog, OverlayLineOrder.Move(Catalog, Catalog, "cpu", -1));
        Assert.Equal(Catalog, OverlayLineOrder.Move(Catalog, Catalog, "net", +1));
    }

    [Fact]
    public void Move_WithNoShownNeighbour_ChangesNothing()
    {
        // Seule « cpu » est affichée : rien à enjamber.
        Assert.Equal(Catalog, OverlayLineOrder.Move(Catalog, new[] { "cpu" }, "cpu", +1));
    }

    [Fact]
    public void Move_OfAnUnknownKey_ChangesNothing()
        => Assert.Equal(Catalog, OverlayLineOrder.Move(Catalog, Catalog, "fans", -1));

    [Fact]
    public void Move_DoesNotModifyItsInput()
    {
        string[] order = { "cpu", "gpu" };

        OverlayLineOrder.Move(order, order, "gpu", -1);

        Assert.Equal(new[] { "cpu", "gpu" }, order);
    }

    [Fact]
    public void Sort_PutsUnknownKeysLast_InTheirOriginalOrder()
    {
        string[] items = { "z", "gpu", "y", "cpu" };

        Assert.Equal(new[] { "cpu", "gpu", "z", "y" }, OverlayLineOrder.Sort(items, key => key, Catalog));
    }

    [Fact]
    public void IsDefault_IgnoresKeysTheCatalogDoesNotKnow()
    {
        Assert.True(OverlayLineOrder.IsDefault(Catalog, Catalog));
        Assert.True(OverlayLineOrder.IsDefault(new[] { "cpu", "battery.rate", "gpu", "ram", "net" }, Catalog));
        Assert.False(OverlayLineOrder.IsDefault(new[] { "gpu", "cpu", "ram", "net" }, Catalog));
    }
}
