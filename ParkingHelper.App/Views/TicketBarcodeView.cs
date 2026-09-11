using ParkingHelper.App.Services;

namespace ParkingHelper.App.Views;

public sealed class TicketBarcodeView : ContentView
{
    public static readonly BindableProperty BarcodeProperty = BindableProperty.Create(nameof(Barcode),
        typeof(BarcodeRenderResult), typeof(TicketBarcodeView), null, propertyChanged: (view, _, _) => ((TicketBarcodeView)view).Refresh());
    public BarcodeRenderResult? Barcode { get => (BarcodeRenderResult?)GetValue(BarcodeProperty); set => SetValue(BarcodeProperty, value); }

    private void Refresh()
    {
        if (Barcode is not { Modules: not null } result)
        {
            Content = new Label { Text = Barcode?.Message ?? "Loading barcode…", HorizontalTextAlignment = TextAlignment.Center,
                VerticalOptions = LayoutOptions.Center, Margin = 24 };
            return;
        }
        Content = new GraphicsView { Drawable = new BarcodeDrawable(result, (float)DeviceDisplay.Current.MainDisplayInfo.Density), BackgroundColor = Colors.White,
            MinimumHeightRequest = 220 };
        SemanticProperties.SetDescription(Content, "Saved parking ticket barcode");
    }

    private sealed class BarcodeDrawable(BarcodeRenderResult barcode, float density) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.FillColor = Colors.White;
            canvas.FillRectangle(dirtyRect);
            canvas.Antialias = false;
            var availableWidth = Math.Max(0, dirtyRect.Width - 24);
            var availableHeight = Math.Max(0, dirtyRect.Height - 24);
            // Snap module edges to physical pixels to avoid seams between adjacent black modules.
            var desiredScale = Math.Min(availableWidth / barcode.Width, availableHeight / barcode.Height);
            var pixelDensity = density > 0 ? density : 1;
            var scale = MathF.Floor(desiredScale * pixelDensity) / pixelDensity;
            if (scale <= 0) return;
            var barHeight = barcode.Height == 1 ? availableHeight : scale;
            var left = MathF.Round((dirtyRect.X + (dirtyRect.Width - barcode.Width * scale) / 2) * pixelDensity) / pixelDensity;
            var top = MathF.Round((dirtyRect.Y + (dirtyRect.Height - barcode.Height * barHeight) / 2) * pixelDensity) / pixelDensity;
            canvas.FillColor = Colors.Black;
            for (var y = 0; y < barcode.Height; y++)
                for (var x = 0; x < barcode.Width;)
                {
                    if (!barcode.Modules![x, y]) { x++; continue; }
                    var start = x;
                    while (x < barcode.Width && barcode.Modules[x, y]) x++;
                    canvas.FillRectangle(left + start * scale, top + y * barHeight, (x - start) * scale, barHeight);
                }
        }
    }
}
