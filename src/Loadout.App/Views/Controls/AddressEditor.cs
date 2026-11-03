using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Loadout.Core;

namespace Loadout.App.Controls;

/// <summary>
/// The PS2 address as four boxes of 0 to 255 (5.6), the desktop version of Loadout Bay's editor. Only digits
/// go in, and each part is clamped as it is typed, so <see cref="Address"/> is always a dotted quad, or empty
/// while none is set. It changes when focus leaves the editor or 800 ms after the last keystroke, never per digit.
/// </summary>
public sealed class AddressEditor : StackPanel
{
    public static readonly DependencyProperty AddressProperty = DependencyProperty.Register(
        nameof(Address),
        typeof(string),
        typeof(AddressEditor),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (editor, e) => ((AddressEditor)editor).Show((string?)e.NewValue)));

    /// <summary>Run with no parameter each time the user changes a part, before the value is committed.</summary>
    public static readonly DependencyProperty EditedCommandProperty = DependencyProperty.Register(
        nameof(EditedCommand),
        typeof(ICommand),
        typeof(AddressEditor));

    private readonly TextBox[] _parts = new TextBox[4];
    private readonly DispatcherTimer _pause;

    // Set by typing or pasting, cleared once the value is committed or shown again from Address.
    private bool _edited;
    private bool _showing;

    public AddressEditor()
    {
        Orientation = Orientation.Horizontal;
        for (var i = 0; i < _parts.Length; i++)
        {
            if (i > 0)
            {
                var dot = new TextBlock { Text = "." };
                dot.SetResourceReference(StyleProperty, "AddressDot");
                Children.Add(dot);
            }

            var box = new TextBox { Tag = i, AllowDrop = false };
            box.SetResourceReference(StyleProperty, "OctetBox");
            AutomationProperties.SetName(box, $"PS2 address, part {i + 1} of 4");
            InputMethod.SetIsInputMethodEnabled(box, false);
            box.PreviewTextInput += OnPreviewTextInput;
            box.PreviewKeyDown += OnPreviewKeyDown;
            box.TextChanged += OnTextChanged;
            box.GotKeyboardFocus += OnGotKeyboardFocus;
            DataObject.AddPastingHandler(box, OnPasting);
            _parts[i] = box;
            Children.Add(box);
        }

        _pause = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(800) };
        _pause.Tick += (_, _) => Commit(focusLeft: false);
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                Commit(focusLeft: true);
            }
        };
    }

    public string Address
    {
        get => (string)GetValue(AddressProperty);
        set => SetValue(AddressProperty, value);
    }

    public ICommand? EditedCommand
    {
        get => (ICommand?)GetValue(EditedCommandProperty);
        set => SetValue(EditedCommandProperty, value);
    }

    public void FocusFirst() => _parts[0].Focus();

    private static int IndexOf(object box) => (int)((TextBox)box).Tag;

    private void Show(string? address)
    {
        _pause.Stop();
        _showing = true;
        var octets = Ipv4Text.TryParse(address, out var parsed) ? parsed : null;
        for (var i = 0; i < _parts.Length; i++)
        {
            var text = octets is null ? string.Empty : octets[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_parts[i].Text != text)
            {
                _parts[i].Text = text;
            }
        }

        _showing = false;
        _edited = false;
    }

    // After a pause only a whole address counts; when focus leaves, an empty part counts as 0. With every part
    // empty nothing changes, and the boxes show the address that is set.
    private void Commit(bool focusLeft)
    {
        _pause.Stop();
        if (!_edited)
        {
            return;
        }

        if (_parts.All(part => part.Text.Length == 0))
        {
            if (focusLeft)
            {
                Show(Address);
            }

            return;
        }

        if (_parts.Any(part => part.Text.Length == 0))
        {
            if (!focusLeft)
            {
                return;
            }

            _showing = true;
            foreach (var part in _parts.Where(part => part.Text.Length == 0))
            {
                part.Text = "0";
            }

            _showing = false;
        }

        _edited = false;
        var address = string.Join('.', _parts.Select(part => Ipv4Text.Octet(part.Text)));
        if (address != Address)
        {
            Address = address;
        }
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_showing)
        {
            return;
        }

        _edited = true;
        _pause.Stop();
        _pause.Start();
        if (EditedCommand is { } edited && edited.CanExecute(null))
        {
            edited.Execute(null);
        }
    }

    // Keyboard focus from Tab or a move selects the part, so typing replaces it; a click places the caret.
    private void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            ((TextBox)sender).SelectAll();
        }
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = true;
        var box = (TextBox)sender;
        if (e.Text is "." or ",")
        {
            Move(IndexOf(box) + 1, atEnd: false);
        }
        else if (e.Text.All(char.IsAsciiDigit))
        {
            Type(box, e.Text);
        }
    }

    // The typed digits replace the selection; the part then clamps, so 3, 0, 0 reads 255.
    private static void Type(TextBox box, string digits)
    {
        var start = box.SelectionStart;
        var typed = box.Text.Remove(start, box.SelectionLength).Insert(start, digits);
        var text = Ipv4Text.Octet(typed);
        box.Text = text;
        box.CaretIndex = Math.Min(start + digits.Length, text.Length);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        var index = IndexOf(box);
        var caretAtStart = box.SelectionLength == 0 && box.CaretIndex == 0;
        var caretAtEnd = box.SelectionLength == 0 && box.CaretIndex == box.Text.Length;
        switch (e.Key)
        {
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown:
                box.Text = Ipv4Text.Step(box.Text, e.Key switch { Key.Up => 1, Key.Down => -1, Key.PageUp => 10, _ => -10 });
                box.CaretIndex = box.Text.Length;
                e.Handled = true;
                break;
            // TextBox takes Space before PreviewTextInput, so it is caught here.
            case Key.Space:
                Move(index + 1, atEnd: false);
                e.Handled = true;
                break;
            case Key.Right when caretAtEnd && index < _parts.Length - 1:
                e.Handled = Move(index + 1, atEnd: false);
                break;
            case Key.Left when caretAtStart && index > 0:
            case Key.Back when box.Text.Length == 0 && index > 0:
                e.Handled = Move(index - 1, atEnd: true);
                break;
        }
    }

    private bool Move(int index, bool atEnd)
    {
        if (index < 0 || index >= _parts.Length)
        {
            return false;
        }

        var box = _parts[index];
        box.Focus();
        if (atEnd)
        {
            box.CaretIndex = box.Text.Length;
        }
        else
        {
            box.SelectAll();
        }

        return true;
    }

    // A whole address pasted anywhere fills all four parts; anything else pastes only its digits.
    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        if (e.SourceDataObject.GetData(DataFormats.UnicodeText, true) is not string text)
        {
            return;
        }

        if (Ipv4Text.TryParse(text, out var octets))
        {
            for (var i = 0; i < _parts.Length; i++)
            {
                _parts[i].Text = octets[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            Move(_parts.Length - 1, atEnd: true);
            return;
        }

        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length > 0)
        {
            Type((TextBox)sender, digits);
        }
    }
}
