using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Loadout.App.Controls;

/// <summary>
/// The 16 px "i" that stands for a paragraph: its tooltip holds the text and
/// shows on keyboard focus too. UI Automation reads it as an image with the tooltip as its help text.
/// </summary>
public sealed class InfoTip : Control
{
    protected override AutomationPeer OnCreateAutomationPeer() => new InfoTipAutomationPeer(this);

    private sealed class InfoTipAutomationPeer(InfoTip owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(InfoTip);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
    }
}
