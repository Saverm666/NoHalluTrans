using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

namespace NoHalluTrans.Desktop;

/// <summary>Uses the native ComboBox list with an edge anchored to the selection field.</summary>
public sealed class AnchoredComboBox : ComboBox
{
    private Popup? listPopup;
    private long verticalToken, horizontalToken;
    private bool positioning;
    private int positionRetries;
    private ContentPresenter? openSelection;
    private TextBox? editableText;
    private Point pressPoint;
    private bool focusedByPointer;
    /// <summary>
    /// The editable ComboBox selects all text when it takes focus. For a mouse click, replace that
    /// select-all with a caret at the clicked character; a drag selection or keyboard focus is left alone.
    /// </summary>
    private void PlaceCaretAtPress(TextBox editor)
    {
        focusedByPointer = false;
        int length = editor.Text.Length;
        if (length == 0 || editor.SelectionLength != length) return;
        int caret = 0;
        while (caret < length)
        {
            var lead = editor.GetRectFromCharacterIndex(caret, false);
            var trail = editor.GetRectFromCharacterIndex(caret, true);
            if ((lead.X + trail.X) / 2 > pressPoint.X) break;
            caret++;
        }
        editor.Select(caret, 0);
    }
    public AnchoredComboBox()
    {
        DefaultStyleKey = typeof(ComboBox);
        DropDownOpened += (_, _) =>
        {
            if (openSelection is not null && !IsEditable)
            {
                openSelection.Content = SelectedItem;
                openSelection.ContentTemplate = ItemTemplate;
                openSelection.Visibility = Visibility.Visible;
            }
            PositionList();
        };
        DropDownClosed += (_, _) => { if (openSelection is not null) { openSelection.Visibility = Visibility.Collapsed; openSelection.Content = null; } };
    }
    protected override void OnApplyTemplate()
    {
        if (listPopup is not null)
        {
            listPopup.UnregisterPropertyChangedCallback(Popup.VerticalOffsetProperty, verticalToken);
            listPopup.UnregisterPropertyChangedCallback(Popup.HorizontalOffsetProperty, horizontalToken);
        }
        base.OnApplyTemplate();
        if (GetTemplateChild("LayoutRoot") is Grid root)
        {
            openSelection = new ContentPresenter { Margin = Padding, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            Grid.SetRow(openSelection, 1); Grid.SetColumn(openSelection, 0);
            root.Children.Add(openSelection);
        }
        if (GetTemplateChild("EditableText") is TextBox editor && !ReferenceEquals(editor, editableText))
        {
            editableText = editor;
            editor.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) => pressPoint = e.GetCurrentPoint(editor).Position), true);
            editor.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => { if (focusedByPointer) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => PlaceCaretAtPress(editor)); }), true);
            editor.GotFocus += (_, _) => focusedByPointer = editor.FocusState == FocusState.Pointer;
        }
        listPopup = GetTemplateChild("Popup") as Popup;
        if (listPopup is not null)
        {
            verticalToken = listPopup.RegisterPropertyChangedCallback(Popup.VerticalOffsetProperty, (_, _) => PositionList());
            horizontalToken = listPopup.RegisterPropertyChangedCallback(Popup.HorizontalOffsetProperty, (_, _) => PositionList());
        }
    }
    private void PositionList()
    {
        if (positioning || listPopup is null || XamlRoot is null || !IsDropDownOpen || GetTemplateChild("Background") is not FrameworkElement field) return;
        positioning = true;
        try
        {
            Point local = field.TransformToVisual(this).TransformPoint(new Point());
            Point root = field.TransformToVisual(XamlRoot.Content).TransformPoint(new Point());
            double below = Math.Max(0, XamlRoot.Size.Height - root.Y - field.ActualHeight - 8);
            double above = Math.Max(0, root.Y - 8);
            // Size the list from its items rather than ActualHeight, which is stale or zero while the popup opens.
            double itemHeight = ContainerFromIndex(0) is FrameworkElement { ActualHeight: > 0 } first ? first.ActualHeight : 37;
            double natural = Math.Min(MaxDropDownHeight, Math.Max(1, Items.Count) * itemHeight + 12);
            // Prefer below: a list that fits, or one that can scroll in at least 160px, stays under the field.
            bool openBelow = below >= Math.Min(natural, 160) || below >= above;
            double height = Math.Min(MaxDropDownHeight, openBelow ? below : above);
            // While the popup opens, layout can briefly report an unplaced field; clamping to that would
            // collapse the list. Retry on the next frame instead (bounded, so a genuinely tiny window still settles).
            if ((field.ActualHeight <= 0 || XamlRoot.Size.Height <= 0 || height < 48) && positionRetries++ < 5)
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, PositionList);
                return;
            }
            positionRetries = 0;
            if (listPopup.Child is FrameworkElement content)
            {
                if (content.MaxHeight != height) content.MaxHeight = height;
                double listHeight = Math.Min(natural, height);
                listPopup.VerticalOffset = openBelow ? local.Y + field.ActualHeight + 4 : local.Y - listHeight - 4;
            }
            listPopup.HorizontalOffset = local.X;
        }
        finally { positioning = false; }
    }
}
