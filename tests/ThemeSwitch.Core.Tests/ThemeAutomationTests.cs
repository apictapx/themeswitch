using ThemeSwitch.Core;
using static ThemeSwitch.Core.Tests.TestData;

namespace ThemeSwitch.Core.Tests;

public class ThemeAutomationTests
{
    readonly AppSettings _settings = new();
    readonly FakeThemeStore _store = new();
    readonly WeatherForecast _clear = Forecast();

    ThemeAutomation Automation => new(_settings, _store);

    [Fact]
    public void TickAppliesTheDecision()
    {
        var status = Automation.Tick(Utc(12), Lisbon, _clear);

        Assert.True(status.Applied);
        Assert.Equal(ThemeChoice.Light, _store.Value);
        Assert.Equal(ThemeChoice.Light, _settings.LastApplied);
    }

    [Fact]
    public void TickDoesNothingWithoutALocation()
    {
        var status = Automation.Tick(Utc(12), null, null);

        Assert.False(status.Applied);
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public void TickDoesNotRewriteAnUnchangedTheme()
    {
        Automation.Tick(Utc(12), Lisbon, _clear);
        Automation.Tick(Utc(12, 30), Lisbon, _clear);

        Assert.Equal(1, _store.Writes);
    }

    [Fact]
    public void ManualToggleHoldsUntilTheNextScheduledChange()
    {
        var automation = Automation;
        automation.Tick(Utc(12), Lisbon, _clear);

        var toggled = automation.Toggle(Utc(12, 5), Lisbon, _clear);
        var lightEnd = automation.Engine.Decide(Utc(12), Lisbon, _clear).LightEndUtc;

        Assert.Equal(ThemeChoice.Dark, _store.Value);
        Assert.Equal(lightEnd, toggled.OverrideUntilUtc);

        automation.Tick(Utc(15), Lisbon, _clear);
        Assert.Equal(ThemeChoice.Dark, _store.Value); // override respected

        automation.Tick(Utc(18), Lisbon, _clear);
        Assert.Null(_settings.OverrideUntilUtc); // expired at sunset, automation agrees anyway
        Assert.Equal(ThemeChoice.Dark, _store.Value);
    }

    [Fact]
    public void EveningOverrideLastsUntilMorningThenAutomationResumes()
    {
        var automation = Automation;
        automation.Tick(Utc(20), Lisbon, _clear);
        automation.Toggle(Utc(20, 1), Lisbon, _clear);
        Assert.Equal(ThemeChoice.Light, _store.Value);

        automation.Tick(Utc(23), Lisbon, _clear);
        Assert.Equal(ThemeChoice.Light, _store.Value);

        automation.Tick(Utc(18, day: 18), Lisbon, null);
        Assert.Equal(ThemeChoice.Dark, _store.Value);
        Assert.Null(_settings.OverrideUntilUtc);
    }

    [Fact]
    public void TogglingBackToTheAutomaticThemeCancelsTheOverride()
    {
        var automation = Automation;
        automation.Tick(Utc(12), Lisbon, _clear);
        automation.Toggle(Utc(12, 1), Lisbon, _clear);

        var status = automation.Toggle(Utc(12, 2), Lisbon, _clear);

        Assert.Equal(ThemeChoice.Light, _store.Value);
        Assert.Null(status.OverrideUntilUtc);
    }

    [Fact]
    public void AChangeMadeOutsideTheAppCountsAsAnOverride()
    {
        var automation = Automation;
        automation.Tick(Utc(12), Lisbon, _clear);

        _store.Value = ThemeChoice.Dark; // user flipped it in Windows Settings
        var status = automation.Tick(Utc(12, 1), Lisbon, _clear);

        Assert.False(status.Applied);
        Assert.Equal(ThemeChoice.Dark, _store.Value);
        Assert.NotNull(status.OverrideUntilUtc);
    }

    [Fact]
    public void FixedModeIsNotOverriddenByAutomation()
    {
        var automation = Automation;
        automation.SetMode(ThemeMode.Dark, Utc(12), Lisbon, _clear);

        automation.Tick(Utc(13), Lisbon, _clear);

        Assert.Equal(ThemeChoice.Dark, _store.Value);
    }

    [Fact]
    public void FixedModeIsEnforcedAtStartup()
    {
        _settings.Mode = ThemeMode.Dark;
        _store.Value = ThemeChoice.Light; // changed while the app wasn't running

        var status = Automation.Tick(Utc(12), Lisbon, _clear);

        Assert.True(status.Applied);
        Assert.Equal(ThemeChoice.Dark, _store.Value);
    }

    [Fact]
    public void FixedModeIsEnforcedEvenWithoutALocation()
    {
        _settings.Mode = ThemeMode.Light;
        _store.Value = ThemeChoice.Dark;

        Automation.Tick(Utc(12), null, null);

        Assert.Equal(ThemeChoice.Light, _store.Value);
    }

    [Fact]
    public void ChangingTheThemeInSettingsWhileInFixedModeFlipsTheMode()
    {
        var automation = Automation;
        automation.SetMode(ThemeMode.Dark, Utc(12), Lisbon, _clear);

        _store.Value = ThemeChoice.Light;
        var status = automation.Tick(Utc(12, 1), Lisbon, _clear);

        Assert.Equal(ThemeMode.Light, status.Mode);
        Assert.Equal(ThemeChoice.Light, _store.Value);
        Assert.Equal("Always Light", StatusText.Line(status, LisbonZone, Utc(12, 1)));
    }

    [Fact]
    public void ToggleInFixedModeFlipsTheMode()
    {
        var automation = Automation;
        automation.SetMode(ThemeMode.Dark, Utc(12), Lisbon, _clear);

        var status = automation.Toggle(Utc(12, 1), Lisbon, _clear);

        Assert.Equal(ThemeMode.Light, status.Mode);
        Assert.Equal(ThemeChoice.Light, _store.Value);
        Assert.Null(status.OverrideUntilUtc);
    }

    [Fact]
    public void BackToAutoAppliesTheDecisionImmediately()
    {
        var automation = Automation;
        automation.SetMode(ThemeMode.Dark, Utc(12), Lisbon, _clear);

        automation.SetMode(ThemeMode.Auto, Utc(12, 1), Lisbon, _clear);

        Assert.Equal(ThemeChoice.Light, _store.Value);
    }

    [Fact]
    public void StatusLineDescribesTheState()
    {
        var automation = Automation;

        var day = automation.Tick(Utc(12), Lisbon, Forecast((14, 3, 90, 0)));
        Assert.Equal("Light (clear) · Dark at ~15:00", StatusText.Line(day, LisbonZone, Utc(12)));

        var manual = automation.Toggle(Utc(12, 1), Lisbon, Forecast((14, 3, 90, 0)));
        Assert.Equal("Dark (manual) until 15:00", StatusText.Line(manual, LisbonZone, Utc(12, 1)));

        var clear = new ThemeAutomation(new AppSettings(), new FakeThemeStore()).Tick(Utc(12), Lisbon, Forecast());
        Assert.Equal("Light (clear) · Dark at 18:26", StatusText.Line(clear, LisbonZone, Utc(12))); // exact: no "~"

        var night = new ThemeAutomation(new AppSettings(), new FakeThemeStore()).Tick(Utc(20), Lisbon, null);
        Assert.StartsWith("Dark (after sunset) · Light at Sun 08:", StatusText.Line(night, LisbonZone, Utc(20)));
    }
}
