using Glassquid.Core.Models;
using Glassquid.Core.Models.Settings;
using Glassquid.Core.Storage;

namespace Glassquid.Core.Tests;

public sealed class AppearanceCustomizationTests
{
    [Fact]
    public void FrostedChoiceSurvivesSavingAndReloadingWithoutChangingTransparency()
    {
        using var temp = new TempDataDirectory();
        var store = new SettingsStore(temp.Paths.SettingsFile);
        store.Load();
        store.Current.Appearance.GlassMaterial = (GlassMaterialMode)2;
        store.Current.Appearance.DockGlassTransparency = .75;
        store.Save();
        store.Load();
        Assert.Equal((GlassMaterialMode)2, store.Current.Appearance.GlassMaterial);
        Assert.Equal(.75, store.Current.Appearance.DockGlassTransparency);
        Assert.Equal(.2, store.Current.Appearance.LauncherGlassTransparency);
        Assert.Equal(GlassMaterialMode.AndroidLiquidGlass,
            SettingsPageEditor.ResetPage(store.Current, "Giao diện").Appearance.GlassMaterial);
    }
}
