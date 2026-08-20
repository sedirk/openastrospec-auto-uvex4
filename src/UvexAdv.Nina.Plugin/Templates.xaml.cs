using System.ComponentModel.Composition;
using System.Windows;

namespace UvexAdv.Nina.Plugin;

[Export(typeof(ResourceDictionary))]
public partial class Templates : ResourceDictionary
{
    public Templates() => InitializeComponent();
}
