#region Copyright Syncfusion Inc. 2001-2022.
// Copyright Syncfusion Inc. 2001-2022. All rights reserved.
// Use of this code is subject to the terms of our license.
// A copy of the current license can be obtained at any time by e-mailing
// licensing@syncfusion.com. Any infringement will be prosecuted under
// applicable laws. 
#endregion
using Syncfusion.SfSkinManager;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace Syncfusion.Themes.MaterialLightCustom.WPF
{
    /// <summary>
    /// Helper class to achieve fluent theme hover and pressed animation 
    /// </summary>
    /// <example>
    /// <code language="XAML">
    /// <![CDATA[
    ///    <Border Width = "300" Height="200" HorizontalAlignment="Center" VerticalAlignment="Center">
    ///        <TextBlock Text = "Testing" HorizontalAlignment="Center" VerticalAlignment="Center"/>
    ///    </Border>
    /// ]]>
    /// </code>
    /// </example>
    /// <exclude/>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [System.ComponentModel.Browsable(false)]
    public class FluentHelper
    {
        static FluentHelper()
        {
            
        }

        #region Attached properties

        /// <summary>
        /// Gets the <see cref="FluentHelper.HoverEffectModeProperty"/> attached property value that denotes the hover animation to be applied on UIElement.
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
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [System.ComponentModel.Browsable(false)]
        public static HoverEffect GetHoverEffectMode(DependencyObject obj)
        {
            return (HoverEffect)obj.GetValue(HoverEffectModeProperty);
        }

        /// <summary>
        /// Sets the <see cref="FluentHelper.HoverEffectModeProperty"/> attached property value that denotes the hover animation to be applied on UIElement.
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
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [System.ComponentModel.Browsable(false)]
        public static void SetHoverEffectMode(DependencyObject obj, HoverEffect value)
        {
            obj.SetValue(HoverEffectModeProperty, value);
        }

        /// <summary>
        /// Gets the <see cref="FluentHelper.PressedEffectModeProperty"/> attached property value that denotes the pressed animation to be applied on UIElement.
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
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [System.ComponentModel.Browsable(false)]
        public static PressedEffect GetPressedEffectMode(DependencyObject obj)
        {
            return (PressedEffect)obj.GetValue(PressedEffectModeProperty);
        }

        /// <summary>
        /// Sets the <see cref="FluentHelper.PressedEffectModeProperty"/> attached property value that denotes the pressed animation to be applied on UIElement.
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
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [System.ComponentModel.Browsable(false)]
        public static void SetPressedEffectMode(DependencyObject obj, PressedEffect value)
        {
            obj.SetValue(PressedEffectModeProperty, value);
        }

        /// <summary>
        /// Gets the <see cref="RevealItemProperty"/> attached property value that denotes the <see cref="RevealItem"/> instance to decide on animation settings to apply for hover and pressed animation.
        /// </summary>
        /// <value>
        /// The <see cref="RevealItem"/> value. The default value is <b>null</b>.
        /// </value>
        /// <exclude/>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [System.ComponentModel.Browsable(false)]
        public static RevealItem GetRevealItem(DependencyObject obj)
        {
            return (RevealItem)obj.GetValue(RevealItemProperty);
        }

        /// <summary>
        /// Sets the <see cref="RevealItemProperty"/> attached property value that denotes the <see cref="RevealItem"/> instance to decide on animation settings to apply for hover and pressed animation.
        /// </summary>
        /// <value>
        /// The <see cref="RevealItem"/> value. The default value is <b>null</b>.
        /// </value>
        /// </value>
        /// <example>
        /// <code language="XAML">
        /// <![CDATA[
        /// 
        ///     <skinmanager:RevealItem x:Key="revealItem" HoverBackground="LightGray" HoverBorder="Black" PressedBackground="LightGray" CornerRadius="2"/>
        /// 
        ///     <Border Width = "300" Height="200" HorizontalAlignment="Center" VerticalAlignment="Center" syncfusion:FluentHelper.RevealItem="{StaticResource revealItem}">
        ///         <TextBlock Text = "Testing" HorizontalAlignment="Center" VerticalAlignment="Center"/>
        ///     </Border>
        ///    
        /// ]]>
        /// </code>
        /// </example>
        /// <exclude/>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [System.ComponentModel.Browsable(false)]
        public static void SetRevealItem(DependencyObject obj, RevealItem value)
        {
            obj.SetValue(RevealItemProperty, value);
        }

        /// <summary>
        /// Identifies the <see cref="FluentHelper.RevealItemProperty" /> dependency attached property to get or set the Reveal item instance to decide on animation settings to apply for hover and pressed animation.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="FluentHelper.RevealItemProperty" /> dependency attached property.
        /// </remarks>
        /// <exclude/>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [System.ComponentModel.Browsable(false)]
        public static readonly DependencyProperty RevealItemProperty =
            DependencyProperty.RegisterAttached("RevealItem", typeof(RevealItem), typeof(FluentHelper), new PropertyMetadata(null, OnRevealItemChanged));

        /// <summary>
        /// Identifies the <see cref="FluentHelper.HoverEffectModeProperty" /> dependency attached property to get or set this property to decide on the hover animation to be applied.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="FluentHelper.HoverEffectModeProperty" /> dependency attached property.
        /// </remarks>
        /// <exclude/>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [System.ComponentModel.Browsable(false)]
        public static readonly DependencyProperty HoverEffectModeProperty =
            DependencyProperty.RegisterAttached("HoverEffectMode", typeof(HoverEffect), typeof(FluentHelper), new FrameworkPropertyMetadata(HoverEffect.BackgroundAndBorder, FrameworkPropertyMetadataOptions.Inherits));

        /// <summary>
        /// Identifies the <see cref="FluentHelper.PressedEffectModeProperty" /> dependency attached property to get or set this property to decide on the pressed animation to be applied.
        /// </summary>
        /// <remarks>
        /// The identifier for the <see cref="FluentHelper.PressedEffectModeProperty" /> dependency attached property.
        /// </remarks>
        /// <exclude/>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [System.ComponentModel.Browsable(false)]
        public static readonly DependencyProperty PressedEffectModeProperty =
            DependencyProperty.RegisterAttached("PressedEffectMode", typeof(PressedEffect), typeof(FluentHelper), new FrameworkPropertyMetadata(PressedEffect.Reveal, FrameworkPropertyMetadataOptions.Inherits));
        #endregion

        #region Methods
        /// <summary>
        /// Helper method to handle <see cref="RevealItemProperty"/> property changed actions
        /// </summary>
        /// <param name="d">Dependency object</param>
        /// <param name="e">Dependency EventArgs</param>
        private static void OnRevealItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var revealElement = GetRevealItem(d);
            if (revealElement != null)
            {
                if (GetHoverEffectMode(d) != HoverEffect.None || GetPressedEffectMode(d) != PressedEffect.None)
                    (d as UIElement).MouseEnter += FluentControl_MouseEnter;
            }
        }

        /// <summary>
        /// Helper method to handle mouse leave operation related to hover and pressed animation.
        /// </summary>
        /// <param name="sender">object</param>
        /// <param name="e">MouseEventArgs</param>
        private static void FluentControl_MouseLeave(object sender, MouseEventArgs e)
        {
            DependencyObject dependencyObject = sender as DependencyObject;
            UIElement uIElement = sender as UIElement;
            if (uIElement != null)
            {
                var revealElement = GetRevealItem(dependencyObject);
                if (revealElement != null)
                {
                    AdornerLayer adornerLayer = AdornerLayer.GetAdornerLayer(uIElement);
                    if (adornerLayer != null)
                    {
                        Adorner[] toRemoveArray = adornerLayer.GetAdorners(sender as UIElement);
                        Adorner toRemove;
                        if (toRemoveArray != null)
                        {
                            toRemove = toRemoveArray[0];
                            adornerLayer.Remove(toRemove);
                        }
                    }
                    uIElement.MouseLeave -= FluentControl_MouseLeave;
                }
            }
        }

        /// <summary>
        /// Helper method to handle mouse enter operation related to hover and pressed animation.
        /// </summary>
        /// <param name="sender">object</param>
        /// <param name="e">MouseEventArgs</param>
        private static void FluentControl_MouseEnter(object sender, MouseEventArgs e)
        {
            DependencyObject dependencyObject = sender as DependencyObject;
            UIElement uIElement = sender as UIElement;
            if (uIElement != null)
            {
                AdornerLayer adorner = AdornerLayer.GetAdornerLayer(uIElement);
                var revealElement = GetRevealItem(dependencyObject);
                if (revealElement != null && adorner != null)
                {
                    var hoverEffect = GetHoverEffectMode(dependencyObject);
                    var pressedEffect = GetPressedEffectMode(dependencyObject);

                    var defaultHoverEffect = RevealItem.HoverEffectModeProperty.DefaultMetadata;
                    if (!hoverEffect.Equals(defaultHoverEffect.DefaultValue))
                        revealElement.HoverEffectMode = hoverEffect;


                    var defaultPressedEffect = RevealItem.HoverEffectModeProperty.DefaultMetadata;
                    if (!pressedEffect.Equals(defaultPressedEffect.DefaultValue))
                        revealElement.PressedEffectMode = pressedEffect;

                    adorner.Add(new RevealItemAdorner(uIElement, revealElement));
                    uIElement.MouseLeave += FluentControl_MouseLeave;
                }
            }
        }
        #endregion
    }
}
