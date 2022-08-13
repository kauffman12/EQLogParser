#region Copyright Syncfusion Inc. 2001-2022.
// Copyright Syncfusion Inc. 2001-2022. All rights reserved.
// Use of this code is subject to the terms of our license.
// A copy of the current license can be obtained at any time by e-mailing
// licensing@syncfusion.com. Any infringement will be prosecuted under
// applicable laws. 
#endregion
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Syncfusion.Themes.MaterialLightCustom.WPF
{
    /// <summary>
    /// Helper class to handle acrylic background for any target UI element.
    /// </summary>
    /// <<exclude/>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [System.ComponentModel.Browsable(false)]
    public class SfAcrylicPanel : ContentControl
    {
        private Rectangle panelRect;

        /// <summary>
        /// Gets or sets the <see cref="SfAcrylicPanel.BackgroundTarget"/> property value that denotes the object UI which will be utilized as acrylic background layer in application.
        /// </summary>
        /// <value>
        /// The <see cref="FrameworkElement"/> Target object. The default value is <b>null</b>.
        /// </value>
        public FrameworkElement BackgroundTarget
        {
            get { return (FrameworkElement)GetValue(BackgroundTargetProperty); }
            set { SetValue(BackgroundTargetProperty, value); }
        }

        /// <summary>
        /// Identifies the <see cref="SfAcrylicPanel.BackgroundTarget" /> dependency property to get or set this property to denote the object UI which will be utilized as acrylic background layer in application.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="SfAcrylicPanel.BackgroundTargetProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty BackgroundTargetProperty =
            DependencyProperty.Register("BackgroundTarget", typeof(FrameworkElement), typeof(SfAcrylicPanel), new PropertyMetadata(null));

        /// <summary>
        /// Gets or sets the <see cref="SfAcrylicPanel.Source"/> property value that denotes the target object for which acrylic background should be applied.
        /// </summary>
        /// <value>
        /// The <see cref="FrameworkElement"/> Source object. The default value is <b>null</b>.
        /// </value>
        public FrameworkElement Source
        {
            get { return (FrameworkElement)GetValue(SourceProperty); }
            set { SetValue(SourceProperty, value); }
        }

        /// <summary>
        /// Identifies the <see cref="SfAcrylicPanel.Source" /> dependency property to get or set this property to denote the target object for which acrylic background should be applied.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="SfAcrylicPanel.SourceProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register("Source", typeof(FrameworkElement), typeof(SfAcrylicPanel), new PropertyMetadata(null));

        /// <summary>
        /// Gets or sets the <see cref="SfAcrylicPanel.TintBrush"/> property value which will be utilized as tint layer brush to apply acrylic background layer in application.
        /// </summary>
        /// <value>
        /// The <see cref="Brush"/> to achieve tint layer. The default value is <b><see cref="Brushes.White"/></b>.
        /// </value>     
        public Brush TintBrush
        {
            get { return (Brush)GetValue(TintBrushProperty); }
            set { SetValue(TintBrushProperty, value); }
        }

        /// <summary>
        /// Identifies the <see cref="SfAcrylicPanel.TintBrush" /> dependency property to get or set this property to utilize as tint layer brush to apply acrylic background layer in application.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="SfAcrylicPanel.TintBrushProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty TintBrushProperty =
            DependencyProperty.Register("TintBrush", typeof(Brush), typeof(SfAcrylicPanel), new PropertyMetadata(new SolidColorBrush(Colors.White)));

        /// <summary>
        /// Gets or sets the <see cref="SfAcrylicPanel.NoiseBrush"/> property value which will be utilized as noise layer brush to apply acrylic background layer in application.
        /// </summary>
        /// <value>
        /// The <see cref="Brush"/> to achieve noise layer. The default value is <b><see cref="Brushes.Transparent"/></b>.
        /// </value>   
        public Brush NoiseBrush
        {
            get { return (Brush)GetValue(NoiseBrushProperty); }
            set { SetValue(NoiseBrushProperty, value); }
        }

        /// <summary>
        /// Identifies the <see cref="SfAcrylicPanel.NoiseBrush" /> dependency property to get or set this property to utilize as noise layer brush to apply acrylic background layer in application.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="SfAcrylicPanel.NoiseBrushProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty NoiseBrushProperty =
            DependencyProperty.Register("NoiseBrush", typeof(Brush), typeof(SfAcrylicPanel), new PropertyMetadata(new SolidColorBrush(Colors.White)));

        /// <summary>
        /// Gets or sets the <see cref="SfAcrylicPanel.TintOpacity"/> property value which will be utilized as tint layer opacity to apply acrylic background layer in application.
        /// </summary>
        /// <value>
        /// The <see cref="double"/> to achieve tint layer. The default value is <b>0.3</b>.
        /// </value>  
        public double TintOpacity
        {
            get { return (double)GetValue(TintOpacityProperty); }
            set { SetValue(TintOpacityProperty, value); }
        }

        /// <summary>
        /// Identifies the <see cref="SfAcrylicPanel.TintOpacity" /> dependency property to get or set this property to utilize as tint layer opacity to apply acrylic background layer in application.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="SfAcrylicPanel.TintOpacityProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty TintOpacityProperty =
            DependencyProperty.Register("TintOpacity", typeof(double), typeof(SfAcrylicPanel), new PropertyMetadata(0.3));
        
        /// <summary>
        /// Gets or sets the <see cref="SfAcrylicPanel.NoiseOpacity"/> property value which will be utilized as noise layer opacity to apply acrylic background layer in application.
        /// </summary>
        /// <value>
        /// The <see cref="double"/> to achieve noise layer. The default value is <b>0.9</b>.
        /// </value>  
        public double NoiseOpacity
        {
            get { return (double)GetValue(NoiseOpacityProperty); }
            set { SetValue(NoiseOpacityProperty, value); }
        }

        /// <summary>
        /// Identifies the <see cref="SfAcrylicPanel.NoiseOpacity" /> dependency property to get or set this property to utilize as noise layer opacity to apply acrylic background layer in application.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="SfAcrylicPanel.NoiseOpacityProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty NoiseOpacityProperty =
            DependencyProperty.Register("NoiseOpacity", typeof(double), typeof(SfAcrylicPanel), new PropertyMetadata(0.9));
        
        /// <summary>
        /// Gets or sets the <see cref="SfAcrylicPanel.BlurRadius"/> property value which will be apply blur radius for acrylic background layer in application.
        /// </summary>
        /// <value>
        /// The <see cref="double"/> to achieve blur effect. The default value is <b>90</b>.
        /// </value>   
        public double BlurRadius
        {
            get { return (double)GetValue(BlurRadiusProperty); }
            set { SetValue(BlurRadiusProperty, value); }
        }

        /// <summary>
        /// Identifies the <see cref="SfAcrylicPanel.BlurRadius" /> dependency property to get or set this property to apply blur radius for acrylic background layer in application.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="SfAcrylicPanel.BlurRadiusProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty BlurRadiusProperty =
            DependencyProperty.Register("BlurRadius", typeof(double), typeof(SfAcrylicPanel), new PropertyMetadata(90.0));

        static SfAcrylicPanel()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(SfAcrylicPanel), new FrameworkPropertyMetadata(typeof(SfAcrylicPanel)));
        }


        public SfAcrylicPanel()
        {
            Source = this;
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            panelRect = this.GetTemplateChild("panelRect") as Rectangle;
            if (panelRect != null)
            {
                panelRect.LayoutUpdated += (_, __) =>
                {
                    if (BackgroundTarget != null)
                    {
                        var relativePosition = BackgroundTarget.TranslatePoint(new Point(0, 0), this.Source);
                        panelRect.RenderTransform = new TranslateTransform(relativePosition.X, relativePosition.Y);
                    }
                };
            }
        }
    }
}
