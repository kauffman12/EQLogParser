#region Copyright Syncfusion Inc. 2001-2022.
// Copyright Syncfusion Inc. 2001-2022. All rights reserved.
// Use of this code is subject to the terms of our license.
// A copy of the current license can be obtained at any time by e-mailing
// licensing@syncfusion.com. Any infringement will be prosecuted under
// applicable laws. 
#endregion
using Syncfusion.SfSkinManager;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Syncfusion.Themes.MaterialLightCustom.WPF
{
    /// <summary>
    /// Helper class to achieve reveal hover and pressed animation 
    /// </summary>
    /// <exclude/>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [System.ComponentModel.Browsable(false)]
    public class RevealItem : ContentControl
    {
        private Color RevealBackground = Colors.White;
        private double RevealBackgroundSize = 100;
        private double RevealBackgroundOpacity = 0.2;
        private Color RevealBorder = Colors.White;
        private double RevealBorderSize = 60;
        private double RevealBorderOpacity = 0.8;

        private Border revealBackground;
        private Border revealBorder;

        private RadialGradientBrush revealPressedRectBrush;

        private Grid revealGrid;

        private Storyboard revealPressedStoryboard;

        /// <summary>
        /// Static constructor to handle reveal animations.
        /// </summary>
        static RevealItem()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(RevealItem), new FrameworkPropertyMetadata(typeof(RevealItem)));
        }

        /// <summary>
        /// Default constructor to handle reveal animations.
        /// </summary>
        public RevealItem()
        {
        }

        /// <summary>
        /// Helper method to handle template change operations for <see cref="RevealItem"/>
        /// </summary>
        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            revealBackground = GetTemplateChild("backgroundMouseOver") as Border;
            revealBorder = GetTemplateChild("borderOpacityMask") as Border;
            revealPressedRectBrush = GetTemplateChild("pressedRectBrush") as RadialGradientBrush;
            revealGrid = GetTemplateChild("rootGrid") as Grid;

            this.UpdateAnimationDetails();
        }

        /// <summary>
        /// Gets or sets the <see cref="RevealItem.HoverEffectMode"/> attached property value that denotes the hover animation to be applied on UIElement.
        /// </summary>
        /// <value>
        /// The default value is <see cref="HoverEffect.BackgroundAndBorder"/>.
        /// <b>Fields:</b>
        /// <list type="table">
        /// <listheader>
        /// <term>Enumeration</term>
        /// <description>Description.</description>
        /// </listheader>
        /// <item>
        /// <term><see cref="HoverEffect.BackgroundAndBorder"/></term>
        /// <description>The hover animation will be applied for both background and border</description>
        /// </item>  
        /// <item>
        /// <term><see cref="HoverEffect.Border"/></term>
        /// <description>The hover animation will be applied for border only</description>
        /// </item>  
        /// <item>
        /// <term><see cref="HoverEffect.Background"/></term>
        /// <description>The hover animation will be applied for background only</description>
        /// </item>  
        /// <item>
        /// <term><see cref="HoverEffect.None"/></term>
        /// <description>The hover animation will not be applied</description>
        /// </item>       
        /// </list>
        /// </value>
        /// <exclude/>
        public HoverEffect HoverEffectMode
        {
            get { return (HoverEffect)GetValue(HoverEffectModeProperty); }
            set { SetValue(HoverEffectModeProperty, value); }
        }

        /// <summary>
        /// Gets or sets the <see cref="RevealItem.PressedEffectMode"/> attached property value that denotes the pressed animation to be applied on UIElement.
        /// </summary>
        /// <value>
        /// The default value is <see cref="PressedEffect.Reveal"/>.
        /// <b>Fields:</b>
        /// <list type="table">
        /// <listheader>
        /// <term>Enumeration</term>
        /// <description>Description.</description>
        /// </listheader>
        /// <item>
        /// <term><see cref="PressedEffect.Reveal"/></term>
        /// <description>The pressed reveal animation will be applied</description>
        /// </item>  
        /// <item>
        /// <term><see cref="PressedEffect.Glow"/></term>
        /// <description>The pressed glow animation will be applied</description>
        /// </item>  
        /// <item>
        /// <term><see cref="PressedEffect.None"/></term>
        /// <description>The pressed animation will not be applied</description>
        /// </item>     
        /// </list>
        /// </value>
        /// <exclude/>
        public PressedEffect PressedEffectMode
        {
            get { return (PressedEffect)GetValue(PressedEffectModeProperty); }
            set { SetValue(PressedEffectModeProperty, value); }
        }

        /// <summary>
        /// Gets or sets the <see cref="RevealItem.HoverBorder"/> property value that denotes the hover border to be applied on UIElement.
        /// </summary>
        /// <value>
        /// The <see cref="Brush"/> value. The default value is <see cref="Brushes.Transparent"/>.
        /// </value>
        /// <exclude/>
        public Brush HoverBorder
        {
            get { return (Brush)GetValue(HoverBorderProperty); }
            set { SetValue(HoverBorderProperty, value); }
        }

        /// <summary>
        /// Gets or sets the <see cref="RevealItem.HoverBackground"/> property value that denotes the hover background to be applied on UIElement.
        /// </summary>
        /// <value>
        /// The <see cref="Brush"/> value. The default value is <see cref="Brushes.Transparent"/>.
        /// </value>
        /// <exclude/>
        public Brush HoverBackground
        {
            get { return (Brush)GetValue(HoverBackgroundProperty); }
            set { SetValue(HoverBackgroundProperty, value); }
        }

        /// <summary>
        /// Gets or sets the <see cref="RevealItem.PressedBackground"/> property value that denotes the pressed background to be applied on UIElement.
        /// </summary>
        /// <value>
        /// The <see cref="Brush"/> value. The default value is <see cref="Brushes.Transparent"/>.
        /// </value>
        /// <exclude/>
        public Brush PressedBackground
        {
            get { return (Brush)GetValue(PressedBackgroundProperty); }
            set { SetValue(PressedBackgroundProperty, value); }
        }

        /// <summary>
        /// Gets or sets the <see cref="RevealItem.CornerRadius"/> property value that denotes the corner radius of hover and pressed border to be applied on UIElement.
        /// </summary>
        /// <value>
        /// The <see cref="CornerRadius"/> value. The default value is <b>0</b>.
        /// </value>
        /// <exclude/>
        public CornerRadius CornerRadius
        {
            get { return (CornerRadius)GetValue(CornerRadiusProperty); }
            set { SetValue(CornerRadiusProperty, value); }
        }

        /// <summary>
        /// Gets or sets the <see cref="RevealItem.PressedBorderOpacity"/> property value that denotes the pressed border opacity to be applied on UIElement.
        /// </summary>
        /// <value>
        /// The <see cref="double"/> value. The default value is <b>0.2</b>.
        /// </value>
        /// <exclude/>
        public double PressedBorderOpacity
        {
            get { return (double)GetValue(PressedBorderOpacityProperty); }
            set { SetValue(PressedBorderOpacityProperty, value); }
        }

        /// <summary>
        /// Gets or sets the <see cref="RevealItem.HoverBorderOpacity"/> property value that denotes the hover border opacity to be applied on UIElement.
        /// </summary>
        /// <value>
        /// The <see cref="double"/> value. The default value is <b>0.4</b>.
        /// </value>
        /// <exclude/>
        public double HoverBorderOpacity
        {
            get { return (double)GetValue(HoverBorderOpacityProperty); }
            set { SetValue(HoverBorderOpacityProperty, value); }
        }

        /// <summary>
        /// Gets or sets the <see cref="RevealItem.Position"/> attached property value that denotes the hover cursor position of the given UIElement.
        /// </summary>
        /// <value>
        /// The <see cref="Point"/> value. 
        /// </value>
        /// <exclude/>
        public Point Position
        {
            get { return (Point)GetValue(PositionProperty); }
            set { SetValue(PositionProperty, value); }
        }

        /// <inheritdoc/>
        public new bool IsMouseOver
        {
            get { return (bool)GetValue(IsMouseOverProperty); }
            set { SetValue(IsMouseOverProperty, value); }
        }

        /// <summary>
        /// Gets or sets the <see cref="RevealItem.IsPressed"/> attached property value that denotes whether the control is pressed or not.
        /// </summary>
        /// <value>
        /// The <see cref="bool"/> value. The default value is <b>false</b>.
        /// </value>
        /// <exclude/>
        public bool IsPressed
        {
            get { return (bool)GetValue(IsPressedProperty); }
            set { SetValue(IsPressedProperty, value); }
        }

        /// <summary>
        /// Identifies the <see cref="RevealItem.Position" /> dependency property to get or set this property to decide on the hover position.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="RevealItem.PositionProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty PositionProperty =
            DependencyProperty.Register("Position", typeof(Point), typeof(RevealItem), new PropertyMetadata(new Point(0, 0), OnPositionChanged));

        /// <summary>
        /// Identifies the <see cref="RevealItem.HoverEffectMode" /> dependency property to get or set this property to decide on the hover animation to be applied.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="RevealItem.HoverEffectModeProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty HoverEffectModeProperty =
            DependencyProperty.Register("HoverEffectMode", typeof(HoverEffect), typeof(RevealItem), new PropertyMetadata(HoverEffect.BackgroundAndBorder));

        /// <summary>
        /// Identifies the <see cref="RevealItem.PressedEffectMode" /> dependency property to get or set this property to decide on the pressed animation to be applied.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="RevealItem.PressedEffectModeProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty PressedEffectModeProperty =
            DependencyProperty.Register("PressedEffectMode", typeof(PressedEffect), typeof(RevealItem), new PropertyMetadata(PressedEffect.Reveal));

        /// <inheritdoc/>
        public static readonly new DependencyProperty IsMouseOverProperty =
            DependencyProperty.Register("IsMouseOver", typeof(bool), typeof(RevealItem), new FrameworkPropertyMetadata(false));

        /// <summary>
        /// Identifies the <see cref="RevealItem.IsPressed" /> dependency property to get or set this property to decide on whether the control is pressed or not.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="RevealItem.IsPressedProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty IsPressedProperty =
            DependencyProperty.Register("IsPressed", typeof(bool), typeof(RevealItem), new FrameworkPropertyMetadata(false));

        /// <summary>
        /// Identifies the <see cref="RevealItem.HoverBackground" /> dependency property to get or set this property to decide on the hover background to be applied for hover animation.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="RevealItem.HoverBackgroundProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty HoverBackgroundProperty =
            DependencyProperty.Register("HoverBackground", typeof(Brush), typeof(RevealItem), new FrameworkPropertyMetadata(Brushes.Transparent));

        /// <summary>
        /// Identifies the <see cref="RevealItem.PressedBackground" /> dependency property to get or set this property to decide on the pressed background to be applied for pressed animation.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="RevealItem.PressedBackgroundProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty PressedBackgroundProperty =
            DependencyProperty.Register("PressedBackground", typeof(Brush), typeof(RevealItem), new FrameworkPropertyMetadata(Brushes.Transparent));

        /// <summary>
        /// Identifies the <see cref="RevealItem.HoverBorder" /> dependency property to get or set this property to decide on the hover border to be applied for hover animation.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="RevealItem.HoverBorderProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty HoverBorderProperty =
            DependencyProperty.Register("HoverBorder", typeof(Brush), typeof(RevealItem), new FrameworkPropertyMetadata(Brushes.Transparent));

        /// <summary>
        /// Identifies the <see cref="RevealItem.CornerRadius" /> dependency property to get or set this property to decide on the corner radius to be applied for hover animation.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="RevealItem.CornerRadiusProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register("CornerRadius", typeof(CornerRadius), typeof(RevealItem), new FrameworkPropertyMetadata(new CornerRadius(0)));

        /// <summary>
        /// Identifies the <see cref="RevealItem.PressedBorderOpacity" /> dependency property to get or set this property to decide on the pressed border opacity to be applied for pressed animation.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="RevealItem.PressedBorderOpacityProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty PressedBorderOpacityProperty =
            DependencyProperty.Register("PressedBorderOpacity", typeof(double), typeof(RevealItem), new PropertyMetadata(0.2));

        /// <summary>
        /// Identifies the <see cref="RevealItem.HoverBorderOpacity" /> dependency property to get or set this property to decide on the hover border opacity to be applied for hover animation.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="RevealItem.HoverBorderOpacityProperty" /> dependency property.
        /// </remarks>
        public static readonly DependencyProperty HoverBorderOpacityProperty =
            DependencyProperty.Register("HoverBorderOpacity", typeof(double), typeof(RevealItem), new PropertyMetadata(0.4));

        /// <summary>
        /// Helper method to handle hover position changed actions
        /// </summary>
        /// <param name="d">Dependency Object</param>
        /// <param name="e">Dependency EventArgs</param>
        private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            RevealItem revealItem = d as RevealItem;

            revealItem.UpdateAnimationDetails();
        }

        /// <summary>
        /// Method to update reveal animation border width
        /// </summary>
        /// <param name="width">Control width</param>
        public void UpdateRevealBorderSize(double width)
        {
            this.RevealBackgroundSize = ((width * 50) / 100);
            this.RevealBorderSize = ((width * 30) / 100);
        }

        /// <summary>
        /// Method to update animation settings to apply hover and pressed animation
        /// </summary>
        private void UpdateAnimationDetails()
        {
            if (this.revealBackground != null)
            {
                this.revealBackground.Background = this.GetRevealBrushValue(this.RevealBackground, this.RevealBackgroundSize, this.RevealBackgroundOpacity, this.Position, this.revealBackground.Background);
            }
            if (this.revealBorder != null)
            {
                this.revealBorder.OpacityMask = this.GetRevealBrushValue(this.RevealBorder, this.RevealBorderSize, this.RevealBorderOpacity, this.Position, this.revealBorder.OpacityMask);
            }
            if (this.revealPressedRectBrush != null)
            {
                this.revealPressedRectBrush.Center = this.Position;
                this.revealPressedRectBrush.GradientOrigin = this.Position;
                this.revealPressedRectBrush.GradientStops[1].Color = (this.PressedBackground as SolidColorBrush).Color;
            }
        }

        /// <summary>
        /// Helper method to get reveal gradient brush value based on position, color and size details
        /// </summary>
        /// <param name="color">Reveal color</param>
        /// <param name="size">Reveal Size</param>
        /// <param name="opacity">Reveal opacity</param>
        /// <param name="position">Reveal position</param>
        /// <param name="brush">Reveal brush to be changer</param>
        /// <returns>The <see cref="Brush"/> value</returns>
        private Brush GetRevealBrushValue(Color color, double size, double opacity, Point position, Brush brush)
        {
            RadialGradientBrush radialGradientBrush;
            Color revealColor = Color.FromArgb(0, color.R, color.G, color.B);
            if (!(brush is RadialGradientBrush))
                radialGradientBrush = new RadialGradientBrush(color, revealColor);
            else
                radialGradientBrush = brush as RadialGradientBrush;
            radialGradientBrush.MappingMode = BrushMappingMode.Absolute;
            radialGradientBrush.RadiusX = size;
            radialGradientBrush.RadiusY = size;

            radialGradientBrush.Opacity = opacity;
            radialGradientBrush.Center = position;
            radialGradientBrush.GradientOrigin = position;
            return radialGradientBrush;
        }

        /// <summary>
        /// Method to start pressed reveal animation
        /// </summary>
        internal void StartPressedRevealAnimation()
        {
            if (revealPressedStoryboard == null && revealGrid != null)
                revealPressedStoryboard = PressedEffectMode == PressedEffect.Reveal ? revealGrid.Resources["PressedRevealStoryboard"] as Storyboard : PressedEffectMode == PressedEffect.Glow ? revealGrid.Resources["PressedGlowStoryboard"] as Storyboard : null;

            if (revealPressedStoryboard != null)
            {
                revealPressedStoryboard.Completed += RevealPressedStoryboard_Completed;
                revealPressedStoryboard.Begin();
            }
        }

        /// <summary>
        /// Helper method to handle storyboard completion action
        /// </summary>
        /// <param name="sender">object</param>
        /// <param name="e">EventArgs</param>
        private void RevealPressedStoryboard_Completed(object sender, System.EventArgs e)
        {
            if (revealPressedStoryboard != null)
            {
                revealPressedStoryboard.Stop();
                revealPressedStoryboard.Completed -= RevealPressedStoryboard_Completed;
            }
        }
    }
}
