using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FileKit.Contracts;

namespace FileKit.TestPlugin;

/// <summary>
/// M2 自测插件：不包含业务逻辑，只验证契约、ALC 加载与 UI 生成链路。
/// </summary>
public sealed class TestPlugin : IFileKitPlugin
{
    private IPluginContext? _context;
    private bool _disposed;

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public UserControl CreateMainView()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TestPlugin));
        }

        var title = new TextBlock
        {
            Text = "测试插件已成功加载！",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var detail = new TextBlock
        {
            Text = $"插件 ID：{_context?.PluginId}    HostVersion：{_context?.HostVersion}",
            FontSize = 14,
            Margin = new Thickness(0, 14, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var dataDirectory = new TextBlock
        {
            Text = $"数据目录：{_context?.DataDirectory}",
            FontSize = 13,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(40),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(title);
        panel.Children.Add(detail);
        panel.Children.Add(dataDirectory);

        var view = new UserControl
        {
            Content = panel,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        // 供底层自测验证 ALC 隔离使用（不影响 Host 展示）。
        System.Reflection.Assembly pluginAssembly = typeof(TestPlugin).Assembly;
        AssemblyLoadContext? loadContext = AssemblyLoadContext.GetLoadContext(pluginAssembly);
        view.Tag = $"assembly={pluginAssembly.GetName().Name};alc={loadContext?.Name}";

        return view;
    }

    public void Dispose()
    {
        _disposed = true;
        _context = null;
    }
}
