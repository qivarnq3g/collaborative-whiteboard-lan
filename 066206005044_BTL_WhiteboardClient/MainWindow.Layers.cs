using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace Btl.Whiteboard.Client;

public sealed partial class MainWindow
{
    private void ActiveLayerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isUpdatingLayerSelection || isMutatingLayersCollection)
        {
            return;
        }

        if (ActiveLayerComboBox.SelectedItem is not LayerState selectedLayer)
        {
            return;
        }

        activeLayerId = selectedLayer.LayerId;
        UpdateLayerControls();
        UpdateToolStatusText();
    }

    private void LayersListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isUpdatingLayerSelection || isMutatingLayersCollection)
        {
            return;
        }

        List<LayerState> selectedLayers = GetSelectedLayers();
        if (selectedLayers.Count == 0)
        {
            UpdateLayerControls();
            return;
        }

        LayerState? preferredLayer = e.AddedItems
            .OfType<LayerState>()
            .OrderBy(layer => layers.IndexOf(layer))
            .LastOrDefault();

        if (preferredLayer is null &&
            LayersListView.SelectedItem is LayerState selectedItem &&
            selectedLayers.Any(layer => string.Equals(layer.LayerId, selectedItem.LayerId, StringComparison.Ordinal)))
        {
            preferredLayer = selectedItem;
        }

        if (preferredLayer is null)
        {
            preferredLayer = selectedLayers
                .OrderBy(layer => layers.IndexOf(layer))
                .LastOrDefault();
        }

        if (preferredLayer is not null)
        {
            activeLayerId = preferredLayer.LayerId;
        }

        LayerState activeLayer = GetActiveLayerState();

        isUpdatingLayerSelection = true;
        ActiveLayerComboBox.SelectedItem = activeLayer;
        isUpdatingLayerSelection = false;

        UpdateLayerControls();
        UpdateToolStatusText();
    }

    private void LayersListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LayerState clickedLayer)
        {
            return;
        }

        SelectLayerFromListInteraction(clickedLayer);
    }

    private void LayerRowControl_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LayerState focusedLayer })
        {
            return;
        }

        SelectLayerFromListInteraction(focusedLayer);
    }

    private void SelectLayerFromListInteraction(LayerState layer)
    {
        if (isUpdatingLayerSelection)
        {
            return;
        }

        if (string.Equals(activeLayerId, layer.LayerId, StringComparison.Ordinal))
        {
            return;
        }

        EndLayerNameEditing();

        isUpdatingLayerSelection = true;
        ActiveLayerComboBox.SelectedItem = layer;
        isUpdatingLayerSelection = false;

        activeLayerId = layer.LayerId;
        UpdateLayerControls();
        UpdateToolStatusText();
    }

    private void LayersListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ReindexLayerOrder();
        UpdateLayerControls();
    }

    private void AddLayerButton_Click(object sender, RoutedEventArgs e)
    {
        LayerState newLayer = new()
        {
            LayerId = $"layer-{Guid.NewGuid():N}",
            Name = $"Layer {nextLayerIndex}",
            IsVisible = true,
            IsLocked = false,
            Opacity = 1.0,
            Order = layers.Count
        };

        nextLayerIndex++;
        RegisterLayer(newLayer, selectLayer: true);
        UpdateToolStatusText();
    }

    private void LayerPanelAddButton_Click(object sender, RoutedEventArgs e)
    {
        AddLayerButton_Click(sender, e);
    }

    private void LayerDuplicateButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedLayerCount() > 1)
        {
            SetStatus("Select one layer to duplicate");
            return;
        }

        LayerState sourceLayer = GetActiveLayerState();
        LayerState duplicatedLayer = new()
        {
            LayerId = $"layer-{Guid.NewGuid():N}",
            Name = $"{sourceLayer.Name} Copy",
            IsVisible = sourceLayer.IsVisible,
            IsLocked = false,
            Opacity = sourceLayer.Opacity,
            Order = sourceLayer.Order
        };

        RegisterLayer(duplicatedLayer, selectLayer: false);
        int sourceIndex = layers.IndexOf(sourceLayer);
        int duplicatedIndex = layers.IndexOf(duplicatedLayer);
        if (sourceIndex >= 0 && duplicatedIndex >= 0)
        {
            int targetIndex = Math.Min(sourceIndex + 1, layers.Count - 1);
            layers.Move(duplicatedIndex, targetIndex);
        }

        ReindexLayerOrder();
        activeLayerId = duplicatedLayer.LayerId;
        UpdateLayerControls();
    }

    private void LayerDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        List<LayerState> selectedLayers = GetSelectedLayers();
        if (selectedLayers.Count == 0)
        {
            selectedLayers.Add(GetActiveLayerState());
        }

        if (layers.Count - selectedLayers.Count < 1)
        {
            SetConnectionState("Error", "Keep at least one layer");
            return;
        }

        HashSet<string> selectedLayerIds = new(selectedLayers.Select(layer => layer.LayerId), StringComparer.Ordinal);
        LayerState fallbackLayer = layers.First(layer => !selectedLayerIds.Contains(layer.LayerId));

        foreach (LayerState layerToDelete in selectedLayers)
        {
            ReassignLayerShapes(layerToDelete.LayerId, fallbackLayer.LayerId);
            layersById.Remove(layerToDelete.LayerId);
        }

        for (int index = layers.Count - 1; index >= 0; index--)
        {
            if (!selectedLayerIds.Contains(layers[index].LayerId))
            {
                continue;
            }

            layers.RemoveAt(index);
        }

        ReindexLayerOrder();

        activeLayerId = fallbackLayer.LayerId;

        isUpdatingLayerSelection = true;
        LayersListView.SelectedItems.Clear();
        LayersListView.SelectedItem = fallbackLayer;
        ActiveLayerComboBox.SelectedItem = fallbackLayer;
        isUpdatingLayerSelection = false;

        UpdateLayerControls();
    }

    private void LayerMoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedLayerCount() > 1)
        {
            SetStatus("Select one layer to move up");
            return;
        }

        LayerState layer = GetActiveLayerState();
        int currentIndex = layers.IndexOf(layer);
        if (currentIndex <= 0)
        {
            return;
        }

        layers.Move(currentIndex, currentIndex - 1);
        ReindexLayerOrder();
        UpdateLayerControls();
    }

    private void LayerMoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedLayerCount() > 1)
        {
            SetStatus("Select one layer to move down");
            return;
        }

        LayerState layer = GetActiveLayerState();
        int currentIndex = layers.IndexOf(layer);
        if (currentIndex < 0 || currentIndex >= layers.Count - 1)
        {
            return;
        }

        layers.Move(currentIndex, currentIndex + 1);
        ReindexLayerOrder();
        UpdateLayerControls();
    }

    private void LayerMergeDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedLayerCount() > 1)
        {
            SetStatus("Select one layer to merge");
            return;
        }

        LayerState sourceLayer = GetActiveLayerState();
        int sourceIndex = layers.IndexOf(sourceLayer);
        if (sourceIndex <= 0)
        {
            SetConnectionState("Error", "Can't merge the bottom layer");
            return;
        }

        LayerState targetLayer = layers[sourceIndex - 1];
        ReassignLayerShapes(sourceLayer.LayerId, targetLayer.LayerId);
        layersById.Remove(sourceLayer.LayerId);
        layers.RemoveAt(sourceIndex);
        ReindexLayerOrder();

        activeLayerId = targetLayer.LayerId;
        UpdateLayerControls();
    }

    private void LayerOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (isUpdatingLayerOpacity)
        {
            return;
        }

        LayerState activeLayer = GetActiveLayerState();
        activeLayer.Opacity = NormalizeLayerOpacity(e.NewValue / 100.0);
        ApplyLayerVisualState(activeLayer.LayerId);
        UpdateLayerPanelVisuals(activeLayer);
    }

    private void ToggleLayerVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        LayerState layer = GetActiveLayerState();
        layer.IsVisible = !layer.IsVisible;

        UpdateLayerControls();
        ApplyLayerVisualState(layer.LayerId);
    }

    private void ToggleLayerLockButton_Click(object sender, RoutedEventArgs e)
    {
        LayerState layer = GetActiveLayerState();
        layer.IsLocked = !layer.IsLocked;

        UpdateLayerControls();
    }

    private void LayerVisibilityCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LayerState layer } frameworkElement)
        {
            return;
        }

        bool isVisible = frameworkElement is ToggleButton toggleButton
            ? toggleButton.IsChecked == true
            : layer.IsVisible;

        activeLayerId = layer.LayerId;
        layer.IsVisible = isVisible;
        ApplyLayerVisualState(layer.LayerId);

        UpdateLayerControls();
    }

    private void LayerLockCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LayerState layer } frameworkElement)
        {
            return;
        }

        bool isLocked = frameworkElement is ToggleButton toggleButton
            ? toggleButton.IsChecked == true
            : layer.IsLocked;

        activeLayerId = layer.LayerId;
        layer.IsLocked = isLocked;

        UpdateLayerControls();
    }

    private void LayerNameTextBlock_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LayerState layer } sourceElement)
        {
            return;
        }

        SelectLayerFromListInteraction(layer);
        BeginLayerNameEditing(layer, sourceElement);
        e.Handled = true;
    }

    private void BeginLayerNameEditing(LayerState targetLayer, FrameworkElement sourceElement)
    {
        foreach (LayerState layer in layers)
        {
            layer.IsNameEditing = string.Equals(layer.LayerId, targetLayer.LayerId, StringComparison.Ordinal);
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ListViewItem? rowContainer = FindAncestor<ListViewItem>(sourceElement);
            if (rowContainer is null)
            {
                return;
            }

            TextBox? nameEditor = FindDescendantByName<TextBox>(rowContainer, "LayerNameEditorTextBox");
            if (nameEditor is null)
            {
                return;
            }

            nameEditor.Focus(FocusState.Programmatic);
            nameEditor.SelectAll();
        });
    }

    private void EndLayerNameEditing()
    {
        foreach (LayerState layer in layers)
        {
            layer.IsNameEditing = false;
        }
    }

    private void LayerNameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not LayerState layer)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            textBox.Text = layer.Name;
            layer.IsNameEditing = false;
            return;
        }

        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        CommitLayerName(textBox);
    }

    private void LayerNameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not LayerState layer || !layer.IsNameEditing)
        {
            return;
        }

        CommitLayerName(textBox);
    }

    private void CommitLayerName(TextBox textBox)
    {
        if (textBox.DataContext is not LayerState layer)
        {
            return;
        }

        string normalizedName = textBox.Text.Trim();
        if (normalizedName.Length == 0)
        {
            normalizedName = $"Layer {Math.Max(1, layers.IndexOf(layer) + 1)}";
        }

        layer.Name = normalizedName;
        layer.IsNameEditing = false;
        textBox.Text = normalizedName;
        UpdateLayerControls();
    }

    private void InitializeLayers()
    {
        layers.Clear();
        layersById.Clear();

        LayerState baseLayer = new()
        {
            LayerId = "layer-1",
            Name = "Layer 1",
            IsVisible = true,
            IsLocked = false,
            Opacity = 1.0,
            Order = 0
        };

        RegisterLayer(baseLayer, selectLayer: true);
        nextLayerIndex = 2;
    }

    private LayerState RegisterLayer(LayerState layer, bool selectLayer)
    {
        if (layersById.TryGetValue(layer.LayerId, out LayerState? existingLayer))
        {
            return existingLayer;
        }

        layersById[layer.LayerId] = layer;
        isMutatingLayersCollection = true;
        try
        {
            layers.Add(layer);
        }
        finally
        {
            isMutatingLayersCollection = false;
        }

        ReindexLayerOrder();

        if (selectLayer)
        {
            activeLayerId = layer.LayerId;
            RefreshLayerComboSelection();
        }

        UpdateLayerControls();
        return layer;
    }

    private LayerState EnsureLayerExists(string layerId)
    {
        string safeLayerId = string.IsNullOrWhiteSpace(layerId) ? "layer-1" : layerId.Trim();

        if (layersById.TryGetValue(safeLayerId, out LayerState? existingLayer))
        {
            return existingLayer;
        }

        LayerState createdLayer = new()
        {
            LayerId = safeLayerId,
            Name = $"Layer {nextLayerIndex}",
            IsVisible = true,
            IsLocked = false,
            Opacity = 1.0,
            Order = layers.Count
        };

        nextLayerIndex++;
        return RegisterLayer(createdLayer, selectLayer: false);
    }

    private LayerState GetActiveLayerState()
    {
        if (layersById.TryGetValue(activeLayerId, out LayerState? activeLayer))
        {
            return activeLayer;
        }

        LayerState? fallbackLayer = layers.FirstOrDefault();
        if (fallbackLayer is not null)
        {
            activeLayerId = fallbackLayer.LayerId;
            return fallbackLayer;
        }

        return EnsureLayerExists("layer-1");
    }

    private List<LayerState> GetSelectedLayers()
    {
        return LayersListView.SelectedItems.OfType<LayerState>().ToList();
    }

    private void RefreshLayerSelectionStates()
    {
        HashSet<string> selectedLayerIds = new(
            LayersListView.SelectedItems.OfType<LayerState>().Select(layer => layer.LayerId),
            StringComparer.Ordinal);

        foreach (LayerState layer in layers)
        {
            layer.IsSelected = selectedLayerIds.Contains(layer.LayerId);
        }
    }

    private int GetSelectedLayerCount()
    {
        return GetSelectedLayers().Count;
    }

    private LayerState? GetPrimarySelectedLayer()
    {
        if (LayersListView.SelectedItem is LayerState selectedLayer)
        {
            return selectedLayer;
        }

        return LayersListView.SelectedItems.OfType<LayerState>().FirstOrDefault();
    }

    private void RefreshLayerComboSelection()
    {
        if (ActiveLayerComboBox is null || LayersListView is null)
        {
            return;
        }

        LayerState? selectedLayer = layersById.TryGetValue(activeLayerId, out LayerState? layer)
            ? layer
            : layers.FirstOrDefault();

        isUpdatingLayerSelection = true;
        ActiveLayerComboBox.SelectedItem = selectedLayer;
        if (selectedLayer is not null && LayersListView.SelectedItems.Count <= 1)
        {
            LayersListView.SelectedItem = selectedLayer;
        }

        isUpdatingLayerSelection = false;
    }

    private void RefreshLayerActiveStates()
    {
        foreach (LayerState layer in layers)
        {
            layer.IsActive = string.Equals(layer.LayerId, activeLayerId, StringComparison.Ordinal);
        }
    }

    private void UpdateLayerControls()
    {
        RefreshLayerComboSelection();
        RefreshLayerSelectionStates();
        LayerState activeLayer = GetActiveLayerState();
        RefreshLayerActiveStates();
        ToggleLayerVisibilityButton.Content = activeLayer.IsVisible ? "Hide" : "Show";
        ToggleLayerLockButton.Content = activeLayer.IsLocked ? "Unlock" : "Lock";
        if (isRootVisualReady)
        {
            UpdateLayerPanelVisuals(activeLayer);
        }

        UpdateToolStatusText();
    }

    private void UpdateLayerPanelVisuals(LayerState activeLayer)
    {
        if (LayersEmptyTextBlock is null ||
            LayerDuplicateButton is null ||
            LayerDeleteButton is null ||
            LayerMoveUpButton is null ||
            LayerMoveDownButton is null ||
            LayerMergeDownButton is null ||
            LayerOpacitySlider is null ||
            LayerOpacityValueTextBlock is null)
        {
            return;
        }

        LayersEmptyTextBlock.Visibility = layers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int activeIndex = layers.IndexOf(activeLayer);
        int selectedCount = GetSelectedLayerCount();
        if (selectedCount == 0 && layers.Count > 0)
        {
            selectedCount = 1;
        }

        bool hasSingleSelection = selectedCount == 1;
        bool canDeleteSelection = selectedCount > 0 && (layers.Count - selectedCount) >= 1;

        LayerDuplicateButton.IsEnabled = layers.Count > 0 && hasSingleSelection;
        LayerDeleteButton.IsEnabled = canDeleteSelection;
        LayerMoveUpButton.IsEnabled = hasSingleSelection && activeIndex > 0;
        LayerMoveDownButton.IsEnabled = hasSingleSelection && activeIndex >= 0 && activeIndex < layers.Count - 1;
        LayerMergeDownButton.IsEnabled = hasSingleSelection && activeIndex > 0;
        UpdateLayerActionTooltips(activeIndex, selectedCount, canDeleteSelection, activeLayer);

        isUpdatingLayerOpacity = true;
        LayerOpacitySlider.Value = Math.Round(activeLayer.Opacity * 100.0, 0);
        LayerOpacityValueTextBlock.Text = $"{Math.Round(activeLayer.Opacity * 100.0, 0):0}%";
        isUpdatingLayerOpacity = false;
    }

    private void UpdateLayerActionTooltips(int activeIndex, int selectedCount, bool canDeleteSelection, LayerState activeLayer)
    {
        ToolTipService.SetToolTip(LayerPanelAddButton, "Create a new layer above current selection.");
        ToolTipService.SetToolTip(
            LayerDuplicateButton,
            selectedCount > 1
                ? "Select one layer to duplicate"
                : "Create a copy of the selected layer.");

        ToolTipService.SetToolTip(
            LayerDeleteButton,
            canDeleteSelection
                ? selectedCount > 1
                    ? $"Delete {selectedCount} selected layers and move strokes to a fallback layer."
                    : "Delete selected layer and move strokes to a fallback layer."
                : "Keep at least one layer");

        ToolTipService.SetToolTip(
            LayerMoveUpButton,
            selectedCount > 1
                ? "Select one layer to move up"
                : activeIndex > 0
                ? "Move selected layer one step up."
                : "Top layer cannot move further up.");

        ToolTipService.SetToolTip(
            LayerMoveDownButton,
            selectedCount > 1
                ? "Select one layer to move down"
                : activeIndex >= 0 && activeIndex < layers.Count - 1
                ? "Move selected layer one step down."
                : "Bottom layer cannot move further down.");

        ToolTipService.SetToolTip(
            LayerMergeDownButton,
            selectedCount > 1
                ? "Select one layer to merge"
                : activeIndex > 0
                ? "Merge selected layer into the layer below."
                : "No lower layer available to merge into.");

        ToolTipService.SetToolTip(LayerOpacitySlider, $"Layer opacity: {Math.Round(activeLayer.Opacity * 100.0, 0):0}%");
    }

    private void ReindexLayerOrder()
    {
        for (int index = 0; index < layers.Count; index++)
        {
            layers[index].Order = index;
        }

        RefreshAllLayerVisualStates();
    }

    private void ReassignLayerShapes(string sourceLayerId, string targetLayerId)
    {
        if (DrawingCanvas is null)
        {
            return;
        }

        foreach (Shape shape in DrawingCanvas.Children.OfType<Shape>())
        {
            if (!string.Equals(shape.Tag as string, sourceLayerId, StringComparison.Ordinal))
            {
                continue;
            }

            ApplyLayerToShape(shape, targetLayerId);
        }

        string[] strokeIds = strokeLayers
            .Where(entry => string.Equals(entry.Value, sourceLayerId, StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .ToArray();

        foreach (string strokeId in strokeIds)
        {
            strokeLayers[strokeId] = targetLayerId;
        }
    }

    private void SelectFirstDrawableLayer()
    {
        LayerState? fallbackLayer = layers.FirstOrDefault(layer => layer.IsVisible && !layer.IsLocked)
            ?? layers.FirstOrDefault();

        if (fallbackLayer is null)
        {
            return;
        }

        activeLayerId = fallbackLayer.LayerId;
        RefreshLayerComboSelection();
    }

    private void ApplyLayerVisualState(string layerId)
    {
        if (DrawingCanvas is null)
        {
            return;
        }

        if (!layersById.TryGetValue(layerId, out LayerState? layer))
        {
            return;
        }

        foreach (Shape shape in DrawingCanvas.Children.OfType<Shape>())
        {
            if (!string.Equals(shape.Tag as string, layerId, StringComparison.Ordinal))
            {
                continue;
            }

            shape.Visibility = layer.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            shape.Opacity = layer.Opacity;
            Canvas.SetZIndex(shape, layer.Order);
        }
    }

    private void RefreshAllLayerVisualStates()
    {
        foreach (LayerState layer in layers)
        {
            ApplyLayerVisualState(layer.LayerId);
        }
    }

    private void ApplyLayerToShape(Shape shape, string layerId)
    {
        LayerState layer = EnsureLayerExists(layerId);
        shape.Tag = layer.LayerId;
        shape.Visibility = layer.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        shape.Opacity = layer.Opacity;
        Canvas.SetZIndex(shape, layer.Order);
    }
}
