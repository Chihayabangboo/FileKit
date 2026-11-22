using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FileKit.TestEffects;

/// <summary>
/// 样例特效共用的 Overlay 创建工具：半透明圆角卡片 + 白色文字，不拦截点击。
/// </summary>
internal static class SampleOverlay
{
    internal static Border Create(
        string text,
        Color background,
        HorizontalAlignment horizontalAlignment,
        VerticalAlignment verticalAlignment,
        Thickness margin)
    {
        return new Border
        {
            Background = new SolidColorBrush(background),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 9, 14, 9),
            Margin = margin,
            HorizontalAlignment = horizontalAlignment,
            VerticalAlignment = verticalAlignment,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 14,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
            },
            Tag = "filekit.dev.sample-overlay",
        };
    }
}
