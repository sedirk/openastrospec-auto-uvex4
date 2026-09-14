using System.Windows;
using System.Windows.Controls;
namespace UvexAdv.Nina.Plugin;
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public partial class SepMainFocusView : UserControl
{
    public static readonly DependencyProperty NativeToolbarProperty = DependencyProperty.Register(nameof(NativeToolbar), typeof(bool), typeof(SepMainFocusView), new PropertyMetadata(false));
    public bool NativeToolbar { get => (bool)GetValue(NativeToolbarProperty); set => SetValue(NativeToolbarProperty, value); }
    private readonly HashSet<ValidationError> errors = [];
    public SepMainFocusView()
    {
        InitializeComponent();
        Loaded += (_, _) => PublishErrors();
        Unloaded += (_, _) => { if (DataContext is SepMainFocusViewModel vm) vm.SetEditorErrors(this, false); };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SepMainFocusViewModel previous) previous.SetEditorErrors(this, false);
            PublishErrors();
        };
    }
    private void OnValidationError(object sender, ValidationErrorEventArgs e)
    {
        if (e.Action == ValidationErrorEventAction.Added) errors.Add(e.Error); else errors.Remove(e.Error);
        PublishErrors();
    }
    private void PublishErrors() { if (IsLoaded && DataContext is SepMainFocusViewModel vm) vm.SetEditorErrors(this, errors.Count > 0); }
}
