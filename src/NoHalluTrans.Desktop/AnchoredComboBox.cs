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
    private ContentPresenter? openSelection;
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
            bool openBelow = below >= Math.Min(MaxDropDownHeight, 160) || below >= above;
            double height = Math.Min(MaxDropDownHeight, openBelow ? below : above);
            if (listPopup.Child is FrameworkElement content)
            {
                content.MaxHeight = height;
                double listHeight = Math.Min(content.ActualHeight > 0 ? content.ActualHeight : height, height);
                listPopup.VerticalOffset = openBelow ? local.Y + field.ActualHeight + 4 : local.Y - listHeight - 4;
            }
            listPopup.HorizontalOffset = local.X;
        }
        finally { positioning = false; }
    }
}
