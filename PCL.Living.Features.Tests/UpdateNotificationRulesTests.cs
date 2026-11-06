using PCL.Living.Features;

internal static class UpdateNotificationRulesTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("update notices show only when launcher is active and idle", PresentOnly);
        yield return ("update notices defer while minimized or in game", DeferredStates);
    }

    private static void PresentOnly()
    {
        Equal(false, UpdateNotificationRules.ShouldDefer(
            isActive: true, isMinimized: false, isLaunching: false, isGameRunning: false));
    }

    private static void DeferredStates()
    {
        Equal(true, UpdateNotificationRules.ShouldDefer(
            isActive: false, isMinimized: false, isLaunching: false, isGameRunning: false));
        Equal(true, UpdateNotificationRules.ShouldDefer(
            isActive: true, isMinimized: true, isLaunching: false, isGameRunning: false));
        Equal(true, UpdateNotificationRules.ShouldDefer(
            isActive: true, isMinimized: false, isLaunching: true, isGameRunning: false));
        Equal(true, UpdateNotificationRules.ShouldDefer(
            isActive: true, isMinimized: false, isLaunching: false, isGameRunning: true));
    }

    private static void Equal(bool expected, bool actual)
    {
        if (expected != actual) throw new Exception($"Expected [{expected}], got [{actual}].");
    }
}
