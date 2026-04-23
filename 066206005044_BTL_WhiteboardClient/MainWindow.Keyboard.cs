using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Btl.Whiteboard.Client;

public sealed partial class MainWindow
{
    private async void MainWindow_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        UpdateAltKeyState(e.Key, isPressed: true);
        UpdateSpaceKeyState(e.Key, isPressed: true);

        if (IsControlKeyDown() && (e.Key == VirtualKey.Number0 || e.Key == VirtualKey.NumberPad0))
        {
            ResetCanvasViewport();
            e.Handled = true;
            return;
        }

        if (IsTextInputFocused())
        {
            if (TryHandlePanelTabShortcut(e.Key))
            {
                e.Handled = true;
            }
            return;
        }

        if (TryHandlePanelTabShortcut(e.Key))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.P:
                SelectDrawingTool(DrawingTool.Pencil);
                e.Handled = true;
                break;

            case VirtualKey.L:
                SelectDrawingTool(DrawingTool.Line);
                e.Handled = true;
                break;

            case VirtualKey.R:
                SelectDrawingTool(DrawingTool.Rectangle);
                e.Handled = true;
                break;

            case VirtualKey.O:
                SelectDrawingTool(DrawingTool.Ellipse);
                e.Handled = true;
                break;

            case VirtualKey.E:
                SelectDrawingTool(DrawingTool.Eraser);
                e.Handled = true;
                break;

            case VirtualKey.N:
                LayerPanelAddButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case VirtualKey.U:
                await SendUndoAsync();
                e.Handled = true;
                break;

            case VirtualKey.Y:
                await SendRedoAsync();
                e.Handled = true;
                break;

            case VirtualKey.Delete:
                LayerDeleteButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private void MainWindow_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        UpdateAltKeyState(e.Key, isPressed: false);
        UpdateSpaceKeyState(e.Key, isPressed: false);
    }

    private void UpdateAltKeyState(VirtualKey key, bool isPressed)
    {
        if (key is VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu)
        {
            isAltKeyPressed = isPressed;
        }
    }

    private void UpdateSpaceKeyState(VirtualKey key, bool isPressed)
    {
        if (key == VirtualKey.Space)
        {
            isSpaceKeyPressed = isPressed;
        }
    }

    private bool IsAltPressedForZoom()
    {
        if (isAltKeyPressed)
        {
            return true;
        }

        CoreVirtualKeyStates genericAlt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu);
        if ((genericAlt & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down)
        {
            return true;
        }

        CoreVirtualKeyStates leftAlt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftMenu);
        if ((leftAlt & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down)
        {
            return true;
        }

        CoreVirtualKeyStates rightAlt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightMenu);
        return (rightAlt & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private bool IsSpacePressedForPan()
    {
        if (isSpaceKeyPressed)
        {
            return true;
        }

        CoreVirtualKeyStates spaceKey = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Space);
        return (spaceKey & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private static bool IsControlKeyDown()
    {
        CoreVirtualKeyStates keyStates = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        return (keyStates & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private bool TryHandlePanelTabShortcut(VirtualKey key)
    {
        if (RightPanelTabView is null)
        {
            return false;
        }

        int targetIndex = key switch
        {
            VirtualKey.F1 => 0,
            VirtualKey.F2 => 1,
            VirtualKey.F3 => 2,
            _ => -1
        };

        if (targetIndex < 0 || targetIndex >= RightPanelTabView.TabItems.Count)
        {
            return false;
        }

        RightPanelTabView.SelectedIndex = targetIndex;
        return true;
    }

    private bool IsTextInputFocused()
    {
        if (Content is not FrameworkElement root || root.XamlRoot is null)
        {
            return false;
        }

        return FocusManager.GetFocusedElement(root.XamlRoot) is TextBox;
    }
}