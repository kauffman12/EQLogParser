using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Syncfusion.Themes.MaterialDarkCustom.WPF
{
    /// <summary>
    /// Helper adorner class to handle reveal item hover and pressed animation for UI element.
    /// </summary>
    /// <<exclude/>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [System.ComponentModel.Browsable(false)]
    public class RevealItemAdorner : Adorner
    {
        public RevealItem RevealItem
        {
            get; set;
        }

        public RevealItemAdorner(UIElement adornedElement, RevealItem revealElement)
          : base(adornedElement)
        {
            RevealItem = revealElement;
            this.IsHitTestVisible = false;
            adornedElement.PreviewMouseMove += AdornedElement_PreviewMouseMove;
            adornedElement.PreviewMouseDown += AdornedElement_PreviewMouseDown;
            adornedElement.MouseLeave += AdornedElement_MouseLeave;
        }

        private void AdornedElement_MouseLeave(object sender, MouseEventArgs e)
        {
            RevealItem.IsMouseOver = false;
        }

        private void AdornedElement_PreviewMouseDown(object sender, MouseEventArgs e)
        {
            RevealItem.Position = e.GetPosition(sender as IInputElement);
            RevealItem.StartPressedRevealAnimation();
        }

        private void AdornedElement_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                RevealItem.IsPressed = false;
            else
                RevealItem.IsPressed = true;

            RevealItem.IsMouseOver = true;
            RevealItem.Position = e.GetPosition(sender as IInputElement);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0;
            double y = 0;
            RevealItem.Arrange(new Rect(x, y, AdornedElement.RenderSize.Width, AdornedElement.RenderSize.Height));
            RevealItem.UpdateRevealBorderSize(AdornedElement.RenderSize.Width);
            return finalSize;
        }

        /// <<exclude/>
        public void Dispose()
        {
            AdornedElement.PreviewMouseMove -= AdornedElement_PreviewMouseMove;
            AdornedElement.PreviewMouseDown -= AdornedElement_PreviewMouseDown;
            AdornedElement.MouseLeave -= AdornedElement_MouseLeave;
        }

        protected override int VisualChildrenCount { get { return 1; } }
        protected override Visual GetVisualChild(int index) { return RevealItem; }
    }
}
