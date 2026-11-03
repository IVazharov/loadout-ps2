using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Loadout.App.Controls;

/// <summary>
/// The panel at the top of a page: the kind's stripe and icon, the headline and
/// detail on one line each, and the progress while it is known. It keeps its height in every state, so
/// nothing below it moves. Narrator hears each new headline through the live region on it.
/// </summary>
[TemplatePart(Name = "PART_Headline", Type = typeof(TextBlock))]
[TemplatePart(Name = "PART_Detail", Type = typeof(TextBlock))]
public sealed class StatusPanel : Control
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(PageStatus), typeof(StatusPanel),
        new PropertyMetadata(null, (d, _) => ((StatusPanel)d).OnStatusChanged()));

    private static readonly DependencyPropertyKey KindKey = DependencyProperty.RegisterReadOnly(
        nameof(Kind), typeof(StatusKind), typeof(StatusPanel), new PropertyMetadata(StatusKind.Idle));

    private static readonly DependencyPropertyKey ShowsProgressKey = DependencyProperty.RegisterReadOnly(
        nameof(ShowsProgress), typeof(bool), typeof(StatusPanel), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey IsIndeterminateKey = DependencyProperty.RegisterReadOnly(
        nameof(IsIndeterminate), typeof(bool), typeof(StatusPanel), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey ProgressKey = DependencyProperty.RegisterReadOnly(
        nameof(Progress), typeof(double), typeof(StatusPanel), new PropertyMetadata(0.0));

    private static readonly DependencyPropertyKey PercentTextKey = DependencyProperty.RegisterReadOnly(
        nameof(PercentText), typeof(string), typeof(StatusPanel), new PropertyMetadata(""));

    public static readonly DependencyProperty KindProperty = KindKey.DependencyProperty;

    public static readonly DependencyProperty ShowsProgressProperty = ShowsProgressKey.DependencyProperty;

    public static readonly DependencyProperty IsIndeterminateProperty = IsIndeterminateKey.DependencyProperty;

    public static readonly DependencyProperty ProgressProperty = ProgressKey.DependencyProperty;

    public static readonly DependencyProperty PercentTextProperty = PercentTextKey.DependencyProperty;

    private TextBlock? _headline;
    private TextBlock? _detail;

    static StatusPanel()
    {
        FocusableProperty.OverrideMetadata(typeof(StatusPanel), new FrameworkPropertyMetadata(false));
    }

    public StatusPanel()
    {
        SizeChanged += (_, _) => UpdateToolTip();
    }

    public PageStatus? Status
    {
        get => (PageStatus?)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>The status's kind, for the template's triggers.</summary>
    public StatusKind Kind => (StatusKind)GetValue(KindProperty);

    /// <summary>True while the status carries a percent, known or not.</summary>
    public bool ShowsProgress => (bool)GetValue(ShowsProgressProperty);

    public bool IsIndeterminate => (bool)GetValue(IsIndeterminateProperty);

    public double Progress => (double)GetValue(ProgressProperty);

    /// <summary>"62%", or empty while how far is not known.</summary>
    public string PercentText => (string)GetValue(PercentTextProperty);

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _headline = GetTemplateChild("PART_Headline") as TextBlock;
        _detail = GetTemplateChild("PART_Detail") as TextBlock;
        UpdateToolTip();
    }

    private void OnStatusChanged()
    {
        SetValue(KindKey, Status?.Kind ?? StatusKind.Idle);
        var percent = Status?.Percent;
        var known = percent is double value && !double.IsNaN(value);
        SetValue(ShowsProgressKey, percent is not null);
        SetValue(IsIndeterminateKey, percent is not null && !known);
        SetValue(ProgressKey, known ? Math.Clamp(percent!.Value, 0, 100) : 0.0);
        SetValue(PercentTextKey, known ? Math.Floor(Math.Clamp(percent!.Value, 0, 100)).ToString("0", CultureInfo.CurrentCulture) + "%" : "");
        // After the bindings have moved the new text into the template.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            UpdateToolTip();
            if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            {
                UIElementAutomationPeer.CreatePeerForElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        });
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new StatusPanelAutomationPeer(this);

    // The whole text when the panel cut either line short.
    private void UpdateToolTip()
    {
        if (_headline is null || _detail is null || Status is not { } status)
        {
            ToolTip = null;
            return;
        }

        ToolTip = IsTrimmed(_headline) || IsTrimmed(_detail) ? status.LogLine : null;
    }

    private static bool IsTrimmed(TextBlock text)
    {
        if (text.Text.Length == 0 || text.ActualWidth <= 0)
        {
            return false;
        }

        var probe = new TextBlock { Style = text.Style, Text = text.Text };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width > text.ActualWidth + 0.5;
    }

    // The template's text is hidden from UI Automation (WPF leaves templated TextBlocks out of the control view),
    // so the panel itself carries it: the headline as its name, the detail as its help text. It is the live region.
    private sealed class StatusPanelAutomationPeer(StatusPanel owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(StatusPanel);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.StatusBar;

        protected override string GetNameCore() => owner.Status?.Headline ?? "";

        protected override string GetHelpTextCore() => owner.Status?.Detail ?? "";

        protected override AutomationLiveSetting GetLiveSettingCore() => AutomationLiveSetting.Polite;
    }
}
