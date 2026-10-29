namespace SBModManager.Views;

/// <summary>
/// Implemented by pages that format user-visible text in code. Switching the language at runtime
/// only re-applies the translation table to the visual tree, which cannot recover strings that
/// were built with <c>string.Format</c> earlier; such pages re-render themselves through this hook.
/// </summary>
public interface ILanguageAwarePage
{
    /// <summary>Rebuild any text this page composed in code, in the now-current language.</summary>
    void OnLanguageChanged();
}
