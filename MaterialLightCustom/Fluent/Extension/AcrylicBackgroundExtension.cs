#region Copyright Syncfusion Inc. 2001-2022.
// Copyright Syncfusion Inc. 2001-2022. All rights reserved.
// Use of this code is subject to the terms of our license.
// A copy of the current license can be obtained at any time by e-mailing
// licensing@syncfusion.com. Any infringement will be prosecuted under
// applicable laws. 
#endregion
using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;

namespace Syncfusion.Themes.MaterialLightCustom.WPF
{
    /// <summary>
    /// Helper markup extension class to apply acrylic background for different containers
    /// </summary>
    /// <example>
    /// <code language="XAML">
    /// <![CDATA[
    /// <Grid x:Name="acrylicTargetContainer" Background="Red">
    /// <Border Width = "300" Height="200" HorizontalAlignment="Center" VerticalAlignment="Center" Background="{syncfusion:AcrylicBackground BackgroundLayerElement={Binding ElementName= acrylicTargetContainer}">
    /// <TextBlock Text = "Testing" HorizontalAlignment="Center" VerticalAlignment="Center"/>
    /// </Border>
    /// </Grid>
    /// ]]>
    /// </code>
    /// </example>
    /// <exclude/>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [System.ComponentModel.Browsable(false)]
    internal class AcrylicBackgroundExtension : MarkupExtension
    {
        /// <summary>
        /// Gets or sets the <see cref="AcrylicBackgroundExtension.BackgroundLayerElement"/> whose object UI will be utilized as acrylic background layer in application.
        /// </summary>
        /// <value>
        /// The <see cref="FrameworkElement"/> Target object. The default value is <b>null</b>.
        /// </value>     
        public FrameworkElement BackgroundLayerElement { get; set; }

        private Brush tintBrush = Brushes.White;

        /// <summary>
        /// Gets or sets the <see cref="AcrylicBackgroundExtension.TintBrush"/> property value which will be utilized as tint layer brush to apply acrylic background layer in application.
        /// </summary>
        /// <value>
        /// The <see cref="Brush"/> to achieve tint layer. The default value is <b><see cref="Brushes.White"/></b>.
        /// </value>      
        public Brush TintBrush
        {
            get { return tintBrush; }
            set { tintBrush = value; }
        }

        private Brush noiseBrush = Brushes.Transparent;

        /// <summary>
        /// Gets or sets the <see cref="AcrylicBackgroundExtension.NoiseBrush"/> property value which will be utilized as noise layer brush to apply acrylic background layer in application.
        /// </summary>
        /// <value>
        /// The <see cref="Brush"/> to achieve noise layer. The default value is <b><see cref="Brushes.Transparent"/></b>.
        /// </value>    
        public Brush NoiseBrush
        {
            get { return noiseBrush; }
            set { noiseBrush = value; }
        }

        private double tintOpacity = 0.3;

        /// <summary>
        /// Gets or sets the <see cref="AcrylicBackgroundExtension.TintOpacity"/> property value which will be utilized as tint layer opacity to apply acrylic background layer in application.
        /// </summary>
        /// <value>
        /// The <see cref="double"/> to achieve tint layer. The default value is <b>0.3</b>.
        /// </value>   
        public double TintOpacity
        {
            get { return tintOpacity; }
            set { tintOpacity = value; }
        }

        private double noiseOpacity = 0.9;

        /// <summary>
        /// Gets or sets the <see cref="AcrylicBackgroundExtension.NoiseOpacity"/> value which will be utilized as noise layer opacity to apply acrylic background layer in application.
        /// </summary>
        /// <value>
        /// The <see cref="double"/> to achieve noise layer. The default value is <b>0.9</b>.
        /// </value>     
        public double NoiseOpacity
        {
            get { return noiseOpacity; }
            set { noiseOpacity = value; }
        }

        private double blurRadius = 90.0;

        /// <summary>
        /// Gets or sets the <see cref="AcrylicBackgroundExtension.BlurRadius"/> property value which will be apply blur radius for acrylic background layer in application.
        /// </summary>
        /// <value>
        /// The <see cref="double"/> to achieve blur effect. The default value is <b>90</b>.
        /// </value>   
        public double BlurRadius
        {
            get { return blurRadius; }
            set { blurRadius = value; }
        }

        /// <summary>
        /// Default constructor receiving the target name to achieve acrylic background layer in application
        /// </summary>
        public AcrylicBackgroundExtension()
        {

        }
        /// <summary>
        /// Constructor receiving the target name to achieve acrylic background layer in application
        /// </summary>
        /// <param name="target">Target object</param>
        public AcrylicBackgroundExtension(FrameworkElement target)
        {
            this.BackgroundLayerElement = target;
        }

        /// <summary>
        /// Helper method to process the acrylic background visual brush.
        /// </summary>
        /// <param name="serviceProvider">IServiceProvider</param>
        /// <returns>Acrylic Visual Brush</returns>
        /// <inheritdoc/>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var pvt = serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
            var target = pvt.TargetObject as FrameworkElement;

            var acrylicPanel = new SfAcrylicPanel()
            {
                TintBrush = this.TintBrush,
                NoiseBrush = this.NoiseBrush,
                TintOpacity = this.TintOpacity,
                NoiseOpacity = this.NoiseOpacity,
                BlurRadius = this.BlurRadius,
                BackgroundTarget = this.BackgroundLayerElement,
                Source= target,
                Width = target.Width,
                Height = target.Height
            };

            var brush = new VisualBrush(acrylicPanel)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
            };

            return brush;
        }
    }
}
