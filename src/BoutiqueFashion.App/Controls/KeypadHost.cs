namespace BoutiqueFashion.App.Controls;

internal static class KeypadHost
{
    private static TouchKeypadOverlay? overlay;

    internal static void Register(TouchKeypadOverlay instance) => overlay = instance;

    public static void Open(KeypadField field) => overlay?.Open(field);
}
