using System.Windows;
using System.Windows.Controls.Primitives;

namespace Hearth.App;

public static class FluentScrollBars
{
    public static void Apply(ResourceDictionary applicationResources)
    {
        var fluent = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Dark.xaml")
        };
        var scrollbar = new Style(typeof(ScrollBar), (Style)fluent[typeof(ScrollBar)]);
        // Keep Microsoft's brushes, fonts, templates, and animations in the
        // scrollbar's resource scope. Other controls retain Hearth's styles.
        scrollbar.Resources.MergedDictionaries.Add(fluent);
        applicationResources[typeof(ScrollBar)] = scrollbar;
    }
}
