using System.ComponentModel;
using System.Media;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Loadout.App;

// The themed MessageBox: an owned modal window. Closing it without a button is Cancel.
public partial class QuestionDialog : LoadoutWindow
{
    private static int _openCount;

    private readonly Question _question;
    private readonly Action? _confirmAction;

    private QuestionDialog(Window? owner, Question question, Action? confirmAction)
    {
        InitializeComponent();
        _question = question;
        _confirmAction = confirmAction;
        if (owner is null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            MaxHeight = SystemParameters.WorkArea.Height * 0.85;
        }
        else
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            MaxHeight = owner.ActualHeight * 0.85;
        }

        TitleText.Text = question.Title;
        BodyText.Text = question.Body;
        AutomationProperties.SetName(this, question.Title);
        AutomationProperties.SetHelpText(this, question.Body);
        if (question.Kind != QuestionKind.Question)
        {
            KindIcon.Data = (Geometry)FindResource(question.Kind == QuestionKind.Warn ? "IconWarn" : "IconError");
            KindIcon.SetResourceReference(Shape.FillProperty, question.Kind == QuestionKind.Warn ? "AppWarn" : "AppError");
            KindIcon.Visibility = Visibility.Visible;
        }

        if (question.Items is { Count: > 0 } items && question.ChooseItem)
        {
            ChoiceList.ItemsSource = items;
            ChoiceList.SelectedIndex = 0;
            ChoiceList.Visibility = Visibility.Visible;
        }
        else if (question.Items is { Count: > 0 } shown)
        {
            ItemList.ItemsSource = shown;
            ItemBox.Visibility = Visibility.Visible;
        }

        ConfirmButton.Content = question.ConfirmLabel;
        CancelButton.Content = question.CancelLabel;

        // Amber never pulls towards data loss: a destructive action is a secondary button in the error colour,
        // and the safe choice is the default. A notice's action is a side trip (open a folder), so Close is the default.
        var defaultButton = question.Destructive || confirmAction is not null ? CancelButton : ConfirmButton;
        defaultButton.IsDefault = true;
        if (question.Destructive)
        {
            ConfirmButton.SetResourceReference(ForegroundProperty, "AppError");
        }
        else if (confirmAction is null)
        {
            ConfirmButton.SetResourceReference(StyleProperty, "PrimaryButton");
        }

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => Clipboard.SetText(PlainText())));
        // A list to choose from takes the keyboard, so Up and Down move and Enter confirms.
        Loaded += (_, _) =>
        {
            if (ChoiceList.Visibility == Visibility.Visible
                && ChoiceList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first)
            {
                first.Focus();
            }
            else
            {
                defaultButton.Focus();
            }
        };
    }

    // True while a question is open, so the owner shows its scrim.
    public static bool IsOpen => _openCount > 0;

    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

    // True when the user chose Confirm.
    public static bool Ask(Window owner, Question question) => new QuestionDialog(owner, question, null).Run();

    // The index of the item chosen with Confirm, or null for Cancel. The question has ChooseItem and items.
    public static int? Choose(Window owner, Question question)
    {
        var dialog = new QuestionDialog(owner, question, null);
        return dialog.Run() && dialog.ChoiceList.SelectedIndex >= 0 ? dialog.ChoiceList.SelectedIndex : null;
    }

    // A notice: Confirm runs the action and leaves the dialog open, Cancel closes it.
    public static void ShowNotice(Window? owner, Question question, Action confirm) => new QuestionDialog(owner, question, confirm).Run();

    private bool Run()
    {
        // The sounds MessageBox played.
        if (_question.Kind == QuestionKind.Warn)
        {
            SystemSounds.Exclamation.Play();
        }
        else if (_question.Kind == QuestionKind.Error)
        {
            SystemSounds.Hand.Play();
        }

        ChangeOpenCount(1);
        try
        {
            return ShowDialog() == true;
        }
        finally
        {
            ChangeOpenCount(-1);
        }
    }

    private static void ChangeOpenCount(int change)
    {
        _openCount += change;
        StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(IsOpen)));
    }

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        if (_confirmAction is null)
        {
            DialogResult = true;
        }
        else
        {
            _confirmAction();
        }
    }

    // What MessageBox copies, without its dashes: the title, the body and the items.
    private string PlainText()
    {
        var parts = new List<string> { _question.Title, _question.Body };
        if (_question.Items is { Count: > 0 } items)
        {
            parts.Add(string.Join(Environment.NewLine, items));
        }

        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }
}
