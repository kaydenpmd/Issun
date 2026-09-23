using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Issun.Views;

/// <summary>
/// A TextBlock that screen readers skip: text drawn as a picture of itself,
/// whose meaning is already spoken somewhere else. The explicit badge's "E" is
/// one: the title beside it says "explicit" in its automation name, and
/// without this the letter was announced again as a stray "E", because a
/// TextBlock hosted in another TextBlock's InlineUIContainer is exposed as a
/// child of it.
/// </summary>
public sealed class DecorativeText : TextBlock
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
