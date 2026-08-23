#if DESKBOX_NATIVE_AOT
namespace DeskBox.Controls;

// ElementName bindings inside FileItemSurface resolve these calculated
// presentation properties through ICustomProperty under NativeAOT. Keep the
// generated provider limited to the six properties proven by the real surface.
[WinRT.GeneratedBindableCustomProperty([
    nameof(IconLayoutVisibility),
    nameof(ListLayoutVisibility),
    nameof(SurfaceHorizontalAlignment),
    nameof(SurfaceMargin),
    nameof(SurfaceMaxWidth),
    nameof(SurfacePadding)
], [])]
public sealed partial class FileItemSurface
{
}
#endif
