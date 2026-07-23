// SPDX-License-Identifier: MIT
using System;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Engine.Editor.Behaviors;

public class NumericInputBehavior
{
    private static readonly Regex NumericRegex = new Regex(@"^[-+]?[0-9]*[.,]?[0-9]*$", RegexOptions.Compiled);

    public static readonly AttachedProperty<bool> IsNumericOnlyProperty =
        AvaloniaProperty.RegisterAttached<NumericInputBehavior, Control, bool>(
            "IsNumericOnly",
            defaultValue: false);

    static NumericInputBehavior()
    {
        IsNumericOnlyProperty.Changed.Subscribe(OnIsNumericOnlyChanged);
    }

    public static bool GetIsNumericOnly(Control control) => control.GetValue(IsNumericOnlyProperty);
    public static void SetIsNumericOnly(Control control, bool value) => control.SetValue(IsNumericOnlyProperty, value);

    private static void OnIsNumericOnlyChanged(AvaloniaPropertyChangedEventArgs<bool> e)
    {
        if (e.Sender is Control control)
        {
            if (e.NewValue.Value)
            {
                control.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
                control.AddHandler(InputElement.LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble);
            }
            else
            {
                control.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
                control.RemoveHandler(InputElement.LostFocusEvent, OnLostFocus);
            }
        }
    }

    private static void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;

        if (sender is TextBox tb)
        {
            string prospectiveText = GetProspectiveText(tb, e.Text);
            if (!NumericRegex.IsMatch(prospectiveText))
            {
                e.Handled = true;
            }
        }
        else if (sender is NumericUpDown)
        {
            if (!Regex.IsMatch(e.Text, @"^[0-9.,-]+$"))
            {
                e.Handled = true;
            }
        }
    }

    private static void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is NumericUpDown nud)
        {
            if (!nud.Value.HasValue)
            {
                nud.Value = nud.Minimum;
            }
        }
        else if (sender is TextBox tb)
        {
            if (string.IsNullOrWhiteSpace(tb.Text) || !double.TryParse(tb.Text.Replace(',', '.'), out _))
            {
                tb.Text = "0";
            }
        }
    }

    private static string GetProspectiveText(TextBox tb, string input)
    {
        string text = tb.Text ?? string.Empty;
        int selectionStart = tb.SelectionStart;
        int selectionEnd = tb.SelectionEnd;

        if (selectionEnd > selectionStart)
        {
            text = text.Remove(selectionStart, selectionEnd - selectionStart);
        }

        return text.Insert(selectionStart, input);
    }
}
